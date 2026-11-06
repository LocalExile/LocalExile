using System.Text.Json;

/// <summary>
/// The area stats an instance is played with: S→C 0x1d's first stat list (1-based Stats.dat ids), which the client copies
/// to the world stats and the level generator reads (its league rolls, the Vaal side area, shrines, the Deepwater and
/// Betrayal content, re/011 §3b), so the Gateway generates the level with the same list (<see cref="ServerLevels"/>).
/// The server composes it from the league, the map item and the device's fragments, the atlas tree and per-tier
/// constants: fitted on the server corpus (Agents/docs/re/011 §8) as
/// area = atlas + fragments (unscaled) + explicit map mods × (100 + e) / 100 truncated each + implicits (unscaled)
/// + derived counts + the tier block + league blocks, e = the atlas's map_mod_effect_+%_for_non_unique_maps.
/// Not composed: the account's constants (quantity base, chisel, astrolabe, uber map … differ per account and league),
/// map_area_underlevelled (its rule is not known), the activated atlas node and the server's own rolls (Beyond, the
/// influence type, the Deepwater pack type, a corrupted map's random notables).
/// </summary>
static class AreaStats
{
    // The per-tier block of a map load, the same on every map of the tier in the corpus: map_hidden_monster_life_+%_final
    // by tier 1..16, map_hidden_monster_damage_+%_final = tier − 1 (none on tier 1), map_atlas_information = 6912 + tier.
    static readonly int[] HiddenLife = [0, 5, 9, 13, 19, 24, 30, 36, 43, 49, 55, 61, 68, 77, 87, 98, 109];

    // The league blocks the server adds when the tree holds any node of a family (the node id's atlas_<family>_ prefix).
    static readonly Dictionary<string, (string Stat, int Value)[]> LeagueBlocks = new()
    {
        ["blight"] = [("blight_path_piece_length_modifier_%", 20), ("map_blight_monsters_damage_taken_from_player_sources_+%_final", -75),
            ("map_blight_tower_damage_+%_final_from_atlas_passive", 300)],
        ["alva"] = [("map_incursion_league", 1)],
        ["harvest"] = [("map_harvest_seeds_are_tier_1", 1), ("map_harvest_seeds_of_other_colours_have_chance_to_upgrade_on_completing_plot", 1)],
    };

    sealed record AtlasNode(string Id, IReadOnlyList<(uint Key, int Value)> Stats);

    // The Atlas passive nodes by PassiveSkillGraphId (PassiveSkills.json, the atlas_ rows; stat keys 1-based).
    static readonly Lazy<Dictionary<ushort, AtlasNode>> Atlas = new(() =>
    {
        var nodes = new Dictionary<ushort, AtlasNode>();
        string path = Path.Combine("data", "tables", "PassiveSkills.json");
        if (!File.Exists(path)) return nodes;
        try
        {
            using var doc = GameTables.Open(path);
            foreach (var row in doc.RootElement.EnumerateArray())
            {
                string id = row.GetProperty("Id").GetString() ?? "";
                if (!id.StartsWith("atlas_", StringComparison.Ordinal)) continue;
                int graphId = row.GetProperty("PassiveSkillGraphId").GetInt32();
                if (graphId is <= 0 or > ushort.MaxValue) continue;
                var keys = row.GetProperty("Stats").EnumerateArray().Select(s => s.GetInt32()).ToList();
                var stats = new List<(uint, int)>();
                for (int i = 0; i < keys.Count && i < 5; i++) stats.Add(((uint)keys[i] + 1, row.GetProperty($"Stat{i + 1}Value").GetInt32()));
                nodes.TryAdd((ushort)graphId, new AtlasNode(id, stats));
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException or KeyNotFoundException) { }
        return nodes;
    });

    /// <summary>The stats of the instance <paramref name="character"/> enters at <paramref name="areaHash"/>.</summary>
    public static Dictionary<uint, int> Compose(string character, ushort areaHash, string? league)
    {
        bool deepwater = league is not null && league.Contains("Deepwater", StringComparison.OrdinalIgnoreCase);
        if (MapDevice.OpenOf(character) is not { } open || open.Area.Hash16 != areaHash)
            return Compose(null, 0, new Dictionary<uint, int>(), new HashSet<string>(), -1, deepwater, Program.Stats);
        // The atlas: the character's preset's node stats, and the families its nodes belong to.
        var presets = CharacterSaves.Load(character)?.AtlasPassivePresets ?? [];
        int preset = presets.FindIndex(p => p.Count > 0);
        var atlas = new Dictionary<uint, int>();
        var families = new HashSet<string>(StringComparer.Ordinal);
        if (preset >= 0)
            foreach (ushort node in presets[preset])
                if (Atlas.Value.TryGetValue(node, out var atlasNode))
                {
                    foreach (var (key, value) in atlasNode.Stats) atlas[key] = atlas.GetValueOrDefault(key) + value;
                    var parts = atlasNode.Id.Split('_');
                    if (parts.Length > 1) families.Add(parts[1]);
                }
        return Compose(open.Mods, open.Area.Tier, atlas, families, preset, deepwater, Program.Stats);
    }

    /// <summary>The composition from its inputs: the map's mods (null: not a map instance), its tier, the atlas preset's
    /// stat sum and node families, the preset's index (−1: none), the league flag; <paramref name="catalog"/> names the keys.</summary>
    public static Dictionary<uint, int> Compose(MapMods? mods, int tier, IReadOnlyDictionary<uint, int> atlas, IReadOnlySet<string> families,
        int preset, bool deepwater, StatCatalog catalog)
    {
        var stats = new Dictionary<uint, int>();
        void Add(string name, int value)
        {
            if (catalog.Key(name) is uint key) stats[key] = stats.GetValueOrDefault(key) + value;
        }
        void Set(string name, int value)
        {
            if (catalog.Key(name) is uint key) stats[key] = value;
        }
        // Every load of a character in the current league carries the league's flag (map_deepwater_league 1 in 3.29).
        if (deepwater) Add("map_deepwater_league", 1);
        if (mods is null) return stats;
        foreach (var (key, value) in atlas) stats[key] = stats.GetValueOrDefault(key) + value;
        if (preset >= 0) Add("map_active_atlas_passive_tree_index", preset + 1);
        int effect = catalog.Key("map_mod_effect_+%_for_non_unique_maps") is uint effectKey ? stats.GetValueOrDefault(effectKey) : 0;
        // The map's explicit mods, each mod's value scaled by the mod effect and truncated toward zero on its own; the
        // implicits, the quality and the fragments' mods unscaled.
        foreach (var mod in mods.ExplicitMods)
            foreach (var (id, value) in mod) if (value != 0) Add(id, value * (100 + effect) / 100);
        foreach (var (id, value) in mods.Implicit) if (value != 0) Add(id, value);
        foreach (var (id, value) in mods.Fragments) if (value != 0) Add(id, value);
        // The derived counts.
        Set("map_rarity", (int)mods.MapRarity);
        Set("map_number_of_explicit_mods", mods.ExplicitCount);
        Set("total_number_of_map_mods", mods.ExplicitCount + mods.FragmentCount);
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
            Set("map_hidden_monster_life_+%_final", HiddenLife[tier]);
            if (tier > 1) Set("map_hidden_monster_damage_+%_final", tier - 1);
        }
        Set("map_device_type", 1);
        // The league blocks of the tree's families; a flag set by several sources is set once.
        foreach (string family in families)
            if (LeagueBlocks.TryGetValue(family, out var block))
                foreach (var (stat, value) in block) Add(stat, value);
        if (catalog.Key("map_incursion_league") is uint incursion && stats.GetValueOrDefault(incursion) > 1) stats[incursion] = 1;
        // The three a map load always carries, 0 when nothing adds to them.
        foreach (string always in new[] { "map_item_drop_quantity_+%", "map_item_drop_rarity_+%", "map_pack_size_+%" })
            if (catalog.Key(always) is uint key) stats.TryAdd(key, 0);
        return stats;
    }

    /// <summary>The composed list with another's entries added (the module's league stats: the ascendancy family).</summary>
    public static IReadOnlyDictionary<uint, int>? Merge(IReadOnlyDictionary<uint, int>? a, IReadOnlyDictionary<uint, int>? b)
    {
        if (a is not { Count: > 0 }) return b;
        if (b is not { Count: > 0 }) return a;
        var merged = new Dictionary<uint, int>(a);
        foreach (var (key, value) in b) merged[key] = merged.GetValueOrDefault(key) + value;
        return merged;
    }
}
