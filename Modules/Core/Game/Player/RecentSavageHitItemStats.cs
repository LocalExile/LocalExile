public static partial class Game
{
    public const uint SavageHitAttackSpeed = Stat.GainAttackSpeedPlusPctFor4SecondsIfTakenSavageHit;
    public const string RecentSavageHitItemFlag = "Item.RecentSavageHit";

    // The client describes this id as attack speed "if you have taken a Savage Hit Recently": the same four-second
    // event window the incoming hit owners already record. Qualification and the recent flag share the source snapshot.
    public static void ApplyRecentSavageHitItemStats(Dictionary<uint, int> stats, StatCatalog catalog,
        IReadOnlyDictionary<string, bool>? flags)
    {
        if (!(SavageHitAttackSpeed is var input) || !(Stat.AttackSpeedPlusPct is var output)) return;
        int value = stats.GetValueOrDefault(input);
        if (value == 0 || flags?.GetValueOrDefault(RecentSavageHitItemFlag) != true) return;
        stats[output] = ActorScale.Saturate((long)stats.GetValueOrDefault(output) + value);
    }
}
