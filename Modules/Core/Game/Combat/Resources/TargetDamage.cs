using GameServer.Core.World;

public static partial class Game
{
    const uint PhysicalVsIgnited = Stat.PhysicalDamagePlusPctVsIgnitedEnemies;
    public const uint DamageVsLowLifePerFrenzy = Stat.DamagePlusPctVsEnemiesOnLowLifePerFrenzyCharge;
    public const uint TinctureDamageVsLowLife = Stat.TincturedWeaponDamagePlusPctVsEnemiesThatAreOnLowLife;
    public static bool EnemyOnLowLife(SpawnedMonster target) => target.Hostile && MonsterOnLowLife(target);

    const uint FireVsBleeding = Stat.FireDamagePlusPctVsBleedingEnemies;

    static bool HasTypedTargetDamage(Func<uint, int> get) => get(PhysicalVsIgnited) != 0 || get(FireVsBleeding) != 0
        || get(DamageVsLowLifePerFrenzy) != 0
        || get(Stat.TinctureApplied) != 0 && get(TinctureDamageVsLowLife) != 0;

    static bool HasLowLifeDamage(Func<uint, int> get) => get(Stat.DamageVsEnemiesOnLowLifePlusPct) != 0
        || get(Stat.DamageVsEnemiesOnLowLifePlusPctFinal) != 0
        || get(Stat.ClawDamageAgainstEnemiesOnLowLifePlusPct) != 0
        || get(Stat.AxeMasteryHitAndAilmentDamagePlusPctFinalVsEnemiesOnLowLife) != 0
        || get(Stat.GladiatorDamagePlusPctFinalAgainstEnemiesOnLowLifeWhileWieldingAxe) != 0;

    static bool HasConditionalHitDamage(SupportedHitInputs inputs) => HasLowLifeDamage(inputs.Get)
        || inputs.Melee && inputs.Get(Stat.MeleeDamagePlusPctAtCloseRange) != 0
        || inputs.Attack && !inputs.Secondary && inputs.Get(Stat.AttackDamagePlusPctVsMaimedEnemies) != 0;

    static bool InCloseRange(Player player, SpawnedMonster target, double at) =>
        TargetAccuracy.InCloseRange(Dist(player.Walk.Position(at), target.Walk.Position(at)));

    readonly record struct TargetDamageModifiers(long HitIncrease, int AilmentIncrease, double More);

    static TargetDamageModifiers TargetModifiers(Func<uint, int> get, bool lowLife, bool maimed = false,
        bool close = false, bool claw = false)
    {
        long hitIncrease = (close ? (long)get(Stat.MeleeDamagePlusPctAtCloseRange) : 0)
            + (maimed ? get(Stat.AttackDamagePlusPctVsMaimedEnemies) : 0);
        if (!lowLife) return new(hitIncrease, 0, 1);
        int clawIncrease = claw ? get(Stat.ClawDamageAgainstEnemiesOnLowLifePlusPct) : 0;
        hitIncrease += (long)get(Stat.DamageVsEnemiesOnLowLifePlusPct) + clawIncrease;
        double more = Factor(get(Stat.DamageVsEnemiesOnLowLifePlusPctFinal))
            * Factor(get(Stat.AxeMasteryHitAndAilmentDamagePlusPctFinalVsEnemiesOnLowLife));
        int axeMore = get(Stat.GladiatorDamagePlusPctFinalAgainstEnemiesOnLowLifeWhileWieldingAxe);
        if (axeMore != 0 && (WeaponTypeDamage.FamilyOf(get(Stat.MainHandWeaponType)) == "axe"
                || WeaponTypeDamage.FamilyOf(get(Stat.OffHandWeaponType)) == "axe"
                || get(WeaponIdentity.MainAllOneHandMelee) != 0 || get(WeaponIdentity.OffAllOneHandMelee) != 0))
            more *= Factor(axeMore);
        return new(hitIncrease, clawIncrease, more);

        static double Factor(int percent) => Math.Max(0, 1 + percent / 100d);
    }

    // Project into the original type's increased bucket before conversion. The converted part inherits that
    // bucket once, and the damaging ailments use the same pre-contact target state independently of the hit.
    static int TypedTargetDamageStat(Func<uint, int> get, uint name, bool ignited, bool bleeding, bool lowLife = false,
        bool attack = true) =>
        ActorScale.Saturate((long)get(name) + (name == Stat.PhysicalDamagePlusPct && ignited ? get(PhysicalVsIgnited) : 0)
            + (name == Stat.FireDamagePlusPct && bleeding ? get(FireVsBleeding) : 0)
            + (name == Stat.DamagePlusPct && lowLife
                ? (long)Math.Max(0, get(Stat.CurrentFrenzyCharges)) * get(DamageVsLowLifePerFrenzy)
                    + (attack && get(Stat.TinctureApplied) != 0 ? get(TinctureDamageVsLowLife) : 0)
                : 0));

    static IReadOnlyDictionary<uint, int> TypedTargetDamageStats(IReadOnlyDictionary<uint, int> stats,
        bool ignited, bool bleeding, bool lowLife = false, bool attack = true)
    {
        int Get(uint name) => GolemStat(stats, name);
        if (!HasTypedTargetDamage(Get) || !ignited && !bleeding && !lowLife) return stats;
        var result = new Dictionary<uint, int>(stats);
        foreach (uint name in new[] { Stat.PhysicalDamagePlusPct, Stat.FireDamagePlusPct, Stat.DamagePlusPct })
            result[name] = TypedTargetDamageStat(Get, name, ignited, bleeding, lowLife, attack);
        return result;
    }
}
