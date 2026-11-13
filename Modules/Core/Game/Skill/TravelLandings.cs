using System.Net.Sockets;
using GameServer.Core.Projectiles;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

public static partial class Game
{
    static async Task<bool> CommitTravelLanding(string remote, NetworkStream stream, ConnectionCiphers ciphers,
        ClientState conn, (int X, int Y) landing, Func<bool> current)
    {
        bool committed = false;
        await SendWorldRecord(remote, stream, ciphers, conn, PlayerId, [], "travel skill landing", atGate: () =>
        {
            lock (conn.Monsters)
            {
                if (!current() || conn.Closed || conn.LeavingArea || conn.PlayerLife == 0) return null;
                if (!conn.Layout.WalkableFor(landing.X, landing.Y, 0) || conn.Barrier.Contains(landing)) return null;
                double now = ProjectileWorldTime(conn);
                committed = true;
                conn.Walk.Teleport(landing.X, landing.Y, now);
                return InstanceProtocol.Teleport(PlayerId, landing.X, landing.Y);
            }
        });
        return committed;
    }

    public sealed partial class ProjectileSession
    {
        internal int WarpCount;
        internal long LastWarpTime;
        internal (int X, int Y) LastWarpDestination;
    }

    static bool ScheduleLightningWarp(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        (ushort Id, int Effect, string Name, int Level, int Quality, uint Gem) gem, (int X, int Y) aim,
        Func<bool> current, SkillCriticalRoll? critical)
    {
        var session = ProjectilesFor(remote, stream, ciphers, conn);
        var source = SupportedSkill(conn, gem.Id);
        int Own(string name) => GolemStat(source.Stats, name);
        if (CastMs(conn, gem.Effect, gem.Id) is not int cast) return false;
        var lifetime = CombatLifetime;
        double started = AreaTime(conn);
        bool Active() => session.Active && ReferenceEquals(lifetime, CombatLifetime) && conn.PlayerLife > 0;
        lock (conn.Monsters)
        {
            long release = Math.Max(session.Runtime.Now, ProjectileRuntime.Time(started + cast / 1000d));
            session.Runtime.Schedule(release, () =>
            {
                if (!Active() || !current() || !SameTriggerSource(source, SupportedSkill(conn, gem.Id))
                    || session.WarpCount >= Own("max_number_of_lightning_warp_markers")) return;
                double now = session.Runtime.Now / (double)ProjectileRuntime.QuantaPerSecond;
                var origin = session.WarpCount == 0 ? conn.Walk.Position(now) : session.LastWarpDestination;
                var landing = MovementLanding(conn, gem.Effect, origin, aim);
                if (origin == landing || conn.Walk.Speed <= 0) return;
                var player = CharacterPlayerStats(conn);
                int walkMs = (int)Math.Min(int.MaxValue, 1000 * Dist(origin, landing) / conn.Walk.Speed);
                double delay = .1 + SkillDurationModifiers.Seconds(walkMs, n => GolemStat(player, n), Own);
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
                        if (!Active() || !SameTriggerSource(source, SupportedSkill(conn, gem.Id))
                            || !conn.Layout.WalkableFor(landing.X, landing.Y, 0) || conn.Barrier.Contains(landing)) return;
                        var sacrifice = new SacrificeUse(conn, gem.Id, gem.Effect, gem.Gem);
                        var departure = conn.Walk.Position(ProjectileWorldTime(conn));
                        Task Hit((int X, int Y) at) => AreaSkillHit(remote, stream, ciphers, conn, gem.Id, gem, at, at,
                            immediate: true, criticalRoll: critical, sacrifice: sacrifice, triggeredCurrent: Active);
                        await Hit(departure);
                        if (await CommitTravelLanding(remote, stream, ciphers, conn, landing, Active))
                            await Hit(landing);
                    });
                });
            });
        }
        return true;
    }
}
