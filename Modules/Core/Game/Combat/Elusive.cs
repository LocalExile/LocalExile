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
    public static int ElusiveAvoidPct(ClientState conn) => (int)Math.Floor(15 * ElusiveFactor(conn) + 1e-9);
    public static int ElusiveMovementPct(ClientState conn) => (int)Math.Floor(30 * ElusiveFactor(conn) + 1e-9);
    public static int ElusiveDisplayPercent(ClientState conn)
    {
        if (ElusiveFactor(conn) <= 0) return 0;
        var state = ElusiveOf(conn);
        double now = ResourceEffectsTime(conn), age = state.Age(now);
        return (int)Math.Max(0, Math.Round(state.Effect(now - (age - Math.Floor(age)))));
    }
    public static void GainElusiveOnCrit(ClientState conn)
    {
        CharacterStatSources(conn);
        lock (conn.Monsters) TryGainElusiveLocked(conn, critical: true, kill: false, ResourceEffectsTime(conn));
    }
    public static Task SyncElusive(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn) =>
        SendElusive(remote, stream, ciphers, conn);
}
