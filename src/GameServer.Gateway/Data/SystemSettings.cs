using GameServer.Core.World;
using GameServer.Storage;

/// <summary>
/// Numeric server settings by key: world.db's <c>system_settings</c> rows, read at startup and on <c>/reload</c>. A key
/// with no row reads 0. Every row is checked on load; a bad value keeps the previous settings.
/// </summary>
public sealed class SystemSettings
{
    static readonly Dictionary<string, string> SocketKeys = new(StringComparer.Ordinal)
    {
        ["Wand"] = "socket_max_wand",
        ["One Hand Sword"] = "socket_max_one_hand_sword",
        ["Thrusting One Hand Sword"] = "socket_max_thrusting_one_hand_sword",
        ["One Hand Axe"] = "socket_max_one_hand_axe",
        ["One Hand Mace"] = "socket_max_one_hand_mace",
        ["Sceptre"] = "socket_max_sceptre",
        ["Dagger"] = "socket_max_dagger",
        ["Rune Dagger"] = "socket_max_rune_dagger",
        ["Claw"] = "socket_max_claw",
        ["Two Hand Sword"] = "socket_max_two_hand_sword",
        ["Two Hand Axe"] = "socket_max_two_hand_axe",
        ["Two Hand Mace"] = "socket_max_two_hand_mace",
        ["Bow"] = "socket_max_bow",
        ["Staff"] = "socket_max_staff",
        ["Warstaff"] = "socket_max_warstaff",
        ["Body Armour"] = "socket_max_body_armour",
        ["Helmet"] = "socket_max_helmet",
        ["Gloves"] = "socket_max_gloves",
        ["Boots"] = "socket_max_boots",
        ["Shield"] = "socket_max_shield",
        ["Belt"] = "socket_max_belt",
    };

    static readonly IReadOnlyDictionary<string, double> Empty = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

    public static SystemSettings Default { get; } = new();

    IReadOnlyDictionary<string, double> _values = Empty;

    public IReadOnlyDictionary<string, double> Values => Volatile.Read(ref _values);

    public double Get(string key) =>
        Values.GetValueOrDefault(key, 0);

    public double GetOrDefault(string key, double fallback) =>
        Values.TryGetValue(key, out double value) ? value : fallback;

    public int GetInt(string key) => (int)Math.Round(Get(key), MidpointRounding.AwayFromZero);

    public int GetIntOrDefault(string key, int fallback) =>
        Values.TryGetValue(key, out double value) ? (int)Math.Round(value, MidpointRounding.AwayFromZero) : fallback;

    public int SocketMax(string itemClass) =>
        SocketKeys.TryGetValue(itemClass, out string? key)
            ? Math.Clamp(GetInt(key), 0, SocketLayout.MaximumSockets)
            : 0;

    /// <summary>The most prefixes/suffixes one rare item may have. Jewels keep their own, lower defaults.</summary>
    public const int MaxRareAffixesPerSide = 6;
    public int RareMaxPrefixes => Math.Clamp(GetInt("rare_max_prefixes"), 0, MaxRareAffixesPerSide);
    public int RareMaxSuffixes => Math.Clamp(GetInt("rare_max_suffixes"), 0, MaxRareAffixesPerSide);
    public int RareJewelMaxPrefixes => Math.Clamp(GetInt("rare_jewel_max_prefixes"), 0, MaxRareAffixesPerSide);
    public int RareJewelMaxSuffixes => Math.Clamp(GetInt("rare_jewel_max_suffixes"), 0, MaxRareAffixesPerSide);

    public int PassivePointsPerLevel => Math.Clamp(GetInt("passive_points_per_level"), 1, 100);

    public double GoldDropMultiplier => Math.Max(0, Get("gold_drop_multiplier"));
    public double GoldChanceMultiplier => Math.Clamp(Get("gold_drop_chance_multiplier"), 0, 100);
    public double GoldAutoLootRadius => Math.Clamp(Get("gold_auto_loot_radius"), 0, 100);

    /// <summary>One projectile movement instance's maximum flight, in cells (10 cells = 1 m): 150 = the client's 15 m
    /// cap. Fork, chain and split start a new movement instance with a fresh budget.</summary>
    public int ProjectileMaximumRangeCells => Math.Clamp(GetInt("projectile_maximum_range_cells"), 1, 10_000);

    public double DropMultiplier(string category) => category switch
    {
        "currency" => Math.Max(0, Get("drop_multiplier_currency")),
        "equipment" => Math.Max(0, Get("drop_multiplier_equipment")),
        "flasks" => Math.Max(0, Get("drop_multiplier_flasks")),
        "gems" => Math.Max(0, Get("drop_multiplier_gems")),
        "maps" => Math.Max(0, Get("drop_multiplier_maps")),
        "unique_rarity" => Math.Max(0, Get("drop_multiplier_unique_rarity")),
        _ => 1,
    };

    public double MonsterPackDensityMultiplier => Math.Clamp(Get("monster_pack_density_multiplier"), 0, 100);
    public double MonsterPackSizeMultiplier => Math.Clamp(Get("monster_pack_size_multiplier"), 0, 50);
    public double ExperienceMultiplier => Math.Clamp(Get("experience_multiplier"), 0, 1000);
    public int MaxLevel => Math.Clamp(GetInt("max_level"), 1, Progression.MaxLevelCeiling);

    public int TattooTotalLimit => Math.Max(0, GetInt("tattoo_total_limit"));
    public int TattooGroupLimit => Math.Max(0, GetInt("tattoo_group_limit"));
    public int TimelessJewelLimit => Math.Max(0, GetInt("timeless_jewel_limit"));
    public int ImpossibleEscapeLimit => Math.Max(0, GetInt("impossible_escape_limit"));
    public int RadiusJewelDefaultLimit => Math.Max(0, GetInt("radius_jewel_default_limit"));
    public int GracePeriodSeconds => Math.Clamp(GetInt("grace_period_seconds"), 0, 3600);

    // Campaign loot, drop scaling, and vendor pacing (retail 3.29 fallbacks when unseeded in tests).
    public double DropNormalChancePct => Math.Clamp(GetOrDefault("drop_normal_chance_pct", 16.0), 0, 100);
    public double DropWeightCurrency => Math.Clamp(GetOrDefault("drop_weight_currency", 40.0), 0, 1000);
    public double DropWeightGems => Math.Clamp(GetOrDefault("drop_weight_gems", 4.0), 0, 1000);
    public double DropWeightFlasks => Math.Clamp(GetOrDefault("drop_weight_flasks", 8.0), 0, 1000);
    public double DropWeightEquipment => Math.Clamp(GetOrDefault("drop_weight_equipment", 48.0), 0, 1000);
    public double DropNaturalQualityChancePct => Math.Clamp(GetOrDefault("drop_natural_quality_chance_pct", 10.0), 0, 100);
    public double DropSocketLinkScale => Math.Clamp(GetOrDefault("drop_socket_link_scale", 1.0), 0, 100);
    public double DropUniqueChanceMultiplier => Math.Clamp(GetOrDefault("drop_unique_chance_multiplier", 1.0), 0, 100);
    public bool DropPenaltyEnabled => GetIntOrDefault("drop_penalty_enabled", 1) != 0;
    public int DropPenaltyLevelOffset => Math.Clamp(GetIntOrDefault("drop_penalty_level_offset", 2), 0, 100);
    public double DropPenaltyPerLevelPct => Math.Clamp(GetOrDefault("drop_penalty_per_level_pct", 2.5), 0, 100);
    public int DropPenaltyCharacterLevelCap => Math.Clamp(GetIntOrDefault("drop_penalty_character_level_cap", 68), 1, 100);
    public double DropCampaignCardChancePct => Math.Clamp(GetOrDefault("drop_campaign_card_chance_pct", 0.5), 0, 100);
    public double DropCampaignMapChanceMultiplier => Math.Clamp(GetOrDefault("drop_campaign_map_chance_multiplier", 0.18), 0, 100);
    public double DropDivineVesselChancePct => Math.Clamp(GetOrDefault("drop_divine_vessel_chance_pct", 0.08), 0, 100);
    public bool DropFirstKillBossBonus => GetIntOrDefault("drop_first_kill_boss_bonus", 1) != 0;
    public int VendorRefreshLevels => Math.Clamp(GetIntOrDefault("vendor_refresh_levels", 1), 1, 100);

    // Shared monster balance & campaign pacing flags (retail 3.29 defaults).
    public bool MonsterSpeciesModsEnabled => GetIntOrDefault("monster_species_mods_enabled", 1) != 0;
    public bool MonsterEsEnabled => GetIntOrDefault("monster_es_enabled", 1) != 0;
    public bool MonsterArmourEnabled => GetIntOrDefault("monster_armour_enabled", 1) != 0;
    public int MonsterBaseCritChancePermyriad => Math.Clamp(GetIntOrDefault("monster_base_crit_chance_permyriad", 500), 0, 10_000);
    public double MonsterAilmentThresholdScaling => Math.Clamp(GetOrDefault("monster_ailment_threshold_scaling", 1.0), 0, 100);
    public double CampaignChestDensityMultiplier => Math.Clamp(GetOrDefault("campaign_chest_density_multiplier", 1.0), 0, 100);
    public bool CampaignDoorsEnabled => GetIntOrDefault("campaign_doors_enabled", 1) != 0;
    public double CampaignPackMagicChancePct => Math.Clamp(GetOrDefault("campaign_pack_magic_chance_pct", 12.0), 0, 100);
    public double CampaignPackRareChancePct => Math.Clamp(GetOrDefault("campaign_pack_rare_chance_pct", 6.0), 0, 100);

    public void Replace(IReadOnlyDictionary<string, double> values) =>
        Volatile.Write(ref _values, new Dictionary<string, double>(values, StringComparer.OrdinalIgnoreCase));

    public static void Reset()
    {
        Default.Replace(Empty);
        Progression.PassivePointsPerLevel = 1;
        Progression.ExperienceMultiplier = 1;
        Progression.MaxLevel = Progression.MaxLevelCeiling;
    }

    public static void Reload(GameDataStore store)
    {
        var loaded = store.LoadSettings().ToDictionary(row => row.Key, row => row.Value, StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in loaded)
            if (!Validate(key, value, loaded, out string? error))
                throw new InvalidOperationException($"system_settings {key} = {value}: {error}");
        Default.Replace(loaded);
        Progression.PassivePointsPerLevel = Default.PassivePointsPerLevel;
        Progression.ExperienceMultiplier = Default.ExperienceMultiplier;
        Progression.MaxLevel = Default.MaxLevel;
    }

    /// <summary>Whether a row's value is in its range; <paramref name="values"/> are the other rows loaded with it.</summary>
    public static bool Validate(string key, double value, IReadOnlyDictionary<string, double> values, out string? error)
    {
        error = null;
        if (key.StartsWith("delve_reward_", StringComparison.OrdinalIgnoreCase))
        {
            bool percentage = key.EndsWith("_pct", StringComparison.OrdinalIgnoreCase);
            if (!double.IsFinite(value) || value < (percentage ? 0 : 1) || value > (percentage ? 1000 : 2000)
                || (!percentage && value != Math.Round(value)))
                error = "Delve reward counts are integers 1..2000; percentages are finite numbers 0..1000";
            if (key == "delve_reward_min_items" && value > values.GetValueOrDefault("delve_reward_max_items", double.MaxValue)) error = "minimum exceeds maximum";
            if (key == "delve_reward_max_items" && value < values.GetValueOrDefault("delve_reward_min_items", 0)) error = "maximum is below minimum";
        }
        else if (key.StartsWith("socket_max_", StringComparison.OrdinalIgnoreCase))
        {
            if (value < 0 || value > SocketLayout.MaximumSockets || value != Math.Round(value))
                error = $"{key} is an integer 0..{SocketLayout.MaximumSockets}";
        }
        else if (key is "rare_max_prefixes" or "rare_max_suffixes" or "rare_jewel_max_prefixes" or "rare_jewel_max_suffixes")
        {
            if (value < 0 || value > MaxRareAffixesPerSide || value != Math.Round(value))
                error = $"{key} is an integer 0..{MaxRareAffixesPerSide}";
        }
        else if (key.Equals("passive_points_per_level", StringComparison.OrdinalIgnoreCase))
        {
            if (value < 1 || value > 100 || value != Math.Round(value))
                error = "passive_points_per_level is an integer 1..100";
        }
        else if (key.Equals("projectile_maximum_range_cells", StringComparison.OrdinalIgnoreCase))
        {
            if (value < 1 || value > 10_000 || value != Math.Round(value))
                error = "projectile_maximum_range_cells is an integer 1..10000 (cells; 10 cells = 1 m)";
        }
        else if (key.StartsWith("drop_multiplier_", StringComparison.OrdinalIgnoreCase))
        {
            if (!double.IsFinite(value) || value < 0 || value > 100)
                error = $"{key} is a number 0..100";
        }
        else if (key.Equals("gold_drop_multiplier", StringComparison.OrdinalIgnoreCase))
        {
            if (!double.IsFinite(value) || value < 0 || value > 10000)
                error = "gold_drop_multiplier is a number 0..10000";
        }
        else if (key.Equals("gold_drop_chance_multiplier", StringComparison.OrdinalIgnoreCase)
            || key.Equals("gold_auto_loot_radius", StringComparison.OrdinalIgnoreCase)
            || key.Equals("drop_scarab_chance_pct", StringComparison.OrdinalIgnoreCase)
            || key.Equals("drop_card_chance_pct", StringComparison.OrdinalIgnoreCase))
        {
            if (!double.IsFinite(value) || value < 0 || value > 100)
                error = $"{key} is a number 0..100";
        }
        else if (key.Equals("monster_pack_density_multiplier", StringComparison.OrdinalIgnoreCase))
        {
            if (!double.IsFinite(value) || value < 0 || value > 100)
                error = "monster_pack_density_multiplier is a number 0..100";
        }
        else if (key.Equals("monster_pack_size_multiplier", StringComparison.OrdinalIgnoreCase))
        {
            if (!double.IsFinite(value) || value < 0 || value > 50)
                error = "monster_pack_size_multiplier is a number 0..50";
        }
        else if (key.Equals("experience_multiplier", StringComparison.OrdinalIgnoreCase))
        {
            if (!double.IsFinite(value) || value < 0 || value > 1000)
                error = "experience_multiplier is a number 0..1000";
        }
        else if (key.Equals("max_level", StringComparison.OrdinalIgnoreCase))
        {
            if (value < 1 || value > Progression.MaxLevelCeiling || value != Math.Round(value))
                error = $"max_level is an integer 1..{Progression.MaxLevelCeiling}";
        }
        else if (key.Equals("grace_period_seconds", StringComparison.OrdinalIgnoreCase))
        {
            if (value < 0 || value > 3600 || value != Math.Round(value))
                error = "grace_period_seconds is an integer 0..3600";
        }
        else if (key is "tattoo_total_limit" or "tattoo_group_limit" or "timeless_jewel_limit"
            or "impossible_escape_limit" or "radius_jewel_default_limit")
        {
            if (value < 0 || value > 1000 || value != Math.Round(value))
                error = $"{key} is an integer 0..1000";
        }
        else if (key.Equals("drop_normal_chance_pct", StringComparison.OrdinalIgnoreCase)
            || key.Equals("drop_natural_quality_chance_pct", StringComparison.OrdinalIgnoreCase)
            || key.Equals("drop_penalty_per_level_pct", StringComparison.OrdinalIgnoreCase)
            || key.Equals("drop_campaign_card_chance_pct", StringComparison.OrdinalIgnoreCase)
            || key.Equals("drop_divine_vessel_chance_pct", StringComparison.OrdinalIgnoreCase)
            || key.Equals("campaign_pack_magic_chance_pct", StringComparison.OrdinalIgnoreCase)
            || key.Equals("campaign_pack_rare_chance_pct", StringComparison.OrdinalIgnoreCase))
        {
            if (!double.IsFinite(value) || value < 0 || value > 100)
                error = $"{key} is a number 0..100";
        }
        else if (key.Equals("drop_weight_currency", StringComparison.OrdinalIgnoreCase)
            || key.Equals("drop_weight_gems", StringComparison.OrdinalIgnoreCase)
            || key.Equals("drop_weight_flasks", StringComparison.OrdinalIgnoreCase)
            || key.Equals("drop_weight_equipment", StringComparison.OrdinalIgnoreCase))
        {
            if (!double.IsFinite(value) || value < 0 || value > 1000)
                error = $"{key} is a number 0..1000";
        }
        else if (key.Equals("drop_socket_link_scale", StringComparison.OrdinalIgnoreCase)
            || key.Equals("drop_unique_chance_multiplier", StringComparison.OrdinalIgnoreCase)
            || key.Equals("drop_campaign_map_chance_multiplier", StringComparison.OrdinalIgnoreCase)
            || key.Equals("campaign_chest_density_multiplier", StringComparison.OrdinalIgnoreCase)
            || key.Equals("monster_ailment_threshold_scaling", StringComparison.OrdinalIgnoreCase))
        {
            if (!double.IsFinite(value) || value < 0 || value > 100)
                error = $"{key} is a number 0..100";
        }
        else if (key.Equals("drop_penalty_enabled", StringComparison.OrdinalIgnoreCase)
            || key.Equals("drop_first_kill_boss_bonus", StringComparison.OrdinalIgnoreCase)
            || key.Equals("monster_species_mods_enabled", StringComparison.OrdinalIgnoreCase)
            || key.Equals("monster_es_enabled", StringComparison.OrdinalIgnoreCase)
            || key.Equals("monster_armour_enabled", StringComparison.OrdinalIgnoreCase)
            || key.Equals("campaign_doors_enabled", StringComparison.OrdinalIgnoreCase))
        {
            if (value is not (0 or 1))
                error = $"{key} is 0 or 1";
        }
        else if (key.Equals("drop_penalty_level_offset", StringComparison.OrdinalIgnoreCase))
        {
            if (value < 0 || value > 100 || value != Math.Round(value))
                error = "drop_penalty_level_offset is an integer 0..100";
        }
        else if (key.Equals("drop_penalty_character_level_cap", StringComparison.OrdinalIgnoreCase)
            || key.Equals("vendor_refresh_levels", StringComparison.OrdinalIgnoreCase))
        {
            if (value < 1 || value > 100 || value != Math.Round(value))
                error = $"{key} is an integer 1..100";
        }
        else if (key.Equals("monster_base_crit_chance_permyriad", StringComparison.OrdinalIgnoreCase))
        {
            if (value < 0 || value > 10_000 || value != Math.Round(value))
                error = "monster_base_crit_chance_permyriad is an integer 0..10000";
        }
        return error is null;
    }
}
