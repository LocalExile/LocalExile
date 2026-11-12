public static class ItemChargeDamage
{
    public const string TakenPerFrenzy = "damage_taken_+%_per_frenzy_charge";
    public const string AttackElementalPerPower = "elemental_damage_with_attack_skills_+%_per_power_charge";

    public static IEnumerable<(string Stat, int Value)> Expand(Func<string, int> get,
        Func<ChargeType, int> held, Func<ChargeType, bool> converted)
    {
        int Count(ChargeType type) => converted(type) ? 0 : Math.Max(0, held(type));
        int taken = ActorScale.Saturate((long)get(TakenPerFrenzy) * Count(ChargeType.Frenzy));
        if (taken != 0) yield return ("base_damage_taken_+%", taken);
        int elemental = ActorScale.Saturate((long)get(AttackElementalPerPower) * Count(ChargeType.Power));
        if (elemental != 0) yield return ("elemental_damage_with_attack_skills_+%", elemental);
    }
}
