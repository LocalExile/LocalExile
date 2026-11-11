using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using ClientPathfinder = GameServer.Core.World.ClientPathfinder;
using GameServer.Core.Protocol.Login;

public static partial class Game
{
    // How far around the player GGG lists monsters in a resync (23.4 cells at most).
    public const int ResyncRadius = 24;

    // GGG wakes only what is near the player, and a monster out of the zone stops ticking on the client: without this a
    // chase the server has given up goes on there for as long as the player stays away. One that comes back is put where
    // the server has it.
    const int WakeZoneRadius = 171, WakeZoneReturn = 163;
    const double WakeZoneSeconds = .5;

    public static async Task KeepWakeZone(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
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
                // Each client's own wake zone (ClientState.AsleepMonsters), of the instance's monsters it was shown.
                var others = InGame.Keys.Where(c => !ReferenceEquals(c, conn) && ReferenceEquals(c.Instance, conn.Instance)
                    && !c.Closed && !c.LeavingArea && c.PlayerSent).ToArray();
                foreach (var (id, m) in conn.Monsters)
                {
                    if (m.Dead || m.Life <= 0 || m.OwnerId is not null || !conn.MonsterViews.ContainsKey(id)) continue;
                    var at = m.Walk.Position(now);
                    double d = Dist(at, player);
                    bool sleeping = conn.AsleepMonsters.Contains(id);
                    if (!sleeping && d > WakeZoneRadius)
                    {
                        conn.AsleepMonsters.Add(id);
                        packets.AddRange(InstanceProtocol.LeaveWakeZone(id));
                        // Out of every client's: the estimate stands where it is too, and the AI leaves it until it is
                        // back (Game.PlanMonsterAi), as GGG's does.
                        if (!m.Asleep && others.All(c => c.AsleepMonsters.Contains(id) || !c.MonsterViews.ContainsKey(id)))
                        {
                            m.Asleep = true;
                            m.Walk.StopAt(now);
                            m.Leg = null;
                        }
                    }
                    else if (sleeping && d <= WakeZoneReturn)
                    {
                        conn.AsleepMonsters.Remove(id);
                        m.Asleep = false;
                        packets.AddRange(InstanceProtocol.EnterWakeZone(id));
                        back.Add((id, at.X, at.Y));
                    }
                }
                if (conn.LockstepClient) packets.AddRange(LockstepPlace(back));
                else foreach (var (id, x, y) in back) packets.AddRange(InstanceProtocol.Teleport(id, x, y, 0x7fffffff));
                return packets.Count == 0 ? null : [.. packets];
            }
        });
    }

    // The monsters around the player where the server has them, each by its own teleport (S→C 0x1b7, terrain height).
    // The player is not among them: the client's position is the truth and the server's walk only estimates it, so S→C
    // 0x128 (whose first record the client puts the player on) snapped the player back to the estimate at every stun,
    // freeze, movement skill and resync request. Called under conn.Monsters.
    public static byte[] ResyncPacketLocked(ClientState conn, double now)
    {
        var player = conn.Walk.Position(now);
        var bytes = new List<byte>();
        foreach (var (id, m) in conn.Monsters)
        {
            if (m.Dead || m.Life <= 0 || conn.AsleepMonsters.Contains(id) || conn.Instance is not null && !conn.MonsterViews.ContainsKey(id)) continue;
            var at = m.Walk.Position(now);
            if (Dist(at, player) <= ResyncRadius) bytes.AddRange(InstanceProtocol.Teleport(id, at.X, at.Y, 0x7fffffff));
        }
        return [.. bytes];
    }

    /// <summary>How far a skill that travels a set distance along the aimed line goes, whatever the target's distance
    /// (live 2026-11-01; no stat carries it); 0 for one that goes to the aimed cell.</summary>
    static int TravelCells(string? effect) =>
        effect is null ? 0 : effect.StartsWith("WhirlingBlades", StringComparison.Ordinal) ? 40
            : IsDashEffect(effect) ? 33 : 0;

    // ActiveSkillType Movement also covers these, which leave the player where it stands when used: the estimate must stay
    // too, or a resync would teleport the client to the aimed cell.
    static readonly string[] MovementInPlace = ["MirrorArrow", "SmokeMine", "PhaseRun", "ChainHook", "WitheringStep", "Ambush"];

    /// <summary>Whether a skill puts the player where it was aimed. A channelled one (Cyclone) reports its own way.</summary>
    public static bool LandsAtAim(ClientState conn, int effect) =>
        SkillsFor(conn).IsMovement(effect) && !SkillsFor(conn).IsChannelled(effect)
        && !(SkillsFor(conn).EffectId(effect) is { } id && MovementInPlace.Any(name => id.StartsWith(name, StringComparison.Ordinal)));

    /// <summary>Where a movement skill ends: the aimed cell, or the last walkable cell of the line to it. One that travels a
    /// set distance goes on past the target and stops at the first cell it cannot walk.</summary>
    public static (int X, int Y) MovementLanding(ClientState conn, int effect, (int X, int Y) from, (int X, int Y) aim)
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

    // Dash uses its ordinary 33-cell landing and resync path. Resolve it before payment and commit it in the same
    // admission transaction: a blocked endpoint or a stale action cannot displace the player without a paid use.
    public static bool TryDashLanding(ClientState conn, int effect, (int X, int Y) aim,
        out (int X, int Y) from, out (int X, int Y) to, double? acceptedAt = null)
    {
        to = from = default;
        if (!IsDashEffect(SkillsFor(conn).EffectId(effect)) || !SkillsFor(conn).IsTravel(effect)
            || !SkillsFor(conn).IsMovement(effect) || !conn.ProjectileActive(conn.ProjectileEpoch)
            || !conn.PlayerSent || conn.PlayerLife == 0 || conn.Paused || conn.Closed || conn.LeavingArea
            || conn.RageRetired || conn.PendingTransfer is not null || InTown(conn)
            || conn.Layout.Walkable is not { } walkable || conn.Layout.GridW <= 0 || conn.Layout.GridH <= 0)
            return false;
        double now = acceptedAt ?? ProjectileWorldTime(conn);
        from = conn.Walk.Position(now);
        // Bound the raw client aim before MovementLanding subtracts coordinates or projects the 33-cell vector.
        if (aim == from || aim.X < 0 || aim.Y < 0 || aim.X >= conn.Layout.GridW || aim.Y >= conn.Layout.GridH
            || from.X < 0 || from.Y < 0 || from.X >= conn.Layout.GridW || from.Y >= conn.Layout.GridH
            || !walkable(from.X, from.Y) || conn.Barrier.Contains(from)) return false;
        var landing = MovementLanding(conn, effect, from, aim);
        to = landing;
        if (landing == from || landing.X < 0 || landing.Y < 0 || landing.X >= conn.Layout.GridW
            || landing.Y >= conn.Layout.GridH || !walkable(landing.X, landing.Y) || conn.Barrier.Contains(landing))
            return false;
        return !conn.Blockers.Any(b => Math.Max(Math.Abs((long)landing.X - b.X), Math.Abs((long)landing.Y - b.Y))
            - b.Size < 2);
    }

    /// <summary>The least time after a movement spell before the resync goes out.</summary>
    const double MovementResyncSeconds = .5;

    /// <summary>How long a movement skill's action lasts on the client: its attack time, or its cast time.</summary>
    public static double MovementSeconds(ClientState conn, int effect, int level) =>
        SkillsFor(conn).IsAttack(effect) ? SkillAttackSeconds(conn, effect, level)
            : Math.Max(MovementResyncSeconds, SkillsFor(conn).CastTimeMs(effect) / 1000.0 / PlayerActionFactor(conn));

    // The server has the player landed while the client is on its way there, so nothing resyncs until the action is over.
    public static void ResyncAfter(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, double seconds, string why)
    {
        conn.LandingUntil = Math.Max(conn.LandingUntil, conn.Clock.Elapsed.TotalSeconds + seconds);
        SendAfter(seconds, () => SendResync(remote, stream, ciphers, conn, why));
    }

    // Something to send later, on its own: the connection may be gone by then.
    public static void SendAfter(double seconds, Func<Task> send) =>
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(seconds));
            try { await send(); }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { }
        });

    public static Task SendResync(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, string why) =>
        Send(remote, stream, ciphers, [], $"0x0128 resync ({why})", atGate: () =>
        {
            lock (conn.Monsters)
                return conn.Closed || conn.LeavingArea || conn.Clock.Elapsed.TotalSeconds < conn.LandingUntil ? null
                    : ResyncPacketLocked(conn, conn.Clock.Elapsed.TotalSeconds);
        });
}
