public static class ItemChargeDamage
{
    public const uint TakenPerFrenzy = Stat.DamageTakenPlusPctPerFrenzyCharge;
    public const uint AttackElementalPerPower = Stat.ElementalDamageWithAttackSkillsPlusPctPerPowerCharge;

    public static IEnumerable<(uint Stat, int Value)> Expand(Func<uint, int> get,
        Func<ChargeType, int> held, Func<ChargeType, bool> converted)
    {
        int Count(ChargeType type) => converted(type) ? 0 : Math.Max(0, held(type));
        int taken = ActorScale.Saturate((long)get(TakenPerFrenzy) * Count(ChargeType.Frenzy));
        if (taken != 0) yield return (Stat.BaseDamageTakenPlusPct, taken);
        int elemental = ActorScale.Saturate((long)get(AttackElementalPerPower) * Count(ChargeType.Power));
        if (elemental != 0) yield return (Stat.ElementalDamageWithAttackSkillsPlusPct, elemental);
    }
}
