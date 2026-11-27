using GameServer.Core.Projectiles;
using GameServer.Core.Protocol.Instance;

public static partial class Game
{
    const string BodyswapSacrificeEffect = "BodyswapAltX";
    // The data has no target search distance. Use the nearby spell target search used by corpse spells.
    const double BodyswapSearchRadius = 60;

    static GolemState.Golem? BodyswapSacrificeMinion(Player player, SpawnedMonster target) =>
        OwnedFighter(player, target.ObjectId) is { } minion && ReferenceEquals(minion.Monster, target)
            && GolemCurrent(player, minion) ? minion : null;

    static bool CanBodyswapTo(Player player, SpawnedMonster target, (int X, int Y) from, double now)
    {
        if (target is not { Dead: false, Life: > 0, WorldHidden: false, Invulnerable: false })
            return false;
        if (!(target.Hostile && target.OwnerId is null && !target.Asleep)
            && BodyswapSacrificeMinion(player, target) is null) return false;
        var at = target.Walk.Position(now);
        return Dist(from, at) <= BodyswapSearchRadius
            && player.Layout.WalkableFor(at.X, at.Y, 0) && !player.Barrier.Contains(at)
            && MovementLanding(player, SkillsFor(player).EffectRow(BodyswapSacrificeEffect), from, at) == at;
    }

    static SpawnedMonster? BodyswapSacrificeTarget(Player player, (int X, int Y) from, (int X, int Y) aim,
        uint targetId, double now)
    {
        if (targetId != 0 && targetId != player.Id)
            return player.Area.Monsters.GetValueOrDefault(targetId) is { } selected
                && CanBodyswapTo(player, selected, from, now) ? selected : null;
        return player.Area.MonstersNear(aim, BodyswapSearchRadius)
            .Select(p => p.Value)
            .Where(m => Dist(aim, m.Walk.Position(now)) <= BodyswapSearchRadius && CanBodyswapTo(player, m, from, now))
            .MinBy(m => (m.Hostile, Dist(aim, m.Walk.Position(now))));
    }

    static bool ScheduleBodyswapSacrifice(Player player,
        (ushort Id, int Effect, string Name, int Level, int Quality, uint Gem) gem, SpawnedMonster target, int castMs,
        ushort sequence, double? startedAt, int recoveryGeneration, Func<bool> current, SkillCriticalRoll? critical, double more)
    {
        var session = ProjectilesFor(player);
        var source = SupportedSkill(player, gem.Id);
        var lifetime = CaptureGemSourceLifetimeLocked(player, gem.Id, gem.Effect);
        if (lifetime is null) return false;
        var module = CombatLifetime;
        bool Current() => session.Active && ReferenceEquals(module, CombatLifetime) && player.PlayerLife > 0 && !player.Paused
            && recoveryGeneration == player.ActionInterruptionGeneration && current()
            && GemSourceLifetimeCurrentLocked(player, gem.Id, gem.Effect, lifetime)
            && SameTriggerSource(source, SupportedSkill(player, gem.Id));
        double started = startedAt ?? AreaTime(player);
        double contact = started + castMs / 1000d
            * Animations.SpellAreaRelease(SkillsFor(player).ClassFor(player.Character.ClassRow).AnimatedObject);
        var sacrifice = new SacrificeUse(player, gem.Id, gem.Effect, gem.Gem);
        session.ScheduleContact(ProjectileRuntime.Time(contact), Current, () =>
        {
            double now = ProjectileWorldTime(player);
            var from = player.Walk.Position(now);
            if (!ReferenceEquals(player.Area.Monsters.GetValueOrDefault(target.ObjectId), target)
                || !CanBodyswapTo(player, target, from, now)) return;
            var to = target.Walk.Position(now);
            var minion = BodyswapSacrificeMinion(player, target);
            double areaMore = minion is null ? 1 : Math.Max(0,
                1 + GolemStat(source.Stats, Stat.CorpseWarpAreaOfEffectPlusPctFinalWhenConsumingMinion) / 100d);
            int area = SkillRadius(player, source.Stats, spell: true, gem.Effect);
            int radius = (int)Math.Max(1, Math.Floor(area * Math.Sqrt(areaMore)));
            uint minionLife = minion?.Monster.MaxLife ?? 0;
            if (minion is not null)
                GolemDieLocked(player, minion, now);

            player.Walk.Teleport(to.X, to.Y, now);
            session.World(player.Id, InstanceProtocol.Teleport(player.Id, to.X, to.Y), "Bodyswap of Sacrifice landing");
            Hit(from);
            Hit(to);
            if (minionLife > 0)
                session.ApplyCorpseExplosion(gem.Id, minionLife, to, radius, now, more: more, minionExplosion: true);

            void Hit((int X, int Y) at)
            {
                var task = AreaSkillHitForUse(null, player, gem.Id, gem, at, at, immediate: true, sequence: sequence,
                    radiusOverride: radius, criticalRoll: critical, sacrifice: sacrifice,
                    damageMore: more, scheduledSession: session);
                if (!task.IsCompleted) throw new InvalidOperationException("Scheduled Bodyswap hit must not yield.");
                task.GetAwaiter().GetResult();
            }
        });
        return true;
    }
}
