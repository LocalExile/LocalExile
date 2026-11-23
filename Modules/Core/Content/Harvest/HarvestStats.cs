namespace GameServer.Modules.Harvest;

/// <summary>
/// The Sacred Grove's stats, taken once per instance from the map's composed area stats (<c>Game.MapModsOf</c>: the map's
/// mods, the Atlas passives with their small-node scaling, scarabs, sextants and Idols, all in one sum). Nothing here
/// reads allocated Atlas nodes: a passive, an Idol and a scarab that grant the same stat act the same.
/// Crop Rotation is read from <c>atlas_keystone_crop_rotation</c> (Stats row 19304, granted by both the Atlas keystone
/// and <c>UniqueMapRelicCropRotation</c>). Not read: <c>map_harvest_seeds_are_tier_1</c> and
/// <c>map_harvest_seeds_of_other_colours_have_chance_to_upgrade_on_completing_plot</c>; no 3.29 passive or mod grants
/// them, and the map load's "harvest" league block (<c>MapAreaStats</c>) carries both on maps whose groves still show
/// Tier 2–4 seeds.
/// </summary>
public sealed record HarvestStats
{
    public static readonly HarvestStats None = new();

    // Crops (GroveCrops).
    public int AdditionalPlotChance { get; init; }
    public int AdditionalPlots { get; init; }
    public int RedWeight { get; init; }
    public int GreenWeight { get; init; }
    public int BlueWeight { get; init; }
    /// <summary>map_harvest_seeds_1_of_every_2_plot_type_override: one crop of each pair has this colour (1–3), or 0.</summary>
    public int ColourOverride { get; init; }
    public int Tier4ChanceIncrease { get; init; }
    /// <summary>map_harvest_plots_%_chance_to_spawn_tier4_seed: a flat per-crop Tier 4 chance on top of the level's.</summary>
    public int Tier4FlatChance { get; init; }
    public int Tier3ChanceIncrease { get; init; }
    public bool AlwaysHighTier { get; init; }
    /// <summary>atlas_keystone_crop_rotation: crops start at Tier 1; completing a crop can upgrade idle crops of other colours.</summary>
    public bool CropRotation { get; init; }
    public bool AtLeastTier2 { get; init; }
    public int Tier2UpgradeChance { get; init; }
    public int Tier3UpgradeChance { get; init; }
    /// <summary>map_harvest_%_chance_for_faridun_seed: each Tier 3 seed's chance to be its colour's Corpse-Grown seed.</summary>
    public int FaridunChance { get; init; }

    // The memory-line grove (MapGiantHarvest, the Overgrown Grove's mod).
    /// <summary>memory_line_num_harvest_plots: how many of the memory-line grove's sites hold a crop.</summary>
    public int MemoryLinePlots { get; init; }
    public bool BigHarvest { get; init; }
    /// <summary>map_harvest_mixed_seed_colours: a crop's seeds take any of the three colours.</summary>
    public bool MixedColours { get; init; }

    // Activation and monsters.
    public int NotWitherChance { get; init; }
    public int AdditionalMonsterChance { get; init; }
    public int DuplicatedMonsterChance { get; init; }
    public int MonsterMoreLife { get; init; }
    public int PackSize { get; init; }

    // Lifeforce, experience and the rest (read by the drop and experience rules).
    public int ExperienceIncrease { get; init; }
    public int LifeforceIncrease { get; init; }
    public bool DoubleLifeforce { get; init; }
    public int DuplicateLifeforceChance { get; init; }
    public int Quantity { get; init; }
    public bool RandomCropsInstead { get; init; }

    public static HarvestStats From(MapMods? mods) => mods is null ? None : new()
    {
        AdditionalPlotChance = mods.Get(Stat.MapHarvestAdditionalPlotChancePct),
        AdditionalPlots = mods.Get(Stat.MapHarvestNumAdditionalPlots),
        RedWeight = mods.Get(Stat.MapHarvestRedPlotChancePlusPct),
        GreenWeight = mods.Get(Stat.MapHarvestGreenPlotChancePlusPct),
        BlueWeight = mods.Get(Stat.MapHarvestBluePlotChancePlusPct),
        ColourOverride = mods.Get(Stat.MapHarvestSeeds1OfEvery2PlotTypeOverride),
        Tier4ChanceIncrease = mods.Get(Stat.MapHarvestBossEncounterChancePlusPct),
        Tier4FlatChance = mods.Get(Stat.MapHarvestPlotsPctChanceToSpawnTier4Seed),
        Tier3ChanceIncrease = mods.Get(Stat.MapHarvestT3ChancePlusPct),
        AlwaysHighTier = mods.Get(Stat.MapHarvestAlwaysHighTierSeeds) != 0,
        CropRotation = mods.Get(Stat.AtlasKeystoneCropRotation) != 0,
        AtLeastTier2 = mods.Get(Stat.MapHarvestSeedsAreAtLeastT2) != 0,
        Tier2UpgradeChance = mods.Get(Stat.MapHarvestSeedT2UpgradePctChance),
        Tier3UpgradeChance = mods.Get(Stat.MapHarvestSeedT3UpgradePctChance),
        FaridunChance = mods.Get(Stat.MapHarvestPctChanceForFaridunSeed),
        MemoryLinePlots = mods.Get(Stat.MemoryLineNumHarvestPlots),
        BigHarvest = mods.Get(Stat.MemoryLineBigHarvest) != 0,
        MixedColours = mods.Get(Stat.MapHarvestMixedSeedColours) != 0,
        NotWitherChance = mods.Get(Stat.MapHarvestChanceForOtherPlotToNotWitherPct),
        AdditionalMonsterChance = mods.Get(Stat.MapHarvestSeedsPctChanceToSpawnAdditionalMonster),
        DuplicatedMonsterChance = mods.Get(Stat.MapHarvestSeedsPctChanceToSpawnDuplicatedMonsters),
        MonsterMoreLife = mods.Get(Stat.MapHarvestMonsterLifePlusPctFinalFromSextant),
        PackSize = mods.PackSize,
        ExperienceIncrease = mods.Get(Stat.MapHarvestMonsterGrantedExperiencePlusPct),
        LifeforceIncrease = mods.Get(Stat.MapHarvestLifeforceDroppedPlusPct),
        DoubleLifeforce = mods.Get(Stat.MapHarvestDoubleLifeforceDropped) != 0,
        DuplicateLifeforceChance = mods.Get(Stat.MapHarvestPctChanceToDuplicateLifeforce),
        Quantity = mods.Quantity,
        RandomCropsInstead = mods.Get(Stat.MapHarvestMonstersGrantRandomCropsInstead) != 0,
    };
}
