public static partial class Game
{
    public const string SavageHitAttackSpeed = "gain_attack_speed_+%_for_4_seconds_if_taken_savage_hit";
    public const string RecentSavageHitItemFlag = "Item.RecentSavageHit";

    // The client describes this id as attack speed "if you have taken a Savage Hit Recently": the same four-second
    // event window the incoming hit owners already record. Qualification and the recent flag share the source snapshot.
    public static void ApplyRecentSavageHitItemStats(Dictionary<uint, int> stats, StatCatalog catalog,
        IReadOnlyDictionary<string, bool>? flags, IReadOnlyDictionary<uint, int> overrides)
    {
        if (flags?.GetValueOrDefault(RecentSavageHitItemFlag) != true
            || catalog.Key(SavageHitAttackSpeed) is not uint input || catalog.Key("attack_speed_+%") is not uint output
            || overrides.ContainsKey(output)) return;
        int value = overrides.GetValueOrDefault(input, stats.GetValueOrDefault(input));
        if (value == 0) return;
        stats[output] = ActorScale.Saturate((long)stats.GetValueOrDefault(output) + value);
    }
}
