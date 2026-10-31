

/// <summary>The bounded inputs read by native 3.29.3b getter 0x141ee8720; no general cost/modifier engine.</summary>
static class VaalSoulCost
{
    public const string NonAuraMultiplier = "virtual_non_aura_vaal_skill_soul_requirement_multipler";
    public const string AuraMultiplier = "virtual_aura_vaal_skill_soul_requirement_multipler";
    public const string FinalPercent = "unique_ring_support_effect_vaal_skill_soul_requirement_+%_final";
    public const string NoSouls = "virtual_vaal_skill_require_no_souls_per_use";
    public const string Aura = "skill_is_aura_skill";
    public static readonly string[] Inputs = [NonAuraMultiplier, AuraMultiplier, FinalPercent, NoSouls, Aura];

    public static int Required(int baseCost, Func<string, int> get)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(baseCost);
        if (get(NoSouls) != 0) return 0;
        long t = baseCost + (long)baseCost * get(get(Aura) != 0 ? AuraMultiplier : NonAuraMultiplier) / 100;
        // Truncate toward zero at EACH division. Widen the second product, then saturate to signed native range.
        // Out-of-range developer inputs deliberately do not emulate the client's signed 32-bit overflow.
        return (int)Int128.Clamp(t + (Int128)t * get(FinalPercent) / 100, 0, int.MaxValue);
    }

    // LocalExile policy, not a proved official capacity algorithm. Signed range also suits native UI consumers.
    public static uint Capacity(int required, int storedUses) =>
        (uint)Math.Clamp((long)required * storedUses, 0, int.MaxValue);
}
