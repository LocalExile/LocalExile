using GameServer.Core.Accounts;
using GameServer.Core.Protocol.Instance;

/// <summary>
/// The area stats a map instance is played with (InstanceEvents.AreaStats; S→C 0x1d's first stat list, which the client
/// copies to the world stats and its level generator reads): composed from the league, the map item and the device's
/// fragments, the character's active atlas preset and per-tier constants, as the server's map loads carry them:
/// area = atlas (global::AtlasStats.Compose) + fragments (unscaled) + explicit map mods × (100 + e) / 100 truncated each
/// + implicits (unscaled) + derived counts + the tier block + league blocks, e = global::AtlasStats.ModEffect. The rarity-gated
/// pack counts and Meticulous Appraiser's quantity are resolved in the list, so its readers need no rules.
/// Not composed: the account's constants (atlas completion bonus, voidstone, Maven, astrolabe, uber map unlocks), the
/// activated atlas node, the boss option and the server's own rolls (Beyond, the influence type, a corrupted map's
/// random notables).
/// </summary>
public static partial class Game
{
    // The per-tier block of a map load, the same on every map of the tier: map_hidden_monster_life_+%_final by tier
    // 1..16, map_hidden_monster_damage_+%_final = tier − 1 (none on tier 1), map_atlas_information = 6912 + tier.
    static readonly int[] MapHiddenLife = [0, 5, 9, 13, 19, 24, 30, 36, 43, 49, 55, 61, 68, 77, 87, 98, 109];

    // The league blocks a map load carries when the atlas tree holds any node of a family (the node id's atlas_<family>_).
    static readonly Dictionary<string, (string Stat, int Value)[]> MapLeagueBlocks = new()
    {
        ["blight"] = [("blight_path_piece_length_modifier_%", 20), ("map_blight_monsters_damage_taken_from_player_sources_+%_final", -75),
            ("map_blight_tower_damage_+%_final_from_atlas_passive", 300)],
        ["alva"] = [("map_incursion_league", 1)],
        ["harvest"] = [("map_harvest_seeds_are_tier_1", 1), ("map_harvest_seeds_of_other_colours_have_chance_to_upgrade_on_completing_plot", 1)],
    };

    /// <summary>InstanceEvents.AreaStats: the stats of the instance <paramref name="request"/> opens.</summary>
    public static IReadOnlyDictionary<uint, int>? MapAreaStats(AreaStatsRequest request)
    {
        bool deepwater = request.League?.Contains("Deepwater", StringComparison.OrdinalIgnoreCase) == true;
        if (request.Map is not { } map) return ComposeMapAreaStats(null, 0, new Dictionary<uint, int>(), new HashSet<string>(), -1, deepwater);
        // The atlas: the active preset's nodes (HardMode vectors in a HardMode league), composed by the tree's rules.
        var presets = CharacterSaves.Load(map.Owner)?.AtlasPassivePresets ?? [];
        int preset = AtlasActivePreset(map.Owner, presets);
        var catalog = ServerLeagues.Resolve(null, request.League, LeagueCatalog.Startup).HardMode
            ? HardModeAtlasPassives : AtlasPassiveNodes;
        var nodes = preset >= 0 ? presets[preset].Distinct().Select(id => catalog.Find(id)).OfType<AtlasPassives.Node>().ToList() : [];
        var families = nodes.Select(global::AtlasStats.Family).Where(f => f.Length > 0).ToHashSet(StringComparer.Ordinal);
        var atlas = global::AtlasStats.Compose(nodes);
        return ComposeMapAreaStats(map.Mods, map.Area.Tier, atlas, families, preset, deepwater);
    }

    public static Dictionary<uint, int> ComposeMapAreaStats(MapMods? mods, int tier, IReadOnlyDictionary<uint, int> atlas,
        IReadOnlySet<string> families, int preset, bool deepwater)
    {
        var stats = new Dictionary<uint, int>();
        void Add(string name, int value)
        {
            if (Stats.Key(name) is uint key) stats[key] = stats.GetValueOrDefault(key) + value;
        }
        void Set(string name, int value)
        {
            if (Stats.Key(name) is uint key) stats[key] = value;
        }
        // Every load of a character in a Deepwater league carries the league's flag.
        if (deepwater) Add("map_deepwater_league", 1);
        if (mods is null) return stats;
        foreach (var (key, value) in atlas) stats[key] = stats.GetValueOrDefault(key) + value;
        if (preset >= 0) Add("map_active_atlas_passive_tree_index", preset + 1);
        int effect = global::AtlasStats.ModEffect(atlas, mods.MapRarity == ItemRarity.Unique, mods.ExplicitMods.Count, mods.FragmentCount);
        // Each explicit mod's values scaled by the mod effect and truncated on their own; implicits and fragments unscaled.
        foreach (var mod in mods.ExplicitMods)
            foreach (var (id, value) in mod) if (value != 0) Add(id, (int)Math.Clamp((long)value * (100L + effect) / 100, int.MinValue, int.MaxValue));
        foreach (var (id, value) in mods.Implicit) if (value != 0) Add(id, value);
        foreach (var (id, value) in mods.Fragments) if (value != 0) Add(id, value);
        // Older modules construct a flat MapMods. Preserve their stats when there is no breakdown to compose.
        if (mods.ExplicitMods.Count == 0 && mods.Implicit.Count == 0 && mods.Fragments.Count == 0)
            foreach (var (id, value) in mods.Stats) if (value != 0) Add(id, value);
        // The scarab's legacy stat is not a CoreLeagues forced-count input. Publish its count in the input both
        // generators read, before the instance freezes its area stats; room placement and shrine ownership stay shared.
        if (Stat.MapLeaguestoneAdditionalShrines is var shrineKey
            && stats.GetValueOrDefault(shrineKey) > 0 && Stat.MapNumExtraShrines is var countKey)
            stats[countKey] = (int)Math.Min(int.MaxValue,
                (long)stats.GetValueOrDefault(countKey) + stats[shrineKey]);
        // "Your Rare Maps contain …" (no area text): only on a map of that rarity, as the plain count.
        foreach (var (rarity, kind) in new[] { (ItemRarity.Rare, "rare"), (ItemRarity.Magic, "magic"), (ItemRarity.Normal, "normal") })
            if (Stats.Key($"map_contains_x_additional_{kind}_packs_if_{kind}") is uint key && stats.Remove(key, out int value)
                && mods.MapRarity == rarity && value != 0)
                Add($"map_contains_x_additional_{kind}_packs", value);
        // Meticulous Appraiser: the quantity modifiers apply to rarity instead, at 300%.
        if (Stat.MapItemDropQuantityInsteadAppliesToMapItemDropRarityAt3TimesValue is var appraiser
            && stats.GetValueOrDefault(appraiser) != 0)
        {
            int quantity = 0;
            foreach (string id in new[] { "map_item_drop_quantity_+%", "map_implicit_item_drop_quantity_+%", "map_unscaled_item_drop_quantity_+%" })
                if (Stats.Key(id) is uint key && stats.Remove(key, out int value)) quantity += value;
            Add("map_item_drop_rarity_+%", 3 * quantity);
        }
        // The derived counts.
        Set("map_rarity", (int)mods.MapRarity);
        Set("map_number_of_explicit_mods", mods.ExplicitMods.Count);
        Set("total_number_of_map_mods", mods.ExplicitMods.Count + mods.FragmentCount);
        if (mods.Corrupted) Set("map_is_corrupted", 1);
        if (mods.FragmentCount > 0) Set("map_num_fragments", mods.FragmentCount);
        if (mods.ScarabCount > 0)
        {
            Set("map_total_scarab_tiers", mods.ScarabCount);
            Set("map_num_scarabs", mods.ScarabCount);
            foreach (var (kind, count) in mods.ScarabKinds) Set($"map_{kind}_scarab_count", count);
        }
        // The tier block and the device.
        if (tier is >= 1 and <= 16)
        {
            Set("map_tier", tier);
            Set("map_atlas_information", 6912 + tier);
            Set("map_hidden_monster_life_+%_final", MapHiddenLife[tier]);
            if (tier > 1) Set("map_hidden_monster_damage_+%_final", tier - 1);
        }
        Set("map_device_type", 1);
        // The league blocks of the tree's families; a flag set by several sources is set once.
        foreach (string family in families)
            if (MapLeagueBlocks.TryGetValue(family, out var block))
                foreach (var (stat, value) in block) Add(stat, value);
        if (Stat.MapIncursionLeague is var incursion && stats.GetValueOrDefault(incursion) > 1) stats[incursion] = 1;
        // The three a map load always carries, 0 when nothing adds to them.
        foreach (string always in new[] { "map_item_drop_quantity_+%", "map_item_drop_rarity_+%", "map_pack_size_+%" })
            if (Stats.Key(always) is uint key) stats.TryAdd(key, 0);
        return stats;
    }
}
