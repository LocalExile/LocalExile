/// <summary>Explicit unavailable gems from GGG's Ruthless rules, also absent from the pinned Ruthless active index.
/// A native HardMode effect definition is not proof that its gem is obtainable. Quickstep remains allowed.</summary>
static class RuthlessGemRules
{
    static readonly HashSet<string> UnavailableBases = new(StringComparer.OrdinalIgnoreCase)
    {
        "SkillGemDash", "SkillGemFrostblink", "SkillGemFlameDash", "SkillGemLightningWarp", "SkillGemLeapSlam",
        "SkillGemShieldCharge", "SkillGemWhirlingBlades", "SkillGemBlinkArrow", "SkillGemSmokeMine", "SkillGemCorpseWarp",
        "SkillGemChargedDash", "SkillGemNewPhaseRun", "SkillGemPhaseRun", "SkillGemWitheringStep", "SkillGemVenomGyre",
        "SupportGemItemRarity",
    };
    static readonly string[] UnavailableEffects =
    [
        "Dash", "Frostblink", "FlameDash", "LightningWarp", "LeapSlam", "ShieldCharge", "WhirlingBlades", "BlinkArrow",
        "SmokeMine", "Bodyswap", "ChargedDash", "PhaseRun", "WitheringStep", "VenomGyre", "SupportItemRarity",
    ];

    public static bool AllowsBase(string baseId)
    {
        string leaf = baseId[(baseId.LastIndexOf('/') + 1)..];
        return !UnavailableBases.Any(id => leaf.Equals(id, StringComparison.OrdinalIgnoreCase)
            || leaf.StartsWith(id + "Alt", StringComparison.OrdinalIgnoreCase));
    }

    public static bool AllowsEffect(string? effectId)
    {
        string name = effectId?.Replace("HardMode", "", StringComparison.OrdinalIgnoreCase) ?? "";
        return !UnavailableEffects.Any(id => name.Equals(id, StringComparison.OrdinalIgnoreCase)
            || name.StartsWith(id + "Alt", StringComparison.OrdinalIgnoreCase));
    }
}
