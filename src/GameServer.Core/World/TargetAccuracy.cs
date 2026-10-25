namespace GameServer.Core.World;

/// <summary>
/// The player attack accuracy against a specific target: the two target-conditional Accuracy Mastery effects, which are
/// final "more" multipliers and so can neither fold into the additive/increased pools (they would also show in the
/// client panel) nor multiply like increased stats. The stat rows are accuracy_rating_+%_final_vs_unique_enemies_from_mastery
/// (Stats.dat row 15547; PassiveSkillMasteryEffects Accuracy1: 40) and
/// accuracy_rating_+%_final_vs_enemies_in_close_range_from_mastery (row 18375; Accuracy5: 50).
/// "Close Range" is 2 metres (ReminderText.json ReminderTextCloseRange "Close Range is up to 2 metres") and the game
/// units are 10 per metre (PoB 16de4b8 ConfigOptions.lua), so the radius is 20 units; the server reads those raw units
/// as grid cells (SkillArea, Righteous Fire's 18 = 1.8 m), so a target is close at a centre distance of 20 cells or less,
/// and "Nearby" is a different mechanic.
/// </summary>
public static class TargetAccuracy
{
    /// <summary>The close-range radius in grid cells (raw game units): 2 metres at 10 units per metre, inclusive.</summary>
    public const double CloseRangeCells = 20.0;

    /// <summary>The reference close-range effect that the accuracy mastery selects (Accuracy5: 50% more).</summary>
    public const int CloseRangeMorePct = 50;

    /// <summary>The reference unique-enemy effect that the accuracy mastery selects (Accuracy1: 40% more).</summary>
    public const int UniqueMorePct = 40;

    /// <summary>Whether a target this far from the player (grid cells, centre to centre) is in close range.</summary>
    public static bool InCloseRange(double distanceCells) => distanceCells >= 0 && distanceCells <= CloseRangeCells;

    /// <summary>The final attack accuracy against this target: each conditional mastery effect the target satisfies
    /// multiplies the pools' accuracy once ("40% more" and "50% more" together are x 2.1), using exact integer math so
    /// the product cannot drift below the mathematical value, and floors to a whole rating like CharacterStats.For's
    /// floor of the increased result. Spell accuracy is untouched: spells do not roll.</summary>
    public static int AgainstTarget(int accuracy, int moreVsUniquePct, int moreInCloseRangePct,
        bool targetIsUnique, bool targetInCloseRange)
    {
        decimal result = Math.Max(0, accuracy);
        if (targetIsUnique) result *= Math.Max(0m, 100m + moreVsUniquePct) / 100m;
        if (targetInCloseRange) result *= Math.Max(0m, 100m + moreInCloseRangePct) / 100m;
        return (int)Math.Clamp(decimal.Floor(result), 0m, int.MaxValue);
    }
}
