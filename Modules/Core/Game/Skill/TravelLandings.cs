using System.Net.Sockets;
using GameServer.Core.Projectiles;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

public static partial class Game
{
    static async Task<(int X, int Y)?> CommitTravelLanding(Player player, (int X, int Y) landing, Func<bool> current)
    {
        (int X, int Y)? departure = null;
        await SendWorldRecord(player, player.Id, [], "travel skill landing", atGate: () =>
        {
            if (!current() || player.Closed || player.LeavingArea || player.PlayerLife == 0) return null;
            if (!player.Layout.WalkableFor(landing.X, landing.Y, 0) || player.Barrier.Contains(landing)) return null;
            double now = ProjectileWorldTime(player);
            departure = player.Walk.Position(now);
            player.Walk.Teleport(landing.X, landing.Y, now);
            return InstanceProtocol.Teleport(player.Id, landing.X, landing.Y);
        });
        return departure;
    }

    public sealed partial class ProjectileSession
    {
        internal int WarpCount;
        internal long LastWarpTime;
        internal (int X, int Y) LastWarpDestination;
    }

    const string VaalLightningWarpEffect = "VaalLightningWarpInstant";

    internal static bool IsLightningWarpEffect(string? effect) =>
        effect is "LightningWarp" or "LightningWarpHardMode" or "LightningWarpRoyale" or VaalLightningWarpEffect;

    static bool ScheduleLightningWarp(Player player,
        (ushort Id, int Effect, string Name, int Level, int Quality, uint Gem) gem, (int X, int Y) aim,
        Func<bool> current, SkillCriticalRoll? critical)
    {
        var session = ProjectilesFor(player);
        var source = SupportedSkill(player, gem.Id);
        bool vaal = SkillsFor(player).EffectId(gem.Effect) == VaalLightningWarpEffect;
        int Own(uint name) => GolemStat(source.Stats, name);
        if (CastMs(player, gem.Effect, gem.Id) is not int cast) return false;
        var lifetime = CombatLifetime;
        double started = AreaTime(player);
        bool Active() => session.Active && ReferenceEquals(lifetime, CombatLifetime) && player.PlayerLife > 0;
        // At the cast clip's contact like every other spell, not at its end: the next action arrives about then
        // and would retire this one (current) with its cost already paid.
        long release = Math.Max(session.Runtime.Now, ProjectileRuntime.Time(started + cast / 1000d
            * Animations.SpellAreaRelease(SkillsFor(player).ClassFor(player.Character.ClassRow).AnimatedObject)));
        session.Runtime.Schedule(release, () =>
        {
            if (!Active() || !current() || !SameTriggerSource(source, SupportedSkill(player, gem.Id))
                || !vaal && session.WarpCount >= Own(Stat.MaxNumberOfLightningWarpMarkers)) return;
            double now = session.Runtime.Now / (double)ProjectileRuntime.QuantaPerSecond;
            var origin = vaal || session.WarpCount == 0 ? player.Walk.Position(now) : session.LastWarpDestination;
            var landing = MovementLanding(player, gem.Effect, origin, aim);
            if (origin == landing || player.Walk.Speed <= 0) return;
            var playerStats = CharacterPlayerStats(player);
            int walkMs = (int)Math.Min(int.MaxValue, 1000 * Dist(origin, landing) / player.Walk.Speed);
            double delay = .1 + SkillDurationModifiers.Seconds(walkMs, n => GolemStat(playerStats, n), Own);
            if (vaal)
            {
                session.Runtime.Schedule(session.Runtime.Now + ProjectileRuntime.Time(delay), () =>
                {
                    if (Active()) WarpEnemies(player, session, gem, origin, landing, source, critical, Active);
                });
                return;
            }
            long due = Math.Max(session.Runtime.Now, session.LastWarpTime) + ProjectileRuntime.Time(delay);
            session.WarpCount++;
            session.LastWarpTime = due;
            session.LastWarpDestination = landing;
            session.Runtime.Schedule(due, () =>
            {
                session.WarpCount--;
                if (!Active()) return;
                session.Enqueue(async () =>
                {
                    bool Current() => Active() && SameTriggerSource(source, SupportedSkill(player, gem.Id));
                    if (await CommitTravelLanding(player, landing, Current) is not { } departure) return;
                    var sacrifice = new SacrificeUse(player, gem.Id, gem.Effect, gem.Gem);
                    Task Hit((int X, int Y) at) => AreaSkillHit(player, gem.Id, gem, at, at,
                        immediate: true, criticalRoll: critical, sacrifice: sacrifice, triggeredCurrent: Current);
                    await Hit(departure);
                    await Hit(landing);
                });
            });
        });
        return true;
    }

    static void WarpEnemies(Player player, ProjectileSession session,
        (ushort Id, int Effect, string Name, int Level, int Quality, uint Gem) gem,
        (int X, int Y) origin, (int X, int Y) landing, SupportedGem source, SkillCriticalRoll? critical, Func<bool> inArea)
    {
        bool Current() => inArea() && SameTriggerSource(source, SupportedSkill(player, gem.Id));
        if (!Current()) return;
        int radius = SkillRadius(player, source.Stats, spell: true, gem.Effect);
        double now = ProjectileWorldTime(player);
        var targets = player.Area.MonstersNear(origin, radius).Where(entry =>
            entry.Value is { Hostile: true, Dead: false, Life: > 0, WorldHidden: false, Invulnerable: false, OwnerId: null }
            && Dist(origin, entry.Value.Walk.Position(now)) <= radius).ToDictionary();
        if (targets.Count == 0) return;

        var sacrifice = new SacrificeUse(player, gem.Id, gem.Effect, gem.Gem);
        // Immediate hits with a scheduled session commit on this frame and queue their presentation.
        void Hit((int X, int Y) at, IReadOnlyDictionary<uint, SpawnedMonster> eligible) =>
            _ = AreaSkillHitForUse(null, player, gem.Id, gem, at, at, immediate: true, criticalRoll: critical, radiusOverride: radius,
                sacrifice: sacrifice, triggeredCurrent: Current, scheduledSession: session, targetFilter: (id, _) =>
                    eligible.TryGetValue(id, out var target) && ReferenceEquals(target, player.Area.Monsters.GetValueOrDefault(id)));
        Hit(origin, targets);
        if (!Current()) return;

        var arrivals = new Dictionary<uint, SpawnedMonster>();
        foreach (var (id, target) in targets)
        {
            if (!MonsterPresent(player, id, target) || target.Invulnerable || !target.Hostile
                || target.Rarity == MonsterRarity.Unique
                || target.Variety is { MovementSpeed: 0 } or { Immobile: true }
                || target.Variety is not null && target.Walk.Speed <= 0) continue;
            var at = target.Walk.Position(now);
            if (Dist(origin, at) > radius) continue;
            var destination = (X: landing.X + at.X - origin.X, Y: landing.Y + at.Y - origin.Y);
            int size = LivingCollision.Size(target);
            bool Walkable((int X, int Y) cell) => player.Layout.WalkableFor(cell.X, cell.Y, size) && !player.Barrier.Contains(cell);
            if (!Walkable(destination)) destination = landing;
            if (!Walkable(destination)) continue;
            target.Walk.Teleport(destination.X, destination.Y, now);
            player.Area.Grid.Set(new(id), destination);
            target.ActionGeneration++;
            target.SwingGeneration = null;
            target.LastTarget = null;
            session.World(id, InstanceProtocol.Teleport(id, destination.X, destination.Y), "Vaal Lightning Warp");
            arrivals.Add(id, target);
        }
        if (arrivals.Count > 0) Hit(landing, arrivals);
    }
}
