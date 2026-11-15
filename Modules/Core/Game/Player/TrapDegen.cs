using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using static Game;

/// <summary>One source's degeneration on the player while they stand in it (RunResourceRecovery deals
/// ClientState.TrapDegenPerSecond) and its buff shown on them: a Labyrinth trap, or a monster's ground (MonsterGrounds).
/// Each source keeps its own share, so one ending does not cancel another's.</summary>
public sealed class TrapDegen
{
    (ushort Instance, ushort Row)? shown;
    double mine;

    /// <summary>This tick's degeneration (0 out of it) and buff row: the buff swapped as the row changes, and the client's
    /// recovery rates sent again on entering and leaving (PlayerStatsUpdateAtGate).</summary>
    public async Task Set(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, double perSecond, ushort? row,
        string where)
    {
        bool was, degen;
        ushort instance = 0;
        lock (conn.Monsters)
        {
            was = mine > 0;
            if (conn.PlayerLife == 0) perSecond = 0;
            conn.TrapDegenPerSecond = Math.Max(0, conn.TrapDegenPerSecond - mine + perSecond);
            mine = perSecond;
            degen = mine > 0;
            if (!degen) row = null;
            if (row is not null && shown?.Row != row) instance = conn.NextBuff++;
        }
        if (shown is { } old && old.Row != row)
        {
            await SendWorldRecord(remote, stream, ciphers, conn, PlayerId, InstanceEntities.RemoveBuff(PlayerId, old.Instance), null);
            shown = null;
        }
        if (row is ushort buffRow && shown is null)
        {
            await SendWorldRecord(remote, stream, ciphers, conn, PlayerId, InstanceEntities.AddBuff(PlayerId, instance, buffRow), null);
            shown = (instance, buffRow);
        }
        if (degen && !was && CharacterPlayerStatsCore(conn, out double lifeRegen) is not null)
            Console.WriteLine($"[gateway] {remote} {where}: degeneration {conn.TrapDegenPerSecond:0}/s (life {conn.PlayerMaxLife}, " +
                $"energy shield {conn.EnergyShield.Maximum}), life regeneration {lifeRegen:0.#}/s, life {conn.PlayerLife}, " +
                $"energy shield {conn.EnergyShield.Current}");
        if (degen != was) await SendCharacterStats(remote, stream, ciphers, conn, degen ? where : "no longer " + where);
    }

    /// <summary>Takes this source's share away without a packet: the player left the area (its buffs go with it).</summary>
    public void Clear(ClientState conn)
    {
        lock (conn.Monsters) { conn.TrapDegenPerSecond = Math.Max(0, conn.TrapDegenPerSecond - mine); mine = 0; shown = null; }
    }
}
