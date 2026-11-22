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
        AdditionalPlotChance = mods.Get("map_harvest_additional_plot_chance_%"),
        AdditionalPlots = mods.Get("map_harvest_num_additional_plots"),
        RedWeight = mods.Get("map_harvest_red_plot_chance_+%"),
        GreenWeight = mods.Get("map_harvest_green_plot_chance_+%"),
        BlueWeight = mods.Get("map_harvest_blue_plot_chance_+%"),
        ColourOverride = mods.Get("map_harvest_seeds_1_of_every_2_plot_type_override"),
        Tier4ChanceIncrease = mods.Get("map_harvest_boss_encounter_chance_+%"),
        Tier4FlatChance = mods.Get("map_harvest_plots_%_chance_to_spawn_tier4_seed"),
        Tier3ChanceIncrease = mods.Get("map_harvest_t3_chance_+%"),
        AlwaysHighTier = mods.Get("map_harvest_always_high_tier_seeds") != 0,
        CropRotation = mods.Get("atlas_keystone_crop_rotation") != 0,
        AtLeastTier2 = mods.Get("map_harvest_seeds_are_at_least_t2") != 0,
        Tier2UpgradeChance = mods.Get("map_harvest_seed_t2_upgrade_%_chance"),
        Tier3UpgradeChance = mods.Get("map_harvest_seed_t3_upgrade_%_chance"),
        FaridunChance = mods.Get("map_harvest_%_chance_for_faridun_seed"),
        MemoryLinePlots = mods.Get("memory_line_num_harvest_plots"),
        BigHarvest = mods.Get("memory_line_big_harvest") != 0,
        MixedColours = mods.Get("map_harvest_mixed_seed_colours") != 0,
        NotWitherChance = mods.Get("map_harvest_chance_for_other_plot_to_not_wither_%"),
        AdditionalMonsterChance = mods.Get("map_harvest_seeds_%_chance_to_spawn_additional_monster"),
        DuplicatedMonsterChance = mods.Get("map_harvest_seeds_%_chance_to_spawn_duplicated_monsters"),
        MonsterMoreLife = mods.Get("map_harvest_monster_life_+%_final_from_sextant"),
        PackSize = mods.PackSize,
        ExperienceIncrease = mods.Get("map_harvest_monster_granted_experience_+%"),
        LifeforceIncrease = mods.Get("map_harvest_lifeforce_dropped_+%"),
        DoubleLifeforce = mods.Get("map_harvest_double_lifeforce_dropped") != 0,
        DuplicateLifeforceChance = mods.Get("map_harvest_%_chance_to_duplicate_lifeforce"),
        Quantity = mods.Quantity,
        RandomCropsInstead = mods.Get("map_harvest_monsters_grant_random_crops_instead") != 0,
    };
}
