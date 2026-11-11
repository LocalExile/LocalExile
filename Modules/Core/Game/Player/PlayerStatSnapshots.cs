using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

public static partial class Game
{
    // Only call while constructing an accepted player-stat packet inside the send gate. Pure stat
    // construction must not acknowledge anything: remember exactly the pair this packet encodes.
    public static byte[] PlayerStatsUpdateAtGate(ClientState conn, IReadOnlyList<(uint Key, int Value)> stats)
    {
        // Under a trap's degeneration the pools only fall: the client steps them up by these rates between updates.
        if ((conn.TrapDegenPerSecond > 0 || SiphoningDegenerationActive(conn)
            || HerEmbraceOf(conn).Until > ResourceEffectsTime(conn)) && Stats.Key("life_recovery_per_minute") is uint lifeKey
            && Stats.Key("energy_shield_recovery_per_minute") is uint shieldKey)
            stats = [.. stats.Select(s => s.Key == lifeKey || s.Key == shieldKey ? (s.Key, 0) : s)];
        var packet = InstanceEntities.StatsUpdate(PlayerId, stats);
        uint? counterKey = Stats.Key(RecentEnemyHits.Counter), chanceKey = Stats.Key(RecentEnemyHits.VirtualChance);
        int? count = null, chance = null;
        foreach (var (key, value) in stats)
        {
            if (key == counterKey) count = value;
            if (key == chanceKey) chance = value;
        }
        if (count is int sentCount && chance is int sentChance) conn.PresentedRecentEnemyHits = (sentCount, sentChance);
        return packet;
    }

    // The stats whose arrival makes the client lay a walking actor's piece again (0x141ea3840): movement_velocity_+%
    // and the two the client's speed and stop tests read beside it.
    static readonly uint[] WalkRelayStats = [StatKeys.MovementVelocityPct, 0xce6, 0x7b5];

    // A player stats message (S→C 0x1b3) at its send gate on a lockstep client, under conn.Monsters. The client sets
    // each stat it lists and tells the stat's listeners whether the value changed or not; for a movement stat the Actor
    // runs its active MoveTo's piece again from the cell the player stands on. The estimate lays its walk again the same
    // way, at the start of the frame being sent, after the steps of the frames before it (tick trace 2026-11-10: four
    // setups of one segment in the frame a full snapshot arrived, three of them at an unchanged step time, and the walk
    // a cell to the side of the estimate's for the next eight frames).
    public static void RelayWalkForStats(ClientState conn, byte[] bytes)
    {
        if (!conn.LockstepClient || conn.LockstepFrame is not { } frame || bytes.Length < 13
            || (bytes[0] << 8 | bytes[1]) != InstanceEntities.StatsUpdateId
            || System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(2)) != PlayerId) return;
        int at = 12;
        uint count = ReadCompactUInt(bytes, ref at);
        for (uint i = 0; i < count && at < bytes.Length; i++)
        {
            uint stat = ReadCompactUInt(bytes, ref at);
            if (at >= bytes.Length) return;
            ReadCompactInt(bytes, ref at);
            if (Array.IndexOf(WalkRelayStats, stat) < 0) continue;
            StepWalkers(conn, frame.Start);
            conn.Walk.Relay(frame.Start);
            return;
        }
    }

    // InstanceProtocol.CompactUInt, read back.
    static uint ReadCompactUInt(byte[] bytes, ref int at)
    {
        byte first = bytes[at];
        int length = first switch { < 0x80 => 1, < 0xc0 => 2, < 0xe0 => 3, < 0xf0 => 4, _ => 5 };
        if (at + length > bytes.Length) { at = bytes.Length; return 0; }
        uint value;
        if (length == 5) value = (uint)(bytes[at + 1] << 24 | bytes[at + 2] << 16 | bytes[at + 3] << 8 | bytes[at + 4]);
        else
        {
            value = (uint)(first & (0x7f >> (length - 1)));
            for (int i = 1; i < length; i++) value = value << 8 | bytes[at + i];
        }
        at += length;
        return value;
    }

    // Every full player stat/Actor refresh shares this gate, including refreshes caused by other mechanics.
    // Otherwise a packet prepared before expiry could overwrite a newer Fortification expiry update.
    public static Task SendPlayerStatSnapshot(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        Func<byte[]> packet, string? label) => SendWorldRecords(remote, stream, ciphers, conn, PlayerId, () =>
        {
            using (conn.Performance.Lock(conn.Monsters, "stats"))
            {
                if (!CurrentGameRules || conn.Closed || conn.LeavingArea || conn.RageRetired) return null;
                return WithOwnAuraRecords(conn, () =>
                {
                    byte[] bytes = packet();
                    RelayWalkForStats(conn, bytes);
                    return [bytes];
                });
            }
        }, label);
}
