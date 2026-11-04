using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using ClientPathfinder = GameServer.Core.World.ClientPathfinder;
using GameServer.Core.Protocol.Login;

static partial class Game
{
    // How far around the player GGG lists monsters in a resync (23.4 cells at most).
    internal const int ResyncRadius = 24;

    // GGG wakes only what is near the player, and a monster out of the zone stops ticking on the client: without this a
    // chase the server has given up goes on there for as long as the player stays away. One that comes back is put where
    // the server has it.
    const int WakeZoneRadius = 171, WakeZoneReturn = 163;
    const double WakeZoneSeconds = .5;

    internal static async Task KeepWakeZone(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
    {
        double checkedAt = conn.Clock.Elapsed.TotalSeconds;
        if (checkedAt < conn.NextWakeZoneAt || !conn.PlayerSent) return;
        conn.NextWakeZoneAt = checkedAt + WakeZoneSeconds;
        await Send(remote, stream, ciphers, [], null, atGate: () =>
        {
            lock (conn.Monsters)
            {
                if (conn.Closed || conn.LeavingArea || conn.Paused) return null;
                double now = conn.Clock.Elapsed.TotalSeconds;
                var player = conn.Walk.Position(now);
                var packets = new List<byte>();
                List<(uint Id, int X, int Y)> back = [];
                foreach (var (id, m) in conn.Monsters)
                {
                    if (m.Dead || m.Life <= 0 || m.OwnerId is not null) continue;
                    var at = m.Walk.Position(now);
                    double d = Dist(at, player);
                    if (!m.Asleep && d > WakeZoneRadius)
                    {
                        m.Asleep = true;
                        // The client's copy stops ticking out there; the estimate stands where it is too, and the AI
                        // leaves it until it is back (Game.PlanMonsterAi), as GGG's does.
                        m.Walk.StopAt(now);
                        m.Leg = null;
                        packets.AddRange(InstanceProtocol.LeaveWakeZone(id));
                    }
                    else if (m.Asleep && d <= WakeZoneReturn)
                    {
                        m.Asleep = false;
                        packets.AddRange(InstanceProtocol.EnterWakeZone(id));
                        back.Add((id, at.X, at.Y));
                    }
                }
                if (conn.LockstepClient) packets.AddRange(LockstepPlace(back));
                else if (back.Count > 0) packets.AddRange(InstanceProtocol.Resync([(PlayerId, player.X, player.Y), .. back]));
                return packets.Count == 0 ? null : [.. packets];
            }
        });
    }

    // S→C 0x128 with the player first, then the monsters around it, where the server has them. Called under conn.Monsters.
    internal static byte[] ResyncPacketLocked(ClientState conn, double now)
    {
        var player = conn.Walk.Position(now);
        List<(uint Id, int X, int Y)> objects = [(PlayerId, player.X, player.Y)];
        foreach (var (id, m) in conn.Monsters)
        {
            if (m.Dead || m.Life <= 0) continue;
            var at = m.Walk.Position(now);
            if (Dist(at, player) <= ResyncRadius) objects.Add((id, at.X, at.Y));
        }
        return InstanceProtocol.Resync(objects);
    }

    /// <summary>How far a skill that travels a set distance along the aimed line goes, whatever the target's distance
    /// (live 2026-11-01; no stat carries it); 0 for one that goes to the aimed cell.</summary>
    static int TravelCells(string? effect) =>
        effect is null ? 0 : effect.StartsWith("WhirlingBlades", StringComparison.Ordinal) ? 40
            : effect is "Dash" or "DashHardMode" ? 33 : 0;

    // ActiveSkillType Movement also covers these, which leave the player where it stands when used: the estimate must stay
    // too, or a resync would teleport the client to the aimed cell.
    static readonly string[] MovementInPlace = ["MirrorArrow", "SmokeMine", "PhaseRun", "ChainHook", "WitheringStep", "Ambush"];

    /// <summary>Whether a skill puts the player where it was aimed. A channelled one (Cyclone) reports its own way.</summary>
    internal static bool LandsAtAim(ClientState conn, int effect) =>
        SkillsFor(conn).IsMovement(effect) && !SkillsFor(conn).IsChannelled(effect)
        && !(SkillsFor(conn).EffectId(effect) is { } id && MovementInPlace.Any(name => id.StartsWith(name, StringComparison.Ordinal)));

    /// <summary>Where a movement skill ends: the aimed cell, or the last walkable cell of the line to it. One that travels a
    /// set distance goes on past the target and stops at the first cell it cannot walk.</summary>
    internal static (int X, int Y) MovementLanding(ClientState conn, int effect, (int X, int Y) from, (int X, int Y) aim)
    {
        if (conn.Layout.Walkable is not { } floor) return aim;
        bool walkable(int x, int y) => floor(x, y) && !conn.Barrier.Contains((x, y));
        if (TravelCells(SkillsFor(conn).EffectId(effect)) is > 0 and var cells && aim != from)
        {
            double f = cells / Math.Sqrt((double)(aim.X - from.X) * (aim.X - from.X) + (double)(aim.Y - from.Y) * (aim.Y - from.Y));
            var end = ((int)Math.Round(from.X + (aim.X - from.X) * f), (int)Math.Round(from.Y + (aim.Y - from.Y) * f));
            var last = from;
            foreach (var cell in ClientPathfinder.Line(from, end).Skip(1))
            {
                if (!walkable(cell.X, cell.Y)) break;
                last = cell;
            }
            return last;
        }
        foreach (var cell in ClientPathfinder.Line(aim, from))
            if (walkable(cell.X, cell.Y)) return cell;
        return from;
    }

    /// <summary>The least time after a movement spell before the resync goes out.</summary>
    const double MovementResyncSeconds = .5;

    /// <summary>How long a movement skill's action lasts on the client: its attack time, or its cast time.</summary>
    internal static double MovementSeconds(ClientState conn, int effect, int level) =>
        SkillsFor(conn).IsAttack(effect) ? SkillAttackSeconds(conn, effect, level)
            : Math.Max(MovementResyncSeconds, SkillsFor(conn).CastTimeMs(effect) / 1000.0 / PlayerActionFactor(conn));

    // The server has the player landed while the client is on its way there, so nothing resyncs until the action is over.
    internal static void ResyncAfter(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, double seconds, string why)
    {
        conn.LandingUntil = Math.Max(conn.LandingUntil, conn.Clock.Elapsed.TotalSeconds + seconds);
        SendAfter(seconds, () => SendResync(remote, stream, ciphers, conn, why));
    }

    // Something to send later, on its own: the connection may be gone by then.
    internal static void SendAfter(double seconds, Func<Task> send) =>
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(seconds));
            try { await send(); }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { }
        });

    internal static Task SendResync(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, string why) =>
        Send(remote, stream, ciphers, [], $"0x0128 resync ({why})", atGate: () =>
        {
            lock (conn.Monsters)
                return conn.Closed || conn.LeavingArea || conn.Clock.Elapsed.TotalSeconds < conn.LandingUntil ? null
                    : ResyncPacketLocked(conn, conn.Clock.Elapsed.TotalSeconds);
        });
}
