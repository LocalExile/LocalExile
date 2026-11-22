public static partial class Game
{
    const string PhysicalVsIgnited = "physical_damage_+%_vs_ignited_enemies";
    public const string DamageVsLowLifePerFrenzy = "damage_+%_vs_enemies_on_low_life_per_frenzy_charge";
    public const string TinctureDamageVsLowLife = "tinctured_weapon_damage_+%_vs_enemies_that_are_on_low_life";
    public static bool EnemyOnLowLife(SpawnedMonster target) => target.Hostile && MonsterOnLowLife(target);

    const string FireVsBleeding = "fire_damage_+%_vs_bleeding_enemies";

    static bool HasTypedTargetDamage(Func<string, int> get) => get(PhysicalVsIgnited) != 0 || get(FireVsBleeding) != 0
        || get(DamageVsLowLifePerFrenzy) != 0
        || get("tincture_applied") != 0 && get(TinctureDamageVsLowLife) != 0;

    // Project into the original type's increased bucket before conversion. The converted part inherits that
    // bucket once, and the damaging ailments use the same pre-contact target state independently of the hit.
    static int TypedTargetDamageStat(Func<string, int> get, string name, bool ignited, bool bleeding, bool lowLife = false,
        bool attack = true) =>
        ActorScale.Saturate((long)get(name) + (name == "physical_damage_+%" && ignited ? get(PhysicalVsIgnited) : 0)
            + (name == "fire_damage_+%" && bleeding ? get(FireVsBleeding) : 0)
            + (name == "damage_+%" && lowLife
                ? (long)Math.Max(0, get("current_frenzy_charges")) * get(DamageVsLowLifePerFrenzy)
                    + (attack && get("tincture_applied") != 0 ? get(TinctureDamageVsLowLife) : 0)
                : 0));

    static IReadOnlyDictionary<uint, int> TypedTargetDamageStats(IReadOnlyDictionary<uint, int> stats,
        bool ignited, bool bleeding, bool lowLife = false, bool attack = true)
    {
        int Get(string name) => GolemStat(stats, name);
        if (!HasTypedTargetDamage(Get) || !ignited && !bleeding && !lowLife) return stats;
        var result = new Dictionary<uint, int>(stats);
        foreach (string name in new[] { "physical_damage_+%", "fire_damage_+%", "damage_+%" })
            if (Stats.Key(name) is uint key) result[key] = TypedTargetDamageStat(Get, name, ignited, bleeding, lowLife, attack);
        return result;
    }
}
