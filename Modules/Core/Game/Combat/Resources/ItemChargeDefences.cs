public static class ItemChargeDefences
{
    public const string ElementalAvoidPerFrenzy = "avoid_elemental_damage_%_per_frenzy_charge";
    public const string ElementalHitTakenPerEndurance = "elemental_damage_taken_from_hits_+%_per_endurance_charge";
    public const string ElementalHitTaken = "base_elemental_damage_taken_when_hit_+%";

    public static IEnumerable<(string Stat, int Value)> Expand(Func<string, int> get,
        Func<ChargeType, int> held, Func<ChargeType, bool> converted)
    {
        int Count(ChargeType type) => converted(type) ? 0 : held(type);
        int avoid = ActorScale.Saturate((long)get(ElementalAvoidPerFrenzy) * Count(ChargeType.Frenzy));
        if (avoid != 0)
            foreach (string type in new[] { "fire", "cold", "lightning" }) yield return ($"avoid_{type}_damage_%", avoid);
        int taken = ActorScale.Saturate((long)get(ElementalHitTakenPerEndurance) * Count(ChargeType.Endurance));
        if (taken != 0) yield return (ElementalHitTaken, taken);
    }
}
