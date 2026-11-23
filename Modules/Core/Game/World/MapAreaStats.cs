using GameServer.Core;
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
/// activated atlas node and the server's own rolls (Beyond, the influence type, a corrupted map's random notables).
/// </summary>
public static partial class Game
{
    // The per-tier block of a map load, the same on every map of the tier: map_hidden_monster_life_+%_final by tier
    // 1..16, map_hidden_monster_damage_+%_final = tier − 1 (none on tier 1), map_atlas_information = 6912 + tier.
    static readonly int[] MapHiddenLife = [0, 5, 9, 13, 19, 24, 30, 36, 43, 49, 55, 61, 68, 77, 87, 98, 109];

    // The league blocks a map load carries when the atlas tree holds any node of a family (the node id's atlas_<family>_).
    static readonly Dictionary<string, (uint Stat, int Value)[]> MapLeagueBlocks = new()
    {
        ["blight"] = [(Stat.BlightPathPieceLengthModifierPct, 20), (Stat.MapBlightMonstersDamageTakenFromPlayerSourcesPlusPctFinal, -75),
            (Stat.MapBlightTowerDamagePlusPctFinalFromAtlasPassive, 300)],
        ["alva"] = [(Stat.MapIncursionLeague, 1)],
        ["harvest"] = [(Stat.MapHarvestSeedsAreTier1, 1), (Stat.MapHarvestSeedsOfOtherColoursHaveChanceToUpgradeOnCompletingPlot, 1)],
    };

    // [inferred] The league blocks' families by stat: each stat a node of a block's family grants (both catalogues), left
    // out when nodes of two families grant it or a MapFragmentMods row's mod does (a scarab's own stats, such as the
    // Harvest Scarab's map_chance_for_area_%_to_contain_harvest, turn on no block). Idols carry the Atlas passives' stats,
    // so a map whose mods hold one of them gets the same block a node of that family gives.
    static Dictionary<uint, string> LeagueBlockStats = [];
    static Dictionary<byte, int> BossOptionMinTiers = new() { [1] = 1, [2] = 9, [3] = 9 };
    static Dictionary<byte, int> BossOptionUnlockFlags = new() { [1] = 225, [2] = 351, [3] = 352 };

    internal static void LoadMapAreaStats()
    {
        var minTiers = new Dictionary<byte, int> { [1] = 1 };
        foreach (var row in ClientTables.TableOrEmpty("AtlasPrimordialBossInfluence"))
        {
            if (row.IntOrNull("Boss") is not int boss || row.IntOrNull("MinMapTier") is not int minTier || minTier <= 0) continue;
            byte option = boss switch { 0 => 2, 1 => 3, _ => 0 };
            if (option == 0) continue;
            minTiers[option] = minTiers.TryGetValue(option, out int prev) ? Math.Min(prev, minTier) : minTier;
        }
        minTiers.TryAdd(2, 9);
        minTiers.TryAdd(3, 9);
        BossOptionMinTiers = minTiers;

        var unlockFlags = new Dictionary<byte, int>();
        foreach (var row in ClientTables.TableOrEmpty("AtlasPrimordialBossOptions"))
            if (row.IntOrNull("__col0") is >= 1 and <= 3 and int opt && row.IntOrNull("__col1") is > 0 and int flag)
                unlockFlags[(byte)opt] = flag;
        unlockFlags.TryAdd(1, 225);
        unlockFlags.TryAdd(2, 351);
        unlockFlags.TryAdd(3, 352);
        BossOptionUnlockFlags = unlockFlags;

        var fragmentStats = new HashSet<uint>();
        var fragmentMods = new HashSet<int>();
        foreach (var row in ClientTables.TableOrEmpty("MapFragmentMods"))
            foreach (int mod in row.Ints("ModsKeys")) fragmentMods.Add(mod);
        if (fragmentMods.Count > 0)
            foreach (var row in ClientTables.TableOrEmpty("Mods"))
                if (fragmentMods.Contains(row.Index))
                    for (int i = 1; i <= 8 && row.IntOrNull($"StatsKey{i}") is int stat; i++) fragmentStats.Add((uint)stat + 1);
        var families = new Dictionary<uint, string>();
        var shared = new HashSet<uint>();
        foreach (var node in (AtlasPassiveNodes?.Nodes ?? []).Concat(HardModeAtlasPassives?.Nodes ?? []))
        {
            string family = global::AtlasStats.Family(node);
            if (!MapLeagueBlocks.ContainsKey(family)) continue;
            foreach (var (key, _) in node.Stats)
            {
                if (fragmentStats.Contains(key)) continue;
                if (!families.TryAdd(key, family) && families[key] != family) shared.Add(key);
            }
        }
        foreach (uint key in shared) families.Remove(key);
        LeagueBlockStats = families;
    }

    /// <summary>Minimum map tier for an AtlasPrimordialBossOptions selection (1 Maven = T1, 2 Eater / 3 Exarch from AtlasPrimordialBossInfluence.MinMapTier).</summary>
    public static int BossOptionMinTier(byte bossOption) =>
        BossOptionMinTiers.TryGetValue(bossOption, out int min) ? min : int.MaxValue;

    /// <summary>Whether <paramref name="bossOption"/> (1 Maven, 2 Eater, 3 Exarch) is allowed on a map of this tier and rarity.</summary>
    public static bool IsBossOptionEligible(byte bossOption, int tier, ItemRarity rarity) =>
        bossOption is >= 1 and <= 3 && rarity != ItemRarity.Unique && tier >= BossOptionMinTier(bossOption) && tier <= 16;

    /// <summary>Validates a map device boss option against Atlas eligibility, map rarity/tier, special layouts, and the player's unlock flag.</summary>
    public static byte EligibleBossOption(Player player, MapDevice.Opening opening, byte bossOption)
    {
        if (!opening.Area.UsesAtlas || !IsBossOptionEligible(bossOption, opening.Area.Tier, opening.Mods.MapRarity))
            return 0;
        if (MapDevices.Atlas?.ByHash16(opening.Area.Hash16) is { RequiresSpecificItem: true })
            return 0;
        if (BossOptionUnlockFlags.TryGetValue(bossOption, out int flag) && !player.Character.LeagueQuestFlags.Contains(flag))
            return 0;
        return bossOption;
    }

    /// <summary>InstanceEvents.AreaStats: the stats of the instance <paramref name="request"/> opens.</summary>
    public static IReadOnlyDictionary<uint, int>? MapAreaStats(AreaStatsRequest request)
    {
        bool deepwater = request.League?.Contains("Deepwater", StringComparison.OrdinalIgnoreCase) == true;
        if (request.Map is not { } map) return ComposeMapAreaStats(null, 0, new Dictionary<uint, int>(), new HashSet<string>(), -1, deepwater);
        // Not an Atlas map: the mods' own stats, without the atlas, the tier block, its hidden life or the preset.
        if (!map.Atlas) return ComposeMapAreaStats(map.Mods, 0, new Dictionary<uint, int>(), new HashSet<string>(), -1, deepwater);
        // The atlas: the active preset's nodes (HardMode vectors in a HardMode league), composed by the tree's rules.
        var presets = CharacterSaves.Load(map.Owner)?.AtlasPassivePresets ?? [];
        int preset = AtlasActivePreset(map.Owner, presets);
        var catalog = ServerLeagues.Resolve(null, request.League, LeagueCatalog.Startup).HardMode
            ? HardModeAtlasPassives : AtlasPassiveNodes;
        var nodes = preset >= 0 ? presets[preset].Distinct().Select(id => catalog.Find(id)).OfType<AtlasPassives.Node>().ToList() : [];
        var families = nodes.Select(global::AtlasStats.Family).Where(f => f.Length > 0).ToHashSet(StringComparer.Ordinal);
        var atlas = global::AtlasStats.Compose(nodes);
        return ComposeMapAreaStats(map.Mods, map.Area.Tier, atlas, families, preset, deepwater, map.BossOption);
    }

    public static Dictionary<uint, int> ComposeMapAreaStats(MapMods? mods, int tier, IReadOnlyDictionary<uint, int> atlas,
        IReadOnlySet<string> families, int preset, bool deepwater = false, byte bossOption = 0)
    {
        var stats = new Dictionary<uint, int>();
        void Add(uint name, int value)
        {
            if (name is var key) stats[key] = stats.GetValueOrDefault(key) + value;
        }
        void Set(uint name, int value)
        {
            if (name is var key) stats[key] = value;
        }
        // Every load of a character in a Deepwater league carries the league's flag.
        if (deepwater) Add(Stat.MapDeepwaterLeague, 1);
        if (mods is null) return stats;
        foreach (var (key, value) in atlas) stats[key] = stats.GetValueOrDefault(key) + value;
        if (preset >= 0) Add(Stat.MapActiveAtlasPassiveTreeIndex, preset + 1);
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
                Add(Stat.Of($"map_contains_x_additional_{kind}_packs"), value);
        // Meticulous Appraiser: the quantity modifiers apply to rarity instead, at 300%.
        if (Stat.MapItemDropQuantityInsteadAppliesToMapItemDropRarityAt3TimesValue is var appraiser
            && stats.GetValueOrDefault(appraiser) != 0)
        {
            int quantity = 0;
            foreach (uint id in new[] { Stat.MapItemDropQuantityPlusPct, Stat.MapImplicitItemDropQuantityPlusPct, Stat.MapUnscaledItemDropQuantityPlusPct })
                if (stats.Remove(id, out int value)) quantity += value;
            Add(Stat.MapItemDropRarityPlusPct, 3 * quantity);
        }
        // The derived counts.
        Set(Stat.MapRarity, (int)mods.MapRarity);
        Set(Stat.MapNumberOfExplicitMods, mods.ExplicitMods.Count);
        Set(Stat.TotalNumberOfMapMods, mods.ExplicitMods.Count + mods.FragmentCount);
        if (mods.Corrupted) Set(Stat.MapIsCorrupted, 1);
        if (mods.FragmentCount > 0) Set(Stat.MapNumFragments, mods.FragmentCount);
        if (mods.ScarabCount > 0)
        {
            Set(Stat.MapTotalScarabTiers, mods.ScarabCount);
            Set(Stat.MapNumScarabs, mods.ScarabCount);
            foreach (var (kind, count) in mods.ScarabKinds) Set(Stat.Of($"map_{kind}_scarab_count"), count);
        }
        // The tier block and the device.
        if (tier is >= 1 and <= 16)
        {
            Set(Stat.MapTier, tier);
            Set(Stat.MapAtlasInformation, 6912 + tier);
            Set(Stat.MapHiddenMonsterLifePlusPctFinal, MapHiddenLife[tier]);
            if (tier > 1) Set(Stat.MapHiddenMonsterDamagePlusPctFinal, tier - 1);
        }
        Set(Stat.MapDeviceType, 1);
        // AtlasPrimordialBossOptions: 1 Maven, 2 Eater of Worlds, 3 Searing Exarch (C→S 0xd8 / 0x297).
        if (IsBossOptionEligible(bossOption, tier, mods.MapRarity)) Set(Stat.MapAtlasPrimordialBossInfluence, bossOption);
        // The league blocks of the tree's families and of the families whose stats the map's own sources (Idols,
        // fragments, mods) carry; a flag set by several sources is set once.
        HashSet<string>? withMods = null;
        foreach (var (id, value) in mods.Stats)
            if (value != 0 && LeagueBlockStats.TryGetValue(id, out string? family) && !families.Contains(family))
                (withMods ??= new(families, StringComparer.Ordinal)).Add(family);
        foreach (string family in withMods ?? families)
            if (MapLeagueBlocks.TryGetValue(family, out var block))
                foreach (var (stat, value) in block) Add(stat, value);
        if (Stat.MapIncursionLeague is var incursion && stats.GetValueOrDefault(incursion) > 1) stats[incursion] = 1;
        // The three a map load always carries, 0 when nothing adds to them.
        foreach (uint always in new[] { Stat.MapItemDropQuantityPlusPct, Stat.MapItemDropRarityPlusPct, Stat.MapPackSizePlusPct })
            stats.TryAdd(always, 0);
        return stats;
    }
}
