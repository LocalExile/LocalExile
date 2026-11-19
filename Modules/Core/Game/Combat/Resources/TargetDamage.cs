public static partial class Game
{
    const uint PhysicalVsIgnited = Stat.PhysicalDamagePlusPctVsIgnitedEnemies;
    public const uint DamageVsLowLifePerFrenzy = Stat.DamagePlusPctVsEnemiesOnLowLifePerFrenzyCharge;
    public static bool EnemyOnLowLife(SpawnedMonster target) => target.Hostile && !target.Dead && target.Life > 0
        && (ulong)target.Life * 2 <= target.MaxLife;

    const uint FireVsBleeding = Stat.FireDamagePlusPctVsBleedingEnemies;

    static bool HasTypedTargetDamage(Func<uint, int> get) => get(PhysicalVsIgnited) != 0 || get(FireVsBleeding) != 0 || get(DamageVsLowLifePerFrenzy) != 0;

    // Project into the original type's increased bucket before conversion. The converted part inherits that
    // bucket once, and the damaging ailments use the same pre-contact target state independently of the hit.
    static int TypedTargetDamageStat(Func<uint, int> get, uint name, bool ignited, bool bleeding, bool lowLife = false) =>
        ActorScale.Saturate((long)get(name) + (name == Stat.PhysicalDamagePlusPct && ignited ? get(PhysicalVsIgnited) : 0)
            + (name == Stat.FireDamagePlusPct && bleeding ? get(FireVsBleeding) : 0)
            + (name == Stat.DamagePlusPct && lowLife ? (long)Math.Max(0, get(Stat.CurrentFrenzyCharges)) * get(DamageVsLowLifePerFrenzy) : 0));

    static IReadOnlyDictionary<uint, int> TypedTargetDamageStats(IReadOnlyDictionary<uint, int> stats,
        bool ignited, bool bleeding, bool lowLife = false)
    {
        int Get(uint name) => GolemStat(stats, name);
        if (!HasTypedTargetDamage(Get) || !ignited && !bleeding && !lowLife) return stats;
        var result = new Dictionary<uint, int>(stats);
        foreach (uint name in new[] { Stat.PhysicalDamagePlusPct, Stat.FireDamagePlusPct, Stat.DamagePlusPct })
            result[name] = TypedTargetDamageStat(Get, name, ignited, bleeding, lowLife);
        return result;
    }
}
