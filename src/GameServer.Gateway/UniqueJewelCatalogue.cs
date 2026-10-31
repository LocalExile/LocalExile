// Generated from Path of Building src/Export/Uniques/jewel.lua for the target 3.29.3b data.
// Do not edit by hand; regenerate with scripts/re/unique_jewels.py (a read-only third-party reference).
// Global jewels have no radius and no conditional mechanic; Special jewels have an item source but a
// mechanic that is not modelled yet.

/// <summary>The non-radius unique jewels: base type and mod HASH16s. Global entries are unconditional;
/// Special entries are item sources whose mechanic remains partial/inert. The unique art of every jewel is
/// in <see cref="UniqueJewelVisuals"/>.</summary>
static class UniqueJewelCatalogue
{
    public sealed record Entry(string Name, string Base, bool Corrupted, int Level,
        string[] Mods, int?[] Values);

    internal static readonly Entry[] SeedGlobal =
    [
        new("Ancestral Vision", "Viridian Jewel", false, 20, ["DexterityUnique__26", "ModifiersToSuppressionApplyToAilmentAvoidUnique__1"], [null, null]),
        new("Ancient Waystones", "Crimson Jewel", true, 20, ["ManaCostOfTotemAurasUniqueCorruptedJewel8"], [null]),
        new("Apparitions", "Viridian Jewel", false, 20, ["MinionRunSpeedUniqueJewel16", "MinionDodgeChanceUniqueJewel16"], [null, null]),
        new("Assassin's Haste", "Cobalt Jewel", false, 20, ["ManaRegenerationUniqueJewel43", "MovementVelocityUniqueJewel43", "AttackAndCastSpeedJewelUniqueJewel43"], [null, null, null]),
        new("Bloodnotch", "Crimson Jewel", false, 20, ["StunningHitsRecoverLifeUnique__1"], [null]),
        new("Brawn", "Crimson Jewel", false, 20, ["PercentageStrengthUniqueJewel29", "PercentageDexterityUniqueJewel29", "PercentageIntelligenceUniqueJewel29"], [null, null, null]),
        new("Cheap Construction", "Viridian Jewel", false, 20, ["TrapDurationUnique__1", "AdditionalTrapsThresholdJewel"], [null, null]),
        new("Combustibles", "Crimson Jewel", true, 20, ["ItemFoundQuantityReduceUniqueCorruptedJewel1", "BurnDamageUniqueCorruptedJewel1"], [null, null]),
        new("Conqueror's Efficiency", "Crimson Jewel", false, 20, ["ManaCostReductionUniqueJewel44", "SkillEffectDurationUniqueJewel44", "ManaReservationEfficiencyUniqueJewel44_"], [null, null, null]),
        new("Conqueror's Longevity", "Viridian Jewel", false, 20, ["ChanceToAvoidElementalStatusAilmentsUniqueJewel46", "FlaskLifeRecoveryRateUniqueJewel46", "ChanceToSuppressSpellsUniqueJewel46"], [null, null, null]),
        new("Conqueror's Potency", "Cobalt Jewel", false, 20, ["CurseEffectivenessUniqueJewel45", "IncreasedFlaskEffectUniqueJewel45", "IncreasedAuraEffectUniqueJewel45"], [null, null, null]),
        new("Emperor's Cunning", "Viridian Jewel", false, 20, ["PercentageDexterityUniqueJewel29", "PercentIncreasedAccuracyJewelUnique__1", "ActorSizeUnique__1"], [null, null, null]),
        new("Emperor's Mastery", "Prismatic Jewel", false, 1, ["AllAttributesPercentUnique__1", "MaximumLifeUnique__2", "ActorSizeUnique__1", "AllDefencesUnique__1"], [null, null, null, null]),
        new("Emperor's Might", "Crimson Jewel", false, 20, ["PercentageStrengthUniqueJewel29", "AllDamageUnique__1", "ActorSizeUnique__1"], [null, null, null]),
        new("Emperor's Wit", "Cobalt Jewel", false, 20, ["PercentageIntelligenceUnique__2", "CriticalStrikeChanceUnique__1", "ActorSizeUnique__1"], [null, null, null]),
        new("Firesong", "Crimson Jewel", false, 20, ["FireResistUnique__31", "SelfIgniteDurationAllElementalAilmentsUnique__1"], [null, null]),
        new("Fragility", "Crimson Jewel", true, 20, ["ReducedMaximumEnduranceChargeUniqueCorruptedJewel17"], [null]),
        new("From Dust", "Cobalt Jewel", false, 20, ["SummonSkeletonsNumberOfSkeletonsToSummonUnique__1", "SummonSkeletonsCooldownTimeUnique__1"], [null, null]),
        new("Hair Trigger", "Viridian Jewel", false, 20, ["TrapDamageUnique___1", "TrapTriggerRadiusUnique__1"], [null, null]),
        new("Immutable Force", "Crimson Jewel", false, 20, ["StunRecoveryUnique__4"], [null]),
        new("Izaro's Turmoil", "Crimson Jewel", false, 20, ["FireDamagePercentUnique__3", "ColdDamagePercentUnique__4", "ChanceToFreezeUnique__2", "IncreasedChanceToIgniteUnique__1"], [null, null, null, null]),
        new("Melding of the Flesh", "Cobalt Jewel", false, 20, ["AllResistancesUnique__24_", "MaximumElementalResistanceUnique__2", "ElementalResistanceHighestMaxResistanceUnique__1_"], [null, null, null]),
        new("Mutated Growth", "Cobalt Jewel", true, 20, ["IncreasedCorruptedGemExperienceUniqueCorruptedJewel9"], [null]),
        new("Pacifism", "Viridian Jewel", true, 20, ["ReducedMaximumFrenzyChargesUniqueCorruptedJewel16"], [null]),
        new("Poacher's Aim", "Viridian Jewel", false, 20, ["ProjectileDamageJewelUniqueJewel41", "AdditionalPierceUniqueJewel__1"], [null, null]),
        new("Powerlessness", "Cobalt Jewel", true, 20, ["ReducedMaximumPowerChargesUniqueCorruptedJewel18"], [null]),
        new("Rain of Splinters", "Crimson Jewel", true, 20, ["ReducedTotemDamageUniqueJewel26", "AdditionalTotemProjectilesUniqueJewel26"], [null, null]),
        new("Reckless Defence", "Cobalt Jewel", true, 20, ["SpellBlockPercentageUnique__1", "ChanceToBeCritJewelUnique__1", "AdditionalBlockUnique__1"], [null, null, null]),
        new("Replica Cheap Construction", "Viridian Jewel", false, 20, ["TrapCritChanceUnique__1", "AdditionalTrapsUnique__2__"], [null, null]),
        new("Replica Conqueror's Efficiency", "Crimson Jewel", false, 20, ["SkillEffectDurationUniqueJewel44", "MaximumRageUnique__2", "ManaCostTotalNonChannelledUnique__1__"], [null, null, null]),
        new("Replica Fragility", "Crimson Jewel", true, 20, ["GainEnduranceChargeEverySecondUnique__1"], [null]),
        new("Replica Pacifism", "Viridian Jewel", true, 20, ["GainFrenzyChargeEverySecondUnique__1"], [null]),
        new("Replica Powerlessness", "Cobalt Jewel", true, 20, ["GainPowerChargeEverySecondUnique__1"], [null]),
        new("Replica Reckless Defence", "Cobalt Jewel", false, 20, ["SpellBlockPercentageUnique__1", "AdditionalBlockUnique__1", "ChanceToBeFrozenShockedIgnitedUnique__1"], [null, null, null]),
        new("Soul's Wick", "Cobalt Jewel", true, 20, ["MaximumMinionCountUnique__1__", "RaiseSpectreManaCostUnique__1_", "SpectreCriticalStrikeChanceUnique__1", "SpectreHaveBaseDurationUnique__1"], [null, null, null, null]),
        new("Stormshroud", "Viridian Jewel", false, 20, ["LightningResistUnique__28", "ShockAvoidanceAllElementalAilmentsUnique__1"], [null, null]),
        new("Survival Instincts", "Viridian Jewel", false, 20, ["FlaskChargesUniqueJewel___8", "FlaskDurationUniqueJewel_____8", "ItemLimitUniqueJewel9"], [null, null, null]),
        new("To Dust", "Cobalt Jewel", true, 20, ["SkeletonDurationUniqueJewel1_", "MinionDamageUniqueJewel1", "MaximumMinionCountUniqueJewel1"], [null, null, null]),
        new("Unstable Payload", "Cobalt Jewel", false, 20, ["TrapTriggerTwiceChanceUnique__1"], [null]),
        new("Vaal Sentencing", "Cobalt Jewel", true, 20, ["VaalSkillCriticalStrikeChanceCorruptedJewel6"], [null]),
        new("Warlord's Reach", "Crimson Jewel", false, 20, ["AttackDamageUniqueJewel42", "IncreasedMeleeWeaponAndUnarmedRangeUniqueJewel42"], [null, null]),
        new("Weight of Sin", "Viridian Jewel", true, 20, ["IncreasedChaosDamageUniqueCorruptedJewel2", "ReducedMovementVelocityUniqueCorruptedJewel2_"], [null, null]),
    ];

    internal static readonly Entry[] SeedSpecial =
    [
        new("Rational Doctrine", "Cobalt Jewel", false, 1, ["ConsecratedGroundStationarySTRHighestUnique__1", "ProfaneGroundCriticalStrikeINTHighestUnique__1", "ConsecratedGroundLingersUnique__1", "ProfaneGroundLingersUnique__1"], [null, null, null, null]),
    ];

    public static Entry[] Global => GameDataCatalog.UniqueJewelGlobals;
    public static Entry[] Special => GameDataCatalog.UniqueJewelSpecials;

    public static Entry? Find(string name) =>
        Global.FirstOrDefault(entry => string.Equals(entry.Name, name, StringComparison.OrdinalIgnoreCase))
        ?? Special.FirstOrDefault(entry => string.Equals(entry.Name, name, StringComparison.OrdinalIgnoreCase));

    public static string[] Names =>
        [.. Global.Concat(Special).Select(entry => entry.Name).Distinct(StringComparer.OrdinalIgnoreCase)];
}
