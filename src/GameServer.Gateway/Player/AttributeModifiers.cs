/// <summary>
/// Raw flat and increased attributes, never final character stats. Combine all sources before applying to the class.
/// PoB 16de4b8 CalcPerform.lua:405 uses max(round(calcLib.val(...)), 0); Common.lua:683 rounds with floor(value + 0.5).
/// This is a server model, not evidence of native fractional parity. Existing attribute-derived pool divisions stay intact.
/// </summary>
readonly record struct AttributeModifiers(long Strength = 0, long Dexterity = 0, long Intelligence = 0,
    long StrengthPct = 0, long DexterityPct = 0, long IntelligencePct = 0, bool ZeroIntelligence = false)
{
    public static bool IsInput(string name) => name is
        "base_strength" or "additional_strength" or "base_dexterity" or "additional_dexterity"
        or "base_intelligence" or "additional_intelligence" or "base_all_attributes" or "additional_all_attributes"
        or "base_strength_and_dexterity" or "additional_strength_and_dexterity"
        or "base_strength_and_intelligence" or "additional_strength_and_intelligence"
        or "base_dexterity_and_intelligence" or "additional_dexterity_and_intelligence"
        or "strength_+%" or "dexterity_+%" or "intelligence_+%" or "all_attributes_+%" or "intelligence_is_0";

    public static AttributeModifiers From(Func<string, int> get) => FromWide(name => get(name));

    public static AttributeModifiers FromWide(Func<string, long> get)
    {
        long all = get("base_all_attributes") + get("additional_all_attributes");
        long strDex = get("base_strength_and_dexterity") + get("additional_strength_and_dexterity");
        long strInt = get("base_strength_and_intelligence") + get("additional_strength_and_intelligence");
        long dexInt = get("base_dexterity_and_intelligence") + get("additional_dexterity_and_intelligence");
        long allPct = get("all_attributes_+%");
        return new(
            get("base_strength") + get("additional_strength") + all + strDex + strInt,
            get("base_dexterity") + get("additional_dexterity") + all + strDex + dexInt,
            get("base_intelligence") + get("additional_intelligence") + all + strInt + dexInt,
            get("strength_+%") + allPct, get("dexterity_+%") + allPct, get("intelligence_+%") + allPct, get("intelligence_is_0") != 0);
    }

    public static int Signed(decimal value) => (int)Math.Clamp(value, int.MinValue, int.MaxValue);

    public (int Strength, int Dexterity, int Intelligence) Apply(long strength = 0, long dexterity = 0,
        long intelligence = 0) => (Final((decimal)Strength + strength, StrengthPct),
            Final((decimal)Dexterity + dexterity, DexterityPct), ZeroIntelligence ? 0 : Final((decimal)Intelligence + intelligence, IntelligencePct));

    public (int Strength, int Dexterity, int Intelligence) Flat =>
        (Signed(Strength), Signed(Dexterity), Signed(Intelligence));

    static int Final(decimal flat, long increased)
    {
        // Keep raw buckets wide through cancellation and scaling. Clamp only the final stat. Test the product's
        // magnitude before multiplying so even a directly constructed extreme long input cannot overflow decimal.
        decimal factor = 100m + increased;
        if (flat == 0 || factor == 0 || Math.Sign(flat) != Math.Sign(factor)) return 0;
        if (Math.Abs(flat) >= int.MaxValue * 100m / Math.Abs(factor)) return int.MaxValue;
        decimal value = flat * factor / 100m;
        return (int)Math.Clamp(decimal.Floor(value + 0.5m), 0, int.MaxValue);
    }
}
