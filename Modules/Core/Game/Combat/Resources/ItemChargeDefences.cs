public static class ItemChargeDefences
{
    public const uint ElementalAvoidPerFrenzy = Stat.AvoidElementalDamagePctPerFrenzyCharge;
    public const uint ElementalHitTakenPerEndurance = Stat.ElementalDamageTakenFromHitsPlusPctPerEnduranceCharge;
    public const uint ElementalHitTaken = Stat.BaseElementalDamageTakenWhenHitPlusPct;

    public static IEnumerable<(uint Stat, int Value)> Expand(Func<uint, int> get,
        Func<ChargeType, int> held, Func<ChargeType, bool> converted)
    {
        int Count(ChargeType type) => converted(type) ? 0 : held(type);
        int avoid = ActorScale.Saturate((long)get(ElementalAvoidPerFrenzy) * Count(ChargeType.Frenzy));
        if (avoid != 0)
            foreach (string type in new[] { "fire", "cold", "lightning" }) yield return (Stat.Of($"avoid_{type}_damage_%"), avoid);
        int taken = ActorScale.Saturate((long)get(ElementalHitTakenPerEndurance) * Count(ChargeType.Endurance));
        if (taken != 0) yield return (ElementalHitTaken, taken);
    }
}
