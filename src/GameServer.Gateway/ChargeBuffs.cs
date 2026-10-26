// Standard-charge foundation adapted from PR #241 (85fffc1154797a97b0a09a939257abad98d791cc).
using GameServer.Core.Protocol.Instance;

/// <summary>
/// The charge buffs on the player (issue #237): BuffDefinitions rows <c>endurance_charge</c> (28), <c>frenzy_charge</c>
/// (27) and <c>power_charge</c> (29), each with <c>ShowCount</c>, its <c>Maximum_StatsKey</c>
/// (<c>virtual_maximum_*_charges</c>) and <c>Current_StatsKey</c> (<c>actual_current_*_charges</c>), and the
/// BuffVisuals 41/40/42 whose BuffVisualOrbs draw the orbs around the character. One buff per type carries the count
/// in 0x1c1's stacks field (flags bit2) and the time left in its duration field (bit1, f32 seconds); a change sends
/// the old instance's 0x1c2 and a new 0x1c1 (11-skills.md §15).
/// </summary>
static class ChargeBuffs
{
    /// <summary>The BuffDefinitions row of each type's buff.</summary>
    public static ushort Row(ChargeType type) => type switch
    {
        ChargeType.Frenzy => 27,
        ChargeType.Endurance => 28,
        ChargeType.Power => 29,
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    /// <summary>The BuffDefinitions Id of each row.</summary>
    public static string Id(ChargeType type) => $"{Charges.Name(type)}_charge";

    /// <summary>The messages for one change: the old buff off (S→C 0x1c2), then the new one on (S→C 0x1c1) with its count
    /// and, for charges that expire, the seconds left. The player is its own source.</summary>
    public static IEnumerable<(byte[] Packet, string Label)> Packets(ChargeSession.BuffChange change, uint player)
    {
        if (change.Remove != 0)
            yield return (InstanceEntities.RemoveBuff(player, change.Remove),
                $"0x01c2 player buff {change.Remove}: {Id(change.Type)} off");
        if (change.Count > 0 && change.Add != 0)
            yield return (Add(change.Type, change.Add, change.Count, change.Remaining, player),
                $"0x01c1 player buff {change.Add}: {Id(change.Type)} (BuffDefinitions row {Row(change.Type)}) × {change.Count}" +
                (change.Remaining is double s ? $", {s:0.###} s" : ", no duration"));
    }

    /// <summary>One charge buff: row, the player as source, stacks = the count, duration = the seconds left.</summary>
    public static byte[] Add(ChargeType type, ushort buff, int count, double? remaining, uint player) =>
        InstanceEntities.AddBuff(player, buff, Row(type), player,
            u68: remaining is double s ? BitConverter.SingleToUInt32Bits((float)s) : null,
            stacks: (ushort)Math.Clamp(count, 0, ushort.MaxValue));
}
