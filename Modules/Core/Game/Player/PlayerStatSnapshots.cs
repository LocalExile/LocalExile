using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

public static partial class Game
{
    // The player's stats as its client gets them. Under a trap's degeneration the pools only fall: the client steps them
    // up by these rates between updates, so they go as 0.
    public static List<(uint, int)> ClientStats(Player player, List<(uint Key, int Value)> stats)
    {
        if (player.TrapDegenPerSecond > 0 || SiphoningDegenerationActive(player) || ChargeDegenerationActive(player)
            || HerEmbraceOf(player).Until > ResourceEffectsTime(player) || FlameTouchedActive(player)
            || player.UtilityFlaskEffects.HasSelfDamage)
            for (int i = 0; i < stats.Count; i++)
                if (stats[i].Key == Stat.LifeRecoveryPerMinute || stats[i].Key == Stat.EnergyShieldRecoveryPerMinute)
                    stats[i] = (stats[i].Key, 0);
        int? count = null, chance = null;
        foreach (var (key, value) in stats)
        {
            if (key == RecentEnemyHits.Counter) count = value;
            if (key == RecentEnemyHits.VirtualChance) chance = value;
        }
        if (count is int c && chance is int h) player.PresentedRecentEnemyHits = (c, h);
        return [.. stats.Select(s => (s.Key, s.Value))];
    }

    /// <summary>Some of the player's stats now (the Stats component writes the changed ones at the frame's start).</summary>
    public static byte[] PlayerStatsUpdateAtGate(Player player, IReadOnlyList<(uint Key, int Value)> stats)
    {
        foreach (var (key, value) in ClientStats(player, [.. stats])) player.Area.Stats.Set(player.Id, key, value);
        return [];
    }

    // The stats whose arrival makes the client lay a walking actor's piece again (0x141ea3840) or change its
    // Pathfinding::GetSpeed (0x141e827a0): movement_velocity_+%, virtual_action_speed_+%, movement_velocity_cap and 0x7b5.
    static readonly uint[] WalkRelayStats = [Stat.MovementVelocityPlusPct, Stat.VirtualActionSpeedPlusPct, Stat.MovementVelocityCap, 0x7b5];

    // A player stats message (S→C 0x1b3) written for an area's player (AreaStats.Written). The client sets
    // each stat it lists and tells the stat's listeners whether the value changed or not; for a movement stat the Actor
    // runs its active MoveTo's piece again from the cell the player stands on at the newly published speed. ApplyMovementSpeedAt
    // reads the published AreaStats values and calls Walk.SetSpeed(speed, frame.Start), which both updates the speed on
    // the exact frame 0x1b3 reaches the client and relays the active walk segment.
    static void RelayWalkForStats(GameArea area, uint id, IReadOnlyList<(uint Key, int Value)> stats)
    {
        if (!stats.Any(s => Array.IndexOf(WalkRelayStats, s.Key) >= 0)) return;
        foreach (var player in InGame.Keys)
        {
            if (player.Id != id || !ReferenceEquals(player.Frames.Area, area)) continue;
            if (!player.Connection.LockstepClient)
            {
                player.ApplyMovementSpeedAt(AreaTime(player));
                continue;
            }
            if (player.Connection.LockstepFrame is not { } frame) continue;
            StepWalkers(player, frame.Start);
            if (ChargeSessions.TryGetValue(player, out var charges)) charges.SettleDegeneration(frame.Start);
            player.ApplyMovementSpeedAt(frame.Start, forceRelay: true);
            RefreshShieldChargeArrival(player);
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
    public static Task SendPlayerStatSnapshot(Player player,
        Func<byte[]> packet, string? label) => SendPlayerStatSnapshot(player, () =>
        {
            byte[] bytes = packet();
            return bytes.Length == 0 ? [] : [bytes];
        }, label);

    public static Task SendPlayerStatSnapshot(Player player,
        Func<byte[][]> packets, string? label) => SendWorldRecords(player, player.Id, () =>
        {
            if (!CurrentGameRules || player.Closed || player.LeavingArea || player.RageRetired) return null;
            return WithOwnAuraRecords(player, packets);
        }, label);

    // InstanceProtocol.CompactInt, read back: 7, 14, 21 or 28 bits with their sign, or 0xf0 and a u32.
    static int ReadCompactInt(byte[] bytes, ref int at)
    {
        byte first = bytes[at];
        int length = first switch { < 0x80 => 1, < 0xc0 => 2, < 0xe0 => 3, < 0xf0 => 4, _ => 5 };
        if (at + length > bytes.Length) { at = bytes.Length; return 0; }
        int value;
        if (length == 5) value = bytes[at + 1] << 24 | bytes[at + 2] << 16 | bytes[at + 3] << 8 | bytes[at + 4];
        else
        {
            int bits = 7 * length;
            value = first & (0x7f >> (length - 1));
            for (int i = 1; i < length; i++) value = value << 8 | bytes[at + i];
            if ((value & 1 << (bits - 1)) != 0) value -= 1 << bits;
        }
        at += length;
        return value;
    }
}
