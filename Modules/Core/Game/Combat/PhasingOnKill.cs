using System.Net.Sockets;
using GameServer.Core;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

public static partial class Game
{
    // BuffDefinitions' "phase_run" row: the phasing visual (its BuffVisuals row carries the transparent effect).
    static readonly Lazy<ushort> PhaseRunBuffRow = new(() =>
    {
        if (!ClientTables.Has("BuffDefinitions")) return (ushort)65;
        foreach (var row in ClientTables.Table("BuffDefinitions"))
            if (row.Text("Id") == "phase_run") return (ushort)row.Int("_row");
        return (ushort)65;
    });

    /// <summary>The charm's "X% chance to gain Phasing for 4 seconds on Kill": one roll per kill, resetting the
    /// connection's timed phasing (<see cref="ClientState.IsPhasing"/>). Phasing already keeps monsters from blocking
    /// the player and answers the <c>_while_phasing</c> item conditions.</summary>
    public static void GainPhasingOnKill(ClientState conn, double now)
    {
        if (Stats.Key("phasing_for_4_seconds_on_kill_%") is not uint key) return;
        int chance = CharacterPlayerStats(conn).GetValueOrDefault(key);
        if (chance <= 0 || Random.Shared.Next(100) >= Math.Min(100, chance)) return;
        lock (conn.Monsters)
            conn.PhasingUntil = Math.Max(conn.PhasingUntil, now + 4);
        Console.WriteLine($"[gateway] phasing on kill: {chance}% -> 4 seconds");
    }

    /// <summary>Tells the client about the timed phasing buff: add it while the charm's phasing runs, refresh it when
    /// the deadline moves, remove it when it ends, and hide it while a Quartz Flask shows its own phasing buff.</summary>
    public static async Task SyncPhasing(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
    {
        byte[]? bytes = null;
        lock (conn.Monsters)
        {
            if (conn.Closed || conn.LeavingArea) return;
            double now = conn.Clock.Elapsed.TotalSeconds;
            bool show = conn.PlayerLife > 0 && conn.PhasingUntil > now && !conn.Phasing;
            if (show)
            {
                // A refresh that moves the shown deadline by less than a second keeps the client's buff.
                if (conn.PhasingBuff == 0 || conn.PhasingUntil - conn.PhasingSentUntil >= 1)
                {
                    ushort buff = conn.PhasingBuff != 0 ? conn.PhasingBuff : conn.NextBuff++;
                    conn.PhasingBuff = buff;
                    conn.PhasingSentUntil = conn.PhasingUntil;
                    bytes = InstanceEntities.AddBuff(PlayerId, buff, PhaseRunBuffRow.Value, PlayerId,
                        u68: BitConverter.SingleToUInt32Bits((float)(conn.PhasingUntil - now)));
                }
            }
            else if (conn.PhasingBuff != 0)
            {
                bytes = InstanceEntities.RemoveBuff(PlayerId, conn.PhasingBuff);
                conn.PhasingBuff = 0;
                conn.PhasingSentUntil = 0;
            }
        }
        if (bytes is not null) await Send(remote, stream, ciphers, bytes, "phasing buff");
    }
}
