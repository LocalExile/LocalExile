using GameServer.Core;
using GameServer.Core.Accounts;
using GameServer.Core.Protocol.Instance;

/// <summary>
/// The area stats a map instance is played with (InstanceEvents.AreaStats; S→C 0x1d's first stat list, which the client
/// copies to the world stats and its level generator reads): composed from the league, the map item and the device's
/// fragments, the character's active atlas preset and per-tier constants, as the server's map loads carry them:
/// area = atlas (AtlasStats.Compose) + fragments (unscaled) + explicit map mods × (100 + e) / 100 truncated each
/// + implicits (unscaled) + derived counts + the tier block + league blocks, e = AtlasStats.ModEffect. The rarity-gated
/// pack counts and Meticulous Appraiser's quantity are resolved in the list, so its readers need no rules.
/// Not composed: the account's constants (atlas completion bonus, voidstone, Maven, astrolabe, uber map unlocks), the
/// activated atlas node and the server's own rolls (Beyond, the influence type, a corrupted map's random notables).
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

    // [inferred] The league blocks' families by stat: each stat a node of a block's family grants (both catalogues), left
    // out when nodes of two families grant it or a MapFragmentMods row's mod does (a scarab's own stats, such as the
    // Harvest Scarab's map_chance_for_area_%_to_contain_harvest, turn on no block). Idols carry the Atlas passives' stats,
    // so a map whose mods hold one of them gets the same block a node of that family gives.
    static readonly Lazy<Dictionary<string, string>> LeagueBlockStats = new(() =>
    {
        var fragmentStats = new HashSet<string>(StringComparer.Ordinal);
        var fragmentMods = new HashSet<int>();
        foreach (var row in ClientTables.TableOrEmpty("MapFragmentMods"))
            foreach (int mod in row.Ints("ModsKeys")) fragmentMods.Add(mod);
        if (fragmentMods.Count > 0)
            foreach (var row in ClientTables.TableOrEmpty("Mods"))
                if (fragmentMods.Contains(row.Index))
                    for (int i = 1; i <= 8 && row.IntOrNull($"StatsKey{i}") is int stat; i++) fragmentStats.Add(Stats.Name((uint)stat + 1));
        var families = new Dictionary<string, string>(StringComparer.Ordinal);
        var shared = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in AtlasPassiveNodes.Nodes.Concat(HardModeAtlasPassives.Value.Nodes))
        {
            string family = AtlasStats.Family(node);
            if (!MapLeagueBlocks.ContainsKey(family)) continue;
            foreach (var (key, _) in node.Stats)
            {
                string name = Stats.Name(key);
                if (fragmentStats.Contains(name)) continue;
                if (!families.TryAdd(name, family) && families[name] != family) shared.Add(name);
            }
        }
        foreach (string name in shared) families.Remove(name);
        return families;
    });

    /// <summary>InstanceEvents.AreaStats: the stats of the instance <paramref name="request"/> opens.</summary>
    public static IReadOnlyDictionary<uint, int>? MapAreaStats(AreaStatsRequest request)
    {
        if (request.Map is not { } map) return ComposeMapAreaStats(null, 0, new Dictionary<uint, int>(), new HashSet<string>(), -1);
        // Not an Atlas map: the mods' own stats, without the atlas, the tier block, its hidden life or the preset.
        if (!map.Atlas) return ComposeMapAreaStats(map.Mods, 0, new Dictionary<uint, int>(), new HashSet<string>(), -1);
        // The atlas: the active preset's nodes (HardMode vectors in a HardMode league), composed by the tree's rules.
        var presets = CharacterSaves.Load(map.Owner)?.AtlasPassivePresets ?? [];
        int preset = AtlasActivePreset(map.Owner, presets);
        var catalog = ServerLeagues.Resolve(null, request.League, LeagueCatalog.Startup).HardMode
            ? HardModeAtlasPassives.Value : AtlasPassiveNodes;
        var nodes = preset >= 0 ? presets[preset].Distinct().Select(id => catalog.Find(id)).OfType<AtlasPassives.Node>().ToList() : [];
        var families = nodes.Select(AtlasStats.Family).Where(f => f.Length > 0).ToHashSet(StringComparer.Ordinal);
        var atlas = AtlasStats.Compose(nodes);
        return ComposeMapAreaStats(map.Mods, map.Area.Tier, atlas, families, preset, map.BossOption);
    }

    public static Dictionary<uint, int> ComposeMapAreaStats(MapMods? mods, int tier, IReadOnlyDictionary<uint, int> atlas,
        IReadOnlySet<string> families, int preset, byte bossOption = 0)
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
        if (mods is null) return stats;
        foreach (var (key, value) in atlas) stats[key] = stats.GetValueOrDefault(key) + value;
        if (preset >= 0) Add("map_active_atlas_passive_tree_index", preset + 1);
        int effect = AtlasStats.ModEffect(atlas, mods.MapRarity == ItemRarity.Unique, mods.ExplicitMods.Count, mods.FragmentCount);
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
        if (Stats.Key("map_leaguestone_additional_shrines") is uint shrineKey
            && stats.GetValueOrDefault(shrineKey) > 0 && Stats.Key("map_num_extra_shrines") is uint countKey)
            stats[countKey] = (int)Math.Min(int.MaxValue,
                (long)stats.GetValueOrDefault(countKey) + stats[shrineKey]);
        // "Your Rare Maps contain …" (no area text): only on a map of that rarity, as the plain count.
        foreach (var (rarity, kind) in new[] { (ItemRarity.Rare, "rare"), (ItemRarity.Magic, "magic"), (ItemRarity.Normal, "normal") })
            if (Stats.Key($"map_contains_x_additional_{kind}_packs_if_{kind}") is uint key && stats.Remove(key, out int value)
                && mods.MapRarity == rarity && value != 0)
                Add($"map_contains_x_additional_{kind}_packs", value);
        // Meticulous Appraiser: the quantity modifiers apply to rarity instead, at 300%.
        if (Stats.Key("map_item_drop_quantity_instead_applies_to_map_item_drop_rarity_at_3_times_value") is uint appraiser
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
        // AtlasPrimordialBossOptions: 1 Maven, 2 Eater of Worlds, 3 Searing Exarch (C→S 0xd8 / 0x297).
        if (bossOption is >= 1 and <= 3) Set("map_atlas_primordial_boss_influence", bossOption);
        // The league blocks of the tree's families and of the families whose stats the map's own sources (Idols,
        // fragments, mods) carry; a flag set by several sources is set once.
        HashSet<string>? withMods = null;
        foreach (var (id, value) in mods.Stats)
            if (value != 0 && LeagueBlockStats.Value.TryGetValue(id, out string? family) && !families.Contains(family))
                (withMods ??= new(families, StringComparer.Ordinal)).Add(family);
        foreach (string family in withMods ?? families)
            if (MapLeagueBlocks.TryGetValue(family, out var block))
                foreach (var (stat, value) in block) Add(stat, value);
        if (Stats.Key("map_incursion_league") is uint incursion && stats.GetValueOrDefault(incursion) > 1) stats[incursion] = 1;
        // The three a map load always carries, 0 when nothing adds to them.
        foreach (string always in new[] { "map_item_drop_quantity_+%", "map_item_drop_rarity_+%", "map_pack_size_+%" })
            if (Stats.Key(always) is uint key) stats.TryAdd(key, 0);
        return stats;
    }
}
