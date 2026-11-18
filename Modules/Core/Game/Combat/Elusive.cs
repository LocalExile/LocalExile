using System.Net.Sockets;
using GameServer.Core.Protocol.Login;

public static partial class Game
{
    // All consumers share the source-qualified owner, including the whole-hit avoidance roll.
    public static double ElusiveFactor(ClientState conn)
    {
        if (!CurrentGameWork || conn.Closed || conn.LeavingArea || conn.PlayerLife == 0
            || !Elusives.TryGetValue(conn, out var state) || !ReferenceEquals(state.Rules, CombatLifetime)
            || state.Epoch != conn.ProjectileEpoch) return 0;
        return state.Effect(ResourceEffectsTime(conn)) / 100;
    }

    /// <summary>The effect's chance to avoid damage from hits, 15% at 100% effect, for the server's avoidance roll.</summary>
    public static int ElusiveAvoidPct(ClientState conn) => (int)Math.Floor(15 * ElusiveFactor(conn) + 1e-9);

    /// <summary>The effect's movement speed, 30% at 100% effect: the pools add it while the buff runs.</summary>
    public static int ElusiveMovementPct(ClientState conn) => (int)Math.Floor(30 * ElusiveFactor(conn) + 1e-9);

    /// <summary>The percentage the buff icon shows: the opening effect less one 5-point step every quarter
    /// second (the game's buff display ticks 20 points a second in 5-point steps). The server's avoidance and
    /// movement use the continuous <see cref="ElusiveFactor"/> instead.</summary>
    public static int ElusiveDisplayPercent(ClientState conn)
    {
        if (ElusiveFactor(conn) <= 0) return 0;
        var state = ElusiveOf(conn);
        double now = ResourceEffectsTime(conn), quarters = state.Age(now) * 4;
        return (int)Math.Max(0, Math.Round(state.Effect(now - (quarters - Math.Floor(quarters)) / 4)));
    }
    public static void GainElusiveOnCrit(ClientState conn)
    {
        CharacterStatSources(conn);
        lock (conn.Monsters) TryGainElusiveLocked(conn, critical: true, kill: false, ResourceEffectsTime(conn));
    }
    public static Task SyncElusive(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn) =>
        SendElusive(remote, stream, ciphers, conn);
}
