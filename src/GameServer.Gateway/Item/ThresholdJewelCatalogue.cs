// Generated from Path of Building src/Export/Uniques/jewel.lua for the target 3.29.3b data.
// Do not edit by hand; regenerate with scripts/re/threshold_jewels.py.

/// <summary>The 33 threshold jewels: item name, base, limits and the client mods. The mods are
/// <c>local_unique_jewel_..._with_40_&lt;attribute&gt;_in_radius</c> (or the 50-attribute variants); the
/// client applies them to the passive tree, while the server's flat stat model only consumes the
/// unconditional global mods. ReadConversion therefore returns null for these items.</summary>
static class ThresholdJewelCatalogue
{
    internal static readonly RadiusJewelCatalogue.Entry[] Seed =
    [
        new("Divine Inferno", "Crimson Jewel", [], "InfernalCryThresholdJewel", 1, 1, false, 1200),
        new("Endless Misery", "Cobalt Jewel", ["ElementalDamageUnique__4"], "DischargeThresholdJewel__1", 1, 1, false, 1200),
        new("Combat Focus", "Crimson Jewel", ["ElementalDamageUniqueJewel_1"], "ElementalHitDisableColdUniqueJewel_1", 2, 1, false, 1200),
        new("Combat Focus", "Cobalt Jewel", ["ElementalDamageUniqueJewel_1"], "ElementalHitDisableFireUniqueJewel_1", 2, 1, false, 1200),
        new("Combat Focus", "Viridian Jewel", ["ElementalDamageUniqueJewel_1"], "ElementalHitDisableLightningUniqueJewel_1", 2, 1, false, 1200),
        new("Collateral Damage", "Viridian Jewel", ["IncreasedPhysicalDamagePercentUnique__7"], "ShrapnelShotThresholdJewel_1", 2, 1, false, 1200),
        new("Dead Reckoning", "Cobalt Jewel", [], "SkeletonAddedChaosDamageShieldUnique__1", 1, 1, false, 1200),
        new("Fight for Survival", "Viridian Jewel", ["ColdDamagePercentUnique__2"], "FrostBladesThresholdJewel_1", 2, 1, false, 1200),
        new("First Snow", "Cobalt Jewel", ["IncreasedProjectileDamageUnique___8"], "FreezingPulseThresholdJewel_1", 2, 1, false, 1200),
        new("Frozen Trail", "Cobalt Jewel", ["IncreasedProjectileDamageUnique___3"], "FrostboltThresholdJewel_1", 2, 1, false, 1200),
        new("Growing Agony", "Viridian Jewel", ["DamageOverTimeUnique___1"], "ViperStrikeThresholdJewel__1", 1, 1, false, 1200, null, ["ViperStrikeThresholdJewel__2"]),
        new("Hazardous Research", "Cobalt Jewel", ["LightningDamagePercentUnique__5"], "SparkThresholdJewel_2", 1, 1, false, 1200),
        new("Inevitability", "Cobalt Jewel", ["FireDamagePercentUnique___2"], "MagmaOrbThresholdJewel_2", 1, 1, false, 1200),
        new("The Long Winter", "Cobalt Jewel", ["ColdDamagePercentUnique__15"], "GlacialCascadeThresholdJewel1", 2, 1, false, 1200),
        new("Might and Influence", "Viridian Jewel", [], "DualStrikeThresholdJewel__2_", 1, 1, false, 1200, null, ["DualStrikeThresholdJewelAxe"]),
        new("Omen on the Winds", "Viridian Jewel", ["IncreasedDamageToChilledEnemies1"], "IceShotThresholdJewel__2", 2, 1, false, 1200),
        new("Overwhelming Odds", "Crimson Jewel", ["IncreasedPhysicalDamagePercentUnique__5"], "CleaveThresholdJewel_1", 1, 1, false, 1200),
        new("Rapid Expansion", "Crimson Jewel", ["IncreasedPhysicalDamagePercentUnique__2"], "GroundSlamThresholdUnique__1", 2, 1, false, 1200, null, ["GroundSlamThresholdUnique__2"]),
        new("Ring of Blades", "Viridian Jewel", ["IncreasedPhysicalDamagePercentUnique__3"], "EtherealKnivesThresholdJewel_1", 1, 1, false, 1200),
        new("Rolling Flames", "Cobalt Jewel", ["FireDamagePercentUnique__11"], "FireballThresholdJewel__3", 1, 1, false, 1200),
        new("Shattered Chains", "Crimson Jewel", ["ColdDamagePercentUnique__13"], "GlacialHammerThresholdJewel__1", 0, 1, false, 1200),
        new("Spirit Guards", "Viridian Jewel", ["MinionDamageUniqueJewel1"], "AnimateBowsAndWandsUnique____70", 1, 1, false, 1200),
        new("Spirited Response", "Cobalt Jewel", ["MaximumManaUnique__1"], "RallyingCryThresholdJewel__1", 1, 1, false, 1200),
        new("Spreading Rot", "Cobalt Jewel", ["IncreasedChaosDamageUnique__3"], "BlightThresholdJewel_2", 1, 1, false, 1200),
        new("Steel Spirit", "Viridian Jewel", ["IncreasedProjectileDamageUnique__7"], "SpectralThrowThresholdJewel__1", 0, 1, false, 1200),
        new("Unending Hunger", "Cobalt Jewel", ["MinionAreaOfEffectUnique__1"], "ChanceForSpectersToGainSoulEaterOnKillUnique__1", 2, 1, false, 1200),
        new("The Vigil", "Crimson Jewel", ["IncreasedPhysicalDamageReductionRatingPercentUnique__1"], "VigilantStrikeThresholdJewel__1", 1, 1, false, 1200),
        new("Violent Dead", "Cobalt Jewel", ["MinionDamageUnique4"], "RaiseZombieThresholdJewel1", 2, 1, false, 1200),
        new("Volley Fire", "Viridian Jewel", ["IncreasedProjectileDamageUnique__2"], "BarrageThresholdUnique__1", 1, 1, false, 1200),
        new("Weight of the Empire", "Crimson Jewel", ["IncreasedPhysicalDamagePercentUnique__1"], "HeavyStrikeThresholdJewel___1", 2, 1, false, 1200),
        new("Wildfire", "Crimson Jewel", ["FireDamagePercentUnique__1"], "MoltenStrikeThresholdJewel__2", 1, 1, false, 1200),
        new("Winter Burial", "Crimson Jewel", ["ColdDamagePercentUnique__12"], "GlacialHammerThresholdJewel_2", 2, 1, false, 1200),
        new("Winter's Bounty", "Cobalt Jewel", ["ColdDamagePercentUnique__1"], "ColdsnapThresholdJewel__1", 1, 1, false, 1200),
    ];

    public static RadiusJewelCatalogue.Entry[] All => GameDataCatalog.ThresholdJewels;
}
