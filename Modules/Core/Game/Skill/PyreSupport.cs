using GameServer.Core.Diagnostics;
using GameServer.Core.Projectiles;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

public static partial class Game
{
    const uint PyreTriggerChance = Stat.SupportCallThePyreChanceToTriggerCallThePyreOnMeleeHitVsIgnitedEnemiesPct;
    const string PyreCompanionEffect = "TriggeredSupportPyre";

    static void TriggerPyreLocked(Player player, ushort drivingSkill, SupportedGem parent,
        AttackHand hand, SkillCatalog.Unarmed weapon, int? offCritical, (int X, int Y) target, double now)
    {
        var support = parent.Supports.FirstOrDefault(s => SupportHas(s, PyreTriggerChance));
        if (support is null || !SameTriggerSource(parent, SupportedSkill(player, drivingSkill))
            || SupportCompanionSourceLocked(player, parent, support, PyreCompanionEffect, "HungryLoopSupportedByPyre") is not { } source
            || GemSkillInstanceDisabled(player, source.Effect, source.Id, triggered: true) is not null
            || CooldownForSkill(player, source.Id) is not { } cooldown) return;

        var session = ProjectilesFor(player);
        if (!session.Active || player.PlayerLife == 0 || player.Paused || player.Closed || player.LeavingArea) return;
        int key = CooldownKey(player, source.Effect, source.Id);
        var state = CooldownStateLocked(player, key, cooldown, now);
        if (!state.Ready || !RandomRolls.Chance(parent.Stats.GetValueOrDefault(PyreTriggerChance), 100, AreaRng(player))
            || !state.TryUse(now, cooldown)) return;

        const string label = "Call the Pyre";
        var gem = SupportedSkill(player, source.Id);
        var attack = CaptureAreaAttack(player, source.Id, gem, hand, weapon, offCritical, 1, 1, label);
        var lifetime = CaptureGemSourceLifetimeLocked(player, source);
        var critical = CaptureSkillCriticalRoll(player, source.Id);
        var caster = player.Walk.Position(now);
        var centres = PyrePillarCentres(player, caster, target, gem.Stats.GetValueOrDefault(Stat.GraftSkillXophFlamePillarsNumberOfPillars));
        int radius = SkillRadius(player, gem.Stats, spell: false, source.Effect);
        bool lockstep = player.Connection.LockstepClient;
        ushort sequence = NextLockstepSequence(player);
        if (!lockstep) sequence &= 0x7fff;
        player.CooldownSources[source.Effect] = source.Id;
        player.SkillReadyAt[key] = now + state.RemainingSeconds;
        state.ConfirmNativeUse(state.UseVersion, source.Id, synchronizeProgress: !lockstep);
        bool Current() => session.Active && CurrentGameWork && player.PlayerLife > 0 && !player.Paused
            && GemSourceLifetimeCurrentLocked(player, source.Id, source.Effect, lifetime);

        long start = ProjectileRuntime.Time(now);
        var use = (source.Id, source.Effect, Name: label, gem.Level, gem.Quality, source.Gem);
        session.ScheduleContact(start, Current, () =>
        {
            session.World(player.Id, InstanceProtocol.InstantAction(player.Id, source.Id, target.X, target.Y,
                sequence: sequence, flags: lockstep ? (ushort)0x101 : (ushort)0, triggered: true),
                $"0x01ce {label} at {target}");
            if (!lockstep) session.Enqueue(() => SendNativeCooldownRefresh(player));
            if (ServerTrace.Skills)
                Console.WriteLine($"[gateway] {player.Connection.Remote} pyre trigger at={session.Runtime.Now} pillars={centres.Count}");
        });
        for (int i = 0; i < centres.Count; i++)
        {
            var centre = centres[i];
            session.ScheduleContact(start + ProjectileRuntime.Time(.15 * i), Current, () => Hit(centre));
        }

        void Hit((int X, int Y) centre)
        {
            if (ServerTrace.Skills)
                Console.WriteLine($"[gateway] {player.Connection.Remote} pyre pillar at={session.Runtime.Now} cell={centre}");
            var hit = AreaSkillHitForUse(null, player, source.Id, use, centre, centre, immediate: true,
                sequence: sequence, melee: false, criticalRoll: critical, radiusOverride: radius, onHit: Ash,
                triggeredCurrent: Current, scheduledSession: session, cryExerted: false, allowMeleeSplash: false,
                nearbyOnly: true, retainedAttack: attack);
            if (!hit.IsCompleted) throw new InvalidOperationException("A scheduled Call the Pyre must not yield.");
            hit.GetAwaiter().GetResult();
        }

        void Ash(uint id)
        {
            if (!player.Area.Monsters.TryGetValue(id, out var monster) || monster.Dead || monster.Life == 0
                || !RandomRolls.Chance(gem.Stats.GetValueOrDefault(Stat.CoverInAshOnHitPct), 100, AreaRng(player))) return;
            double at = ProjectileWorldTime(player);
            monster.CoveredInAshUntil = Math.Max(monster.CoveredInAshUntil, at + HitDebuffSeconds / monster.RecipientDebuffRate);
            monster.PresentationChanged?.Invoke();
        }
    }

    static List<(int X, int Y)> PyrePillarCentres(Player player, (int X, int Y) caster, (int X, int Y) target, int count)
    {
        count = Math.Clamp(count, 0, 32);
        var centres = new List<(int, int)>(count);
        int dx = target.X - caster.X;
        int dy = target.Y - caster.Y;
        float length = MathF.Sqrt(dx * (float)dx + dy * (float)dy);
        if (length == 0)
        {
            centres.Add(caster);
            return centres;
        }
        for (int i = 0; i < count; i++)
        {
            // The native action offsets the first pillar by 7.5 cells, then walks by 15 cells.
            float scale = (7.5f + 15 * i) / length;
            var centre = (X: caster.X + (int)MathF.Floor(dx * scale + 0.5f), Y: caster.Y + (int)MathF.Floor(dy * scale + 0.5f));
            if (!player.Layout.WalkableFor(centre.X, centre.Y, 0) || player.Barrier.Contains(centre)) break;
            centres.Add(centre);
        }
        if (centres.Count == 0) centres.Add(caster);
        return centres;
    }
}
