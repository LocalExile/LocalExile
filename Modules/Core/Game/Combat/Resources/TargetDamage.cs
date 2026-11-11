public static partial class Game
{
    const string PhysicalVsIgnited = "physical_damage_+%_vs_ignited_enemies";
    const string FireVsBleeding = "fire_damage_+%_vs_bleeding_enemies";

    static bool HasTypedTargetDamage(Func<string, int> get) => get(PhysicalVsIgnited) != 0 || get(FireVsBleeding) != 0;

    // Project into the original type's increased bucket before conversion. The converted part inherits that
    // bucket once, and the damaging ailments use the same pre-contact target state independently of the hit.
    static int TypedTargetDamageStat(Func<string, int> get, string name, bool ignited, bool bleeding) =>
        ActorScale.Saturate((long)get(name) + (name == "physical_damage_+%" && ignited ? get(PhysicalVsIgnited) : 0)
            + (name == "fire_damage_+%" && bleeding ? get(FireVsBleeding) : 0));

    static IReadOnlyDictionary<uint, int> TypedTargetDamageStats(IReadOnlyDictionary<uint, int> stats,
        bool ignited, bool bleeding)
    {
        int Get(string name) => GolemStat(stats, name);
        if (!HasTypedTargetDamage(Get) || !ignited && !bleeding) return stats;
        var result = new Dictionary<uint, int>(stats);
        foreach (string name in new[] { "physical_damage_+%", "fire_damage_+%" })
            if (Stats.Key(name) is uint key) result[key] = TypedTargetDamageStat(Get, name, ignited, bleeding);
        return result;
    }
}
