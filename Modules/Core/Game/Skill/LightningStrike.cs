using System.Net.Sockets;
using GameServer.Core.Projectiles;
using GameServer.Core.Protocol.Login;
using GameServer.Core.Protocol.Instance;

static partial class Game
{
    internal static bool IsLightningStrike(int effect) => Skills.EffectId(effect) is "LightningStrike" or "LightningStrikeAltX";

    readonly record struct ExtraProjectileStrike(uint? Target, (int X, int Y) From, (int X, int Y) Aim);
    readonly record struct ProjectileStrikeContact(uint Target, (int X, int Y) At, ProjectileActorKey? Excluded, int StopRange);

    // The client's strike action chooses the spirit targets as the swing starts. It processes the main
    // and extra contacts together, then releases a volley from each branch of the attack.
    internal static Task ScheduleProjectileStrike(Player player, ushort skillId, (ushort Id, int Effect, string Name, int Level, int Quality, uint Gem) gem,
        (int X, int Y) from, (int X, int Y) aim, uint targetId, int recoveryGeneration,
        CooldownUseTicket? ticket, SkillCriticalRoll criticalRoll, ushort sequence, double? swingAt, bool cryExerted = false, int ancestralTargets = 0,
        int? rallyingDamage = null, TraumaUse? traumaUse = null)
    {
        var session = ProjectilesFor(player);
        session.Advance();
        double now = ProjectileWorldTime(player);
        double acceptedAt = swingAt ?? now;
        double duration = SupportedAttackSeconds(player, skillId, sequence);
        double contact = GemAttackContactDelay(player, gem.Effect, duration, out _, out _);
        int repeats = MeleeAttackRepeats(player, skillId);
        int action = player.ActionCount;
        var skill = SupportedSkill(player, skillId);
        bool frostBlades = IsFrostBlades(gem.Effect);
        bool wildStrike = IsWildStrike(gem.Effect);
        double ruthless = RuthlessBlowMore(player, skillId, skill.Stats.GetValueOrDefault);
        List<ExtraProjectileStrike>? extras = null;
        byte[]? memoryBuff = null;
        if (!wildStrike)
            using (ProjectileMutation(player, advance: false))
            {
                (var spirits, var echo, memoryBuff) = StrikeExtrasLocked(player, skillId, gem.Effect, skill.Stats, from, aim, sequence, targetId, now,
                    groundTarget: () => LightningStrikeTarget(player, from, aim, 0, now,
                        frostBlades ? FrostBladesReach(player, skill.Stats) : 0), exertedTargets: ancestralTargets);
                if (spirits.Count > 0 || echo is not null)
                {
                    extras = new(spirits.Count + 1);
                    foreach (uint id in spirits)
                        if (player.Area.Monsters.TryGetValue(id, out var monster))
                        {
                            var at = monster.Walk.Position(now);
                            var towards = (ProjectileSession.Vector(at) - ProjectileSession.Vector(from)).Unit;
                            // 0x141c9d7c0 places the copy eight cells behind its target, with per-axis rounding.
                            var origin = (at.X - (int)Math.Floor(towards.X * 8 + .5), at.Y - (int)Math.Floor(towards.Y * 8 + .5));
                            extras.Add(new(id, origin, at));
                        }
                    if (echo is { } previous) extras.Add(new(null, previous.Player, previous.Centre));
                }
            }
        if (memoryBuff is not null)
            session.World(player.Id, memoryBuff, $"0x01c1 player buff skill_location_memory for skill 0x{skillId:x4}");

        bool Valid() => session.Active && player.ActionInterruptionGeneration == recoveryGeneration
            && (ticket?.Current(player) ?? PlayerStateAllowsAction(player))
            && GemSkillInstanceDisabled(player, gem.Effect, skillId) is null;
        for (int repeat = 0; repeat <= repeats; repeat++)
        {
            int index = repeat;
            long contactAt = ProjectileRuntime.Time(acceptedAt + contact + repeat * duration);
            bool RepeatValid() => Valid() && (index == 0 || player.ActionCount == action);
            if (wildStrike)
                session.Schedule(contactAt, RepeatValid, () =>
                    ReleaseWildStrike(player, session, skillId, gem, from, aim, targetId,
                        criticalRoll, sequence, index, cryExerted, ruthless, ancestralTargets, rallyingDamage, traumaUse));
            else
                session.ScheduleContact(contactAt, RepeatValid, () =>
                    ReleaseProjectileStrike(player, session, skillId, gem, from, aim, targetId, criticalRoll, sequence,
                        index, cryExerted, ruthless, frostBlades, extras, ancestralTargets, rallyingDamage, traumaUse));
        }
        return Task.CompletedTask;
    }

    static void ReleaseProjectileStrike(Player player, ProjectileSession session, ushort skillId,
        (ushort Id, int Effect, string Name, int Level, int Quality, uint Gem) gem,
        (int X, int Y) from, (int X, int Y) aim, uint targetId, SkillCriticalRoll criticalRoll, ushort sequence,
        int repeat, bool cryExerted, double ruthlessMore, bool frostBlades, List<ExtraProjectileStrike>? extras, int ancestralTargets, int? rallyingDamage,
        TraumaUse? traumaUse)
    {
        double now = session.Runtime.Now / (double)ProjectileRuntime.QuantaPerSecond;
        var source = SupportedSkill(player, skillId);
        var stats = source.Stats;
        int extraReach = frostBlades ? FrostBladesReach(player, stats) : 0;
        int reach = WeaponFor(player).Range + extraReach;
        ProjectileStrikeContact Contact(uint id, ExtraProjectileStrike? strike = null) => id != 0 && player.Area.Monsters.TryGetValue(id, out var monster)
            && monster is { Hostile: true, Dead: false, Life: > 0, OwnerId: null, WorldHidden: false }
            && (strike is not { } extra || ExtraStrikeCanReach(player, monster, extra, now, reach))
            ? new(id, monster.Walk.Position(now), session.ActorKey(id, monster), monster.StopRange) : default;
        var primary = Contact(LightningStrikeTarget(player, from, aim, targetId, now, extraReach));
        List<uint>? additionalTargets = null;
        ProjectileStrikeContact[]? additionalContacts = null;
        if (extras is { Count: > 0 })
        {
            additionalTargets = new(extras.Count);
            additionalContacts = new ProjectileStrikeContact[extras.Count];
            for (int i = 0; i < extras.Count; i++)
            {
                var extra = extras[i];
                var selected = Contact(extra.Target ?? LightningStrikeTarget(player, extra.From, extra.Aim, 0, now, extraReach), extra);
                if (selected.Target == 0 || selected.Target == primary.Target || additionalTargets.Contains(selected.Target))
                    continue;
                additionalTargets.Add(selected.Target);
                additionalContacts[i] = selected;
            }
        }

        double more = MeleeRepeatDamageMore(player, skillId, repeat);
        bool primaryHit = false;
        var extraHits = additionalTargets is { Count: > 0 } ? new HashSet<uint>() : null;
        if (primary.Target != 0 || extraHits is not null)
        {
            var melee = AreaSkillHitForUse(ruthlessMore, player, skillId, gem, from, primary.Target != 0 ? primary.At : aim,
                immediate: true, sequence: sequence, criticalRoll: criticalRoll, targetId: primary.Target,
                radiusOverride: reach + primary.StopRange, damageMore: more,
                singleStrikeTarget: primary.Target, onHit: target =>
                {
                    if (target == primary.Target) primaryHit = true;
                    else extraHits?.Add(target);
                }, scheduledSession: session, cryExerted: cryExerted, repeat: repeat, additionalStrikeTargets: additionalTargets,
                ancestralTargets: ancestralTargets, rallyingDamage: rallyingDamage, traumaUse: traumaUse);
            if (!melee.IsCompleted) throw new InvalidOperationException("Scheduled projectile strike contact must not yield.");
            melee.GetAwaiter().GetResult();
        }

        if (frostBlades && !primaryHit && extraHits is not { Count: > 0 }) return;
        var use = CaptureGemProjectile(player, session, skillId, gem, aim, criticalRoll, weaponHand: null, sequence: sequence,
            cryExerted: cryExerted, otherWarcryExertions: ancestralTargets > 0 ? 1 : 0, rallyingDamage: rallyingDamage,
            ancestralExerted: ancestralTargets > 0);
        if (traumaUse is { Gained: false } && use.Payload is ProjectileOffense offense)
            use = use with { Payload = offense with { Trauma = traumaUse, TraumaSource = source } };
        if (frostBlades)
        {
            if (primaryHit) session.ReleaseNow(FrostBladesVolley(player, use, stats, now, from, aim, primary, more));
            if (additionalContacts is null || extraHits is null) return;
            for (int i = 0; i < additionalContacts.Length; i++)
            {
                var selected = additionalContacts[i];
                if (extraHits.Contains(selected.Target))
                    session.ReleaseNow(FrostBladesVolley(player, use, stats, now, extras![i].From, extras[i].Aim, selected, more));
            }
            return;
        }

        session.ReleaseNow(LightningStrikeVolley(player, session, skillId, gem, use, sequence, primary, primaryHit, more));
        if (extras is null || additionalContacts is null) return;
        for (int i = 0; i < extras.Count; i++)
        {
            var extra = extras[i];
            var selected = additionalContacts[i];
            var start = ProjectileSession.Vector(extra.From);
            var target = ProjectileSession.Vector(extra.Aim);
            var ray = target - start;
            // Keep the outward aim of the copy rather than firing its volley from the player.
            var origin = start + ray.Unit * Math.Min(3, ray.Length / 2);
            var volley = use with { Origin = origin, Aim = target };
            session.ReleaseNow(LightningStrikeVolley(player, session, skillId, gem, volley, sequence, selected,
                extraHits?.Contains(selected.Target) == true, more));
        }
    }

    static bool ExtraStrikeCanReach(Player player, SpawnedMonster monster, ExtraProjectileStrike extra, double now, int reach)
    {
        var at = monster.Walk.Position(now);
        if (!SwingInReach(player, monster, extra.From, at, reach + 2)) return false;
        var origin = ProjectileSession.Vector(extra.From);
        var intended = ProjectileSession.Vector(extra.Aim) - origin;
        var current = ProjectileSession.Vector(at) - origin;
        // The extra strike keeps its facing during the windup (0x141ca2d90's 60 degree half angle).
        return intended.Length == 0 || current.Dot(intended.Unit) >= current.Length * .5;
    }

    static ProjectileUse LightningStrikeVolley(Player player, ProjectileSession session, ushort skillId,
        (ushort Id, int Effect, string Name, int Level, int Quality, uint Gem) gem,
        ProjectileUse use, ushort sequence, ProjectileStrikeContact contact, bool hit, double more)
    {
        use = ClientDrawnVolley(player, session, skillId, gem, use, sequence, 0);
        var shot = (ProjectileOffense)use.Payload!;
        return use with
        {
            Payload = shot with
            {
                // The exclusion travels with this volley. A different strike's bolts may hit this enemy.
                ExcludedTarget = contact.Excluded,
                AccuracyProfile = hit && shot.AccuracyProfile is { } accuracy ? accuracy with { CannotBeEvaded = true } : shot.AccuracyProfile,
                Hit = shot.Hit.Scale(more),
                Ignite = RepeatAilment(shot.Ignite, more),
                Bleed = RepeatAilment(shot.Bleed, more),
                Poison = RepeatAilment(shot.Poison, more),
            }
        };
    }

    internal static uint LightningStrikeTarget(Player player, (int X, int Y) from, (int X, int Y) aim,
        uint targetId, double now, int extraReach = 0)
    {
        int range = WeaponFor(player).Range + extraReach;
        return player.Area.Monsters.Where(e => e.Value.Hostile && !e.Value.Dead && e.Value.Life > 0 && e.Value.OwnerId is null
                && (targetId == 0 || e.Key == targetId)
                && SwingInReach(player, e.Value, from, e.Value.Walk.Position(now), range + 2)
                && (targetId != 0 || Dist(e.Value.Walk.Position(now), aim) <= range + e.Value.StopRange))
            .OrderBy(e => Dist(e.Value.Walk.Position(now), aim)).ThenBy(e => e.Key)
            .Select(e => e.Key).FirstOrDefault();
    }

    // Scheduled contacts commit rewards and queue charge gains before yielding to the outbox. A held socket must
    // not postpone gameplay, and the ordinary area-hit path must not recursively advance this runtime.
    public static void CommitScheduledMeleeHit(ProjectileSession session, uint id, SpawnedMonster monster,
        MonsterHit hit, int damage, bool critical, StunHit stun, ushort sequence, string label, bool settleKill = true,
        byte hitCode = 0, SpawnedMonster.NativeKnockback? knockback = null, bool actionHit = true,
        (uint Id, (int X, int Y) At)? proxyActor = null)
    {
        var player = session.Player;
        double now = session.Runtime.Now / (double)ProjectileRuntime.QuantaPerSecond;
        if (hit.Life == 0) SetStats(player, id, [(Stat.HideMiniLifeBar, 1)]);
        if (HitReaction.TryFlinch(monster, stun, now, AreaRng(player)))
        {
            double until = monster.FlinchUntil;
            session.Packet(() => TakeHitPacket(player, id, monster, until, now));
        }
        var target = monster.Walk.Position(now);
        if (actionHit)
        {
            var actor = proxyActor ?? (player.Id, player.Walk.Position(now));
            var order = HitOrder(player, actor.Id);
            ushort flags = (ushort)(0x4201 | (critical ? 0x20 : 0));
            byte kind = 3, angle = HitAngle(target.X - actor.At.X, target.Y - actor.At.Y);
            ApplyNativeKnockback(knockback ?? TakeNativeKnockbackLocked(monster), ref flags, ref kind, ref angle,
                out ushort u3c, out ushort u3e);
            // World records, as a projectile's hit: the copy for a party member gets its key renamed only on a declared record.
            session.World(actor.Id, InstanceProtocol.ActionHit(actor.Id, sequence, id, flags: flags,
                kind: kind, code: hitCode, angle: angle, u3c: u3c, u3e: u3e,
                orderKey: order.Key, checkpoint: order.Hit));
            session.World(id, InstanceProtocol.LifeUpdate(id, hit.Life, orderKey: order.Key, checkpoint: order.Life),
                $"{label} melee t={now:0.000000} target=0x{id:x} damage={damage} life={hit.Life}/{monster.MaxLife}");
        }
        else
            session.World(id, InstanceProtocol.LifeUpdate(id, hit.Life),
                $"{label} t={now:0.000000} target=0x{id:x} damage={damage} life={hit.Life}/{monster.MaxLife}");
        if (critical && proxyActor is null)
        {
            CommitProjectileCritFlasks(session, now, AreaRng(player));
            session.Enqueue(() => MaybeCreateProfaneGround(player, target));
        }
        if (settleKill && hit.Life == 0) CommitProjectileKill(session, id, monster, session.Runtime.Now, AreaRng(player));
        if (monster.ThresholdReached) session.Enqueue(() => QuestThreshold(player, id, monster));
    }
}
