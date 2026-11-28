using GameServer.Core.Projectiles;
using GameServer.Core.Protocol.Instance;

public static partial class Game
{
    const string CorpseBodyswapEffect = "Bodyswap";
    const int CorpseBodyswapBaseRadius = 10;
    // The client tables do not export the target distance. Use the nearby corpse spell search distance.
    const int CorpseBodyswapReach = 60;
    readonly record struct BodyswapDestination(uint Id, SpawnedMonster Monster, bool Corpse);

    static BodyswapDestination? FindCorpseBodyswapTarget(Player player, int effect, (int X, int Y) from,
        (int X, int Y) aim, uint targetId, double now, int bodySize = 0)
    {
        if (targetId != 0 && targetId != player.Id)
        {
            if (!player.Area.Monsters.TryGetValue(targetId, out var selected)) return null;
            var target = new BodyswapDestination(targetId, selected, IsCorpse(selected));
            return CorpseBodyswapLanding(player, effect, target, from, now, bodySize) is not null ? target : null;
        }

        if (Nearest(player.Area.SyncDeadMonsters(), corpse: true) is { } dead)
            return dead;
        return Nearest(player.Area.MonstersNear(aim, CorpseBodyswapReach).Select(entry => (entry.Key, entry.Value)), corpse: false);

        BodyswapDestination? Nearest(IEnumerable<(uint Id, SpawnedMonster Monster)> candidates, bool corpse)
        {
            BodyswapDestination? nearest = null;
            double nearestDistance = CorpseBodyswapReach;
            foreach (var (id, monster) in candidates)
            {
                double distance = Dist(aim, monster.Walk.Position(now));
                if (distance > nearestDistance || distance == nearestDistance && nearest is { } best && id > best.Id) continue;
                var candidate = new BodyswapDestination(id, monster, corpse);
                if (CorpseBodyswapLanding(player, effect, candidate, from, now, bodySize) is null) continue;
                nearest = candidate;
                nearestDistance = distance;
            }
            return nearest;
        }
    }

    static (int X, int Y)? CorpseBodyswapLanding(Player player, int effect, BodyswapDestination target,
        (int X, int Y) from, double now, int bodySize)
    {
        var monster = target.Monster;
        if (!ReferenceEquals(player.Area.Monsters.GetValueOrDefault(target.Id), monster)
            || !monster.Hostile || monster.OwnerId is not null || monster.WorldHidden) return null;
        if (target.Corpse ? !IsCorpse(monster) || monster.Ailments.Shattered
            : monster.Dead || monster.Life == 0 || monster.Invulnerable || monster.Asleep || MonsterAsleep(player, target.Id)) return null;
        var to = monster.Walk.Position(now);
        return Dist(from, to) <= CorpseBodyswapReach && player.Layout.WalkableFor(to.X, to.Y, bodySize)
            && !player.Barrier.Contains(to) && MovementLanding(player, effect, from, to) == to ? to : null;
    }

    static int CorpseBodyswapRadius(Player player, int effect, IReadOnlyDictionary<uint, int> own, bool corpse,
        IReadOnlyDictionary<uint, int>? caster = null)
    {
        var stats = SkillAreaStats(player, own, effect, caster);
        int Get(uint stat) => stats.GetValueOrDefault(stat);
        double areaMore = corpse ? Math.Max(0, 1 + Get(Stat.CorpseWarpAreaOfEffectPlusPctFinalWhenConsumingCorpse) / 100d) : 1;
        return SkillArea.Radius(CorpseBodyswapBaseRadius, Get, spell: true, areaMore);
    }

    static bool ScheduleCorpseBodyswap(Player player,
        (ushort Id, int Effect, string Name, int Level, int Quality, uint Gem) gem, BodyswapDestination target,
        int castMs, ushort sequence, double? startedAt, int recoveryGeneration, Func<bool> current, SkillCriticalRoll? critical)
    {
        var source = SupportedSkill(player, gem.Id);
        var sourceLifetime = CaptureGemSourceLifetimeLocked(player, gem.Id, gem.Effect);
        if (sourceLifetime is null) return false;
        var session = ProjectilesFor(player);
        var combat = CombatLifetime;
        bool Current() => ReferenceEquals(combat, CombatLifetime) && player.PlayerLife > 0 && !player.Paused
            && recoveryGeneration == player.ActionInterruptionGeneration && current()
            && GemSourceLifetimeCurrentLocked(player, gem.Id, gem.Effect, sourceLifetime)
            && SameTriggerSource(source, SupportedSkill(player, gem.Id));
        double contact = (startedAt ?? AreaTime(player)) + castMs / 1000d
            * Animations.SpellAreaRelease(SkillsFor(player).ClassFor(player.Character.ClassRow).AnimatedObject);
        var sacrifice = new SacrificeUse(player, gem.Id, gem.Effect, gem.Gem);
        session.ScheduleContact(ProjectileRuntime.Time(contact), Current,
            () => ReleaseCorpseBodyswap(player, session, gem, source, target, sequence, sacrifice, critical));
        return true;
    }

    static void ReleaseCorpseBodyswap(Player player, ProjectileSession session,
        (ushort Id, int Effect, string Name, int Level, int Quality, uint Gem) gem,
        SupportedGem source, BodyswapDestination target, ushort sequence, SacrificeUse sacrifice, SkillCriticalRoll? critical)
    {
        double now = ProjectileWorldTime(player);
        var from = player.Walk.Position(now);
        if (CorpseBodyswapLanding(player, gem.Effect, target, from, now, 0) is not { } to) return;

        int radius = CorpseBodyswapRadius(player, gem.Effect, source.Stats, target.Corpse);
        if (target.Corpse) session.ConsumeSpellCorpse(target.Id, target.Monster, now);
        player.Walk.Teleport(to.X, to.Y, now);
        session.World(player.Id, InstanceProtocol.Teleport(player.Id, to.X, to.Y), "Bodyswap landing");
        Hit(from);
        Hit(to);
        if (target.Corpse) session.ApplyCorpseExplosion(gem.Id, target.Monster.MaxLife, to, radius, now);

        void Hit((int X, int Y) at) => _ = AreaSkillHit(player, gem.Id, gem, at, at, immediate: true, sequence: sequence,
            criticalRoll: critical, radiusOverride: radius, sacrifice: sacrifice, scheduledSession: session);
    }

    static Action? PrepareTriggeredCorpseBodyswap(Player player, ProjectileSession session,
        (ushort Id, int Effect, string Name, int Level, int Quality, uint Gem) gem, SupportedGem source,
        (int X, int Y) aim, Func<bool> current)
    {
        double now = ProjectileWorldTime(player);
        var target = FindCorpseBodyswapTarget(player, gem.Effect, player.Walk.Position(now), aim, 0, now);
        if (target is not { } body || player.PlayerLife == 0) return null;
        var sacrifice = new SacrificeUse(player, gem.Id, gem.Effect, gem.Gem, triggered: true);
        var critical = CaptureSkillCriticalRoll(player, gem.Id);
        return () => session.ScheduleContact(session.Runtime.Now, () => current() && sacrifice.Current,
            () => ReleaseCorpseBodyswap(player, session, gem, source, body, 0xffff, sacrifice, critical));
    }

    static void CastCorpseBodyswapTotem(Player player, TotemState.Totem totem, double now, WorldRecordBuilder bytes)
    {
        var from = totem.At;
        if (FindCorpseBodyswapTarget(player, totem.Effect, from, from, 0, now, totem.BodySize) is not { } target)
        {
            totem.NextCast = now + .25;
            return;
        }
        if (TotemCastSeconds(player, totem.SkillId) is not double seconds) return;
        var aim = target.Monster.Walk.Position(now);
        ushort sequence = ++totem.Sequence;
        totem.NextCast = now + seconds;
        bytes.World(totem.Id, InstanceProtocol.ActionStarted(totem.Id, from.X, from.Y, aim.X, aim.Y,
            0x4000, sequence, player.MonsterOrderFlags, targetObjectId: target.Id));
        var session = ProjectilesFor(player);
        var combat = CombatLifetime;
        session.ScheduleContact(ProjectileRuntime.Time(totem.NextCast),
            () => ReferenceEquals(combat, CombatLifetime) && SpellTotemCurrent(player, totem), () =>
        {
            double contact = ProjectileWorldTime(player);
            var from = totem.At;
            if (CorpseBodyswapLanding(player, totem.Effect, target, from, contact, totem.BodySize) is not { } to) return;
            var source = SupportedSkill(player, totem.SkillId);
            var caster = TotemCasterStats(CharacterPlayerStats(player));
            int radius = CorpseBodyswapRadius(player, totem.Effect, source.Stats, target.Corpse, caster);
            if (target.Corpse) session.ConsumeSpellCorpse(target.Id, target.Monster, contact, playerConsumes: false);
            totem.Relocate(to);
            player.SetObjectBlocker(totem.Id, to.X, to.Y, totem.BodySize, true);
            session.World(totem.Id, InstanceProtocol.Teleport(totem.Id, to.X, to.Y), "Bodyswap totem landing");
            var critical = CaptureSkillCriticalRoll(player, totem.SkillId);
            double more = Math.Max(0, 1 + GolemStat(source.Stats, Stat.SupportTotemDamagePlusPctFinal) / 100d);
            TotemNovaLocked(player, session, totem, from, radius, sequence, damageMore: more, sharedCritical: critical);
            TotemNovaLocked(player, session, totem, to, radius, sequence, damageMore: more, sharedCritical: critical);
            if (target.Corpse)
                session.ApplyCorpseExplosion(totem.SkillId, target.Monster.MaxLife, to, radius, contact, more: more, totem: true);
        });
    }

    static void ReleasePlacedCorpseBodyswap(Player player, PlacedAreaHit placement, (int X, int Y) aim, double now,
        WorldRecordBuilder bytes)
    {
        var device = placement.Device;
        var from = device.At;
        if (FindCorpseBodyswapTarget(player, device.Effect, from, aim, 0, now) is not { } target
            || CorpseBodyswapLanding(player, device.Effect, target, from, now, 0) is not { } to) return;
        var session = ProjectilesFor(player);
        int radius = CorpseBodyswapRadius(player, device.Effect, device.Own, target.Corpse, device.Offense?.Caster);
        bytes.World(device.Id, InstanceProtocol.ActionStarted(device.Id, from.X, from.Y, to.X, to.Y,
            0x4000, 0xffff, player.MonsterOrderFlags, targetObjectId: target.Id));
        bytes.World(device.Id, InstanceProtocol.Teleport(device.Id, to.X, to.Y));
        if (target.Corpse) session.ConsumeSpellCorpse(target.Id, target.Monster, now, playerConsumes: false);
        placement.Apply(player, from, radius, now);
        placement.Apply(player, to, radius, now);
        if (target.Corpse) session.ApplyCorpseExplosion(device.Skill, target.Monster.MaxLife, to, radius, now, placement);
    }
}
