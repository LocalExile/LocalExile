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
    readonly DropDataProvider _dropDataProvider;
    readonly DropPoolOverlays _dropOverlays;
    readonly string? _leagueId;
    readonly Lock _rebuildLock = new();

    public ItemDrops(ItemCatalog catalog, ItemRolls? rolls = null, string folder = "data", bool hardMode = false, AtlasCatalog? maps = null,
        UniqueCatalog? uniques = null, UniqueWeightProvider? uniqueWeights = null, LeagueRules? league = null,
        DropDataProvider? dropData = null, string? leagueId = null)
    {
        this.catalog = catalog;
        this.rolls = rolls;
        this.folder = folder;
        this.hardMode = hardMode;
        this.maps = maps;
        this.uniques = uniques;
        this.uniqueWeights = uniqueWeights;
        this.league = league;
        _dropDataProvider = dropData ?? DropDataProvider.Default;
        _dropOverlays = _dropDataProvider.Overlays;
        _leagueId = leagueId;
        _uniqueWeightProvider = uniqueWeights ?? UniqueWeightProvider.Default;
        _scarabTypeStats = LoadScarabTypeStats();
        _uniqueWeightsFile = LoadUniqueWeights(folder, hardMode);
        _mapWorldAreaNames = LoadMapWorldAreaNames();
        _worldAreaTags = LoadWorldAreaTags();
        _state = BuildState(
            _dropDataProvider.Current,
            _dropOverlays.Revision,
            catalog.Revision,
            uniques?.Revision ?? UniqueCatalog.OverlayRevision,
            _uniqueWeightProvider.Revision,
            GemSources.Revision);
    }

    readonly UniqueWeightProvider _uniqueWeightProvider;
    readonly Dictionary<string, string> _mapWorldAreaNames;
    readonly Dictionary<string, HashSet<string>> _worldAreaTags;
    PoolState _state;

    /// <summary>Rebuilds pools lazily whenever drop overlays, drop tables, catalogs, unique weights, or gem sources change.</summary>
    public long PoolRevision => CurrentState().RevisionStamp;

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
        ("Metadata/Items/Currency/CurrencyIdentification", 1150), ("Metadata/Items/Currency/CurrencyPortal", 360),
        ("Metadata/Items/Currency/CurrencyUpgradeToMagic", 500), ("Metadata/Items/Currency/CurrencyArmourQuality", 450),
        ("Metadata/Items/Currency/CurrencyWeaponQuality", 300), ("Metadata/Items/Currency/CurrencyAddModToMagic", 400),
        ("Metadata/Items/Currency/CurrencyRerollMagic", 250), ("Metadata/Items/Currency/CurrencyRerollSocketColours", 110),
        ("Metadata/Items/Currency/CurrencyRerollSocketNumbers", 150), ("Metadata/Items/Currency/CurrencyUpgradeRandomly", 120),
        ("Metadata/Items/Currency/CurrencyUpgradeToRare", 90), ("Metadata/Items/Currency/CurrencyFlaskQuality", 60),
        ("Metadata/Items/Currency/CurrencyRerollSocketLinks", 50), ("Metadata/Items/Currency/CurrencyRerollRare", 40),
        ("Metadata/Items/Currency/CurrencyConvertToNormal", 30), ("Metadata/Items/Currency/CurrencyPassiveRefund", 16),
        ("Metadata/Items/Currency/CurrencyCorrupt", 25), ("Metadata/Items/Currency/CurrencyUpgradeMagicToRare", 20),
        ("Metadata/Items/Currency/CurrencyGemQuality", 20), ("Metadata/Items/Currency/CurrencyRerollImplicit", 15),
        ("Metadata/Items/Currency/CurrencyAddModToRare", 2.2), ("Metadata/Items/Currency/CurrencyRemoveMod", 1.5),
        ("Metadata/Items/Currency/CurrencyModValues", 2.2), ("Metadata/Items/Currency/CurrencyDuplicate", 0.0014),
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

    sealed record ResolvedOverlayEntry(DropPoolEntry Entry, ItemCatalog.BaseType? BaseType, UniqueCatalog.Definition? Unique);

    // The uniques by base type Id, for the catalogue they were built from (the database or overlays may replace it while running).
    sealed record UniqueEntry(UniqueCatalog.Definition Definition, int DropLevel);
    sealed record UniqueIndex(Dictionary<string, List<UniqueEntry>> ByBase);

    sealed record PoolState(
        long RevisionStamp,
        DropDataProvider.Snapshot DropData,
        long DropOverlayRevision,
        long ItemCatalogRevision,
        long UniqueCatalogRevision,
        long UniqueWeightRevision,
        long GemPoolRevision,
        Pools Pools,
        List<ItemCatalog.BaseType> Cards,
        Dictionary<string, double> CardWeights,
        List<ItemCatalog.BaseType> Scarabs,
        Dictionary<string, double> ScarabWeights,
        UniqueIndex? Uniques,
        IReadOnlyList<ResolvedOverlayEntry> CurrencyOverlays,
        IReadOnlyList<ResolvedOverlayEntry> EquipmentOverlays,
        IReadOnlyList<ResolvedOverlayEntry> FlaskOverlays,
        IReadOnlyList<ResolvedOverlayEntry> TinctureOverlays,
        IReadOnlyList<ResolvedOverlayEntry> GemOverlays,
        IReadOnlyList<ResolvedOverlayEntry> CardOverlays,
        IReadOnlyList<ResolvedOverlayEntry> ScarabOverlays,
        IReadOnlyList<ResolvedOverlayEntry> UniqueOverlays,
        IReadOnlyList<ResolvedOverlayEntry> BossOverlays,
        IReadOnlyList<ResolvedOverlayEntry> GeneralOverlays,
        Dictionary<string, List<ResolvedOverlayEntry>> UniqueOverlaysByName);

    long _stateBuildCount;

    PoolState CurrentState()
    {
        var dropData = _dropDataProvider.Current;
        long dropOverlayRev = _dropOverlays.Revision;
        long itemRev = catalog.Revision;
        long uniqueRev = uniques?.Revision ?? UniqueCatalog.OverlayRevision;
        long uniqueWeightRev = _uniqueWeightProvider.Revision;
        long gemRev = GemSources.Revision;
        var current = Volatile.Read(ref _state);
        if (ReferenceEquals(current.DropData, dropData)
            && current.DropOverlayRevision == dropOverlayRev
            && current.ItemCatalogRevision == itemRev
            && current.UniqueCatalogRevision == uniqueRev
            && current.UniqueWeightRevision == uniqueWeightRev
            && current.GemPoolRevision == gemRev)
            return current;
        lock (_rebuildLock)
        {
            dropData = _dropDataProvider.Current;
            dropOverlayRev = _dropOverlays.Revision;
            itemRev = catalog.Revision;
            uniqueRev = uniques?.Revision ?? UniqueCatalog.OverlayRevision;
            uniqueWeightRev = _uniqueWeightProvider.Revision;
            gemRev = GemSources.Revision;
            current = _state;
            if (ReferenceEquals(current.DropData, dropData)
                && current.DropOverlayRevision == dropOverlayRev
                && current.ItemCatalogRevision == itemRev
                && current.UniqueCatalogRevision == uniqueRev
                && current.UniqueWeightRevision == uniqueWeightRev
                && current.GemPoolRevision == gemRev)
                return current;
            var rebuilt = BuildState(dropData, dropOverlayRev, itemRev, uniqueRev, uniqueWeightRev, gemRev);
            Volatile.Write(ref _state, rebuilt);
            return rebuilt;
        }
    }

    PoolState BuildState(
        DropDataProvider.Snapshot dropData,
        long dropOverlayRev,
        long itemRev,
        long uniqueRev,
        long uniqueWeightRev,
        long gemRev)
    {
        var pools = Build(catalog, folder, hardMode, league, dropData);
        var cards = LoadCards(catalog);
        var cardWeights = PublishedWeights(dropData, "card");
        var scarabWeights = PublishedWeights(dropData, "scarab");
        var scarabs = LoadScarabs(catalog, folder);

        var currencyOverlays = new List<ResolvedOverlayEntry>();
        var equipmentOverlays = new List<ResolvedOverlayEntry>();
        var flaskOverlays = new List<ResolvedOverlayEntry>();
        var tinctureOverlays = new List<ResolvedOverlayEntry>();
        var gemOverlays = new List<ResolvedOverlayEntry>();
        var cardOverlays = new List<ResolvedOverlayEntry>();
        var scarabOverlays = new List<ResolvedOverlayEntry>();
        var uniqueOverlays = new List<ResolvedOverlayEntry>();
        var bossOverlays = new List<ResolvedOverlayEntry>();
        var generalOverlays = new List<ResolvedOverlayEntry>();
        var uniqueOverlaysByName = new Dictionary<string, List<ResolvedOverlayEntry>>(StringComparer.OrdinalIgnoreCase);

        var overlaySnapshot = _dropOverlays.Snapshot;
        for (int i = 0; i < overlaySnapshot.Length; i++)
        {
            var entry = overlaySnapshot[i].Entry;
            switch (entry.Category)
            {
                case DropPoolCategory.Currency:
                    if (ResolveBase(entry.ItemId) is { } cur) currencyOverlays.Add(new(entry, cur, null));
                    break;
                case DropPoolCategory.Equipment:
                    if (ResolveBase(entry.ItemId) is { } eq) equipmentOverlays.Add(new(entry, eq, null));
                    break;
                case DropPoolCategory.Flask:
                    if (ResolveBase(entry.ItemId) is { } fl) flaskOverlays.Add(new(entry, fl, null));
                    break;
                case DropPoolCategory.Tincture:
                    if (ResolveBase(entry.ItemId) is { } ti) tinctureOverlays.Add(new(entry, ti, null));
                    break;
                case DropPoolCategory.Gem:
                    if (ResolveBase(entry.ItemId) is { } gm) gemOverlays.Add(new(entry, gm, null));
                    break;
                case DropPoolCategory.DivinationCard:
                    if (ResolveBase(entry.ItemId) is { } cd) cardOverlays.Add(new(entry, cd, null));
                    break;
                case DropPoolCategory.Scarab:
                    if (ResolveBase(entry.ItemId) is { } sc) scarabOverlays.Add(new(entry, sc, null));
                    break;
                case DropPoolCategory.Unique:
                    if (ResolveExactUnique(entry.ItemId) is { } uq)
                    {
                        var resolved = new ResolvedOverlayEntry(entry, ResolveBase(uq.BaseTypeId), uq);
                        uniqueOverlays.Add(resolved);
                        string key = UniqueCatalog.NameKey(uq.Name);
                        if (!uniqueOverlaysByName.TryGetValue(key, out var list))
                            uniqueOverlaysByName[key] = list = [];
                        list.Add(resolved);
                    }
                    break;
                case DropPoolCategory.Boss:
                    if (catalog.ById(entry.ItemId) is { Supported: true } bossExactBase)
                        bossOverlays.Add(new(entry, bossExactBase, null));
                    else if (ResolveExactUnique(entry.ItemId) is { } bossUq)
                        bossOverlays.Add(new(entry, ResolveBase(bossUq.BaseTypeId), bossUq));
                    else if (ResolveBase(entry.ItemId) is { } bossBase)
                        bossOverlays.Add(new(entry, bossBase, null));
                    break;
                case DropPoolCategory.General:
                    if (catalog.ById(entry.ItemId) is { Supported: true } genExactBase)
                        generalOverlays.Add(new(entry, genExactBase, null));
                    else if (ResolveExactUnique(entry.ItemId) is { } genUq)
                        generalOverlays.Add(new(entry, ResolveBase(genUq.BaseTypeId), genUq));
                    else if (ResolveBase(entry.ItemId) is { } genBase)
                        generalOverlays.Add(new(entry, genBase, null));
                    break;
            }
        }

        UniqueIndex? uniqueIndex = BuildUniqueIndex(uniqueOverlaysByName);
        long stamp = Interlocked.Increment(ref _stateBuildCount);
        return new PoolState(
            stamp, dropData, dropOverlayRev, itemRev, uniqueRev, uniqueWeightRev, gemRev,
            pools, cards, cardWeights, scarabs, scarabWeights, uniqueIndex,
            currencyOverlays, equipmentOverlays, flaskOverlays, tinctureOverlays, gemOverlays,
            cardOverlays, scarabOverlays, uniqueOverlays, bossOverlays, generalOverlays, uniqueOverlaysByName);

        ItemCatalog.BaseType? ResolveBase(string idOrName)
        {
            if (catalog.ById(idOrName) is { Supported: true } exact) return exact;
            var all = catalog.All;
            for (int j = 0; j < all.Count; j++)
                if (all[j].Supported && string.Equals(all[j].Name, idOrName, StringComparison.OrdinalIgnoreCase))
                    return all[j];
            return null;
        }

        UniqueCatalog.Definition? ResolveExactUnique(string name)
        {
            if (uniques is null || string.IsNullOrWhiteSpace(name)) return null;
            string key = UniqueCatalog.NameKey(name);
            var all = uniques.All;
            for (int j = 0; j < all.Count; j++)
                if (UniqueCatalog.NameKey(all[j].Name).Equals(key, StringComparison.OrdinalIgnoreCase))
                    return all[j];
            return null;
        }
    }

    DropContext NormalizeContext(DropContext? context, DropSourceMask defaultSource, MonsterRarity? rarity = null,
        bool isBoss = false, string? areaId = null)
    {
        if (context is { } c)
        {
            return c with
            {
                AreaId = string.IsNullOrEmpty(c.AreaId) ? areaId : c.AreaId,
                LeagueId = string.IsNullOrEmpty(c.LeagueId) ? _leagueId : c.LeagueId,
                HardMode = c.HardMode || hardMode,
            };
        }
        return new DropContext(defaultSource, rarity, isBoss, null, areaId, null, _leagueId, hardMode);
    }

    IReadOnlySet<string>? AreaTagsOf(in DropContext context) =>
        context.AreaTags ?? (string.IsNullOrEmpty(context.AreaId) ? null : _worldAreaTags.GetValueOrDefault(context.AreaId));

    // Indexed drop_card_areas snapshot (rebuilt only when DropDataProvider replaces its snapshot).
    // Campaign rows (min_map_tier == 0) are keyed strictly by WorldAreas.Id so Part 1/Part 2 shared area names
    // never collide; Atlas map rows (min_map_tier >= 1) resolve by area_id, area_name, or WorldAreas.Id -> Name.
    sealed record CardAreaIndex(object From,
        Dictionary<string, HashSet<string>> CampaignByAreaId,
        Dictionary<string, List<(string CardId, int MinMapTier)>> MapByAreaKey,
        HashSet<string> MapExclusiveCards,
        HashSet<string> SourceExclusiveCards);
    volatile CardAreaIndex? _cardAreaIndex;
    int _minCampaignMapDropLevel = -1;

    public sealed record Drop(Item Item, ItemCatalog.BaseType Type);

    static List<ItemCatalog.BaseType> LoadCards(ItemCatalog catalog)
    {
        var list = new List<ItemCatalog.BaseType>();
        foreach (var t in catalog.All)
            if (t.Class == "DivinationCard" && t.Supported && !string.IsNullOrEmpty(t.Name)) list.Add(t);
        return list;
    }

    static Dictionary<string, HashSet<string>> LoadWorldAreaTags()
    {
        var result = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        if (!ClientTables.Has("WorldAreas") || !ClientTables.Has("Tags")) return result;
        var tagNames = ClientTables.Table("Tags").ToDictionary(r => r.Index, r => r.Text("Id") ?? "");
        foreach (var area in ClientTables.Table("WorldAreas"))
        {
            if (area.TextOrNull("Id") is not { Length: > 0 } areaId) continue;
            var tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (int k in area.Ints("AreaTypeTags"))
                if (tagNames.TryGetValue(k, out var t) && t.Length > 0) tags.Add(t);
            foreach (int k in area.Ints("Tags"))
                if (tagNames.TryGetValue(k, out var t) && t.Length > 0) tags.Add(t);
            if (area.Bool("IsMapArea")) tags.Add("map");
            if (area.Bool("IsUniqueMapArea")) tags.Add("unique_map");
            if (tags.Count > 0) result[areaId] = tags;
        }
        return result;
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

    CardAreaIndex GetCardAreaIndex(DropDataProvider.Snapshot snapshot)
    {
        var current = _cardAreaIndex;
        if (current is not null && ReferenceEquals(current.From, snapshot)) return current;
        var campaign = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var mapAreas = new Dictionary<string, List<(string CardId, int MinMapTier)>>(StringComparer.OrdinalIgnoreCase);
        var mapExclusive = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sourceExclusive = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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
                mapExclusive.Add(row.CardId);
                AddMap(row.AreaId, row.CardId, row.MinMapTier);
                if (!row.AreaName.Equals(row.AreaId, StringComparison.OrdinalIgnoreCase))
                    AddMap(row.AreaName, row.CardId, row.MinMapTier);
            }
        }
        foreach (var row in snapshot.CardSources.Values)
            if (!string.IsNullOrEmpty(row.CardId)) sourceExclusive.Add(row.CardId);
        _cardAreaIndex = current = new(snapshot, campaign, mapAreas, mapExclusive, sourceExclusive);
        return current;
    }

    bool HasCampaignCards(PoolState state, string areaId, int level, in DropContext context)
    {
        if (GetCardAreaIndex(state.DropData).CampaignByAreaId.ContainsKey(areaId)) return true;
        if (state.CardOverlays.Count == 0) return false;
        var tags = AreaTagsOf(context);
        for (int i = 0; i < state.CardOverlays.Count; i++)
        {
            var ov = state.CardOverlays[i];
            if (ov.BaseType is { } bt && Allowed(bt, level) && ov.Entry.Matches(level, context, tags))
                return true;
        }
        return false;
    }

    /// <summary>A random divination card. When the published card weights cover cards at this level, the pick follows
    /// them; otherwise it is uniform among the client's cards. <paramref name="level"/> 0 skips the DropLevel gate.</summary>
    public Drop? Card(Random rng, int level = 0) => Card(rng, level, areaId: null, mapTier: 0);

    /// <summary>A divination card filtered by area and map tier through <c>drop_card_areas</c> when <paramref name="areaId"/>
    /// is supplied. Campaign areas (<paramref name="mapTier"/> == 0) match strictly by <c>WorldAreas.Id</c> against
    /// <c>min_map_tier = 0</c> rows; maps combine tier-eligible area cards (<c>area_id</c>, <c>area_name</c>,
    /// <c>WorldAreas.Id</c> → <c>Name</c>, or without trailing " Map") with global non-boss cards so area-specific
    /// MLE weights are not renormalized to 100%.</summary>
    public Drop? Card(Random rng, int level, string? areaId, int mapTier, DropWeights? factors = null, DropContext? context = null)
    {
        var state = CurrentState();
        var ctx = NormalizeContext(context, DropSourceMask.MonsterKill, areaId: areaId);
        if (!string.IsNullOrEmpty(areaId))
        {
            var index = GetCardAreaIndex(state.DropData);
            if (mapTier <= 0)
            {
                index.CampaignByAreaId.TryGetValue(areaId, out var allowedIds);
                var overlayCards = MatchingOverlays(state.CardOverlays, level, ctx);
                if ((allowedIds is null || allowedIds.Count == 0) && overlayCards.Count == 0)
                    return null;
                var matched = allowedIds is { Count: > 0 }
                    ? state.Cards.Where(t => Allowed(t, level) && (allowedIds.Contains(t.Name) || allowedIds.Contains(t.Id))).ToList()
                    : [];
                return PickCategory(state, matched, "card", level, rng, factors, overlayCards) is { } picked
                    ? new Drop(ItemCatalog.Create(picked.Type) with { StackSize = ClampStack(picked.Type, picked.StackSize) }, picked.Type)
                    : null;
            }
            if (index.MapByAreaKey.Count > 0 || index.SourceExclusiveCards.Count > 0)
            {
                HashSet<string>? areaAllowed = null;
                if (ResolveMapCardRows(index, areaId) is { Count: > 0 } areaRows)
                {
                    areaAllowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var (cardId, minTier) in areaRows)
                        if (mapTier >= minTier) areaAllowed.Add(cardId);
                }
                static bool InSet(HashSet<string> set, ItemCatalog.BaseType t) => set.Contains(t.Name) || set.Contains(t.Id);
                var pool = state.Cards.Where(t => Allowed(t, level) && (
                    areaAllowed is not null && InSet(areaAllowed, t)
                    || !InSet(index.MapExclusiveCards, t) && !InSet(index.SourceExclusiveCards, t))).ToList();
                var overlayCards = MatchingOverlays(state.CardOverlays, level, ctx);
                if (pool.Count > 0 || overlayCards.Count > 0)
                    return PickCategory(state, pool, "card", level, rng, factors, overlayCards) is { } picked
                        ? new Drop(ItemCatalog.Create(picked.Type) with { StackSize = ClampStack(picked.Type, picked.StackSize) }, picked.Type)
                        : null;
            }
        }
        return Card(rng, level, factors, context);
    }

    /// <param name="factors">A mod's factor per card (<see cref="DropWeights.Item"/>) on its published or uniform weight.</param>
    public Drop? Card(Random rng, int level, DropWeights? factors) => Card(rng, level, factors, context: null);

    public Drop? Card(Random rng, int level, DropWeights? factors, DropContext? context)
    {
        var state = CurrentState();
        var ctx = NormalizeContext(context, DropSourceMask.MonsterKill);
        var overlayCards = MatchingOverlays(state.CardOverlays, level, ctx);
        return PickCategory(state, state.Cards, "card", level, rng, factors, overlayCards) is { } picked
            ? new Drop(ItemCatalog.Create(picked.Type) with { StackSize = ClampStack(picked.Type, picked.StackSize) }, picked.Type)
            : null;
    }

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
    static Dictionary<string, double> PublishedWeights(DropDataProvider.Snapshot snapshot, string category) =>
        snapshot.ItemWeights
            .Where(row => row.Key.Category == category && row.Value is { Enabled: true, Weight: > 0 })
            .ToDictionary(row => row.Key.ItemId, row => row.Value.Weight, StringComparer.Ordinal);

    IReadOnlyList<ResolvedOverlayEntry> MatchingOverlays(IReadOnlyList<ResolvedOverlayEntry> overlays, int level, in DropContext context)
    {
        if (overlays.Count == 0) return [];
        var tags = AreaTagsOf(context);
        List<ResolvedOverlayEntry>? hits = null;
        for (int i = 0; i < overlays.Count; i++)
        {
            var ov = overlays[i];
            if (ov.BaseType is { } bt && !Allowed(bt, level)) continue;
            if (!ov.Entry.Matches(level, context, tags)) continue;
            (hits ??= []).Add(ov);
        }
        return (IReadOnlyList<ResolvedOverlayEntry>?)hits ?? [];
    }

    static ushort ClampStack(ItemCatalog.BaseType type, ushort stackSize) =>
        (ushort)Math.Clamp(stackSize == 0 ? 1 : stackSize, 1, Math.Max(1, (int)type.MaxStack));

    /// <summary>One of the types, by the published weights when they cover one whose DropLevel the level meets, else
    /// uniformly; either weight times the type's factor in <paramref name="factors"/>. Null when none can drop.</summary>
    static (ItemCatalog.BaseType Type, ushort StackSize)? PickCategory(
        PoolState state, List<ItemCatalog.BaseType> types, string category, int level, Random rng, DropWeights? factors,
        IReadOnlyList<ResolvedOverlayEntry> overlays)
    {
        var weights = category == "card" ? state.CardWeights : state.ScarabWeights;
        if (overlays.Count == 0)
        {
            if (weights.Count > 0 && types.Where(t => weights.ContainsKey(t.Id) && Allowed(t, level))
                    .Select(t => (Value: t, Weight: weights[t.Id] * (factors?.Item(t.Id) ?? 1))).ToList() is { Count: > 0 } published)
                return Pick(published, rng) is { } p ? (p, (ushort)1) : null;
            var pool = Droppable(types, level);
            if (pool.Count == 0) return null;
            var chosen = factors is { Items.Count: > 0 }
                ? Pick(pool.Select(t => (Value: t, Weight: factors.Item(t.Id))).ToList(), rng)
                : pool[rng.Next(pool.Count)];
            return chosen is null ? null : (chosen, (ushort)1);
        }

        var combined = new List<((ItemCatalog.BaseType Type, ushort StackSize) Value, double Weight)>();
        bool usePublished = weights.Count > 0 && types.Any(t => weights.ContainsKey(t.Id) && Allowed(t, level));
        if (usePublished)
        {
            foreach (var t in types)
                if (weights.TryGetValue(t.Id, out double w) && Allowed(t, level))
                {
                    double scaled = w * (factors?.Item(t.Id) ?? 1);
                    if (scaled > 0) combined.Add(((t, (ushort)1), scaled));
                }
        }
        else
        {
            foreach (var t in Droppable(types, level))
            {
                double scaled = factors?.Item(t.Id) ?? 1;
                if (scaled > 0) combined.Add(((t, (ushort)1), scaled));
            }
        }
        for (int i = 0; i < overlays.Count; i++)
        {
            var ov = overlays[i];
            if (ov.BaseType is not { } bt || ov.Entry.Weight <= 0) continue;
            double scaled = ov.Entry.Weight * (factors?.Item(bt.Id) ?? 1);
            if (scaled > 0) combined.Add(((bt, ov.Entry.StackSize), scaled));
        }
        return PickStruct(combined, rng);
    }

    static bool Allowed(ItemCatalog.BaseType type, int level) => level <= 0 || type.DropLevel <= level;

    static List<ItemCatalog.BaseType> Droppable(List<ItemCatalog.BaseType> types, int level) =>
        level <= 0 ? types : types.Where(t => Allowed(t, level)).ToList();

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
            foreach (var t in catalog.All)
                if (t.Class == "MapFragment" && t.Id.Contains("/Scarabs/", StringComparison.Ordinal) && t.Supported && seen.Add(t.Row))
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
    public Drop? Scarab(Random rng, int level, MapMods? mods, DropWeights? factors) => Scarab(rng, level, mods, factors, context: null);

    public Drop? Scarab(Random rng, int level, MapMods? mods, DropWeights? factors, DropContext? context)
    {
        var state = CurrentState();
        var ctx = NormalizeContext(context, DropSourceMask.MonsterKill);
        var overlayScarabs = MatchingOverlays(state.ScarabOverlays, level, ctx);
        if (mods is not null && ScarabFactors(mods) is { Count: > 0 } areaFactors)
        {
            var weights = state.ScarabWeights;
            var pool = Droppable(state.Scarabs, level);
            bool published = weights.Count > 0 && pool.Any(t => weights.ContainsKey(t.Id));
            var favoured = new List<((ItemCatalog.BaseType Type, ushort StackSize) Value, double Weight)>();
            foreach (var t in pool)
            {
                if (published && !weights.ContainsKey(t.Id)) continue;
                double w = (published ? weights[t.Id] : 1) * areaFactors.GetValueOrDefault(t.Row, 1) * (factors?.Item(t.Id) ?? 1);
                if (w > 0) favoured.Add(((t, (ushort)1), w));
            }
            for (int i = 0; i < overlayScarabs.Count; i++)
            {
                var ov = overlayScarabs[i];
                if (ov.BaseType is not { } bt || ov.Entry.Weight <= 0) continue;
                double w = ov.Entry.Weight * areaFactors.GetValueOrDefault(bt.Row, 1) * (factors?.Item(bt.Id) ?? 1);
                if (w > 0) favoured.Add(((bt, ov.Entry.StackSize), w));
            }
            return PickStruct(favoured, rng) is { } picked
                ? new Drop(ItemCatalog.Create(picked.Type) with { StackSize = ClampStack(picked.Type, picked.StackSize) }, picked.Type)
                : null;
        }
        return PickCategory(state, state.Scarabs, "scarab", level, rng, factors, overlayScarabs) is { } chosen
            ? new Drop(ItemCatalog.Create(chosen.Type) with { StackSize = ClampStack(chosen.Type, chosen.StackSize) }, chosen.Type)
            : null;
    }

    /// <summary>Extra drops from any MapFragmentMods / map-quality stats: card packs, card/scarab/currency drop chances.
    /// New scarabs that use those stat name patterns are included without an Id list.</summary>
    public IEnumerable<Drop> FromMapMods(MapMods mods, int monsterLevel, MonsterRarity rarity, Random rng, DropContext? context = null)
    {
        var state = CurrentState();
        var ctx = NormalizeContext(context, DropSourceMask.MapCurrency, rarity);
        if (rarity == MonsterRarity.Magic && mods.PacksDropCards > 0 && Card(rng, monsterLevel, factors: null, context: ctx) is { } packCard)
            yield return packCard;
        if (mods.CardDropChance > 0 && rng.Next(100) < Math.Clamp(mods.CardDropChance, 0, 80) && Card(rng, monsterLevel, factors: null, context: ctx) is { } card)
            yield return card;
        if (mods.ScarabDropChance > 0 && rng.Next(100) < Math.Clamp(mods.ScarabDropChance, 0, 80) && Scarab(rng, monsterLevel, mods, factors: null, context: ctx) is { } scarab)
            yield return scarab;
        if (mods.CurrencyDropChance > 0 && rng.Next(100) < Math.Clamp(mods.CurrencyDropChance, 0, 80)
            && PickCurrency(state, monsterLevel, rng, factors: null, in ctx) is { } currency)
            yield return new Drop(ItemCatalog.Create(currency.Type) with { StackSize = ClampStack(currency.Type, currency.StackSize) }, currency.Type);
    }

    /// <summary>A reward container's explicit number of rolls, without a monster's quantity or personal/mod multipliers.</summary>
    public IReadOnlyList<Drop> Container(int level, int count, int increasedRarity, Random rng) =>
        Container(level, count, increasedRarity, rng, null, null);

    public IReadOnlyList<Drop> Container(int level, int count, int increasedRarity, Random rng,
        Func<ItemCatalog.BaseType, bool>? accepts) =>
        Container(level, count, increasedRarity, rng, accepts, null);

    public IReadOnlyList<Drop> Container(int level, int count, int increasedRarity, Random rng,
        Func<ItemCatalog.BaseType, bool>? accepts, DropContext? context)
    {
        if (count < 0 || count > 2000) throw new ArgumentOutOfRangeException(nameof(count));
        var state = CurrentState();
        var ctx = NormalizeContext(context, DropSourceMask.Container);
        int clampedLevel = Math.Clamp(level, 1, 100);
        var chances = WithUniqueMultiplier(WithIncreasedRarity(RarityChances(MonsterRarity.Rare), increasedRarity,
            SystemSettings.Default.DropMultiplier("unique_rarity")));
        var result = new List<Drop>();
        for (int attempt = 0; result.Count < count && attempt < count * (accepts is null ? 8 : 256); attempt++)
            if (RollOne(state, clampedLevel, clampedLevel, chances, rng, in ctx) is { } drop
                && (accepts is null || accepts(drop.Type))) result.Add(drop);
        AppendChanceOverlays(state, result, clampedLevel, clampedLevel, chances, rng, ctx, quantity: 1.0, accepts);
        return result;
    }

    /// <summary>Evaluates independent-chance (<see cref="DropPoolEntry.Chance"/>) overlays matching <paramref name="context"/>
    /// for callers that roll specialized container or strongbox tables directly.</summary>
    public IReadOnlyList<Drop> RollSourceExtras(int level, Random rng, DropContext context, int increasedRarity = 0,
        double rarityMultiplier = 1, Func<ItemCatalog.BaseType, bool>? accepts = null)
    {
        var state = CurrentState();
        var ctx = NormalizeContext(context, context.Source);
        int clampedLevel = Math.Clamp(level, 1, 100);
        var chances = WithUniqueMultiplier(WithIncreasedRarity(RarityChances(MonsterRarity.Rare), increasedRarity,
            rarityMultiplier * SystemSettings.Default.DropMultiplier("unique_rarity")));
        var result = new List<Drop>();
        AppendChanceOverlays(state, result, clampedLevel, clampedLevel, chances, rng, ctx, quantity: 1.0, accepts);
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
        double scarabQuantity = 1, int? characterLevel = null, int? areaLevel = null, DropWeights? weights = null,
        DropContext? context = null)
    {
        var state = CurrentState();
        var ctx = NormalizeContext(context, DropSourceMask.MonsterKill, rarity, isBoss: false, areaId: areaId);
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
        int itemLevel = monsterLevel + ItemLevelBonus(rarity);
        var drops = new List<Drop>();
        for (int i = 0; i < count; i++)
        {
            // No draw for a whole multiplier, so the ordinary 1 rolls exactly as before.
            int times = whole + ((quantity < 1 || part > 0) && rng.NextDouble() < part ? 1 : 0);
            for (int r = 0; r < times; r++)
                if (RollOne(state, monsterLevel, itemLevel, chances, rng, in ctx, inMap: mapTier > 0, dropPenalty: dropPenalty, weights: weights) is { } d)
                    drops.Add(d);
        }
        if (mapTier > 0)
        {
            double scarabChance = scarabQuantity * (weights?.Scarabs ?? 1);
            if (scarabChance > 0 && MapExtra("drop_scarab_chance_pct", quantity * scarabChance, rng)
                && Scarab(rng, monsterLevel, mods, weights, ctx) is { } scarab)
                drops.Add(scarab with { Item = scarab.Item with { ItemLevel = (byte)Math.Clamp(monsterLevel, 1, 100) } });
            if (MapExtra("drop_card_chance_pct", quantity * dropPenalty * (weights?.Cards ?? 1), rng) && Card(rng, monsterLevel, areaId, mapTier, weights, ctx) is { } card)
                drops.Add(card);
        }
        else if (!string.IsNullOrEmpty(areaId) && HasCampaignCards(state, areaId, monsterLevel, in ctx))
        {
            double campaignChance = SystemSettings.Default.DropCampaignCardChancePct / 100.0 * quantity * dropPenalty * (weights?.Cards ?? 1);
            if (campaignChance > 0 && rng.NextDouble() < campaignChance && Card(rng, monsterLevel, areaId, mapTier: 0, weights, ctx) is { } card)
                drops.Add(card);
        }
        if ((monsterLevel >= DivineVesselDropLevel || mapTier >= 1)
            && DivineVessel(monsterLevel, rarity, quantity, rng) is { } vessel)
            drops.Add(vessel);
        AppendChanceOverlays(state, drops, monsterLevel, itemLevel, chances, rng, ctx, quantity);
        return drops;
    }

    public const string DivineVesselId = PantheonCatalog.CurrencyDivineVesselId;
    public const int DivineVesselDropLevel = 68;

    /// <summary>Retail Divine Vessel drop roll at monster level 68+ (or inside Atlas maps), scaled by monster rarity
    /// and item quantity via <c>drop_divine_vessel_chance_pct</c>.</summary>
    public Drop? DivineVessel(int monsterLevel, MonsterRarity rarity, double quantityMultiplier, Random rng)
    {
        double baseChance = SystemSettings.Default.DropDivineVesselChancePct / 100.0;
        if (baseChance <= 0) return null;
        double rarityScale = rarity switch
        {
            MonsterRarity.Magic => 2.0,
            MonsterRarity.Rare => 5.0,
            MonsterRarity.Unique => 10.0,
            _ => 1.0,
        };
        double quantity = double.IsFinite(quantityMultiplier) ? Math.Max(0, quantityMultiplier) : 1.0;
        double chance = Math.Min(1.0, baseChance * rarityScale * quantity);
        if (chance <= 0 || rng.NextDouble() >= chance) return null;
        return catalog.Find(DivineVesselId) is { Supported: true } vesselType
            ? new Drop(ItemCatalog.Create(vesselType) with { ItemLevel = (byte)Math.Clamp(monsterLevel, 1, 100) }, vesselType)
            : null;
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
        int increasedRarity = 0, double rarityMultiplier = 1, DropContext? context = null)
    {
        if (!SystemSettings.Default.DropFirstKillBossBonus) return [];
        var state = CurrentState();
        var ctx = NormalizeContext(context, DropSourceMask.BossBonus, MonsterRarity.Unique, isBoss: true);
        int level = Math.Clamp(monsterLevel, 1, 100);
        int itemLevel = Math.Clamp(level + ItemLevelBonus(MonsterRarity.Unique), 1, 100);
        var chances = WithUniqueMultiplier(WithIncreasedRarity(RarityChances(MonsterRarity.Unique), increasedRarity,
            rarityMultiplier * SystemSettings.Default.DropMultiplier("unique_rarity")));
        int rareCount = actBoss ? 3 : 2;
        int currencyCount = actBoss ? 2 : 1;
        var drops = new List<Drop>(rareCount + currencyCount);
        for (int i = 0; i < rareCount; i++)
        {
            if (PickEquipmentBase(state, level, inMap: false, rng, in ctx) is not { } eqType) continue;
            var (sockets, links) = catalog.DropSockets(eqType, itemLevel, rng);
            byte quality = RollNaturalQuality(eqType, rng);
            var item = ItemCatalog.Create(eqType, sockets, links, random: rng) with
            {
                ItemLevel = (byte)itemLevel,
                Quality = quality,
            };
            if (rolls is not null)
            {
                if (rng.NextDouble() < chances.Unique && Unique(state, eqType, level, itemLevel, rng, in ctx) is { } unique)
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
            if (CurrencyDrop(level, rng, weights: null, context: ctx) is { } cur) drops.Add(cur);
        // Weighted boss overlays (without explicit Chance) compete proportionally against stock weight 100.
        if (state.BossOverlays.Count > 0)
        {
            var weightedBoss = MatchingWeightedOverlays(state.BossOverlays, level, in ctx);
            if (weightedBoss.Count > 0)
            {
                double bossWeight = 0;
                for (int i = 0; i < weightedBoss.Count; i++) bossWeight += weightedBoss[i].Entry.Weight;
                if (bossWeight > 0 && rng.NextDouble() < bossWeight / (100.0 + bossWeight)
                    && PickWeightedOverlay(weightedBoss, rng) is { } chosen
                    && CreateOverlayDrop(chosen, level, itemLevel, chances, rng, in ctx) is { } bossDrop)
                    drops.Add(bossDrop);
            }
        }
        AppendChanceOverlays(state, drops, level, itemLevel, chances, rng, ctx, quantity: 1.0);
        return drops;
    }

    /// <summary>Curated Act 1 Twilight Strand Hillock starter drop: life/mana flasks, Scrolls of Wisdom, a linked
    /// magic class-appropriate weapon, and a linked armour piece.</summary>
    public IReadOnlyList<Drop> RollHillockStarterDrop(int classRow, Random rng)
    {
        var pools = CurrentState().Pools;
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
            .SelectMany(c => pools.Equipment.GetValueOrDefault(c) ?? [])
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
            .SelectMany(c => pools.Equipment.GetValueOrDefault(c) ?? [])
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
    /// (respecting <c>drop_currency_weights.drop_level</c> and runtime currency overlays).</summary>
    public Drop? CurrencyDrop(int level, Random rng) => CurrencyDrop(level, rng, null);

    /// <param name="weights">A mod's factor per currency type (<see cref="DropWeights.Item"/>); null for none.</param>
    public Drop? CurrencyDrop(int level, Random rng, DropWeights? weights) => CurrencyDrop(level, rng, weights, context: null, accepts: null);

    public Drop? CurrencyDrop(int level, Random rng, DropWeights? weights, DropContext? context, Func<ItemCatalog.BaseType, bool>? accepts = null)
    {
        var state = CurrentState();
        var ctx = NormalizeContext(context, DropSourceMask.MonsterKill);
        return PickCurrency(state, level, rng, weights, in ctx, accepts) is { } picked
            ? new Drop(ItemCatalog.Create(picked.Type) with
            {
                StackSize = ClampStack(picked.Type, picked.StackSize),
                ItemLevel = (byte)Math.Clamp(level, 1, 100),
            }, picked.Type)
            : null;
    }

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

    static readonly double[] GoldBaseTable = BuildGoldBaseTable();

    static double[] BuildGoldBaseTable()
    {
        var table = new double[100];
        const double base68 = 2.1 + 0.103 * 68;
        double base84 = base68 * Math.Pow(1.041, 16);
        for (int level = 1; level <= 100; level++)
            table[level - 1] = level <= 68 ? 2.1 + 0.103 * level
                : level <= 84 ? base68 * Math.Pow(1.041, level - 68)
                : base84 + 0.5 * (level - 84);
        return table;
    }

    /// <summary>Retail-aligned base gold per normal-monster pile across monster levels 1–100:
    /// ~2.2 at L1, ~4.2 at L20, ~6.8 at L45, ~9.1 at L68 (T1), ~10.7 at L72 (T5), ~16.7 at L83 (T16),
    /// ~17.3 at L84 (T17), and ~25.3 at L100 (gentle linear tail above T17, avoiding exponential blowup).</summary>
    public static double GoldBaseForLevel(int monsterLevel) => GoldBaseTable[Math.Clamp(monsterLevel, 1, 100) - 1];

    /// <summary>Concave IIR multiplier for gold drops mirroring PoE's equipment-rarity conversion:
    /// +50% → 1.25×, +100% → 1.43×, +200% → 1.67×, +300% → 1.82×, +500% → 2.00× (asymptote 2.50×).</summary>
    public static double GoldRarityScale(int increasedRarity) =>
        increasedRarity <= 0
            ? Math.Max(0.0, 1.0 + increasedRarity / 100.0)
            : 1.0 + 1.5 * increasedRarity / (increasedRarity + 250.0);

    /// <summary>The gold a dying monster drops, 0 for none: normal monsters 25% of the time, magic 60%, rare and unique
    /// always; <see cref="GoldBaseForLevel"/> × 1 / 2.5 / 6 / 15 by rarity, ±25%.</summary>
    public static int GoldAmount(int monsterLevel, MonsterRarity rarity, Random rng) =>
        GoldRoll(monsterLevel, rarity, rng, 1.0, int.MaxValue);

    static int GoldRoll(int monsterLevel, MonsterRarity rarity, Random rng, double totalMult, int maxAmount)
    {
        if (!double.IsFinite(totalMult) || totalMult <= 0) return 0;
        double chance = rarity switch { MonsterRarity.Normal => 0.25, MonsterRarity.Magic => 0.60, _ => 1 };
        chance = Math.Min(1, chance * SystemSettings.Default.GoldChanceMultiplier);
        if (chance <= 0 || rng.NextDouble() >= chance) return 0;
        double times = rarity switch { MonsterRarity.Magic => 2.5, MonsterRarity.Rare => 6, MonsterRarity.Unique => 15, _ => 1 };
        double scaled = GoldBaseForLevel(monsterLevel) * times * (0.75 + 0.5 * rng.NextDouble()) * totalMult;
        if (scaled >= maxAmount) return maxAmount;
        return (int)Math.Round(scaled);
    }

    public Drop? Gold(int monsterLevel, MonsterRarity rarity, Random rng, double multiplier = 1) =>
        Gold(monsterLevel, rarity, rng, multiplier, increasedRarity: 0, quantityFactor: 1);

    /// <summary>A pile of gold for a dying monster (<see cref="GoldAmount"/>), scaled by <paramref name="multiplier"/>,
    /// effective IIR (<paramref name="increasedRarity"/>, mapped through <see cref="GoldRarityScale"/>) and area/player
    /// IIQ (<paramref name="quantityFactor"/>), or null for none.</summary>
    public Drop? Gold(int monsterLevel, MonsterRarity rarity, Random rng, double multiplier,
        int increasedRarity, double quantityFactor = 1.0)
    {
        if (catalog.Find(GoldId) is not { Supported: true } type) return null;
        double mult = double.IsFinite(multiplier) ? Math.Max(0, multiplier) : 0;
        double iirScale = GoldRarityScale(increasedRarity);
        double iiqScale = double.IsFinite(quantityFactor) ? Math.Max(0, quantityFactor) : 1.0;
        double totalMult = mult * iirScale * iiqScale;
        int maxStack = type.MaxStack > 0 ? Math.Min(type.MaxStack, ushort.MaxValue) : ushort.MaxValue;
        return GoldRoll(monsterLevel, rarity, rng, totalMult, maxStack) is int amount and > 0
            ? new Drop(ItemCatalog.Create(type) with { StackSize = (ushort)amount }, type) : null;
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

    sealed record BossDropIndex(object From, Dictionary<string, List<GameServer.Storage.GameDataStore.BossItemRow>> ByBoss);
    volatile BossDropIndex? _bossDropIndex;

    BossDropIndex GetBossDropIndex(DropDataProvider.Snapshot snapshot)
    {
        var current = _bossDropIndex;
        if (current is not null && ReferenceEquals(current.From, snapshot)) return current;
        var byBoss = new Dictionary<string, List<GameServer.Storage.GameDataStore.BossItemRow>>(StringComparer.Ordinal);
        foreach (var row in snapshot.BossItems.Values)
        {
            if (!byBoss.TryGetValue(row.BossId, out var list)) byBoss[row.BossId] = list = [];
            list.Add(row);
        }
        _bossDropIndex = current = new(snapshot, byBoss);
        return current;
    }

    /// <summary>Data-driven boss drops from world.db <c>drop_boss_items</c>: non-unique items (fragments, crests,
    /// invitations, boss-only currency, gems, cards) roll by their configured chance — with mutually-exclusive
    /// guaranteed fragment pools (2+ <c>other</c> rows whose fractional chances sum to ~1.0, e.g. Shaper/Elder/T17
    /// fragments) selecting exactly one fragment per kill — and unpublished (<c>chance IS NULL</c>,
    /// <c>override_chance IS NULL</c>) unique pool rows granting one weighted unique from the boss's retail unique
    /// pool without double-rolling <see cref="EncounterJewels"/>.</summary>
    public IReadOnlyList<Drop> BossDrops(string bossId, int monsterLevel, Random random, DropDataProvider.Snapshot? data = null)
    {
        var snapshot = data ?? _dropDataProvider.Current;
        if (snapshot.Bosses.TryGetValue(bossId, out var boss) && !boss.Enabled) return [];
        if (!GetBossDropIndex(snapshot).ByBoss.TryGetValue(bossId, out var rows) || rows.Count == 0) return [];
        var result = new List<Drop>();
        byte dropLevel = (byte)Math.Clamp(monsterLevel, 1, 100);
        List<(UniqueCatalog.Definition Def, double Weight)>? uniquePool = null;
        List<(GameServer.Storage.GameDataStore.BossItemRow Row, double Chance)>? fractionalOthers = null;
        double fractionalOtherSum = 0;
        for (int i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            if (row.ItemKind == "unique")
            {
                // Explicit chance or override rows are handled by EncounterJewels or dedicated module handlers.
                if (row.OverrideChance is not null || row.Chance is not null || uniques is null) continue;
                if (uniques.Find(row.ItemId) is not { } def || def.Name != row.ItemId) continue;
                double weight = Math.Max(0.05, StockUniqueWeight(def.Name));
                (uniquePool ??= []).Add((def, weight));
                continue;
            }
            if ((row.OverrideChance ?? row.Chance) is not (> 0 and <= 1 and var chance)) continue;
            if (row.ItemKind == "other" && chance < 1.0)
            {
                (fractionalOthers ??= []).Add((row, chance));
                fractionalOtherSum += chance;
                continue;
            }
            if (chance < 1.0 && random.NextDouble() >= chance) continue;
            if (catalog.Find(row.ItemId) is { Supported: true } type)
                result.Add(new(ItemCatalog.Create(type) with { ItemLevel = dropLevel }, type));
        }
        if (fractionalOthers is { Count: > 0 })
        {
            // Mutually-exclusive guaranteed fragment pools (e.g. Shaper 0.50/0.50, Elder 0.50/0.50, T17 0.50/0.50 or
            // 0.34/0.33/0.33) always pick exactly one fragment; single fractional items roll independently.
            if (fractionalOthers.Count >= 2 && fractionalOtherSum is >= 0.95 and <= 1.05)
            {
                if (Pick(fractionalOthers, random) is { } chosenOther
                    && catalog.Find(chosenOther.ItemId) is { Supported: true } type)
                    result.Add(new(ItemCatalog.Create(type) with { ItemLevel = dropLevel }, type));
            }
            else
            {
                for (int i = 0; i < fractionalOthers.Count; i++)
                {
                    var (row, chance) = fractionalOthers[i];
                    if (random.NextDouble() < chance && catalog.Find(row.ItemId) is { Supported: true } type)
                        result.Add(new(ItemCatalog.Create(type) with { ItemLevel = dropLevel }, type));
                }
            }
        }
        if (uniquePool is { Count: > 0 } && uniques is not null && Pick(uniquePool, random) is { } chosen)
        {
            int bonus = chosen.EncounterItemLevelBonus ?? ItemLevelBonus(MonsterRarity.Unique);
            byte uqLevel = (byte)Math.Clamp(monsterLevel + (long)bonus, 1, 100);
            if (uniques.Create(chosen with { ItemLevel = uqLevel }, random) is { } item
                && catalog.ByHash(item.TypeHash) is { } type)
                result.Add(new(item with { Identified = false }, type));
        }
        return result;
    }

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

    const string NightmareMapId = "Metadata/Items/Maps/MapKeyNightmareBoss";

    /// <summary>An approximate generic-map drop: a map of the current map tier (or tier 1 in the late campaign once
    /// monsters meet the tier-1 map's DropLevel). Our own rates until the game pools are known. <paramref name="chanceFactor"/>
    /// scales the chance (a monster's or league's map-related drop chance), and <paramref name="tierUpgradePermillage"/>
    /// rolls a +1 tier upgrade (clamped to 16) only after the map drop roll succeeds. Each socketed Voidstone (0..4) adds
    /// +25% (+250 permillage) chance for maps to drop 1 tier higher, converts maps below tier 16 to gold proportionally to
    /// the elevated Atlas quadrants (25%/50%/75%/100%), and adds 0.4% per Voidstone for tier-16 drops to become Nightmare
    /// (Tier 17) maps.</summary>
    public Drop? Map(int monsterLevel, MonsterRarity monster, int mapTier, Random rng, int voidstones = 0,
        double chanceFactor = 1, int tierUpgradePermillage = 0, int increasedRarity = 0, int nightmareChancePermyriad = 0) =>
        Map(monsterLevel, monster, mapTier, rng, voidstones, chanceFactor, tierUpgradePermillage, increasedRarity,
            corruptEightMods: false, nightmareChancePermyriad: nightmareChancePermyriad);

    public Drop? Map(int monsterLevel, MonsterRarity monster, int mapTier, Random rng, int voidstones,
        double chanceFactor, int tierUpgradePermillage, int increasedRarity, bool corruptEightMods,
        int nightmareChancePermyriad = 0)
    {
        if (maps is null || (mapTier <= 0 && monsterLevel < MinCampaignMapDropLevel())) return null;
        double chance = monster switch { MonsterRarity.Normal => 0.004, MonsterRarity.Magic => 0.012, MonsterRarity.Rare => 0.05, _ => 0.20 };
        if (mapTier <= 0) chance *= SystemSettings.Default.DropCampaignMapChanceMultiplier;
        chance = Math.Min(1, chance * SystemSettings.Default.DropMultiplier("maps") * Math.Max(0, chanceFactor));
        if (chance <= 0 || rng.NextDouble() >= chance) return null;
        int stones = Math.Clamp(voidstones, 0, 4);
        int tier = Math.Clamp(mapTier > 0 ? mapTier : 1, 1, 16);
        int totalUpgrade = Math.Max(0, tierUpgradePermillage) + stones * 250;
        if (totalUpgrade > 0 && mapTier > 0 && tier < 16)
        {
            if (totalUpgrade <= 1000)
            {
                if (rng.NextDouble() < totalUpgrade / 1000d)
                    tier = Math.Min(16, tier + 1);
            }
            else
            {
                int guaranteed = totalUpgrade / 1000;
                int remainder = totalUpgrade % 1000;
                tier = Math.Min(16, tier + guaranteed);
                if (remainder > 0 && tier < 16 && rng.NextDouble() < remainder / 1000d)
                    tier = Math.Min(16, tier + 1);
            }
        }
        // Socketed Voidstones elevate Atlas quadrants to T16 and convert found maps below tier 16 to gold
        // proportionally (25%/50%/75% for 1–3 stones, 100% at 4 stones).
        if (stones > 0 && tier < 16 && (stones >= 4 || rng.NextDouble() < stones / 4.0))
            return Gold(monsterLevel, monster, rng);
        // Tier 16 map drops with socketed Voidstones can become Nightmare (Tier 17) maps (0.4% per Voidstone = 40 permyriad each).
        if (tier == 16 && stones > 0 && mapTier >= 16)
        {
            double nightmareChance = Math.Clamp((stones * 40 + Math.Max(0, nightmareChancePermyriad)) / 10_000d, 0, 1);
            if (nightmareChance > 0 && rng.NextDouble() < nightmareChance
                && catalog.ById(NightmareMapId) is { Supported: true } nightmareType)
            {
                var nightmare = ItemCatalog.Create(nightmareType) with { ItemLevel = (byte)Math.Clamp(Math.Max(84, monsterLevel + ItemLevelBonus(monster)), 1, 100) };
                if (rolls is not null)
                    nightmare = rolls.Roll(nightmare, ItemRarity.Rare, rng) with { Identified = false };
                return new(nightmare, nightmareType);
            }
        }
        if (maps.KeyOfTier(tier) is not uint hash || catalog.ByHash(hash) is not { Supported: true } type
            || (mapTier <= 0 && monsterLevel < type.DropLevel)) return null;
        var item = ItemCatalog.Create(type) with { ItemLevel = (byte)Math.Clamp(monsterLevel + ItemLevelBonus(monster), 1, 100) };
        if (corruptEightMods)
        {
            item = rolls is not null && rolls.Pool(type, item).Count > 0
                ? rolls.Roll(item, ItemRarity.Rare, rng, shape: (8, 4), respectMetamods: false) with
                {
                    Corrupted = true,
                    Identified = true,
                }
                : item with { Corrupted = true };
        }
        else if (rolls is not null)
        {
            double iirFactor = Math.Max(0, 100 + increasedRarity) / 100.0;
            double rareChance = Math.Min(0.50, 0.04 * iirFactor);
            double magicChance = Math.Min(1.0 - rareChance, 0.12 * iirFactor);
            double rarityRoll = rng.NextDouble();
            if (rarityRoll < rareChance)
                item = rolls.Roll(item, ItemRarity.Rare, rng) with { Identified = false };
            else if (rarityRoll < rareChance + magicChance)
                item = rolls.Roll(item, ItemRarity.Magic, rng) with { Identified = false };
        }
        return new(item, type);
    }

    // Exceptional support gems (Empower, Enhance, Enlighten) drop in a separate rare sub-roll (0.15% of gem drops at L38+).
    const double ExceptionalGemChance = 0.0015;

    // One item: its kind, for equipment its slot and level-weighted base, then its rarity (stages 2 to 4).
    Drop? RollOne(PoolState state, int level, int itemLevel, Chances chances, Random rng, in DropContext context,
        bool inMap = false, double dropPenalty = 1.0, DropWeights? weights = null)
    {
        var pools = state.Pools;
        var settings = SystemSettings.Default;
        double currencyWeight = settings.DropWeightCurrency * settings.DropMultiplier("currency") * (weights?.Currency ?? 1);
        double gemWeight = settings.DropWeightGems * settings.DropMultiplier("gems") * (weights?.Gems ?? 1);
        double flaskWeight = settings.DropWeightFlasks * settings.DropMultiplier("flasks") * (weights?.Flasks ?? 1);
        double equipmentWeight = settings.DropWeightEquipment * settings.DropMultiplier("equipment") * (weights?.Equipment ?? 1);
        IReadOnlyList<ResolvedOverlayEntry>? generalWeighted = null;
        IReadOnlyList<ResolvedOverlayEntry>? bossWeighted = null;
        double generalWeight = 0;
        double bossWeight = 0;
        if (state.GeneralOverlays.Count > 0)
        {
            generalWeighted = MatchingWeightedOverlays(state.GeneralOverlays, level, in context);
            for (int i = 0; i < generalWeighted.Count; i++) generalWeight += generalWeighted[i].Entry.Weight;
        }
        if (state.BossOverlays.Count > 0 && (context.IsBoss || context.Source == DropSourceMask.BossBonus))
        {
            bossWeighted = MatchingWeightedOverlays(state.BossOverlays, level, in context);
            for (int i = 0; i < bossWeighted.Count; i++) bossWeight += bossWeighted[i].Entry.Weight;
        }
        double totalWeight = currencyWeight + gemWeight + flaskWeight + equipmentWeight + generalWeight + bossWeight;
        if (totalWeight <= 0) return null;
        double kind = rng.NextDouble() * totalWeight;
        ItemCatalog.BaseType? type = null;
        if (kind < currencyWeight)
        {
            if (dropPenalty < 1.0 && rng.NextDouble() >= dropPenalty) return null;
            if (PickCurrency(state, level, rng, weights, in context) is { } picked)
                return new Drop(ItemCatalog.Create(picked.Type) with
                {
                    StackSize = ClampStack(picked.Type, picked.StackSize),
                    ItemLevel = (byte)Math.Clamp(itemLevel, 1, 100),
                }, picked.Type);
            return null;
        }
        else if (kind < currencyWeight + gemWeight)
        {
            type = PickGem(state, level, rng, in context);
        }
        else if (kind < currencyWeight + gemWeight + flaskWeight)
        {
            type = PickFlask(state, level, rng, in context);
        }
        else if (kind < currencyWeight + gemWeight + flaskWeight + equipmentWeight)
        {
            type = PickEquipmentBase(state, level, inMap, rng, in context);
        }
        else if (kind < currencyWeight + gemWeight + flaskWeight + equipmentWeight + generalWeight && generalWeighted is { Count: > 0 })
        {
            return PickWeightedOverlay(generalWeighted, rng) is { } chosen
                ? CreateOverlayDrop(chosen, level, itemLevel, chances, rng, in context)
                : null;
        }
        else if (bossWeighted is { Count: > 0 })
        {
            return PickWeightedOverlay(bossWeighted, rng) is { } chosen
                ? CreateOverlayDrop(chosen, level, itemLevel, chances, rng, in context)
                : null;
        }
        return type is null ? null : Rolled(state, type, level, itemLevel, chances, rng, in context);
    }

    IReadOnlyList<ResolvedOverlayEntry> MatchingWeightedOverlays(IReadOnlyList<ResolvedOverlayEntry> overlays, int level, in DropContext context)
    {
        if (overlays.Count == 0) return [];
        var tags = AreaTagsOf(context);
        List<ResolvedOverlayEntry>? hits = null;
        for (int i = 0; i < overlays.Count; i++)
        {
            var ov = overlays[i];
            if (ov.Entry.Chance is > 0 || ov.Entry.Weight <= 0) continue;
            if (ov.BaseType is { } bt && !Allowed(bt, level)) continue;
            if (!ov.Entry.Matches(level, context, tags)) continue;
            (hits ??= []).Add(ov);
        }
        return (IReadOnlyList<ResolvedOverlayEntry>?)hits ?? [];
    }

    static ResolvedOverlayEntry? PickWeightedOverlay(IReadOnlyList<ResolvedOverlayEntry> list, Random rng)
    {
        double total = 0;
        for (int i = 0; i < list.Count; i++) total += Math.Max(0, list[i].Entry.Weight);
        if (total <= 0) return null;
        double pick = rng.NextDouble() * total;
        for (int i = 0; i < list.Count; i++)
        {
            double w = Math.Max(0, list[i].Entry.Weight);
            if (pick < w) return list[i];
            pick -= w;
        }
        return list[^1];
    }

    Drop? CreateOverlayDrop(ResolvedOverlayEntry overlay, int level, int itemLevel, Chances chances, Random rng, in DropContext context)
    {
        if (overlay.Unique is { } uq && uniques is not null)
        {
            if (uniques.Create(uq with { ItemLevel = (byte)Math.Clamp(itemLevel, 1, 100) }, rng) is { } item
                && catalog.ByHash(item.TypeHash) is { } uqType)
                return new Drop(item with { Identified = false }, uqType);
            return null;
        }
        if (overlay.BaseType is not { Supported: true } type) return null;
        if (type.Stackable)
            return new Drop(ItemCatalog.Create(type) with
            {
                StackSize = ClampStack(type, overlay.Entry.StackSize),
                ItemLevel = (byte)Math.Clamp(itemLevel, 1, 100),
            }, type);
        return Rolled(CurrentState(), type, level, itemLevel, chances, rng, in context);
    }

    void AppendChanceOverlays(PoolState state, List<Drop> target, int level, int itemLevel, Chances chances, Random rng,
        DropContext context, double quantity, Func<ItemCatalog.BaseType, bool>? accepts = null)
    {
        void Eval(IReadOnlyList<ResolvedOverlayEntry> list)
        {
            if (list.Count == 0) return;
            var tags = AreaTagsOf(context);
            for (int i = 0; i < list.Count; i++)
            {
                var ov = list[i];
                if (ov.Entry.Chance is not (> 0 and double baseChance)) continue;
                // First-kill campaign bosses invoke both RollBossFirstKillBonus and Roll; only run overlays that
                // explicitly opted into BossBonus (not default DropSourceMask.All) during BossBonus to avoid double-drops.
                if (context.Source == DropSourceMask.BossBonus && ov.Entry.Sources == DropSourceMask.All) continue;
                if (ov.BaseType is { } bt && !Allowed(bt, level)) continue;
                if (!ov.Entry.Matches(level, context, tags)) continue;
                double effective = context.Source == DropSourceMask.MonsterKill
                    ? Math.Min(1.0, baseChance * quantity)
                    : Math.Min(1.0, baseChance);
                if (effective <= 0 || rng.NextDouble() >= effective) continue;
                if (CreateOverlayDrop(ov, level, itemLevel, chances, rng, in context) is { } drop
                    && (accepts is null || accepts(drop.Type)))
                    target.Add(drop);
            }
        }
        Eval(state.GeneralOverlays);
        if (context.IsBoss || context.Source == DropSourceMask.BossBonus) Eval(state.BossOverlays);
        Eval(state.UniqueOverlays);
    }

    /// <summary>A weighted gem drop from the natural gem pool plus any matching <see cref="DropPoolCategory.Gem"/> overlays.</summary>
    public Drop? GemDrop(int level, Random rng, bool supportsOnly = false, DropContext? context = null)
    {
        var state = CurrentState();
        var ctx = NormalizeContext(context, DropSourceMask.MonsterKill);
        return PickGem(state, level, rng, in ctx, supportsOnly) is { } type
            ? new Drop(ItemCatalog.Create(type) with { ItemLevel = 1 }, type)
            : null;
    }

    ItemCatalog.BaseType? PickGem(PoolState state, int level, Random rng, in DropContext context, bool supportsOnly = false)
    {
        var pools = state.Pools;
        var overlays = MatchingOverlays(state.GemOverlays, level, in context);
        List<ResolvedOverlayEntry>? exceptionalOverlays = null;
        List<ResolvedOverlayEntry>? regularOverlays = null;
        for (int i = 0; i < overlays.Count; i++)
        {
            if (overlays[i].Entry.Weight <= 0 || overlays[i].BaseType is not { } bt) continue;
            if (supportsOnly && bt.Class != "Support Skill Gem") continue;
            if (overlays[i].Entry.ExceptionalGem) (exceptionalOverlays ??= []).Add(overlays[i]);
            else (regularOverlays ??= []).Add(overlays[i]);
        }

        if ((pools.ExceptionalGems.Count > 0 || exceptionalOverlays is { Count: > 0 }) && rng.NextDouble() < ExceptionalGemChance)
        {
            var exceptional = pools.ExceptionalGems.Where(g => g.DropLevel <= level && (!supportsOnly || g.Class == "Support Skill Gem")).ToList();
            if (exceptionalOverlays is not { Count: > 0 })
            {
                if (exceptional.Count > 0) return exceptional[rng.Next(exceptional.Count)];
            }
            else
            {
                var weighted = new List<(ItemCatalog.BaseType Value, double Weight)>(exceptional.Count + exceptionalOverlays.Count);
                for (int i = 0; i < exceptional.Count; i++) weighted.Add((exceptional[i], 1.0));
                for (int i = 0; i < exceptionalOverlays.Count; i++) weighted.Add((exceptionalOverlays[i].BaseType!, exceptionalOverlays[i].Entry.Weight));
                if (Pick(weighted, rng) is { } pickedEx) return pickedEx;
            }
        }
        var regular = pools.RegularGems.Where(g => g.DropLevel <= level && (!supportsOnly || g.Class == "Support Skill Gem")).ToList();
        if (regularOverlays is not { Count: > 0 })
            return regular.Count > 0 ? regular[rng.Next(regular.Count)] : null;
        var allRegular = new List<(ItemCatalog.BaseType Value, double Weight)>(regular.Count + regularOverlays.Count);
        for (int i = 0; i < regular.Count; i++) allRegular.Add((regular[i], 1.0));
        for (int i = 0; i < regularOverlays.Count; i++) allRegular.Add((regularOverlays[i].BaseType!, regularOverlays[i].Entry.Weight));
        return Pick(allRegular, rng);
    }

    // Balance flask sub-pools so Tinctures do not take 31% of flask drops (~55% Recovery / 35% Utility / 10% Tincture).
    // Recovery flasks use level-weighted tiering; Utility flasks & Tinctures stay accessible at all levels once unlocked.
    ItemCatalog.BaseType? PickFlask(PoolState state, int level, Random rng, in DropContext context)
    {
        var pools = state.Pools;
        var recovery = pools.RecoveryFlasks.Where(f => f.DropLevel <= level).ToList();
        var utility = pools.UtilityFlasks.Where(f => f.DropLevel <= level).ToList();
        var tincture = pools.Tinctures.Where(f => f.DropLevel <= level).ToList();
        var flaskOverlays = MatchingOverlays(state.FlaskOverlays, level, in context);
        var tinctureOverlays = MatchingOverlays(state.TinctureOverlays, level, in context);
        double wRec = recovery.Count > 0 ? 55.0 : 0;
        double wUtil = utility.Count > 0 ? 35.0 : 0;
        double wTinc = tincture.Count > 0 ? 10.0 : 0;
        double wOv = 0;
        for (int i = 0; i < flaskOverlays.Count; i++) if (flaskOverlays[i].Entry.Weight > 0) wOv += flaskOverlays[i].Entry.Weight;
        for (int i = 0; i < tinctureOverlays.Count; i++) if (tinctureOverlays[i].Entry.Weight > 0) wOv += tinctureOverlays[i].Entry.Weight;
        double total = wRec + wUtil + wTinc + wOv;
        if (total <= 0) return null;
        double pick = rng.NextDouble() * total;
        if (pick < wRec) return PickLevelWeighted(recovery, level, rng);
        if (pick < wRec + wUtil) return utility[rng.Next(utility.Count)];
        if (pick < wRec + wUtil + wTinc) return tincture[rng.Next(tincture.Count)];
        double ovPick = pick - (wRec + wUtil + wTinc);
        ItemCatalog.BaseType? last = null;
        for (int i = 0; i < flaskOverlays.Count; i++)
        {
            double w = Math.Max(0, flaskOverlays[i].Entry.Weight);
            if (w <= 0 || flaskOverlays[i].BaseType is not { } bt) continue;
            last = bt;
            if (ovPick < w) return bt;
            ovPick -= w;
        }
        for (int i = 0; i < tinctureOverlays.Count; i++)
        {
            double w = Math.Max(0, tinctureOverlays[i].Entry.Weight);
            if (w <= 0 || tinctureOverlays[i].BaseType is not { } bt) continue;
            last = bt;
            if (ovPick < w) return bt;
            ovPick -= w;
        }
        return last;
    }

    ItemCatalog.BaseType? PickEquipmentBase(PoolState state, int level, bool inMap, Random rng, in DropContext context)
    {
        var pools = state.Pools;
        var slots = Slots.Where(s => pools.Equipment.GetValueOrDefault(s.Class)?.Any(t => t.Type.DropLevel <= level && (inMap || !t.Atlas)) == true).ToList();
        var overlays = MatchingOverlays(state.EquipmentOverlays, level, in context);
        if (overlays.Count > 0)
        {
            double stockTotal = slots.Sum(s => Math.Max(0, s.Weight));
            double ovTotal = 0;
            for (int i = 0; i < overlays.Count; i++)
                if (overlays[i].BaseType is not null && overlays[i].Entry.Weight > 0 && (inMap || !overlays[i].Entry.AtlasOnly))
                    ovTotal += overlays[i].Entry.Weight;
            if (ovTotal > 0)
            {
                double total = stockTotal + ovTotal;
                double roll = rng.NextDouble() * total;
                if (roll >= stockTotal)
                {
                    double ovPick = roll - stockTotal;
                    ItemCatalog.BaseType? last = null;
                    for (int i = 0; i < overlays.Count; i++)
                    {
                        var ov = overlays[i];
                        if (ov.BaseType is not { } bt || ov.Entry.Weight <= 0 || (!inMap && ov.Entry.AtlasOnly)) continue;
                        last = bt;
                        if (ovPick < ov.Entry.Weight) return bt;
                        ovPick -= ov.Entry.Weight;
                    }
                    return last;
                }
            }
        }
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

    public static ItemCatalog.BaseType PickLevelWeighted(IReadOnlyList<ItemCatalog.BaseType> bases, int monsterLevel, Random rng)
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
        double rarityMultiplier = 1, DropContext? context = null)
    {
        if (!type.Supported) return null;
        var state = CurrentState();
        var ctx = NormalizeContext(context, DropSourceMask.MonsterKill, rarity);
        var chances = WithUniqueMultiplier(WithIncreasedRarity(RarityChances(rarity), increasedRarity,
            rarityMultiplier * SystemSettings.Default.DropMultiplier("unique_rarity")));
        return Rolled(state, type, monsterLevel, monsterLevel + ItemLevelBonus(rarity), chances, rng, in ctx);
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

    Drop? Rolled(PoolState state, ItemCatalog.BaseType type, int level, int itemLevel, Chances chances, Random rng, in DropContext context)
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
            if (rolledUnique && Unique(state, type, level, itemLevel, rng, in context) is { } unique)
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
            if (rolledUnique && Unique(state, type, level, itemLevel, rng, in context) is { } unique)
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
            if (rolledUnique && Unique(state, type, level, itemLevel, rng, in context) is { } unique)
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
    /// encounter-only exclusions, T0 drop-level floors, unique weights, and runtime unique overlays.</summary>
    public Item? TryUnique(ItemCatalog.BaseType type, int monsterLevel, int itemLevel, Random rng, DropContext? context = null)
    {
        var state = CurrentState();
        var ctx = NormalizeContext(context, DropSourceMask.MonsterKill);
        return Unique(state, type, monsterLevel, itemLevel, rng, in ctx);
    }

    /// <summary>Whether <paramref name="definition"/> is eligible for general drops at <paramref name="monsterLevel"/>
    /// (not encounter-only, and meeting both its mod-derived drop level and T0 drop-level floor).</summary>
    public bool IsDropEligibleUnique(UniqueCatalog.Definition definition, int monsterLevel, DropContext? context = null)
    {
        if (definition.EncounterOnly) return false;
        var state = CurrentState();
        if (context is { } rawCtx && state.UniqueOverlaysByName.TryGetValue(UniqueCatalog.NameKey(definition.Name), out var ovList))
        {
            var ctx = NormalizeContext(rawCtx, rawCtx.Source);
            var tags = AreaTagsOf(ctx);
            bool anyMatched = false;
            for (int i = 0; i < ovList.Count; i++)
                if (ovList[i].Entry.Matches(monsterLevel, ctx, tags)) { anyMatched = true; break; }
            if (!anyMatched && uniques?.OverlayWeight(definition.Name, hardMode) is null
                && (uniques is null || !uniques.HasStockDefinition(definition.Name)))
                return false;
        }
        if (state.Uniques is { } index
            && index.ByBase.TryGetValue(definition.BaseTypeId, out var entries))
        {
            foreach (var entry in entries)
                if (ReferenceEquals(entry.Definition, definition)
                    || entry.Definition.Name.Equals(definition.Name, StringComparison.OrdinalIgnoreCase))
                    return entry.DropLevel <= monsterLevel;
        }
        return EffectiveUniqueDropLevel(definition, _uniqueWeightProvider.HasTable || _uniqueWeightsFile.Count > 0, state.UniqueOverlaysByName) <= monsterLevel;
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

    int EffectiveUniqueDropLevel(UniqueCatalog.Definition definition, bool hasWeights,
        Dictionary<string, List<ResolvedOverlayEntry>> uniqueOverlaysByName)
    {
        int dropLevel = UniqueDropLevel(definition, catalog.Find(definition.BaseTypeId)?.DropLevel ?? 1, catalog.Mods);
        if (definition.MinDropLevel is int minFloor && minFloor > 0)
            dropLevel = Math.Max(dropLevel, minFloor);
        bool isOverlay = uniqueOverlaysByName.ContainsKey(UniqueCatalog.NameKey(definition.Name))
            || uniques?.OverlayWeight(definition.Name, hardMode) is not null;
        if (TierZeroUniques.Contains(definition.Name)
            || (!isOverlay && hasWeights && StockUniqueWeight(definition.Name) is > 0 and <= 0.002))
            dropLevel = Math.Max(dropLevel, TierZeroMinDropLevel);
        return dropLevel;
    }

    UniqueIndex? BuildUniqueIndex(Dictionary<string, List<ResolvedOverlayEntry>> uniqueOverlaysByName)
    {
        if (uniques is null) return null;
        var all = uniques.All;
        var byBase = new Dictionary<string, List<UniqueEntry>>(StringComparer.Ordinal);
        bool hasWeights = _uniqueWeightProvider.HasTable || _uniqueWeightsFile.Count > 0;
        foreach (var definition in all)
        {
            if (definition.EncounterOnly) continue;
            int dropLevel = EffectiveUniqueDropLevel(definition, hasWeights, uniqueOverlaysByName);
            if (!byBase.TryGetValue(definition.BaseTypeId, out var list)) byBase[definition.BaseTypeId] = list = [];
            list.Add(new(definition, dropLevel));
        }
        return new UniqueIndex(byBase);
    }

    // A unique of this base legal at the monster's level, or null when the base has none the server can create (then
    // the item rolls on as rare or magic, as the game makes it rare).
    Item? Unique(PoolState state, ItemCatalog.BaseType type, int monsterLevel, int itemLevel, Random rng, in DropContext context)
    {
        if (state.Uniques is not { } index || !index.ByBase.TryGetValue(type.Id, out var candidates)) return null;
        var allowed = candidates.Where(c => c.DropLevel <= monsterLevel).Select(c => c.Definition).ToList();
        if (allowed.Count == 0 || PickUnique(state, allowed, rng, monsterLevel, context) is not { } chosen) return null;
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
    public UniqueCatalog.Definition? PickUnique(IReadOnlyList<UniqueCatalog.Definition> candidates, Random rng) =>
        PickUnique(CurrentState(), candidates, rng, level: 0, context: null);

    public UniqueCatalog.Definition? PickUnique(IReadOnlyList<UniqueCatalog.Definition> candidates, Random rng, int level, DropContext? context) =>
        PickUnique(CurrentState(), candidates, rng, level, context);

    UniqueCatalog.Definition? PickUnique(PoolState state, IReadOnlyList<UniqueCatalog.Definition> candidates, Random rng,
        int level, DropContext? context)
    {
        double total = 0;
        for (int i = 0; i < candidates.Count; i++)
            total += Math.Max(0, UniqueWeight(state, candidates[i].Name, level, context));
        double pick = rng.NextDouble() * Math.Max(1, total);
        for (int i = 0; i < candidates.Count; i++)
        {
            double weight = Math.Max(0, UniqueWeight(state, candidates[i].Name, level, context));
            if (pick < weight) return candidates[i];
            pick -= weight;
        }
        return null;
    }

    double StockUniqueWeight(string name) =>
        _uniqueWeightProvider.HasTable
            ? _uniqueWeightProvider.Weight(name, hardMode)
            : _uniqueWeightsFile is { Count: > 0 } weights ? weights.GetValueOrDefault(UniqueCatalog.NameKey(name)) : 1;

    /// <summary>How likely a unique is once its base rolled one: the inverse of its disenchant value
    /// (VillageUniqueDisenchantValues, HardMode has its own column), the client's own measure of how rare a unique is:
    /// Wanderlust 1.06, Kaom's Heart 43, Mageblood 891. Runtime unique overlays and <see cref="DropPoolOverlays"/>
    /// unique entries layer cleanly on top of the stock table.</summary>
    public double UniqueWeight(string name) => UniqueWeight(CurrentState(), name, level: 0, context: null);

    double UniqueWeight(PoolState state, string name, int level, DropContext? context)
    {
        string key = UniqueCatalog.NameKey(name);
        bool anyWeightedOverlay = false;
        if (state.UniqueOverlaysByName.TryGetValue(key, out var overlays))
        {
            double weight = 0;
            bool anyMatched = false;
            var ctx = context ?? NormalizeContext(null, DropSourceMask.MonsterKill);
            var tags = AreaTagsOf(ctx);
            for (int i = 0; i < overlays.Count; i++)
            {
                var ov = overlays[i];
                if (ov.Entry.Chance is > 0 || ov.Entry.Weight <= 0) continue;
                anyWeightedOverlay = true;
                bool matched;
                if (context is null)
                {
                    matched = (ov.Entry.HardModeOnly is not bool hm || hm == hardMode)
                        && (ov.Entry.Leagues is not { Count: > 0 }
                            || (!string.IsNullOrEmpty(_leagueId)
                                && ov.Entry.Leagues.Any(l => string.Equals(l, _leagueId, StringComparison.OrdinalIgnoreCase))));
                }
                else
                {
                    matched = ov.Entry.Matches(level, ctx, tags);
                }
                if (matched)
                {
                    anyMatched = true;
                    weight += ov.Entry.Weight;
                }
            }
            if (anyMatched) return weight;
        }
        if (uniques?.OverlayWeight(name, hardMode) is double overlayWeight)
            return Math.Max(0, overlayWeight);
        if (anyWeightedOverlay && uniques is not null && !uniques.HasStockDefinition(name))
            return 0;
        return StockUniqueWeight(name);
    }

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

    // Allocation-free fast path: a kill's currency rolls run here. A mod's factors (DropWeights.Item) multiply each type's weight.
    (ItemCatalog.BaseType Type, ushort StackSize)? PickCurrency(PoolState state, int level, Random rng,
        DropWeights? factors, in DropContext context, Func<ItemCatalog.BaseType, bool>? accepts = null)
    {
        var pool = state.Pools.Currency;
        var overlays = state.CurrencyOverlays;
        if (factors is not { Items.Count: > 0 }) factors = null;
        var areaTags = overlays.Count > 0 ? AreaTagsOf(context) : null;
        double total = 0;
        for (int i = 0; i < pool.Count; i++)
            if (pool[i].DropLevel <= level && (accepts is null || accepts(pool[i].Type))
                && Weight(pool[i]) is var w && w > 0) total += w;
        for (int i = 0; i < overlays.Count; i++)
        {
            var ov = overlays[i];
            if (ov.BaseType is not { } bt || bt.DropLevel > level || ov.Entry.Weight <= 0) continue;
            if (accepts is not null && !accepts(bt)) continue;
            if (!ov.Entry.Matches(level, context, areaTags)) continue;
            double w = factors is null ? ov.Entry.Weight : ov.Entry.Weight * factors.Item(bt.Id);
            if (w > 0) total += w;
        }
        if (total <= 0) return null;
        double pick = rng.NextDouble() * total;
        (ItemCatalog.BaseType Type, ushort StackSize)? last = null;
        for (int i = 0; i < pool.Count; i++)
        {
            var entry = pool[i];
            if (entry.DropLevel > level || (accepts is not null && !accepts(entry.Type))) continue;
            double weight = Weight(entry);
            if (weight <= 0) continue;
            last = (entry.Type, (ushort)1);
            if (pick < weight) return last;
            pick -= weight;
        }
        for (int i = 0; i < overlays.Count; i++)
        {
            var ov = overlays[i];
            if (ov.BaseType is not { } bt || bt.DropLevel > level || ov.Entry.Weight <= 0) continue;
            if (accepts is not null && !accepts(bt)) continue;
            if (!ov.Entry.Matches(level, context, areaTags)) continue;
            double weight = factors is null ? ov.Entry.Weight : ov.Entry.Weight * factors.Item(bt.Id);
            if (weight <= 0) continue;
            last = (bt, ov.Entry.StackSize);
            if (pick < weight) return last;
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

    static T? PickStruct<T>(List<(T Value, double Weight)> weighted, Random rng) where T : struct
    {
        double total = 0;
        for (int i = 0; i < weighted.Count; i++) total += Math.Max(0, weighted[i].Weight);
        if (total <= 0) return null;
        double pick = rng.NextDouble() * total;
        for (int i = 0; i < weighted.Count; i++)
        {
            double w = Math.Max(0, weighted[i].Weight);
            if (pick < w) return weighted[i].Value;
            pick -= w;
        }
        return weighted[^1].Value;
    }

    private static Pools Build(ItemCatalog catalog, string folder, bool hardMode, LeagueRules? league, DropDataProvider.Snapshot dropData)
    {
        var currency = new List<(ItemCatalog.BaseType Type, double Weight, int DropLevel)>();
        // The seeded/overridden database table wins; the compiled table is the fallback while it is empty or the
        // database fails. Either way the higher of the client's DropLevel and the table's drop_level gates the roll.
        var seeded = dropData.CurrencyWeights;
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
        foreach (var t in catalog.All)
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
