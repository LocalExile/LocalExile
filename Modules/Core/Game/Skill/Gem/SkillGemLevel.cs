

/// <summary>Effective active-gem level; stored experience and intrinsic requirements are independent.</summary>
public static class SkillGemLevel
{
    public static readonly uint[] FamilyStats =
        [Stat.HeraldSkillGemLevelPlus, Stat.VaalSkillGemLevelPlus, Stat.LinkSkillGemLevelPlus,
            Stat.RaiseZombieGemLevelPlus, Stat.RaiseSpectreGemLevelPlus, Stat.SummonSkeletonGemLevelPlus,
            Stat.CurseSkillGemLevelPlus];

    public static readonly (uint Stat, string Tag)[] TaggedActiveLevelStats =
    [
        (Stat.PhysicalSkillGemLevelPlus, "physical"), (Stat.FireSkillGemLevelPlus, "fire"),
        (Stat.ColdSkillGemLevelPlus, "cold"), (Stat.LightningSkillGemLevelPlus, "lightning"),
        (Stat.ChaosSkillGemLevelPlus, "chaos"), (Stat.SpellSkillGemLevelPlus, "spell"),
        (Stat.MinionSkillGemLevelPlus, "minion"),
    ];

    // "All Skill Gems" means active skill gems (PoB keyword grants_active_skill); supports have their own stat.
    public const uint AllSkillGems = Stat.AllSkillGemLevelPlus;
    public const uint NonExceptionalSupports = Stat.NonExceptionalSupportGemLevelPlus;
    public const uint AllSkillGemQuality = Stat.AllSkillGemQualityPlus;

    public static int Resolve(int intrinsic, int supportedBonus, IEnumerable<int> familyBonuses) =>
        (int)Math.Clamp(intrinsic + (long)supportedBonus + familyBonuses.Sum(value => (long)value), 1, int.MaxValue);

    /// <summary>"+1 to Level of all Strength/Dexterity/Intelligence Skill Gems" (the Honoured Warlord, Hunter and Wise
    /// tattoos): a gem's SkillGems GemColour names the attribute it belongs to - 1 red strength, 2 green dexterity,
    /// 3 blue intelligence; a white (4) gem has none.</summary>
    public static uint? AttributeStat(int colour) => colour switch
    {
        1 => Stat.StrengthSkillGemLevelPlus,
        2 => Stat.DexteritySkillGemLevelPlus,
        3 => Stat.IntelligenceSkillGemLevelPlus,
        _ => null,
    };
}
