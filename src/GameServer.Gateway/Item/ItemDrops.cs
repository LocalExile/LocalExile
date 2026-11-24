using GameServer.Core.Protocol.Instance;
using GameServer.Core;

/// <summary>
/// What a dying monster drops, rolled in stages matching retail 3.29 (Agents/docs/protocol/15-progression.md §3) and
/// retunable through <c>system_settings</c>:
/// 1. How many items: normal monsters 1 at <c>drop_normal_chance_pct</c> (16% default), magic ~1.12 (7×), rare ~2.40
///    (15×), unique ~4.72 (29.5×), times item quantity (league, mod, area, diminished player IIQ, excess monster IIQ).
/// 2. For each, what kind: currency (40% default, subject to over-levelled <see cref="DropPenaltyMultiplier"/>), gem
///    (4%, with Empower/Enhance/Enlighten in a separate 0.15% sub-roll at L38+), flask (8%, balanced 55% Recovery /
///    35% Utility / 10% native Tincture) or equipment/jewels (48%).
/// 3. Equipment &amp; recovery flasks: slot (<see cref="Slots"/>, including basic Crimson/Viridian/Cobalt Jewels at L20+),
///    then a level-weighted base whose DropLevel the monster's level meets. Atlas bases drop only in maps.
/// 4. Natural quality (1–20% at <c>drop_natural_quality_chance_pct</c>) and rarity (<see cref="RarityChances"/>,
///    scaled by item rarity and <c>drop_unique_chance_multiplier</c>): unique (respecting T0 drop-level floors and
///    <see cref="UniqueWeight"/>, else rare), rare, magic (minimum rarity for jewels and tinctures), or normal; magic,
///    rare and unique items drop unidentified at monster level plus <see cref="ItemLevelBonus"/>.
/// 5. Maps roll base card and scarab chances; campaign areas with <c>drop_card_areas</c> (<c>min_map_tier = 0</c>) roll
///    <c>drop_campaign_card_chance_pct</c> subject to <see cref="DropPenaltyMultiplier"/>.
/// </summary>
public sealed class ItemDrops
{
    readonly ItemCatalog catalog;
    readonly ItemRolls? rolls;
    readonly string folder;
    readonly bool hardMode;
    readonly AtlasCatalog? maps;
    readonly UniqueCatalog? uniques;
    readonly UniqueWeightProvider? uniqueWeights;
    readonly LeagueRules? league;

    public ItemDrops(ItemCatalog catalog, ItemRolls? rolls = null, string folder = "data", bool hardMode = false, AtlasCatalog? maps = null,
    UniqueCatalog? uniques = null, UniqueWeightProvider? uniqueWeights = null, LeagueRules? league = null)
    {
        this.catalog = catalog;
        this.rolls = rolls;
        this.folder = folder;
        this.hardMode = hardMode;
        this.maps = maps;
        this.uniques = uniques;
        this.uniqueWeights = uniqueWeights;
        this.league = league;
        _uniqueWeightProvider = uniqueWeights ?? UniqueWeightProvider.Default;
        _pools = Build(catalog, folder, hardMode, league);
        _cards = LoadCards();
        _cardWeights = PublishedWeights("card");
        _scarabWeights = PublishedWeights("scarab");
        _scarabs = LoadScarabs(catalog, folder);
        _scarabTypeStats = LoadScarabTypeStats();
        _uniqueWeightsFile = LoadUniqueWeights(folder, hardMode);
        _goldRespecCosts = LoadGoldRespecCosts();
        _mapWorldAreaNames = LoadMapWorldAreaNames();
    }

    readonly UniqueWeightProvider _uniqueWeightProvider;
    readonly int[] _goldRespecCosts;
    readonly Dictionary<string, string> _mapWorldAreaNames;

    /// <summary>A dropped piece of equipment's chances by the monster's rarity, each of what the earlier ones left: first
    /// unique, then rare, then magic.</summary>
    public readonly record struct Chances(double Unique, double Rare, double Magic);

    /// <summary>What a monster's rarity adds to the item level of its drops: magic 1, rare and unique 2.
    /// The level that decides which bases and currency can drop stays the monster's.</summary>
    public static int ItemLevelBonus(MonsterRarity monster) => monster switch
    {
        MonsterRarity.Magic => 1,
        MonsterRarity.Rare or MonsterRarity.Unique => 2,
        _ => 0,
    };

    public static Chances RarityChances(MonsterRarity monster) => monster switch
    {
        MonsterRarity.Magic => new(0.0020, 0.06, 0.32),
        MonsterRarity.Rare => new(0.0080, 0.22, 0.42),
        MonsterRarity.Unique => new(0.0250, 0.45, 0.35),
        _ => new(0.0006, 0.02, 0.18),
    };

    /// <summary>PoE-style diminishing returns for player increased item quantity (returns fractional bonus, e.g. 0.67 for +100%).</summary>
    public static double EffectivePlayerIiq(int rawIncreasedPct) =>
        rawIncreasedPct <= 0 ? Math.Max(-100, rawIncreasedPct) / 100.0 : rawIncreasedPct / (100.0 + 0.5 * rawIncreasedPct);

    /// <summary>PoE-style diminishing returns for player increased item rarity (returns effective percentage, e.g. 80 for +100%).</summary>
    public static double EffectivePlayerIir(int rawIncreasedPct) =>
        rawIncreasedPct <= 0 ? Math.Max(-100, rawIncreasedPct) : rawIncreasedPct / (1.0 + 0.0025 * rawIncreasedPct);

    /// <summary>Retail PoE currency and divination card drop-rate penalty for over-levelled characters farming low areas.
    /// Character level is capped at <c>drop_penalty_character_level_cap</c> (68); levels up to <c>areaLevel + offset</c>
    /// incur no penalty; each level beyond that reduces currency and card drops by <c>drop_penalty_per_level_pct</c>%.</summary>
    public static double DropPenaltyMultiplier(int characterLevel, int areaLevel)
    {
        var settings = SystemSettings.Default;
        if (!settings.DropPenaltyEnabled || characterLevel <= 0 || areaLevel <= 0) return 1.0;
        int effectiveLevel = Math.Min(characterLevel, settings.DropPenaltyCharacterLevelCap);
        int excess = effectiveLevel - (areaLevel + settings.DropPenaltyLevelOffset);
        if (excess <= 0) return 1.0;
        return Math.Max(0.0, 1.0 - excess * (settings.DropPenaltyPerLevelPct / 100.0));
    }

    /// <summary>The chances with item rarity: each × (1 + increased / 100) × the multiplier (a league's, a mod's), at most
    /// 1. As in the game, more rarity makes every better rarity likelier, uniques too; past 1 the better one takes all.</summary>
    public static Chances WithIncreasedRarity(Chances chances, int increased, double multiplier = 1)
    {
        double factor = Math.Max(0, 100 + increased) / 100.0 * (double.IsFinite(multiplier) ? Math.Max(0, multiplier) : 1);
        return new(Math.Min(1, chances.Unique * factor), Math.Min(1, chances.Rare * factor), Math.Min(1, chances.Magic * factor));
    }

    // (BaseItemTypes Id, weight), each only from its own DropLevel: Scrolls of Wisdom most, then the basic orbs, down to the
    // Exalted and Divine Orbs and the Mirror of Kalandra.
    public static readonly (string Id, double Weight)[] Currency =
    [
        ("Metadata/Items/Currency/CurrencyIdentification", 1000), ("Metadata/Items/Currency/CurrencyPortal", 300),
        ("Metadata/Items/Currency/CurrencyUpgradeToMagic", 500), ("Metadata/Items/Currency/CurrencyArmourQuality", 450),
        ("Metadata/Items/Currency/CurrencyWeaponQuality", 300), ("Metadata/Items/Currency/CurrencyAddModToMagic", 400),
        ("Metadata/Items/Currency/CurrencyRerollMagic", 250), ("Metadata/Items/Currency/CurrencyRerollSocketColours", 200),
        ("Metadata/Items/Currency/CurrencyRerollSocketNumbers", 150), ("Metadata/Items/Currency/CurrencyUpgradeRandomly", 120),
        ("Metadata/Items/Currency/CurrencyUpgradeToRare", 90), ("Metadata/Items/Currency/CurrencyFlaskQuality", 60),
        ("Metadata/Items/Currency/CurrencyRerollSocketLinks", 50), ("Metadata/Items/Currency/CurrencyRerollRare", 40),
        ("Metadata/Items/Currency/CurrencyConvertToNormal", 30), ("Metadata/Items/Currency/CurrencyPassiveRefund", 25),
        ("Metadata/Items/Currency/CurrencyCorrupt", 25), ("Metadata/Items/Currency/CurrencyUpgradeMagicToRare", 20),
        ("Metadata/Items/Currency/CurrencyGemQuality", 20), ("Metadata/Items/Currency/CurrencyRerollImplicit", 15),
        ("Metadata/Items/Currency/CurrencyAddModToRare", 2), ("Metadata/Items/Currency/CurrencyRemoveMod", 1.5),
        ("Metadata/Items/Currency/CurrencyModValues", 1), ("Metadata/Items/Currency/CurrencyDuplicate", 0.01),
    ];

    // Equipment slots (ItemClasses Id) and how often each is picked: armour pieces most, then weapons, jewellery seldom.
    static readonly (string Class, double Weight)[] Slots =
    [
        ("Body Armour", 10), ("Helmet", 10), ("Gloves", 10), ("Boots", 10), ("Shield", 6),
        ("One Hand Sword", 2), ("Thrusting One Hand Sword", 1.5), ("One Hand Axe", 2), ("One Hand Mace", 2), ("Sceptre", 2),
        ("Dagger", 2), ("Rune Dagger", 1), ("Claw", 2), ("Wand", 2),
        ("Two Hand Sword", 2), ("Two Hand Axe", 2), ("Two Hand Mace", 2), ("Staff", 2), ("Warstaff", 1), ("Bow", 3), ("Quiver", 2.5),
        ("Ring", 3), ("Amulet", 1.5), ("Belt", 2.5), ("Jewel", 1.5),
    ];
    public static readonly HashSet<string> EquipmentClasses = [.. Slots.Select(s => s.Class)];

    // Basic Crimson/Viridian/Cobalt Jewels carry the not_for_sale tag in BaseItemTypes yet belong in the general drop table
    // at DropLevel 20; Prismatic, Timeless and Cluster Jewels stay excluded.
    static readonly HashSet<string> DroppableJewelBases = new(StringComparer.Ordinal)
    {
        "Metadata/Items/Jewels/JewelStr",
        "Metadata/Items/Jewels/JewelDex",
        "Metadata/Items/Jewels/JewelInt",
    };

    // Bases that exist in the tables but never drop (event, league and test items) and carry no not_for_sale tag.
    static readonly string[] NotDropped = ["Royale", "Demigod", "Talisman", "Breach", "Synthesis", "Fishing", "Test", "Unarmed", "Atlas",
        "Expedition", "Heist", "Relic", "Basilisk", "Crusader", "Eyrie", "Elder", "Shaper", "Necropolis", "Sanctum", "Ultimatum", "Legion"];

    /// <summary>Whether a base can be in the general drop table: not tagged not_for_sale (the atlas bases and basic
    /// Crimson/Viridian/Cobalt jewels aside), not one of the event and league bases, and with a name and a drop level.</summary>
    public static bool Droppable(ItemCatalog.BaseType type) =>
        type.Supported && type.DropLevel >= 1 && !string.IsNullOrEmpty(type.Name)
        && (!type.Tags.Contains("not_for_sale") || type.Tags.Contains("atlas_base_type") || DroppableJewelBases.Contains(type.Id))
        && !NotDropped.Any(n => type.Id.Contains(n) && !(n == "Atlas" && type.Tags.Contains("atlas_base_type")));

    sealed record Pools(List<(ItemCatalog.BaseType Type, double Weight, int DropLevel)> Currency,
        Dictionary<string, List<(ItemCatalog.BaseType Type, bool Atlas)>> Equipment,
        List<ItemCatalog.BaseType> RecoveryFlasks,
        List<ItemCatalog.BaseType> UtilityFlasks,
        List<ItemCatalog.BaseType> Tinctures,
        List<ItemCatalog.BaseType> RegularGems,
        List<ItemCatalog.BaseType> ExceptionalGems);

    private readonly Pools _pools;

    // The uniques by base type Id, for the catalogue they were built from (the database may replace it while running).
    sealed record UniqueEntry(UniqueCatalog.Definition Definition, int DropLevel);
    sealed record UniqueIndex(IReadOnlyList<UniqueCatalog.Definition> From, Dictionary<string, List<UniqueEntry>> ByBase);
    volatile UniqueIndex? _uniques;

    // Indexed drop_card_areas snapshot (rebuilt only when DropDataProvider replaces its snapshot).
    // Campaign rows (min_map_tier == 0) are keyed strictly by WorldAreas.Id so Part 1/Part 2 shared area names
    // never collide; Atlas map rows (min_map_tier >= 1) resolve by area_id, area_name, or WorldAreas.Id -> Name.
    sealed record CardAreaIndex(object From,
        Dictionary<string, HashSet<string>> CampaignByAreaId,
        Dictionary<string, List<(string CardId, int MinMapTier)>> MapByAreaKey);
    volatile CardAreaIndex? _cardAreaIndex;
    int _minCampaignMapDropLevel = -1;

    public sealed record Drop(Item Item, ItemCatalog.BaseType Type);

    readonly List<ItemCatalog.BaseType> _cards;

    List<ItemCatalog.BaseType> LoadCards()
    {
        var list = new List<ItemCatalog.BaseType>();
        for (int row = 0; catalog.Row(row) is { } t; row++)
            if (t.Class == "DivinationCard" && t.Supported && !string.IsNullOrEmpty(t.Name)) list.Add(t);
        return list;
    }

    static Dictionary<string, string> LoadMapWorldAreaNames()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!ClientTables.Has("WorldAreas")) return map;
        foreach (var row in ClientTables.Table("WorldAreas"))
            if ((row.Bool("IsMapArea") || row.Bool("IsUniqueMapArea"))
                && row.TextOrNull("Id") is { Length: > 0 } id
                && row.TextOrNull("Name") is { Length: > 0 } name)
                map.TryAdd(id, name);
        return map;
    }

    // The published weights (poedata MLE, the drop_item_weights table); empty until the database is seeded.
    readonly Dictionary<string, double> _cardWeights;
    readonly Dictionary<string, double> _scarabWeights;

    CardAreaIndex GetCardAreaIndex()
    {
        var snapshot = DropDataProvider.Default.Current;
        var current = _cardAreaIndex;
        if (current is not null && ReferenceEquals(current.From, snapshot)) return current;
        var campaign = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var mapAreas = new Dictionary<string, List<(string CardId, int MinMapTier)>>(StringComparer.OrdinalIgnoreCase);
        void AddMap(string key, string cardId, int minTier)
        {
            if (string.IsNullOrEmpty(key)) return;
            if (!mapAreas.TryGetValue(key, out var list)) mapAreas[key] = list = [];
            list.Add((cardId, minTier));
        }
        foreach (var row in snapshot.CardAreas.Values)
        {
            if (row.MinMapTier == 0)
            {
                if (string.IsNullOrEmpty(row.AreaId)) continue;
                if (!campaign.TryGetValue(row.AreaId, out var set)) campaign[row.AreaId] = set = new(StringComparer.OrdinalIgnoreCase);
                set.Add(row.CardId);
            }
            else
            {
                AddMap(row.AreaId, row.CardId, row.MinMapTier);
                if (!row.AreaName.Equals(row.AreaId, StringComparison.OrdinalIgnoreCase))
                    AddMap(row.AreaName, row.CardId, row.MinMapTier);
            }
        }
        _cardAreaIndex = current = new(snapshot, campaign, mapAreas);
        return current;
    }

    bool HasCampaignCards(string areaId) => GetCardAreaIndex().CampaignByAreaId.ContainsKey(areaId);

    /// <summary>A random divination card. When the published card weights cover cards at this level, the pick follows
    /// them; otherwise it is uniform among the client's cards. <paramref name="level"/> 0 skips the DropLevel gate.</summary>
    public Drop? Card(Random rng, int level = 0) => Card(rng, level, areaId: null, mapTier: 0);

    /// <summary>A divination card filtered by area and map tier through <c>drop_card_areas</c> when <paramref name="areaId"/>
    /// is supplied. Campaign areas (<paramref name="mapTier"/> == 0) match strictly by <c>WorldAreas.Id</c> against
    /// <c>min_map_tier = 0</c> rows; maps resolve by <c>area_id</c>, <c>area_name</c>, <c>WorldAreas.Id</c> → <c>Name</c>,
    /// or name without trailing " Map", falling back to global weights when unmapped.</summary>
    public Drop? Card(Random rng, int level, string? areaId, int mapTier, DropWeights? factors = null)
    {
        if (!string.IsNullOrEmpty(areaId))
        {
            var index = GetCardAreaIndex();
            HashSet<string>? allowedIds = null;
            if (mapTier <= 0)
            {
                if (!index.CampaignByAreaId.TryGetValue(areaId, out allowedIds) || allowedIds.Count == 0)
                    return null;
            }
            else
            {
                List<(string CardId, int MinMapTier)>? areaRows = ResolveMapCardRows(index, areaId);
                if (areaRows is { Count: > 0 })
                {
                    allowedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var (cardId, minTier) in areaRows)
                        if (mapTier >= minTier) allowedIds.Add(cardId);
                }
            }
            if (allowedIds is not null)
            {
                var matched = _cards.Where(t => Allowed(t, level) && (allowedIds.Contains(t.Name) || allowedIds.Contains(t.Id))).ToList();
                return PickCategory(matched, "card", level, rng, factors) is { } picked
                    ? new Drop(ItemCatalog.Create(picked) with { StackSize = 1 }, picked)
                    : null;
            }
        }
        return Card(rng, level, factors);
    }
    /// <param name="factors">A mod's factor per card (<see cref="DropWeights.Item"/>) on its published or uniform weight.</param>
    public Drop? Card(Random rng, int level, DropWeights? factors) =>
        PickCategory(_cards, "card", level, rng, factors) is { } type
            ? new Drop(ItemCatalog.Create(type) with { StackSize = 1 }, type)
            : null;

    List<(string CardId, int MinMapTier)>? ResolveMapCardRows(CardAreaIndex index, string areaId)
    {
        if (index.MapByAreaKey.TryGetValue(areaId, out var rows)) return rows;
        const string mapSuffix = " Map";
        if (areaId.EndsWith(mapSuffix, StringComparison.OrdinalIgnoreCase)
            && index.MapByAreaKey.TryGetValue(areaId[..^mapSuffix.Length], out rows))
            return rows;
        if (_mapWorldAreaNames.TryGetValue(areaId, out string? worldName))
        {
            if (index.MapByAreaKey.TryGetValue(worldName, out rows)) return rows;
            if (worldName.EndsWith(mapSuffix, StringComparison.OrdinalIgnoreCase)
                && index.MapByAreaKey.TryGetValue(worldName[..^mapSuffix.Length], out rows))
                return rows;
        }
        return null;
    }

    /// <summary>The published weights for a category, by BaseItemTypes Id. Empty when the database has not been seeded.</summary>
    static Dictionary<string, double> PublishedWeights(string category) =>
        DropDataProvider.Default.Current.ItemWeights
            .Where(row => row.Key.Category == category && row.Value is { Enabled: true, Weight: > 0 })
            .ToDictionary(row => row.Key.ItemId, row => row.Value.Weight, StringComparer.Ordinal);

    /// <summary>One of the types, by the published weights when they cover one whose DropLevel the level meets, else
    /// uniformly; either weight times the type's factor in <paramref name="factors"/>. Null when none can drop.</summary>
    ItemCatalog.BaseType? PickCategory(List<ItemCatalog.BaseType> types, string category, int level, Random rng, DropWeights? factors)
    {
        var weights = category == "card" ? _cardWeights : _scarabWeights;
        if (weights.Count > 0 && types.Where(t => weights.ContainsKey(t.Id) && Allowed(t, level))
                .Select(t => (Value: t, Weight: weights[t.Id] * (factors?.Item(t.Id) ?? 1))).ToList() is { Count: > 0 } published)
            return Pick(published, rng);
        var pool = Droppable(types, level);
        if (pool.Count == 0) return null;
        return factors is { Items.Count: > 0 }
            ? Pick(pool.Select(t => (Value: t, Weight: factors.Item(t.Id))).ToList(), rng)
            : pool[rng.Next(pool.Count)];
    }

    static bool Allowed(ItemCatalog.BaseType type, int level) => level <= 0 || type.DropLevel <= level;

    static List<ItemCatalog.BaseType> Droppable(List<ItemCatalog.BaseType> types, int level) =>
        level <= 0 ? types : types.Where(t => Allowed(t, level)).ToList();

    readonly List<ItemCatalog.BaseType> _scarabs;

    // Each scarab's BaseItemTypes row → its type's MoreLikely and DisableDrops stat keys (Scarabs.Items, ScarabTypes).
    readonly Dictionary<int, (uint MoreLikely, uint Disable)> _scarabTypeStats;

    static Dictionary<int, (uint MoreLikely, uint Disable)> LoadScarabTypeStats()
    {
        var result = new Dictionary<int, (uint, uint)>();
        if (!ClientTables.Has("Scarabs") || !ClientTables.Has("ScarabTypes") || !ClientTables.Has("Stats")) return result;
        var types = ClientTables.Table("ScarabTypes").ToArray();
        var stats = ClientTables.Table("Stats");
        uint Key(int? row) => row is int r && stats.Row(r) is { } s && s.Text("Id") is { Length: > 0 } id ? Stat.Of(id) : 0;
        foreach (var row in ClientTables.Table("Scarabs"))
            if (row.IntOrNull("Type") is int t && t >= 0 && t < types.Length)
                foreach (int item in row.Ints("Items"))
                    result.TryAdd(item, (Key(types[t].IntOrNull("MoreLikely")), Key(types[t].IntOrNull("DisableDrops"))));
        return result;
    }

    /// <summary>The weight factor of each scarab the area's stats change (absent: 1).</summary>
    Dictionary<int, double> ScarabFactors(MapMods mods)
    {
        var factors = new Dictionary<int, double>();
        foreach (var (row, (more, disable)) in _scarabTypeStats)
        {
            if (disable != 0 && mods.Get(disable) != 0) factors[row] = 0;
            else if (more != 0 && mods.Get(more) is int x and not 0) factors[row] = Math.Max(0, 100 + x) / 100.0;
        }
        return factors;
    }

    static List<ItemCatalog.BaseType> LoadScarabs(ItemCatalog catalog, string folder)
    {
        var list = new List<ItemCatalog.BaseType>();
        var seen = new HashSet<int>();
        if (ClientTables.Has("Scarabs"))
        {
            foreach (var row in ClientTables.Table("Scarabs"))
                foreach (int n in row.Ints("Items"))
                {
                    if (!seen.Add(n)) continue;
                    if (catalog.Row(n) is { Supported: true } type) list.Add(type);
                }
        }
        if (list.Count == 0)
        {
            for (int row = 0; catalog.Row(row) is { } t; row++)
                if (t.Class == "MapFragment" && t.Id.Contains("/Scarabs/", StringComparison.Ordinal) && t.Supported)
                    list.Add(t);
        }
        return list;
    }

    /// <summary>A random scarab. When the published scarab weights cover scarabs at this level, the pick follows them;
    /// otherwise it is uniform among the client's scarabs. <paramref name="level"/> 0 skips the DropLevel gate.</summary>
    public Drop? Scarab(Random rng, int level = 0) => Scarab(rng, level, null);

    /// <summary>A random scarab, with the area's scarab-type stats (ScarabTypes): its <c>MoreLikely</c> stat (the Atlas
    /// "Carapaces" notables, <c>map_&lt;type&gt;_scarab_more_likely_%</c>: "Scarabs found in your Maps are X% more likely to be
    /// &lt;type&gt; Scarabs") multiplies the type's weight by (100 + X)%, and its <c>DisableDrops</c> stat drops none of it.</summary>
    public Drop? Scarab(Random rng, int level, MapMods? mods) => Scarab(rng, level, mods, null);

    /// <param name="factors">A mod's factor per scarab (<see cref="DropWeights.Item"/>), on top of the area's.</param>
    public Drop? Scarab(Random rng, int level, MapMods? mods, DropWeights? factors)
    {
        if (mods is not null && ScarabFactors(mods) is { Count: > 0 } areaFactors)
        {
            var weights = _scarabWeights;
            var pool = Droppable(_scarabs, level);
            bool published = weights.Count > 0 && pool.Any(t => weights.ContainsKey(t.Id));
            var favoured = pool.Where(t => !published || weights.ContainsKey(t.Id))
                .Select(t => (Value: t, Weight: (published ? weights[t.Id] : 1) * areaFactors.GetValueOrDefault(t.Row, 1)
                    * (factors?.Item(t.Id) ?? 1)))
                .Where(p => p.Weight > 0).ToList();
            return Pick(favoured, rng) is { } picked ? new Drop(ItemCatalog.Create(picked) with { StackSize = 1 }, picked) : null;
        }
        return PickCategory(_scarabs, "scarab", level, rng, factors) is { } type
            ? new Drop(ItemCatalog.Create(type) with { StackSize = 1 }, type)
            : null;
    }

    /// <summary>Extra drops from any MapFragmentMods / map-quality stats: card packs, card/scarab/currency drop chances.
    /// New scarabs that use those stat name patterns are included without an Id list.</summary>
    public IEnumerable<Drop> FromMapMods(MapMods mods, int monsterLevel, MonsterRarity rarity, Random rng)
    {
        if (rarity == MonsterRarity.Magic && mods.PacksDropCards > 0 && Card(rng, monsterLevel) is { } packCard)
            yield return packCard;
        if (mods.CardDropChance > 0 && rng.Next(100) < Math.Clamp(mods.CardDropChance, 0, 80) && Card(rng, monsterLevel) is { } card)
            yield return card;
        if (mods.ScarabDropChance > 0 && rng.Next(100) < Math.Clamp(mods.ScarabDropChance, 0, 80) && Scarab(rng, monsterLevel, mods) is { } scarab)
            yield return scarab;
        if (mods.CurrencyDropChance > 0 && rng.Next(100) < Math.Clamp(mods.CurrencyDropChance, 0, 80)
            && PickCurrency(_pools.Currency, monsterLevel, rng) is { } currency)
            yield return new Drop(ItemCatalog.Create(currency) with { StackSize = 1 }, currency);
    }

    /// <summary>A reward container's explicit number of rolls, without a monster's quantity or personal/mod multipliers.</summary>
    public IReadOnlyList<Drop> Container(int level, int count, int increasedRarity, Random rng) =>
        Container(level, count, increasedRarity, rng, null);

    public IReadOnlyList<Drop> Container(int level, int count, int increasedRarity, Random rng,
        Func<ItemCatalog.BaseType, bool>? accepts)
    {
        if (count < 0 || count > 2000) throw new ArgumentOutOfRangeException(nameof(count));
        var chances = WithUniqueMultiplier(WithIncreasedRarity(RarityChances(MonsterRarity.Rare), increasedRarity,
            SystemSettings.Default.DropMultiplier("unique_rarity")));
        var result = new List<Drop>();
        for (int attempt = 0; result.Count < count && attempt < count * (accepts is null ? 8 : 256); attempt++)
            if (RollOne(Math.Clamp(level, 1, 100), Math.Clamp(level, 1, 100), chances, rng) is { } drop
                && (accepts is null || accepts(drop.Type))) result.Add(drop);
        return result;
    }

    /// <summary>The items a monster of this level and rarity drops (possibly none). The quantity multiplier scales how
    /// many: each of the monster's rolls happens floor(multiplier) times, and once more with the fraction's chance (below
    /// 1, that chance alone: HardMode's 0.2). The rarity multiplier scales every rarity chance, as increased rarity does.
    /// In a map (<paramref name="mapTier"/> &gt; 0) the kill also rolls the base card and scarab chances from
    /// <c>drop_card_chance_pct</c>/<c>drop_scarab_chance_pct</c>, scaled by the quantity multiplier. In campaign areas
    /// with <c>drop_card_areas</c> entries, rolls <c>drop_campaign_card_chance_pct</c>.</summary>
    public IReadOnlyList<Drop> Roll(int monsterLevel, MonsterRarity rarity, Random rng, int increasedRarity = 0,
        double quantityMultiplier = 1, double rarityMultiplier = 1, int mapTier = 0) =>
        Roll(monsterLevel, rarity, rng, increasedRarity, quantityMultiplier, rarityMultiplier, mapTier, null);

    public IReadOnlyList<Drop> Roll(int monsterLevel, MonsterRarity rarity, Random rng, int increasedRarity,
        double quantityMultiplier, double rarityMultiplier, int mapTier, MapMods? mods, double scarabQuantity = 1) =>
        Roll(monsterLevel, rarity, rng, increasedRarity, quantityMultiplier, rarityMultiplier, mapTier, mods, scarabQuantity, weights: null);

    /// <param name="mods">The map's stats, for its scarab-type weights (<see cref="Scarab(Random, int, MapMods?)"/>).</param>
    /// <param name="weights">The mods' factors on the kinds, the map's base card and scarab chances and single types
    /// (<see cref="DropRoll.Weights"/>); null for none.</param>
    public IReadOnlyList<Drop> Roll(int monsterLevel, MonsterRarity rarity, Random rng, int increasedRarity,
        double quantityMultiplier, double rarityMultiplier, int mapTier, MapMods? mods, double scarabQuantity,
        DropWeights? weights) =>
        Roll(monsterLevel, rarity, rng, increasedRarity, quantityMultiplier, rarityMultiplier, mapTier, mods, areaId: null, scarabQuantity: scarabQuantity, weights: weights);

    /// <param name="mods">The map's stats, for its scarab-type weights (<see cref="Scarab(Random, int, MapMods?)"/>).</param>
    /// <param name="areaId">Current world area id or map name for <c>drop_card_areas</c> filtering.</param>
    /// <param name="characterLevel">Killing character's level for currency/card drop penalty (optional).</param>
    /// <param name="areaLevel">Current area level for currency/card drop penalty (defaults to <paramref name="monsterLevel"/>).</param>
    public IReadOnlyList<Drop> Roll(int monsterLevel, MonsterRarity rarity, Random rng, int increasedRarity,
        double quantityMultiplier, double rarityMultiplier, int mapTier, MapMods? mods, string? areaId,
        double scarabQuantity = 1, int? characterLevel = null, int? areaLevel = null, DropWeights? weights = null)
    {
        double quantity = double.IsFinite(quantityMultiplier) ? Math.Max(0, quantityMultiplier) : 1;
        int whole = (int)Math.Min(Math.Floor(quantity), int.MaxValue);
        double part = quantity < 1 ? quantity : quantity - Math.Floor(quantity);
        double normalChance = SystemSettings.Default.DropNormalChancePct / 100.0;
        int count = rarity switch
        {
            MonsterRarity.Magic => RollExpectedCount(normalChance * 7.0, rng),
            MonsterRarity.Rare => RollExpectedCount(normalChance * 15.0, rng),
            MonsterRarity.Unique => RollExpectedCount(normalChance * 29.5, rng),
            _ => rng.NextDouble() < normalChance ? 1 : 0,
        };
        var chances = WithUniqueMultiplier(WithIncreasedRarity(RarityChances(rarity), increasedRarity,
            rarityMultiplier * SystemSettings.Default.DropMultiplier("unique_rarity")));
        double dropPenalty = characterLevel is int cl ? DropPenaltyMultiplier(cl, areaLevel ?? monsterLevel) : 1.0;
        var drops = new List<Drop>();
        for (int i = 0; i < count; i++)
        {
            // No draw for a whole multiplier, so the ordinary 1 rolls exactly as before.
            int times = whole + ((quantity < 1 || part > 0) && rng.NextDouble() < part ? 1 : 0);
            for (int r = 0; r < times; r++)
                if (RollOne(monsterLevel, monsterLevel + ItemLevelBonus(rarity), chances, rng, inMap: mapTier > 0, dropPenalty: dropPenalty, weights: weights) is { } d)
                    drops.Add(d);
        }
        if (mapTier > 0)
        {
            double scarabChance = scarabQuantity * (weights?.Scarabs ?? 1);
            if (scarabChance > 0 && MapExtra("drop_scarab_chance_pct", quantity * scarabChance, rng)
                && Scarab(rng, monsterLevel, mods, weights) is { } scarab)
                drops.Add(scarab with { Item = scarab.Item with { ItemLevel = (byte)Math.Clamp(monsterLevel, 1, 100) } });
            if (MapExtra("drop_card_chance_pct", quantity * dropPenalty * (weights?.Cards ?? 1), rng) && Card(rng, monsterLevel, areaId, mapTier, weights) is { } card)
                drops.Add(card);
        }
        else if (!string.IsNullOrEmpty(areaId) && HasCampaignCards(areaId))
        {
            double campaignChance = SystemSettings.Default.DropCampaignCardChancePct / 100.0 * quantity * dropPenalty * (weights?.Cards ?? 1);
            if (campaignChance > 0 && rng.NextDouble() < campaignChance && Card(rng, monsterLevel, areaId, mapTier: 0, weights) is { } card)
                drops.Add(card);
        }
        return drops;
    }

    static int RollExpectedCount(double expected, Random rng)
    {
        if (expected <= 0) return 0;
        double jittered = expected * (0.85 + 0.30 * rng.NextDouble());
        int floor = (int)Math.Floor(jittered);
        return floor + (rng.NextDouble() < jittered - floor ? 1 : 0);
    }

    static Chances WithUniqueMultiplier(Chances chances)
    {
        double mult = SystemSettings.Default.DropUniqueChanceMultiplier;
        return mult == 1.0 ? chances : chances with { Unique = Math.Min(1.0, chances.Unique * mult) };
    }

    /// <summary>First-kill bonus loot for campaign quest bosses (2 guaranteed rares + 1 currency) and act bosses
    /// (3 guaranteed rares + 2 currency), gated by <c>drop_first_kill_boss_bonus</c>.</summary>
    public IReadOnlyList<Drop> RollBossFirstKillBonus(int monsterLevel, Random rng, bool actBoss = true,
        int increasedRarity = 0, double rarityMultiplier = 1)
    {
        if (!SystemSettings.Default.DropFirstKillBossBonus) return [];
        int level = Math.Clamp(monsterLevel, 1, 100);
        int itemLevel = Math.Clamp(level + ItemLevelBonus(MonsterRarity.Unique), 1, 100);
        var chances = WithUniqueMultiplier(WithIncreasedRarity(RarityChances(MonsterRarity.Unique), increasedRarity,
            rarityMultiplier * SystemSettings.Default.DropMultiplier("unique_rarity")));
        int rareCount = actBoss ? 3 : 2;
        int currencyCount = actBoss ? 2 : 1;
        var drops = new List<Drop>(rareCount + currencyCount);
        for (int i = 0; i < rareCount; i++)
        {
            if (PickEquipmentBase(level, inMap: false, rng) is not { } eqType) continue;
            var (sockets, links) = catalog.DropSockets(eqType, itemLevel, rng);
            byte quality = RollNaturalQuality(eqType, rng);
            var item = ItemCatalog.Create(eqType, sockets, links, random: rng) with
            {
                ItemLevel = (byte)itemLevel,
                Quality = quality,
            };
            if (rolls is not null)
            {
                if (rng.NextDouble() < chances.Unique && Unique(eqType, level, itemLevel, rng) is { } unique)
                {
                    item = unique with { Identified = false, Quality = Math.Max(unique.Quality, quality) };
                    eqType = catalog.ByHash(item.TypeHash) ?? eqType;
                }
                else
                {
                    item = rolls.Roll(item, ItemRarity.Rare, rng) with { Identified = false };
                }
            }
            drops.Add(new Drop(item, eqType));
        }
        for (int i = 0; i < currencyCount; i++)
            if (CurrencyDrop(level, rng) is { } cur) drops.Add(cur);
        return drops;
    }

    /// <summary>Curated Act 1 Twilight Strand Hillock starter drop: life/mana flasks, Scrolls of Wisdom, a linked
    /// magic class-appropriate weapon, and a linked armour piece.</summary>
    public IReadOnlyList<Drop> RollHillockStarterDrop(int classRow, Random rng)
    {
        var drops = new List<Drop>(5);
        void AddPlain(string id, ushort stack = 1)
        {
            if (catalog.Find(id) is { Supported: true } t)
                drops.Add(new Drop(ItemCatalog.Create(t, random: rng) with { StackSize = stack, ItemLevel = 2 }, t));
        }
        AddPlain("Metadata/Items/Flasks/FlaskLife1");
        AddPlain("Metadata/Items/Flasks/FlaskMana1");
        AddPlain("Metadata/Items/Currency/CurrencyIdentification", stack: 2);

        // Characters.dat rows: 0 Marauder, 1 Witch, 2 Scion, 3 Ranger, 4 Duelist, 5 Shadow, 6 Templar.
        string[] weaponClasses = classRow switch
        {
            0 => ["One Hand Mace", "Two Hand Mace", "One Hand Axe"],
            1 => ["Wand"],
            2 => ["One Hand Sword", "Thrusting One Hand Sword"],
            3 => ["Bow"],
            4 => ["One Hand Sword", "One Hand Axe"],
            5 => ["Dagger", "Claw"],
            6 => ["Sceptre", "Staff"],
            _ => ["One Hand Sword"],
        };
        var weaponPool = weaponClasses
            .SelectMany(c => _pools.Equipment.GetValueOrDefault(c) ?? [])
            .Where(t => t.Type.DropLevel <= 2 && !t.Atlas)
            .Select(t => t.Type).ToList();
        if (weaponPool.Count > 0)
        {
            var weaponType = weaponPool[rng.Next(weaponPool.Count)];
            var (sockets, links) = catalog.DefaultSockets(weaponType, itemLevel: 2, random: rng);
            var weapon = ItemCatalog.Create(weaponType, sockets, links, random: rng) with { ItemLevel = 2 };
            if (rolls is not null) weapon = rolls.Roll(weapon, ItemRarity.Magic, rng) with { Identified = false };
            drops.Add(new Drop(weapon, weaponType));
        }

        string[] armourClasses = ["Body Armour", "Helmet", "Gloves", "Boots"];
        var armourPool = armourClasses
            .SelectMany(c => _pools.Equipment.GetValueOrDefault(c) ?? [])
            .Where(t => t.Type.DropLevel <= 2 && !t.Atlas)
            .Select(t => t.Type).ToList();
        if (armourPool.Count > 0)
        {
            var armourType = armourPool[rng.Next(armourPool.Count)];
            var (sockets, links) = catalog.DefaultSockets(armourType, itemLevel: 2, random: rng);
            var armour = ItemCatalog.Create(armourType, sockets, links, random: rng) with { ItemLevel = 2 };
            if (rolls is not null)
                armour = rolls.Roll(armour, rng.NextDouble() < 0.25 ? ItemRarity.Rare : ItemRarity.Magic, rng) with { Identified = false };
            drops.Add(new Drop(armour, armourType));
        }
        return drops;
    }

    /// <summary>Our own base chance (percent per kill, scaled by item quantity) until the game's is known; the map
    /// device's own card/scarab stats stay separate.</summary>
    static bool MapExtra(string setting, double quantity, Random rng) =>
        rng.NextDouble() < Math.Clamp(SystemSettings.Default.Get(setting), 0, 100) / 100.0 * quantity;

    /// <summary>A weighted currency drop using this instance's resolved, DropDataProvider-backed currency pool
    /// (respecting <c>drop_currency_weights.drop_level</c>).</summary>
    public Drop? CurrencyDrop(int level, Random rng) => CurrencyDrop(level, rng, null);

    /// <param name="weights">A mod's factor per currency type (<see cref="DropWeights.Item"/>); null for none.</param>
    public Drop? CurrencyDrop(int level, Random rng, DropWeights? weights) =>
        PickCurrency(_pools.Currency, level, rng, weights) is { } picked
            ? new Drop(ItemCatalog.Create(picked) with { StackSize = 1, ItemLevel = (byte)Math.Clamp(level, 1, 100) }, picked)
            : null;

    /// <summary>A drop event's stackable items combined into stacks of their type's maximum, as the game drops currency:
    /// a monster's two Orb of Alteration rolls become one stack of two, and a total past the type's maximum becomes
    /// several stacks (a divine orb's maximum is ten, so twenty-one become 10, 10 and 1). Everything else keeps one
    /// world item each.</summary>
    public static IReadOnlyList<Drop> Stacked(IEnumerable<Drop> drops)
    {
        var result = new List<Drop>();
        var groups = new Dictionary<StackKey, (Drop Drop, int Total)>();
        var order = new List<StackKey>();
        foreach (var drop in drops)
        {
            if (drop.Type.MaxStack <= 1) { result.Add(drop); continue; }
            // The type and the item without its count: two rolls of one currency are the same item whatever their stack.
            var key = StackKey.For(drop.Item);
            if (groups.TryGetValue(key, out var group)) groups[key] = (group.Drop, group.Total + drop.Item.StackSize);
            else { groups[key] = (drop, drop.Item.StackSize); order.Add(key); }
        }
        foreach (var key in order)
        {
            var (first, total) = groups[key];
            for (int left = total; left > 0; left -= first.Type.MaxStack)
                result.Add(new Drop(first.Item with { StackSize = (ushort)Math.Min(left, first.Type.MaxStack) }, first.Type));
        }
        return result;
    }

    readonly struct StackKey : IEquatable<StackKey>
    {
        readonly uint _typeHash;
        readonly ushort _headerFlags, _visualIdentity;
        readonly byte _flags, _quality;
        readonly byte[]? _payload;
        readonly int _hash;

        StackKey(uint typeHash, ushort headerFlags, ushort visualIdentity, byte flags, byte quality, byte[]? payload, int hash)
        {
            _typeHash = typeHash;
            _headerFlags = headerFlags;
            _visualIdentity = visualIdentity;
            _flags = flags;
            _quality = quality;
            _payload = payload;
            _hash = hash;
        }

        public static StackKey For(Item item)
        {
            if (IsPlainStackable(item, out bool hasQuality))
            {
                ushort header = (ushort)(item.HeaderFlags & 0x01ff);
                byte flags = (byte)((item.Corrupted ? 0x80 : 0) | (hasQuality ? 0x40 : 0) | ((byte)item.Influences & 0x3f));
                byte quality = hasQuality && item.AlternateQualityType is null ? item.Quality : (byte)0;
                int hash = HashCode.Combine(item.TypeHash, header, item.VisualIdentity, flags, quality);
                return new StackKey(item.TypeHash, header, item.VisualIdentity, flags, quality, null, hash);
            }
            byte[] payload = (item with { StackSize = 1 }).Payload();
            uint fnv = 2166136261u;
            for (int i = 0; i < payload.Length; i++) fnv = (fnv ^ payload[i]) * 16777619u;
            return new StackKey(item.TypeHash, 0, 0, 0, 0, payload, (int)fnv);
        }

        static bool IsPlainStackable(Item item, out bool hasQuality)
        {
            hasQuality = false;
            bool hasBase = false;
            if (item.Price.Count > 0 || item.SkinTransferSource is not null) return false;
            var components = item.Components;
            for (int i = 0; i < components.Count; i++)
            {
                string c = components[i];
                if (c == "Base") { hasBase = true; continue; }
                if (c == "Stack" || Item.NoData.Contains(c)) continue;
                if (c == "Quality") { hasQuality = true; continue; }
                return false;
            }
            return hasBase;
        }

        public bool Equals(StackKey other)
        {
            if (_payload is null && other._payload is null)
                return _typeHash == other._typeHash && _headerFlags == other._headerFlags
                    && _visualIdentity == other._visualIdentity && _flags == other._flags
                    && _quality == other._quality;
            if (_payload is not null && other._payload is not null)
                return _payload.AsSpan().SequenceEqual(other._payload);
            return false;
        }

        public override bool Equals(object? obj) => obj is StackKey other && Equals(other);
        public override int GetHashCode() => _hash;
    }

    /// <summary>The gold item (ItemClasses "Gold"): on the ground a pile of <see cref="Item.StackSize"/> gold, which a pickup
    /// adds to the stash's gold instead of the inventory.</summary>
    public const string GoldId = "Metadata/Items/Currency/GoldCoin", GoldClass = "Gold";

    static int[] LoadGoldRespecCosts()
    {
        if (!ClientTables.Has("VillageBalancePerLevelShared")) return [];
        var table = ClientTables.Table("VillageBalancePerLevelShared");
        var costs = new int[table.Count];
        for (int i = 0; i < table.Count; i++)
            costs[i] = Math.Max(1, table[i].Int("GoldRespecCost"));
        return costs;
    }

    // Gentle gold base curve tracking VillageBalancePerLevelShared.GoldRespecCost across levels 1-100:
    // ~5 at L1, ~12 at L20, ~37 at L45, ~98 at L68, ~230 at L80, ~926 at L100.
    static double GoldBaseForLevel(int monsterLevel, int[]? respecCosts = null)
    {
        int clamped = Math.Clamp(monsterLevel, 1, 100);
        double respecCost = respecCosts is { Length: > 0 }
            ? respecCosts[Math.Min(clamped - 1, respecCosts.Length - 1)]
            : 4.0 * Math.Exp(0.088 * (clamped - 1));
        return 3.0 + 1.15 * Math.Pow(Math.Max(1.0, respecCost), 0.65);
    }

    /// <summary>The gold a dying monster drops, 0 for none: normal monsters 40% of the time, magic 75%, rare and unique
    /// always; base curve tracking <c>VillageBalancePerLevelShared.GoldRespecCost</c> × 1 / 2 / 5 / 15 by rarity, ±25%.</summary>
    public static int GoldAmount(int monsterLevel, MonsterRarity rarity, Random rng) =>
        GoldAmountCore(monsterLevel, rarity, rng, null);

    static int GoldAmountCore(int monsterLevel, MonsterRarity rarity, Random rng, int[]? respecCosts)
    {
        double chance = rarity switch { MonsterRarity.Normal => 0.40, MonsterRarity.Magic => 0.75, _ => 1 };
        chance = Math.Min(1, chance * SystemSettings.Default.GoldChanceMultiplier);
        if (chance <= 0 || rng.NextDouble() >= chance) return 0;
        double times = rarity switch { MonsterRarity.Magic => 2, MonsterRarity.Rare => 5, MonsterRarity.Unique => 15, _ => 1 };
        return Math.Max(1, (int)Math.Round(GoldBaseForLevel(monsterLevel, respecCosts) * times * (0.75 + 0.5 * rng.NextDouble())));
    }

    public Drop? Gold(int monsterLevel, MonsterRarity rarity, Random rng, double multiplier = 1) =>
        Gold(monsterLevel, rarity, rng, multiplier, increasedRarity: 0, quantityFactor: 1);

    /// <summary>A pile of gold for a dying monster (<see cref="GoldAmount"/>), scaled by <paramref name="multiplier"/>,
    /// effective IIR (<paramref name="increasedRarity"/>, already diminished by the caller) and area IIQ
    /// (<paramref name="quantityFactor"/>), or null for none.</summary>
    public Drop? Gold(int monsterLevel, MonsterRarity rarity, Random rng, double multiplier,
        int increasedRarity, double quantityFactor = 1.0)
    {
        double iirScale = Math.Max(0.0, 1.0 + increasedRarity / 100.0);
        double iiqScale = double.IsFinite(quantityFactor) ? Math.Max(0, quantityFactor) : 1.0;
        double totalMult = Math.Max(0, multiplier) * iirScale * iiqScale;
        return (int)Math.Floor(GoldAmountCore(monsterLevel, rarity, rng, _goldRespecCosts) * totalMult) is int amount and > 0
            && catalog.Find(GoldId) is { Supported: true } type
            ? new Drop(ItemCatalog.Create(type) with { StackSize = (ushort)Math.Min(amount, ushort.MaxValue) }, type) : null;
    }

    /// <summary>The King in the Mists' four guaranteed uniques, by the wiki's crowdsourced 3.26 shares (n=576). They are
    /// absolute weights in one pick, so the sum is the distribution and the boss always drops exactly one of them.</summary>
    public static readonly IReadOnlyList<(string Name, double Weight)> KingInMistsUniques =
    [
        ("The Untouched Soul", 40), ("Pragmatism", 35), ("The Light of Meaning", 20), ("The Burden of Shadows", 5),
    ];

    /// <summary>The boss-only gem drops (wiki 3.28: Hextoad 6%, Hexpass 10%) and the drop-restricted card.</summary>
    public static readonly IReadOnlyList<(string Id, double Chance)> KingInMistsExtras =
    [
        ("Metadata/Items/Gems/SupportGemHextoad", 0.06),
        ("Metadata/Items/Gems/SupportGemHexpass", 0.10),
        ("Metadata/Items/DivinationCards/DivinationCardTheEndlessDarkness", 0.15),
    ];

    public IReadOnlyList<Drop> EncounterJewels(string bossId, int monsterLevel, Random random, DropDataProvider.Snapshot data)
    {
        if (uniques is null || data.Bosses.TryGetValue(bossId, out var boss) && !boss.Enabled) return [];
        var result = new List<Drop>();
        foreach (var row in data.BossItems.Values)
        {
            if (row.BossId != bossId || row.ItemKind != "unique" || (row.OverrideChance ?? row.Chance) is not (> 0 and <= 1 and var chance))
                continue;
            if (uniques.Find(row.ItemId) is not { EncounterOnly: true } definition || definition.Name != row.ItemId
                || random.NextDouble() >= chance) continue;
            int bonus = definition.EncounterItemLevelBonus ?? ItemLevelBonus(MonsterRarity.Unique);
            byte itemLevel = (byte)Math.Clamp(monsterLevel + (long)bonus, 1, 100);
            if (uniques.Create(definition with { ItemLevel = itemLevel }, random) is not { } jewel
                || catalog.ByHash(jewel.TypeHash) is not { } type) continue;
            result.Add(new(jewel with { Identified = false }, type));
        }
        return result;
    }

    /// <summary>The King in the Mists' guaranteed unique, then its support gems and card. Corpse items (the 50% roll)
    /// are not modelled yet: the item system has no itemised-corpse consumer.</summary>
    public IReadOnlyList<Drop> KingInMists(int level, Random rng)
    {
        var drops = new List<Drop>();
        if (uniques is not null && KingInMistsUniques.Sum(u => u.Weight) is > 0 and var total)
        {
            double pick = rng.NextDouble() * total;
            var chosen = KingInMistsUniques[^1].Name;
            foreach (var (name, weight) in KingInMistsUniques)
            {
                if (pick < weight) { chosen = name; break; }
                pick -= weight;
            }
            bool light = false;
            if (uniques.Create(chosen, rng) is { } item && catalog.ByHash(item.TypeHash) is { } type)
            {
                drops.Add(new(item with { ItemLevel = (byte)Math.Clamp(level, 1, 100), Identified = false }, type));
                light = chosen == "The Light of Meaning";
            }
            // The Light of Meaning replaced That Which Was Taken in the 3.25 pool, but they are paired here: whenever the
            // Light is the rolled unique, the original Crimson Jewel falls with it in the same drop event.
            if (light
                && uniques.Create("That Which Was Taken", rng) is { } taken && catalog.ByHash(taken.TypeHash) is { } takenType)
                drops.Add(new(taken with { ItemLevel = (byte)Math.Clamp(level, 1, 100), Identified = false }, takenType));
        }
        foreach (var (id, chance) in KingInMistsExtras)
            if (rng.NextDouble() < chance && catalog.Find(id) is { Supported: true } type)
                drops.Add(new(ItemCatalog.Create(type) with { ItemLevel = (byte)Math.Clamp(level, 1, 100) }, type));
        return drops;
    }

    int MinCampaignMapDropLevel()
    {
        if (_minCampaignMapDropLevel >= 0) return _minCampaignMapDropLevel;
        return _minCampaignMapDropLevel = maps?.KeyOfTier(1) is uint t1 && catalog.ByHash(t1) is { DropLevel: > 0 } t1Type
            ? t1Type.DropLevel : 58;
    }

    /// <summary>A generic map within the monster's tier eligibility, or a tier 1 map in the late campaign once
    /// monsters meet its DropLevel. Our own rates until the game pools are known. <paramref name="chanceFactor"/>
    /// scales the chance (a monster's or league's map-related drop chance), and <paramref name="tierUpgradePermillage"/>
    /// rolls a +1 tier upgrade (clamped to 16) only after the map drop roll succeeds.</summary>
    public Drop? Map(int monsterLevel, MonsterRarity monster, int mapTier, Random rng, int voidstones = 0,
        double chanceFactor = 1, int tierUpgradePermillage = 0) =>
        Map(monsterLevel, monster, mapTier, rng, mapBoss: false, voidstones, chanceFactor, tierUpgradePermillage);

    /// <summary>Use the area's declared boss roster to admit natural drops up to two tiers higher.</summary>
    public Drop? Map(int monsterLevel, MonsterRarity monster, int mapTier, Random rng, bool mapBoss,
        int voidstones = 0, double chanceFactor = 1, int tierUpgradePermillage = 0)
    {
        if (maps is null || (mapTier <= 0 && monsterLevel < MinCampaignMapDropLevel())) return null;
        double chance = monster switch { MonsterRarity.Normal => 0.004, MonsterRarity.Magic => 0.012, MonsterRarity.Rare => 0.05, _ => 0.20 };
        chance = Math.Min(1, chance * SystemSettings.Default.DropMultiplier("maps") * Math.Max(0, chanceFactor));
        if (chance <= 0 || rng.NextDouble() >= chance) return null;
        int stones = Math.Max(0, voidstones);
        int tier = Math.Clamp(mapTier, 1, 16);
        int bonus = mapTier <= 0 ? 0 : monster switch
        {
            MonsterRarity.Magic or MonsterRarity.Rare => 1,
            MonsterRarity.Unique when mapBoss => 2,
            _ => 0,
        };
        int highest = Math.Min(16, tier + bonus);
        if (highest > tier) tier = rng.Next(tier, highest + 1);
        if (stones > 0) tier = Math.Min(16, tier + 1);
        if (tierUpgradePermillage > 0 && mapTier > 0 && tier < 16
            && rng.NextDouble() < Math.Min(1000, tierUpgradePermillage) / 1000d)
            tier = Math.Min(16, tier + 1);
        // Socketed Voidstones convert found maps below tier 16 to gold (maps.md).
        if (stones > 0 && tier < 16) return Gold(monsterLevel, monster, rng);
        if (maps.KeyOfTier(tier) is not uint hash || catalog.ByHash(hash) is not { Supported: true } type
            || (mapTier <= 0 && monsterLevel < type.DropLevel)) return null;
        var item = ItemCatalog.Create(type) with { ItemLevel = (byte)Math.Clamp(monsterLevel + ItemLevelBonus(monster), 1, 100) };
        if (rolls is not null && rng.NextDouble() < 0.10) item = rolls.Roll(item, ItemRarity.Magic, rng) with { Identified = true };
        return new(item, type);
    }

    // Exceptional support gems (Empower, Enhance, Enlighten) drop in a separate rare sub-roll (0.15% of gem drops at L38+).
    const double ExceptionalGemChance = 0.0015;

    // One item: its kind, for equipment its slot and level-weighted base, then its rarity (stages 2 to 4).
    Drop? RollOne(int level, int itemLevel, Chances chances, Random rng, bool inMap = false, double dropPenalty = 1.0, DropWeights? weights = null)
    {
        var pools = _pools;
        var settings = SystemSettings.Default;
        double currencyWeight = settings.DropWeightCurrency * settings.DropMultiplier("currency") * (weights?.Currency ?? 1);
        double gemWeight = settings.DropWeightGems * settings.DropMultiplier("gems") * (weights?.Gems ?? 1);
        double flaskWeight = settings.DropWeightFlasks * settings.DropMultiplier("flasks") * (weights?.Flasks ?? 1);
        double equipmentWeight = settings.DropWeightEquipment * settings.DropMultiplier("equipment") * (weights?.Equipment ?? 1);
        double totalWeight = currencyWeight + gemWeight + flaskWeight + equipmentWeight;
        if (totalWeight <= 0) return null;
        double kind = rng.NextDouble() * totalWeight;
        ItemCatalog.BaseType? type = null;
        if (kind < currencyWeight)
        {
            if (dropPenalty < 1.0 && rng.NextDouble() >= dropPenalty) return null;
            type = PickCurrency(pools.Currency, level, rng, weights);
        }
        else if (kind < currencyWeight + gemWeight)
        {
            type = PickGem(pools, level, rng);
        }
        else if (kind < currencyWeight + gemWeight + flaskWeight)
        {
            type = PickFlask(pools, level, rng);
        }
        else
        {
            type = PickEquipmentBase(level, inMap, rng);
        }
        return type is null ? null : Rolled(type, level, itemLevel, chances, rng);
    }

    static ItemCatalog.BaseType? PickGem(Pools pools, int level, Random rng)
    {
        if (pools.ExceptionalGems.Count > 0 && rng.NextDouble() < ExceptionalGemChance)
        {
            var exceptional = pools.ExceptionalGems.Where(g => g.DropLevel <= level).ToList();
            if (exceptional.Count > 0) return exceptional[rng.Next(exceptional.Count)];
        }
        var regular = pools.RegularGems.Where(g => g.DropLevel <= level).ToList();
        return regular.Count > 0 ? regular[rng.Next(regular.Count)] : null;
    }

    // Balance flask sub-pools so Tinctures do not take 31% of flask drops (~55% Recovery / 35% Utility / 10% Tincture).
    // Recovery flasks use level-weighted tiering; Utility flasks & Tinctures stay accessible at all levels once unlocked.
    static ItemCatalog.BaseType? PickFlask(Pools pools, int level, Random rng)
    {
        var recovery = pools.RecoveryFlasks.Where(f => f.DropLevel <= level).ToList();
        var utility = pools.UtilityFlasks.Where(f => f.DropLevel <= level).ToList();
        var tincture = pools.Tinctures.Where(f => f.DropLevel <= level).ToList();
        double wRec = recovery.Count > 0 ? 55.0 : 0;
        double wUtil = utility.Count > 0 ? 35.0 : 0;
        double wTinc = tincture.Count > 0 ? 10.0 : 0;
        double total = wRec + wUtil + wTinc;
        if (total <= 0) return null;
        double pick = rng.NextDouble() * total;
        if (pick < wRec) return PickLevelWeighted(recovery, level, rng);
        if (pick < wRec + wUtil) return utility[rng.Next(utility.Count)];
        return tincture[rng.Next(tincture.Count)];
    }

    ItemCatalog.BaseType? PickEquipmentBase(int level, bool inMap, Random rng)
    {
        var pools = _pools;
        var slots = Slots.Where(s => pools.Equipment.GetValueOrDefault(s.Class)?.Any(t => t.Type.DropLevel <= level && (inMap || !t.Atlas)) == true).ToList();
        if (Pick(slots, rng) is not { } slot) return null;
        var bases = pools.Equipment[slot].Where(t => t.Type.DropLevel <= level && (inMap || !t.Atlas)).Select(t => t.Type).ToList();
        return bases.Count > 0 ? PickLevelWeighted(bases, level, rng) : null;
    }

    // Level-weighted base tiering: strongly favours bases within ~16 levels of the highest available tier and
    // progressively retires obsolete low-level bases (while still letting low-tier bases appear rarely).
    static double BaseLevelWeight(int baseDropLevel, int monsterLevel, int maxDropLevel)
    {
        int effectiveCap = Math.Min(monsterLevel, maxDropLevel);
        int gap = Math.Max(0, effectiveCap - baseDropLevel);
        if (gap <= 8) return 1.0;
        if (gap <= 16) return 0.55;
        if (gap <= 24) return 0.20;
        if (gap <= 35) return 0.05;
        return 0.01;
    }

    static ItemCatalog.BaseType PickLevelWeighted(List<ItemCatalog.BaseType> bases, int monsterLevel, Random rng)
    {
        if (bases.Count == 1) return bases[0];
        int maxDropLevel = 0;
        for (int i = 0; i < bases.Count; i++)
            if (bases[i].DropLevel > maxDropLevel) maxDropLevel = bases[i].DropLevel;
        double total = 0;
        for (int i = 0; i < bases.Count; i++)
            total += BaseLevelWeight(bases[i].DropLevel, monsterLevel, maxDropLevel);
        if (total <= 0) return bases[rng.Next(bases.Count)];
        double pick = rng.NextDouble() * total;
        for (int i = 0; i < bases.Count; i++)
        {
            double w = BaseLevelWeight(bases[i].DropLevel, monsterLevel, maxDropLevel);
            if (pick < w) return bases[i];
            pick -= w;
        }
        return bases[^1];
    }

    /// <summary>A base type as a monster of this level and rarity drops it: its sockets, and for equipment, jewels and
    /// flasks a rarity by the kill's chances (as <see cref="Roll"/>: unique, rare, magic, else normal). Null when it is
    /// none the server can create.</summary>
    public Drop? RollBase(ItemCatalog.BaseType type, int monsterLevel, MonsterRarity rarity, Random rng, int increasedRarity = 0,
        double rarityMultiplier = 1)
    {
        if (!type.Supported) return null;
        var chances = WithUniqueMultiplier(WithIncreasedRarity(RarityChances(rarity), increasedRarity,
            rarityMultiplier * SystemSettings.Default.DropMultiplier("unique_rarity")));
        return Rolled(type, monsterLevel, monsterLevel + ItemLevelBonus(rarity), chances, rng);
    }

    static bool RollsRarity(ItemCatalog.BaseType type) => EquipmentClasses.Contains(type.Class) || type.Class is "Jewel" or "AbyssJewel";

    static bool IsFlask(ItemCatalog.BaseType type) =>
        type.Class is "LifeFlask" or "ManaFlask" or "HybridFlask" or "UtilityFlask" or "UtilityFlaskCritical";

    // Natural 1-20% quality roll weighted toward lower values (mean ~8-9%) on equipment, flasks and gems.
    static byte RollNaturalQuality(ItemCatalog.BaseType type, Random rng)
    {
        if (!type.Components.Contains("Quality")) return 0;
        double chance = SystemSettings.Default.DropNaturalQualityChancePct / 100.0;
        if (chance <= 0 || rng.NextDouble() >= chance) return 0;
        return (byte)Math.Clamp(1 + (int)(Math.Pow(rng.NextDouble(), 1.5) * 20), 1, 20);
    }

    Drop? Rolled(ItemCatalog.BaseType type, int level, int itemLevel, Chances chances, Random rng)
    {
        var (sockets, links) = catalog.DropSockets(type, itemLevel, rng);
        byte quality = RollNaturalQuality(type, rng);
        var item = ItemCatalog.Create(type, sockets, links, random: rng) with
        {
            // A dropped gem is unlevelled, like a vendor's: its level comes from its experience (ItemCatalog.GemLevel).
            ItemLevel = catalog.Gem(type) is not null ? (byte)1 : (byte)Math.Clamp(itemLevel, 1, 100),
            Quality = quality,
        };
        if (rolls is not null && RollsRarity(type))
        {
            // Magic, rare and unique items drop unidentified (a Scroll of Wisdom identifies them, C→S 0x71).
            bool rolledUnique = rng.NextDouble() < chances.Unique;
            if (rolledUnique && Unique(type, level, itemLevel, rng) is { } unique)
            {
                // The unique's own base row (an Id can name another row than the one rolled).
                item = unique with { Identified = false, Quality = Math.Max(unique.Quality, quality) };
                type = catalog.ByHash(item.TypeHash) ?? type;
            }
            else if (rolledUnique || rng.NextDouble() < chances.Rare) item = rolls.Roll(item, ItemRarity.Rare, rng) with { Identified = false };
            else if (type.Class is "Jewel" or "AbyssJewel" || rng.NextDouble() < chances.Magic) item = rolls.Roll(item, ItemRarity.Magic, rng) with { Identified = false };
        }
        else if (rolls is not null && IsFlask(type))
        {
            // Life, Mana, Hybrid and Utility flasks can roll Unique (if the base has one) or Magic (~18%+ scaled by IIR).
            bool rolledUnique = rng.NextDouble() < chances.Unique;
            if (rolledUnique && Unique(type, level, itemLevel, rng) is { } unique)
            {
                item = unique with { Identified = false, Quality = Math.Max(unique.Quality, quality) };
                type = catalog.ByHash(item.TypeHash) ?? type;
            }
            else if (rolledUnique || rng.NextDouble() < chances.Magic)
                item = rolls.Roll(item, ItemRarity.Magic, rng) with { Identified = false };
        }
        else if (rolls is not null && type.Class == "Tincture")
        {
            // Tinctures are magic-only (plus unique): never roll rare on drop.
            bool rolledUnique = rng.NextDouble() < chances.Unique;
            if (rolledUnique && Unique(type, level, itemLevel, rng) is { } unique)
            {
                item = unique with { Identified = false, Quality = Math.Max(unique.Quality, quality) };
                type = catalog.ByHash(item.TypeHash) ?? type;
            }
            else
                item = rolls.Roll(item, ItemRarity.Magic, rng) with { Identified = false };
        }
        return new Drop(item, type);
    }

    /// <summary>Try rolling a drop-eligible unique of <paramref name="type"/> at <paramref name="monsterLevel"/>, respecting
    /// encounter-only exclusions, T0 drop-level floors, and unique weights.</summary>
    public Item? TryUnique(ItemCatalog.BaseType type, int monsterLevel, int itemLevel, Random rng) =>
        Unique(type, monsterLevel, itemLevel, rng);

    /// <summary>Whether <paramref name="definition"/> is eligible for general drops at <paramref name="monsterLevel"/>
    /// (not encounter-only, and meeting both its mod-derived drop level and T0 drop-level floor).</summary>
    public bool IsDropEligibleUnique(UniqueCatalog.Definition definition, int monsterLevel)
    {
        if (definition.EncounterOnly) return false;
        if (GetUniqueIndex() is { } index
            && index.ByBase.TryGetValue(definition.BaseTypeId, out var entries))
        {
            foreach (var entry in entries)
                if (ReferenceEquals(entry.Definition, definition)
                    || entry.Definition.Name.Equals(definition.Name, StringComparison.OrdinalIgnoreCase))
                    return entry.DropLevel <= monsterLevel;
        }
        return EffectiveUniqueDropLevel(definition, _uniqueWeightProvider.HasTable || _uniqueWeightsFile.Count > 0) <= monsterLevel;
    }

    // Endgame T0 uniques whose low-level base types (e.g. Leather/Heavy Belt, Paua Amulet) would otherwise allow them
    // to leak into Acts 4-5 campaign areas; enforce a minimum drop level of 75.
    static readonly HashSet<string> TierZeroUniques = new(StringComparer.OrdinalIgnoreCase)
    {
        "Mageblood", "Headhunter", "The Squire", "Kalandra's Touch", "Defiance of Destiny",
        "Reefbane", "The Wellhook", "Rakiata's Dance", "Soul Taker", "Voltaxic Rift",
        "Bino's Kitchen Knife", "Marohi Erqi", "Bloodseeker", "Lioneye's Glare", "Divinarius",
        "Essentia Sanguis", "Kingmaker", "Varunastra", "Original Sin", "Stasis Prison",
        "Angler's Plait", "Echoforge", "Starforge", "Voidforge", "Atziri's Disfavour",
        "The Saviour", "Hateforge", "Uul-Netol's Vow", "Nimis",
    };

    const int TierZeroMinDropLevel = 75;

    int EffectiveUniqueDropLevel(UniqueCatalog.Definition definition, bool hasWeights)
    {
        int dropLevel = UniqueDropLevel(definition, catalog.Find(definition.BaseTypeId)?.DropLevel ?? 1, catalog.Mods);
        if (TierZeroUniques.Contains(definition.Name)
            || (hasWeights && UniqueWeight(definition.Name) is > 0 and <= 0.002))
            dropLevel = Math.Max(dropLevel, TierZeroMinDropLevel);
        return dropLevel;
    }

    UniqueIndex? GetUniqueIndex()
    {
        if (uniques is null) return null;
        var all = uniques.All;
        var index = _uniques;
        if (index is null || !ReferenceEquals(index.From, all))
        {
            var byBase = new Dictionary<string, List<UniqueEntry>>(StringComparer.Ordinal);
            bool hasWeights = _uniqueWeightProvider.HasTable || _uniqueWeightsFile.Count > 0;
            foreach (var definition in all)
            {
                if (definition.EncounterOnly) continue;
                int dropLevel = EffectiveUniqueDropLevel(definition, hasWeights);
                if (!byBase.TryGetValue(definition.BaseTypeId, out var list)) byBase[definition.BaseTypeId] = list = [];
                list.Add(new(definition, dropLevel));
            }
            _uniques = index = new(all, byBase);
        }
        return index;
    }

    // A unique of this base legal at the monster's level, or null when the base has none the server can create (then
    // the item rolls on as rare or magic, as the game makes it rare).
    Item? Unique(ItemCatalog.BaseType type, int monsterLevel, int itemLevel, Random rng)
    {
        if (GetUniqueIndex() is not { } index || !index.ByBase.TryGetValue(type.Id, out var candidates)) return null;
        var allowed = candidates.Where(c => c.DropLevel <= monsterLevel).Select(c => c.Definition).ToList();
        if (allowed.Count == 0 || PickUnique(allowed, rng) is not { } chosen) return null;
        return uniques!.Create(chosen with { ItemLevel = (byte)Math.Clamp(itemLevel, 1, 100) }, rng);
    }

    // Minimum monster level for a unique drop: at least its base DropLevel and its mod-derived required level (matching
    // ItemModStats.LevelRequirement's Mods.dat Level * 4 / 5 rule), so high-level uniques on low-level bases cannot
    // drop from low-level campaign monsters.
    static int UniqueDropLevel(UniqueCatalog.Definition definition, int baseDropLevel, ModCatalog mods)
    {
        int required = Math.Max(1, baseDropLevel);
        int plus = 0;
        int? over = null;
        void Inspect(string[]? ids)
        {
            if (ids is null) return;
            foreach (string id in ids)
            {
                if (mods.ById(id) is not { } mod) continue;
                required = Math.Max(required, mod.Level * 4 / 5);
                for (int i = 0; i < mod.Stats.Count; i++)
                {
                    if (!mod.Stats[i].Local) continue;
                    uint stat = mod.Stats[i].Id;
                    if (stat == Stat.LocalLevelRequirementOverride)
                    {
                        int v = mod.RangeAt(i).Min;
                        if (v != 0) over = v;
                    }
                    else if (stat == Stat.LocalLevelRequirementPlus)
                        plus += mod.RangeAt(i).Min;
                }
            }
        }
        Inspect(definition.Implicits);
        Inspect(definition.Mods);
        int level = over is int o ? Math.Max(0, o) : Math.Max(0, required + plus);
        return Math.Max(baseDropLevel, level);
    }

    /// <summary>One of a base's uniques by weight, or null. The weights are absolute: while a base's add up to less than 1
    /// the rest is no unique, so a base whose only unique is a Mageblood still gives one seldom.</summary>
    public UniqueCatalog.Definition? PickUnique(IReadOnlyList<UniqueCatalog.Definition> candidates, Random rng)
    {
        double pick = rng.NextDouble() * Math.Max(1, candidates.Sum(d => UniqueWeight(d.Name)));
        foreach (var definition in candidates)
        {
            double weight = UniqueWeight(definition.Name);
            if (pick < weight) return definition;
            pick -= weight;
        }
        return null;
    }

    /// <summary>How likely a unique is once its base rolled one: the inverse of its disenchant value
    /// (VillageUniqueDisenchantValues, HardMode has its own column), the client's own measure of how rare a unique is:
    /// Wanderlust 1.06, Kaom's Heart 43, Mageblood 891. The table leaves out old names and some non-equipment; those do not
    /// drop. The Gateway publishes the seeded/overridden weights through <see cref="UniqueWeightProvider"/>; without a
    /// reference table the extracted JSON is the fallback and every unique weighs 1.</summary>
    public double UniqueWeight(string name) =>
        _uniqueWeightProvider.HasTable
            ? _uniqueWeightProvider.Weight(name, hardMode)
            : _uniqueWeightsFile is { Count: > 0 } weights ? weights.GetValueOrDefault(UniqueCatalog.NameKey(name)) : 1;

    private readonly Dictionary<string, double> _uniqueWeightsFile;

    static Dictionary<string, double> LoadUniqueWeights(string folder, bool hardMode)
    {
        var weights = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        if (!ClientTables.Has("VillageUniqueDisenchantValues") || !ClientTables.Has("Words")) return weights;
        var wordsDoc = ClientTables.Table("Words");
        foreach (var row in ClientTables.Table("VillageUniqueDisenchantValues"))
        {
            double value = row.Real(hardMode ? "RuthlessValue" : "Value");
            if (value > 0 && wordsDoc[row.Int("UniqueName")].Text("Text") is { Length: > 0 } text)
                weights[UniqueCatalog.NameKey(text)] = Math.Min(1, 1 / value);
        }
        return weights;
    }

    // Allocation-free: a kill's currency rolls run here. A mod's factors (DropWeights.Item) multiply each type's weight.
    static ItemCatalog.BaseType? PickCurrency(List<(ItemCatalog.BaseType Type, double Weight, int DropLevel)> pool, int level, Random rng,
        DropWeights? factors = null)
    {
        if (factors is not { Items.Count: > 0 }) factors = null;
        double total = 0;
        for (int i = 0; i < pool.Count; i++)
            if (pool[i].DropLevel <= level && Weight(pool[i]) is var w && w > 0) total += w;
        if (total <= 0) return null;
        double pick = rng.NextDouble() * total;
        ItemCatalog.BaseType? last = null;
        for (int i = 0; i < pool.Count; i++)
        {
            var entry = pool[i];
            if (entry.DropLevel > level) continue;
            double weight = Weight(entry);
            if (weight <= 0) continue;
            last = entry.Type;
            if (pick < weight) return entry.Type;
            pick -= weight;
        }
        return last;

        double Weight((ItemCatalog.BaseType Type, double Weight, int DropLevel) entry) =>
            factors is null ? entry.Weight : entry.Weight * factors.Item(entry.Type.Id);
    }

    static T? Pick<T>(List<(T Value, double Weight)> weighted, Random rng) where T : class
    {
        double total = weighted.Sum(w => Math.Max(0, w.Weight));
        if (total <= 0) return null;
        double pick = rng.NextDouble() * total;
        foreach (var (value, weight) in weighted)
        {
            if (pick < weight) return value;
            pick -= Math.Max(0, weight);
        }
        return weighted[^1].Value;
    }

    private static Pools Build(ItemCatalog catalog, string folder, bool hardMode, LeagueRules? league)
    {
        var currency = new List<(ItemCatalog.BaseType Type, double Weight, int DropLevel)>();
        // The seeded/overridden database table wins; the compiled table is the fallback while it is empty or the
        // database fails. Either way the higher of the client's DropLevel and the table's drop_level gates the roll.
        var seeded = DropDataProvider.Default.Current.CurrencyWeights;
        foreach (var row in seeded.Values)
            if (row is { Enabled: true, Weight: > 0 }
                && catalog.Find(row.CurrencyId) is { Supported: true } t && t.Id == row.CurrencyId)
                currency.Add((t, row.Weight, Math.Max(t.DropLevel, row.DropLevel)));
        if (currency.Count == 0)
            foreach (var (id, weight) in Currency)
                if (catalog.Find(id) is { Supported: true } t && t.Id == id) currency.Add((t, weight, t.DropLevel));
        var equipment = new Dictionary<string, List<(ItemCatalog.BaseType Type, bool Atlas)>>(StringComparer.Ordinal);
        var recoveryFlasks = new List<ItemCatalog.BaseType>();
        var utilityFlasks = new List<ItemCatalog.BaseType>();
        var tinctures = new List<ItemCatalog.BaseType>();
        for (int row = 0; catalog.Row(row) is { } t; row++)
        {
            if (!Droppable(t)) continue;
            if (EquipmentClasses.Contains(t.Class))
            {
                if (!equipment.TryGetValue(t.Class, out var list)) equipment[t.Class] = list = [];
                list.Add((t, t.Tags.Contains("atlas_base_type")));
            }
            else if (t.Class is "LifeFlask" or "ManaFlask" or "HybridFlask") recoveryFlasks.Add(t);
            else if (t.Class is "UtilityFlask" or "UtilityFlaskCritical") utilityFlasks.Add(t);
            else if (t.Class == "Tincture" && catalog.IsNativeTincture(t)) tinctures.Add(t);
        }
        // Obtainable gems only (GemSources): the data's Graft, legacy and placeholder rows are not normal drops.
        var pool = GemSources.DropPool(catalog, folder);
        var regularGems = new List<ItemCatalog.BaseType>();
        var exceptionalGems = new List<ItemCatalog.BaseType>();
        foreach (int row in pool)
            if (catalog.Row(row) is { DropLevel: >= 1 } type
                && !(hardMode && type.Id == "Metadata/Items/Gems/SkillGemPortal")
                && (league?.AllowsGemBase(type.Id) != false))
            {
                if (GemSources.IsExceptionalSupport(type)) exceptionalGems.Add(type);
                else regularGems.Add(type);
            }
        return new(currency, equipment, recoveryFlasks, utilityFlasks, tinctures, regularGems, exceptionalGems);
    }
}
