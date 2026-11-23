using GameServer.Core.Protocol.Instance;


/// <summary>
/// The charge buffs on the player (issue #237): BuffDefinitions rows <c>endurance_charge</c> (28), <c>frenzy_charge</c>
/// (27) and <c>power_charge</c> (29), each with <c>ShowCount</c>, its <c>Maximum_StatsKey</c>
/// (<c>virtual_maximum_*_charges</c>) and <c>Current_StatsKey</c> (<c>actual_current_*_charges</c>), and the
/// BuffVisuals 41/40/42 whose BuffVisualOrbs draw the orbs around the character. Converted charges
/// (<see cref="Charges.ConversionFlag"/>) use the special rows <c>brutal_charge</c> (1757), <c>affliction_charge</c>
/// (1758) and <c>absorption_charge</c> (1759), whose <c>Current_StatsKey</c> is <c>current_*_charges</c> itself.
/// One buff per type carries the count in 0x1c1's stacks field (flags bit2) and the time left in its duration field
/// (bit1, f32 seconds); a change sends the old instance's 0x1c2 and a new 0x1c1 (11-skills.md §15).
/// </summary>
public static class ChargeBuffs
{
    /// <summary>The BuffDefinitions row of each type's buff, or of its special type's when <paramref name="converted"/>.</summary>
    public static ushort Row(ChargeType type, bool converted = false) => (type, converted) switch
    {
        (ChargeType.Frenzy, false) => 27,
        (ChargeType.Endurance, false) => 28,
        (ChargeType.Power, false) => 29,
        (ChargeType.Endurance, true) => 1757,
        (ChargeType.Frenzy, true) => 1758,
        (ChargeType.Power, true) => 1759,
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    /// <summary>The BuffDefinitions Id of each row.</summary>
    public static string Id(ChargeType type, bool converted = false) => $"{Charges.Label(type, converted)}_charge";

    /// <summary>One change on the player's Buffs component: the type's buff (its row, or its special row when converted)
    /// with its count and, for charges that expire, the seconds left; none at 0. The player is its own source. The log
    /// line comes back.</summary>
    /// <paramref name="art"/> gives a type's BuffVisuals row when its art is not the buff's own (an Alternate Charge Skin).
    public static string Apply(ChargeSession.BuffChange change, Player player, Func<ChargeType, ushort?>? art = null)
    {
        var buffs = player.Area.Buffs;
        ushort row = Row(change.Type, change.Converted), other = Row(change.Type, !change.Converted);
        buffs.Clear(player.Id, other, player.Id);
        if (change.Count <= 0) { buffs.Clear(player.Id, row, player.Id); return $"{Id(change.Type)} off"; }
        buffs.Set(player.Id, new(row, player.Id, Visual: change.Converted ? null : art?.Invoke(change.Type),
            U68: change.Remaining is double s ? Buff.Seconds(s) : null, Stacks: (ushort)Math.Clamp(change.Count, 0, ushort.MaxValue)));
        return $"{Id(change.Type, change.Converted)} (BuffDefinitions row {row}) × {change.Count}"
            + (change.Remaining is double left ? $", {left:0.###} s" : ", no duration");
    }
}
