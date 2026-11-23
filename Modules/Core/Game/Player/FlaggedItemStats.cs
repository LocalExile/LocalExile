public static partial class Game
{
    public const string RecentlyDamagedByHitItemFlag = "Item.RecentlyDamagedByHit";
    // A RecentEvents key: an accepted enemy hit that dealt damage (to life, energy shield or mana).
    public const string DamagedByHitEvent = "Item.DamagedByHit";

    // Item stats whose condition is not a suffix StatConditions folds, read from a condition flag the source snapshot
    // already keys on. Each adds its value to the stats it names while the flag holds.
    static readonly (uint Input, string Flag, uint[] Outputs)[] FlaggedItemStatRules =
    [
        (Stat.AttackCastAndMovementSpeedPlusPctDuringOnslaught, "_while_have_onslaught",
            [Stat.AttackAndCastSpeedPlusPct, Stat.MovementVelocityPlusPct]),
        // "+#% Chance to Block Attack Damage if you have been Damaged by a Hit Recently".
        (Stat.BlockChanceOnDamageTakenPct, RecentlyDamagedByHitItemFlag, [Stat.AdditionalBlockPct]),
    ];

    public static void ApplyFlaggedItemStats(Dictionary<uint, int> stats, IReadOnlyDictionary<string, bool>? flags)
    {
        if (flags is null) return;
        foreach (var (input, flag, outputs) in FlaggedItemStatRules)
        {
            if (!flags.GetValueOrDefault(flag) || stats.GetValueOrDefault(input) is not (var value and not 0)) continue;
            foreach (uint output in outputs)
                stats[output] = ActorScale.Saturate((long)stats.GetValueOrDefault(output) + value);
        }
    }
}
