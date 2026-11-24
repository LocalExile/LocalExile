using System.Net.Sockets;
using GameServer.Core.Projectiles;
using GameServer.Core.Protocol.Login;
using GameServer.Core.Protocol.Instance;

static partial class Game
{
    internal static bool IsLightningStrike(int effect) => Skills.EffectId(effect) == "LightningStrike";

    // Strike projectiles release at the contact keyframe of each swing in the same paid use.
    internal static Task ScheduleProjectileStrike(Player player, ushort skillId, (ushort Id, int Effect, string Name, int Level, int Quality, uint Gem) gem,
        (int X, int Y) from, (int X, int Y) aim, uint targetId, int recoveryGeneration,
        CooldownUseTicket? ticket, SkillCriticalRoll criticalRoll, ushort sequence, double? swingAt, bool cryExerted = false)
    {
        var session = ProjectilesFor(player);
        session.Advance();
        double acceptedAt = swingAt ?? ProjectileWorldTime(player);
        double duration = SupportedAttackSeconds(player, skillId, sequence);
        double contact = GemAttackContactDelay(player, gem.Effect, duration, out _, out _);
        int repeats = MeleeAttackRepeats(player, skillId);
        int action = player.ActionCount;
        var stats = SupportedSkill(player, skillId).Stats;
        bool frostBlades = IsFrostBlades(gem.Effect);
        double ruthless = RuthlessBlowMore(player, skillId, name => name is var key ? stats.GetValueOrDefault(key) : 0);
        bool Valid() => session.Active && player.ActionInterruptionGeneration == recoveryGeneration
            && (ticket?.Current(player) ?? PlayerStateAllowsAction(player))
            && GemSkillInstanceDisabled(player, gem.Effect, skillId) is null;
        for (int repeat = 0; repeat <= repeats; repeat++)
        {
            int index = repeat;
            session.Schedule(ProjectileRuntime.Time(acceptedAt + contact + repeat * duration),
                () => Valid() && (index == 0 || player.ActionCount == action), () =>
                frostBlades
                    ? ReleaseFrostBlades(player, session, skillId, gem, from, aim, targetId,
                        criticalRoll, sequence, index, cryExerted, ruthless)
                    : ReleaseLightningStrike(player, session, skillId, gem, from, aim, targetId,
                        criticalRoll, sequence, index, cryExerted, ruthless));
        }
        return Task.CompletedTask;
    }

    internal static ProjectileUse ReleaseLightningStrike(Player player, ProjectileSession session, ushort skillId,
        (ushort Id, int Effect, string Name, int Level, int Quality, uint Gem) gem,
        (int X, int Y) from, (int X, int Y) aim, uint targetId, SkillCriticalRoll criticalRoll, ushort sequence, int repeat = 0,
        bool cryExerted = false, double? ruthlessMore = null)
    {
        double now = session.Runtime.Now / (double)ProjectileRuntime.QuantaPerSecond;
        uint primary = LightningStrikeTarget(player, from, aim, targetId, now);
        bool hit = false;
        ProjectileActorKey? excluded = null;
        double more = MeleeRepeatDamageMore(player, skillId, repeat);
        if (primary != 0 && player.Area.Monsters.TryGetValue(primary, out var monster))
        {
            excluded = session.ActorKey(primary, monster);
            var at = monster.Walk.Position(now);
            // Immediate means the scheduler already waited for contact. Selection is one strike, not the
            // generic area approximation, and its success is known before presentation can await network IO.
            var melee = AreaSkillHitForUse(ruthlessMore, player, skillId, gem, from, at,
                immediate: true, sequence: sequence, criticalRoll: criticalRoll, targetId: primary,
                radiusOverride: WeaponFor(player).Range + monster.StopRange, damageMore: more,
                singleStrikeTarget: primary, onHit: _ => hit = true, scheduledSession: session, cryExerted: cryExerted, repeat: repeat);
            if (!melee.IsCompleted) throw new InvalidOperationException("Scheduled Lightning Strike contact must not yield.");
            melee.GetAwaiter().GetResult();
        }
        var use = ClientDrawnVolley(player, session, skillId, gem,
            CaptureGemProjectile(player, session, skillId, gem, aim, criticalRoll, sequence, cryExerted: cryExerted), sequence, 0);
        var shot = (ProjectileOffense)use.Payload!;
        return use with
        {
            Payload = shot with
            {
                ExcludedTarget = excluded,
                // The native skill makes the bolts unavoidable when its melee contact hit.
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
        byte hitCode = 0, SpawnedMonster.NativeKnockback? knockback = null)
    {
        var player = session.Player;
        double now = session.Runtime.Now / (double)ProjectileRuntime.QuantaPerSecond;
        if (hit.Life == 0) SetStats(player, id, [(Stat.HideMiniLifeBar, 1)]);
        if (HitReaction.TryFlinch(monster, stun, now, AreaRng(player)))
        {
            double until = monster.FlinchUntil;
            session.Packet(() => TakeHitPacket(player, id, monster, until, now));
        }
        var playerAt = player.Walk.Position(now);
        var target = monster.Walk.Position(now);
        var order = HitOrder(player, player.Id);
        ushort flags = (ushort)(0x4201 | (critical ? 0x20 : 0));
        byte kind = 3, angle = HitAngle(target.X - playerAt.X, target.Y - playerAt.Y);
        ApplyNativeKnockback(knockback ?? TakeNativeKnockbackLocked(monster), ref flags, ref kind, ref angle,
            out ushort u3c, out ushort u3e);
        // World records, as a projectile's hit: the copy for a party member gets its key renamed only on a declared record.
        session.World(player.Id, InstanceProtocol.ActionHit(player.Id, sequence, id, flags: flags,
            kind: kind, code: hitCode, angle: angle, u3c: u3c, u3e: u3e,
            orderKey: order.Key, checkpoint: order.Hit));
        session.World(id, InstanceProtocol.LifeUpdate(id, hit.Life, orderKey: order.Key, checkpoint: order.Life),
            $"{label} melee t={now:0.000000} target=0x{id:x} damage={damage} life={hit.Life}/{monster.MaxLife}");
        if (critical)
        {
            CommitProjectileCritFlasks(session, now, AreaRng(player));
            session.Enqueue(() => MaybeCreateProfaneGround(player, target));
        }
        if (settleKill && hit.Life == 0) CommitProjectileKill(session, id, monster, session.Runtime.Now, AreaRng(player));
        if (monster.ThresholdReached) session.Enqueue(() => QuestThreshold(player, id, monster));
    }
}
