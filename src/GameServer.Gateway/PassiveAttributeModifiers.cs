/// <summary>Attribute-dependent passive effects, evaluated from final attributes after all flat and increased stats.</summary>
static class PassiveAttributeModifiers
{
    public readonly record struct Increases(int Strength = 0, int Dexterity = 0, int Intelligence = 0, bool NoIntelligence = false)
    {
        public (int Str, int Dex, int Int) Apply((int Str, int Dex, int Int) attributes) =>
            (Scale(attributes.Str, Strength), Scale(attributes.Dex, Dexterity), NoIntelligence ? 0 : Scale(attributes.Int, Intelligence));
    }

    public static Increases Percentages(Func<string, int> get)
    {
        int all = get("all_attributes_+%");
        return new(all + get("strength_+%"), all + get("dexterity_+%"), all + get("intelligence_+%"), get("intelligence_is_0") != 0);
    }

    /// <summary>Items and gems share this global requirement modifier. Local item reductions are applied separately,
    /// before the global modifier; each stage rounds down. Level requirements are unaffected.</summary>
    public static int Requirement(int baseRequirement, Func<string, int> get) =>
        Requirement(baseRequirement, get("global_item_attribute_requirements_+%"), get("global_no_attribute_requirements") != 0);

    public static int Requirement(int baseRequirement, int increase, bool ignored = false) =>
        ignored ? 0 : Scale(baseRequirement, increase);

    /// <summary>"8% increased damage per 5 of your lowest attribute" counts complete groups of five and adds to the
    /// ordinary increased-damage pool, rather than multiplying the final damage a second time.</summary>
    public static int DamageIncrease(Func<string, int> get, int strength, int dexterity, int intelligence) =>
        (int)Math.Clamp((long)Math.Max(0, Math.Min(strength, Math.Min(dexterity, intelligence))) / 5
            * get("damage_+%_per_5_of_your_lowest_attribute"), int.MinValue, int.MaxValue);

    public readonly record struct Immunities(bool Ignite = false, bool Freeze = false, bool Shock = false);

    /// <summary>The Aristocrat comparisons are strict: equal attributes do not grant the conditional immunity.</summary>
    public static Immunities AilmentImmunities(Func<string, int> get, int strength, int dexterity, int intelligence) => new(
        Ignite: strength > dexterity && get("cannot_be_ignited_with_strength_higher_than_dex") != 0,
        Freeze: dexterity > intelligence && get("cannot_be_frozen_with_dex_higher_than_int") != 0,
        Shock: intelligence > strength && get("cannot_be_shocked_with_int_higher_than_strength") != 0);

    private static int Scale(int value, int increase) =>
        (int)Math.Clamp((long)Math.Max(0, value) * Math.Max(0L, 100L + increase) / 100, 0, int.MaxValue);
}
