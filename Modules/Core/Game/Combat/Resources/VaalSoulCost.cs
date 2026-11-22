

/// <summary>The bounded inputs read by native 3.29.3c getter 0x141eeef80; no general cost/modifier engine.</summary>
public static class VaalSoulCost
{
    public const uint NonAuraMultiplier = Stat.VirtualNonAuraVaalSkillSoulRequirementMultipler;
    public const uint AuraMultiplier = Stat.VirtualAuraVaalSkillSoulRequirementMultipler;
    public const uint FinalPercent = Stat.UniqueRingSupportEffectVaalSkillSoulRequirementPlusPctFinal;
    public const uint NoSouls = Stat.VirtualVaalSkillRequireNoSoulsPerUse;
    public const uint Aura = Stat.SkillIsAuraSkill;
    public static readonly uint[] Inputs = [NonAuraMultiplier, AuraMultiplier, FinalPercent, NoSouls, Aura];

    public static int Required(int baseCost, Func<uint, int> get)
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
