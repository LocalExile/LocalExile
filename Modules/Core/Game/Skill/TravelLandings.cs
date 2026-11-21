using System.Net.Sockets;
using GameServer.Core.Projectiles;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

public static partial class Game
{
    static async Task<bool> CommitTravelLanding(Player player, (int X, int Y) landing, Func<bool> current)
    {
        bool committed = false;
        await SendWorldRecord(player, player.Id, [], "travel skill landing", atGate: () =>
        {
            if (!current() || player.Closed || player.LeavingArea || player.PlayerLife == 0) return null;
            if (!player.Layout.WalkableFor(landing.X, landing.Y, 0) || player.Barrier.Contains(landing)) return null;
            double now = ProjectileWorldTime(player);
            committed = true;
            player.Walk.Teleport(landing.X, landing.Y, now);
            return InstanceProtocol.Teleport(player.Id, landing.X, landing.Y);
        });
        return committed;
    }

    public sealed partial class ProjectileSession
    {
        internal int WarpCount;
        internal long LastWarpTime;
        internal (int X, int Y) LastWarpDestination;
    }

    static bool ScheduleLightningWarp(Player player,
        (ushort Id, int Effect, string Name, int Level, int Quality, uint Gem) gem, (int X, int Y) aim,
        Func<bool> current, SkillCriticalRoll? critical)
    {
        var session = ProjectilesFor(player);
        var source = SupportedSkill(player, gem.Id);
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
                || session.WarpCount >= Own(Stat.MaxNumberOfLightningWarpMarkers)) return;
            double now = session.Runtime.Now / (double)ProjectileRuntime.QuantaPerSecond;
            var origin = session.WarpCount == 0 ? player.Walk.Position(now) : session.LastWarpDestination;
            var landing = MovementLanding(player, gem.Effect, origin, aim);
            if (origin == landing || player.Walk.Speed <= 0) return;
            var playerStats = CharacterPlayerStats(player);
            int walkMs = (int)Math.Min(int.MaxValue, 1000 * Dist(origin, landing) / player.Walk.Speed);
            double delay = .1 + SkillDurationModifiers.Seconds(walkMs, n => GolemStat(playerStats, n), Own);
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
                    if (!Active() || !SameTriggerSource(source, SupportedSkill(player, gem.Id))
                        || !player.Layout.WalkableFor(landing.X, landing.Y, 0) || player.Barrier.Contains(landing)) return;
                    var sacrifice = new SacrificeUse(player, gem.Id, gem.Effect, gem.Gem);
                    var departure = player.Walk.Position(ProjectileWorldTime(player));
                    Task Hit((int X, int Y) at) => AreaSkillHit(player, gem.Id, gem, at, at,
                        immediate: true, criticalRoll: critical, sacrifice: sacrifice, triggeredCurrent: Active);
                    await Hit(departure);
                    if (await CommitTravelLanding(player, landing, Active))
                        await Hit(landing);
                });
            });
        });
        return true;
    }
}
