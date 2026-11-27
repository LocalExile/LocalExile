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

    static bool CanBodyswapTo(Player player, SpawnedMonster target, (int X, int Y) from, double now, int bodySize)
    {
        if (target is not { Dead: false, Life: > 0, WorldHidden: false, Invulnerable: false })
            return false;
        if (!(target.Hostile && target.OwnerId is null && !target.Asleep)
            && BodyswapSacrificeMinion(player, target) is null) return false;
        var at = target.Walk.Position(now);
        return Dist(from, at) <= BodyswapSearchRadius
            && player.Layout.WalkableFor(at.X, at.Y, bodySize) && !player.Barrier.Contains(at)
            && MovementLanding(player, SkillsFor(player).EffectRow(BodyswapSacrificeEffect), from, at) == at;
    }

    static SpawnedMonster? BodyswapSacrificeTarget(Player player, (int X, int Y) from, (int X, int Y) aim,
        uint targetId, double now, int bodySize = 0)
    {
        if (targetId != 0 && targetId != player.Id)
            return player.Area.Monsters.GetValueOrDefault(targetId) is { } selected
                && CanBodyswapTo(player, selected, from, now, bodySize) ? selected : null;
        return player.Area.MonstersNear(aim, BodyswapSearchRadius)
            .Select(p => p.Value)
            .Where(m => Dist(aim, m.Walk.Position(now)) <= BodyswapSearchRadius && CanBodyswapTo(player, m, from, now, bodySize))
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
        session.ScheduleContact(ProjectileRuntime.Time(contact), Current,
            () => ReleasePlayerBodyswap(player, session, gem, source, target, sequence, sacrifice, critical, more));
        return true;
    }

    static ((int X, int Y) At, int Radius, uint MinionLife)? CommitBodyswapSacrifice(Player player,
        SpawnedMonster target, (int X, int Y) from, int effect, IReadOnlyDictionary<uint, int> own, double now, int bodySize = 0)
    {
        if (!ReferenceEquals(player.Area.Monsters.GetValueOrDefault(target.ObjectId), target)
            || !CanBodyswapTo(player, target, from, now, bodySize)) return null;
        var at = target.Walk.Position(now);
        var minion = BodyswapSacrificeMinion(player, target);
        double areaMore = minion is null ? 1 : Math.Max(0,
            1 + GolemStat(own, Stat.CorpseWarpAreaOfEffectPlusPctFinalWhenConsumingMinion) / 100d);
        int area = SkillRadius(player, own, spell: true, effect);
        int radius = (int)Math.Max(1, Math.Floor(area * Math.Sqrt(areaMore)));
        uint life = minion?.Monster.MaxLife ?? 0;
        if (minion is not null) GolemDieLocked(player, minion, now);
        return (at, radius, life);
    }

    static void ReleasePlayerBodyswap(Player player, ProjectileSession session,
        (ushort Id, int Effect, string Name, int Level, int Quality, uint Gem) gem, SupportedGem source,
        SpawnedMonster target, ushort sequence, SacrificeUse sacrifice, SkillCriticalRoll? critical, double more)
    {
        double now = ProjectileWorldTime(player);
        var from = player.Walk.Position(now);
        if (CommitBodyswapSacrifice(player, target, from, gem.Effect, source.Stats, now) is not { } release) return;
        var (to, radius, minionLife) = release;
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
    }

    static Action? PrepareTriggeredBodyswap(Player player, ProjectileSession session,
        (ushort Id, int Effect, string Name, int Level, int Quality, uint Gem) gem, SupportedGem source,
        (int X, int Y) aim, uint preferredTarget, Func<bool> current)
    {
        double now = ProjectileWorldTime(player);
        var from = player.Walk.Position(now);
        // A triggering attack supplies the aim, while Bodyswap itself selects its target.
        if (player.Area.Monsters.GetValueOrDefault(preferredTarget) is { } trigger) aim = trigger.Walk.Position(now);
        var target = BodyswapSacrificeTarget(player, from, aim, 0, now);
        if (target is null || player.PlayerLife == 0) return null;
        var sacrifice = new SacrificeUse(player, gem.Id, gem.Effect, gem.Gem, triggered: true);
        var critical = CaptureSkillCriticalRoll(player, gem.Id);
        return () => session.ScheduleContact(session.Runtime.Now, () => current() && sacrifice.Current,
            () => ReleasePlayerBodyswap(player, session, gem, source, target, 0xffff, sacrifice, critical, 1));
    }

    static void ScheduleBodyswapTotem(Player player, TotemState.Totem totem, double now, WorldRecordBuilder bytes)
    {
        var from = totem.At;
        var target = BodyswapSacrificeTarget(player, from, from, 0, now, totem.BodySize);
        if (target is null)
        {
            totem.NextCast = now + .25;
            return;
        }
        if (TotemCastSeconds(player, totem.SkillId) is not double seconds) return;
        var aim = target.Walk.Position(now);
        ushort sequence = ++totem.Sequence;
        totem.NextCast = now + seconds;
        bytes.World(totem.Id, InstanceProtocol.ActionStarted(totem.Id, from.X, from.Y, aim.X, aim.Y,
            0x4000, sequence, player.MonsterOrderFlags, targetObjectId: target.ObjectId));
        var session = ProjectilesFor(player);
        var lifetime = CombatLifetime;
        session.ScheduleContact(ProjectileRuntime.Time(totem.NextCast),
            () => ReferenceEquals(lifetime, CombatLifetime) && SpellTotemCurrent(player, totem), () =>
        {
            double contact = ProjectileWorldTime(player);
            var own = SupportedSkill(player, totem.SkillId).Stats;
            if (CommitBodyswapSacrifice(player, target, from, totem.Effect, own, contact, totem.BodySize) is not { } release) return;
            var (to, radius, minionLife) = release;
            totem.Relocate(to);
            player.SetObjectBlocker(totem.Id, to.X, to.Y, totem.BodySize, true);
            session.World(totem.Id, InstanceProtocol.Teleport(totem.Id, to.X, to.Y), "Bodyswap totem landing");
            var critical = CaptureSkillCriticalRoll(player, totem.SkillId);
            TotemNovaLocked(player, session, totem, from, radius, sequence, sharedCritical: critical);
            TotemNovaLocked(player, session, totem, to, radius, sequence, sharedCritical: critical);
            if (minionLife > 0)
                session.ApplyCorpseExplosion(totem.SkillId, minionLife, to, radius, contact, minionExplosion: true, totem: true);
        });
    }

    static void ReleasePlacedBodyswap(Player player, PlacedAreaHit placement, (int X, int Y) aim, double now,
        WorldRecordBuilder bytes)
    {
        var device = placement.Device;
        var from = device.At;
        var target = BodyswapSacrificeTarget(player, from, aim, 0, now);
        if (target is null || CommitBodyswapSacrifice(player, target, from, device.Effect, device.Own, now) is not { } release)
            return;
        var (to, radius, minionLife) = release;
        bytes.World(device.Id, InstanceProtocol.ActionStarted(device.Id, from.X, from.Y, to.X, to.Y,
            0x4000, 0xffff, player.MonsterOrderFlags, targetObjectId: target.ObjectId));
        bytes.World(device.Id, InstanceProtocol.Teleport(device.Id, to.X, to.Y));
        placement.Apply(player, from, radius, now);
        placement.Apply(player, to, radius, now);
        if (minionLife > 0)
            ProjectilesFor(player).ApplyCorpseExplosion(device.Skill, minionLife, to, radius, now, placement, minionExplosion: true);
    }
}
