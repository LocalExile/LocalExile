/// <summary>
/// Raw flat and increased attributes, never final character stats. Combine all sources before applying to the class.
/// PoB 16de4b8 CalcPerform.lua:405 uses max(round(calcLib.val(...)), 0); Common.lua:683 rounds with floor(value + 0.5).
/// This is a server model, not evidence of native fractional parity. Existing attribute-derived pool divisions stay intact.
/// </summary>
public readonly record struct AttributeModifiers(long Strength = 0, long Dexterity = 0, long Intelligence = 0,
    long StrengthPct = 0, long DexterityPct = 0, long IntelligencePct = 0, bool ZeroIntelligence = false)
{
    // Not in the client's Stats table (a server-only id); items and passives may still name it.
    public static readonly uint BaseAllAttributes = Stat.Of("base_all_attributes");

    public static bool IsInput(uint name) => name == BaseAllAttributes || name is
        Stat.BaseStrength or Stat.AdditionalStrength or Stat.BaseDexterity or Stat.AdditionalDexterity
        or Stat.BaseIntelligence or Stat.AdditionalIntelligence or Stat.AdditionalAllAttributes
        or Stat.BaseStrengthAndDexterity or Stat.AdditionalStrengthAndDexterity
        or Stat.BaseStrengthAndIntelligence or Stat.AdditionalStrengthAndIntelligence
        or Stat.BaseDexterityAndIntelligence or Stat.AdditionalDexterityAndIntelligence
        or Stat.StrengthPlusPct or Stat.DexterityPlusPct or Stat.IntelligencePlusPct or Stat.AllAttributesPlusPct or Stat.IntelligenceIs0;

    public static AttributeModifiers From(Func<uint, int> get) => FromWide(name => get(name));

    public static AttributeModifiers FromWide(Func<uint, long> get)
    {
        long all = get(BaseAllAttributes) + get(Stat.AdditionalAllAttributes);
        long strDex = get(Stat.BaseStrengthAndDexterity) + get(Stat.AdditionalStrengthAndDexterity);
        long strInt = get(Stat.BaseStrengthAndIntelligence) + get(Stat.AdditionalStrengthAndIntelligence);
        long dexInt = get(Stat.BaseDexterityAndIntelligence) + get(Stat.AdditionalDexterityAndIntelligence);
        long allPct = get(Stat.AllAttributesPlusPct);
        return new(
            get(Stat.BaseStrength) + get(Stat.AdditionalStrength) + all + strDex + strInt,
            get(Stat.BaseDexterity) + get(Stat.AdditionalDexterity) + all + strDex + dexInt,
            get(Stat.BaseIntelligence) + get(Stat.AdditionalIntelligence) + all + strInt + dexInt,
            get(Stat.StrengthPlusPct) + allPct, get(Stat.DexterityPlusPct) + allPct, get(Stat.IntelligencePlusPct) + allPct, get(Stat.IntelligenceIs0) != 0);
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
