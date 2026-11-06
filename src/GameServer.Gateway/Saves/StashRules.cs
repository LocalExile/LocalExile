using System.Text.Json;
using GameServer.Core.Protocol.Instance;

/// <summary>
/// What each stash tab type holds (Agents/docs/protocol/18-stash.md §7). An item's affinities (StashTabAffinityId rows)
/// come from StashTabAffinityByBaseItemType, StashtabAffinityByItemClassCategory and the special tabs' layouts; each
/// affinity lists the StashType rows that may hold it (StashTabAffinities). Normal, premium and quad tabs hold anything.
/// The currency, essence, fragment, delve and blight tabs give each item type one slot (their *StashTabLayout
/// FirstSlotIndex): the client shows a slot as cell (slot, 0), and an item dropped anywhere on the tab goes into its own
/// slot. Currency slots use CurrencyItems.CurrencyTab_StackSize; other slotted tabs default to <see cref="SlotStack"/>.
/// </summary>
sealed class StashRules
{
    public UniqueStashLayout Uniques { get; private set; } = new();
    public const byte Currency = 3, Unique = 4, Map = 5, Divination = 6, Essence = 8, Fragment = 9, Delve = 12, Blight = 13,
        Ultimatum = 14, Delirium = 15, Flask = 17, Gem = 18, Breach = 22;

    /// <summary>Fallback limit for slotted tabs without an item-specific currency-tab limit.</summary>
    public const ushort SlotStack = 5000;

    // Affinity rows (StashTabAffinityId).
    const int CurrencyItems = 3, Maps = 5, DivinationCards = 6, Essences = 8, Fragments = 9, DelveItems = 12, BlightItems = 13,
        DeliriumItems = 15;

    readonly Dictionary<uint, HashSet<int>> _affinities = [];        // item type HASH32 → affinities
    readonly Dictionary<int, HashSet<byte>> _heldBy = [];             // affinity → StashType rows
    readonly Dictionary<byte, Dictionary<uint, int>> _slots = [];     // StashType row → item type → slot
    readonly Dictionary<uint, ushort> _currencyTabStack = [];  // CurrencyItems.CurrencyTab_StackSize; else SlotStack
    readonly Dictionary<byte, HashSet<int>> _anySlots = [];          // StashType row → slots that take any item
    readonly Dictionary<uint, int> _mapTiers = [];                   // map item HASH32 → MapKeys.MapTier (1..16)
    readonly List<uint> _mapBases = [];                              // the map tab's bases: MapKeys rows + the four special maps
    readonly List<uint> _legendaryMaps = [];                         // MapStashUniqueMapInfo HASH16: the legendary maps' pages
    readonly Dictionary<ushort, (int Group, int Id)> _guardianMods = [];   // Mods HASH16 → map tab group and guardian id

    /// <summary>The map tab group and guardian of an item from its map modifiers, or (0, 0) when it carries none:
    /// `MapConquerorContainsBossN` (map_contains_citadel 1..4) for a conqueror, `MapElderContainsBossN`
    /// (map_elder_boss_variation 1..4) for an elder guardian (#382).</summary>
    public (int Group, int Id) GuardianOf(Item item)
    {
        foreach (var mod in item.Mods)
            if (_guardianMods.TryGetValue(mod.Hash, out var guardian) && guardian.Group > 0) return guardian;
        return (0, 0);
    }

    /// <summary>The modifier hash of a guardian's identity, for tests and tools; 0 when there is none.</summary>
    public ushort GuardianModHash(int group, int id) =>
        _guardianMods.FirstOrDefault(kv => kv.Value == (group, id)) is { Key: not 0 } hit ? hit.Key : (ushort)0;

    /// <summary>The tier of a map item (MapKeys), or null when the type is not a map. The map tab's pages are one per
    /// map base (#477).</summary>
    public int? MapTier(uint hash) => _mapTiers.TryGetValue(hash, out int tier) ? tier : null;

    /// <summary>The base types the map tab gives pages to, in tier order: the MapKeys rows and the four special maps. The
    /// client's expected page keys are per base type, not per tier number (#477).</summary>
    public IReadOnlyList<uint> MapBases => _mapBases;

    /// <summary>The legendary maps the map tab gives a page each, keyed by MapStashUniqueMapInfo.HASH16 (#477).</summary>
    public IReadOnlyList<uint> LegendaryMaps => _legendaryMaps;

    /// <summary>
    /// Whether cell (x, y) of a tab type is a slot that takes any item: the currency tab's crafting slot
    /// (CurrencyStashTabLayout row CraftingSlot, slot 28, the big cell in the middle; the client drops onto it as (28, 0),
    /// live 2026-10-23). An item there keeps its own stack size.
    /// </summary>
    public bool TakesAnything(byte tabType, int x, int y) => y == 0 && _anySlots.TryGetValue(tabType, out var s) && s.Contains(x);

    readonly Dictionary<byte, List<int>> _overflowSlots = [];   // StashType row → spare slots, in screen order

    /// <summary>
    /// The spare slots of a tab type, left to right and top to bottom on screen: the currency tab's 14 cells under the
    /// crafting slot (CurrencyStashTabLayout rows Generic1–Generic14, slots 30–34 and 40–48). They take any item the tab
    /// holds; Ctrl + click puts there what its own slot has no room for.
    /// </summary>
    public IReadOnlyList<int> OverflowSlots(byte tabType) => _overflowSlots.TryGetValue(tabType, out var s) ? s : [];

    public bool IsOverflowSlot(byte tabType, int x, int y) => y == 0 && _overflowSlots.TryGetValue(tabType, out var s) && s.Contains(x);

    /// <summary>False without the tables: then every tab holds anything (there is nothing to check against).</summary>
    public bool Loaded { get; private set; }

    /// <summary>Normal, premium, quad (and PC-bang premium) tabs: a grid that holds any item.</summary>
    public static bool IsGrid(byte tabType) => tabType is 0 or 1 or 7 or 10;

    /// <summary>
    /// Tab types whose page the client lays out its own way, which the Gateway doesn't know yet: items saved there are
    /// not shown, so these tabs take nothing. The map tab: the 3.29 client lists maps by tier, not at grid cells (two Beach
    /// Maps at (0, 0) and (0, 1) were saved but not shown, live 2026-10-20). Unique parents hold no direct items:
    /// AccountStash routes them to hidden category inventories using UniqueStashLayout. Breach and ultimatum
    /// still have no supported item layout.
    /// </summary>
    public static bool NotShown(byte tabType) => tabType is Map or Unique or Breach or Ultimatum;

    public bool Holds(byte tabType, uint itemType) =>
        !NotShown(tabType) && (IsGrid(tabType) || !Loaded
            || (_affinities.TryGetValue(itemType, out var a) && a.Any(x => _heldBy.TryGetValue(x, out var t) && t.Contains(tabType))));

    /// <summary>Whether the tab type gives each item type its own slot.</summary>
    public bool Slotted(byte tabType) => _slots.ContainsKey(tabType);

    /// <summary>The item types that have a slot of their own in a slotted tab type, with their slot (empty for other types).</summary>
    public IReadOnlyDictionary<uint, int> Slots(byte tabType) => _slots.TryGetValue(tabType, out var s) ? s : NoSlots;

    static readonly Dictionary<uint, int> NoSlots = [];

    /// <summary>The item type's slot in a slotted tab type, or null.</summary>
    public int? SlotOf(byte tabType, uint itemType) =>
        _slots.TryGetValue(tabType, out var s) && s.TryGetValue(itemType, out int slot) ? slot : null;

    /// <summary>How many of the item type one cell of the tab holds, or null for the item's own stack size.</summary>
    public ushort? MaxStack(byte tabType, uint itemType) =>
        Slotted(tabType) ? tabType == Currency && _currencyTabStack.TryGetValue(itemType, out var n) ? n : SlotStack : null;

    public static StashRules Load(string folder, IReadOnlyList<ItemCatalog.BaseType> rows)
    {
        var rules = new StashRules { Uniques = UniqueStashLayout.Load(folder) };
        string Table(string name) => Path.Combine(folder, "tables", name + ".json");
        string[] needed = ["StashTabAffinities", "StashTabAffinityByBaseItemType", "StashtabAffinityByItemClassCategory", "ItemClasses",
            "CurrencyStashTabLayout", "EssenceStashTabLayout", "FragmentStashTabLayout", "DelveStashTabLayout", "BlightStashTabLayout",
            "DeliriumStashTabLayout", "DivinationCardStashTabLayout", "Mods"];
        if (rows.Count == 0 || needed.Any(n => !File.Exists(Table(n)))) return rules;
        var byId = rows.GroupBy(r => r.Id).ToDictionary(g => g.Key, g => g.First().Hash);
        uint? HashOfRow(int row) => row >= 0 && row < rows.Count ? rows[row].Hash : null;
        void Give(uint hash, int affinity) => (rules._affinities.TryGetValue(hash, out var s) ? s : rules._affinities[hash] = []).Add(affinity);

        if (File.Exists(Table("CurrencyItems")))
        {
            using var d = GameTables.Open(Table("CurrencyItems"));
            foreach (var r in d.RootElement.EnumerateArray())
                if (r.GetProperty("BaseItemTypesKey") is { ValueKind: JsonValueKind.Number } b
                    && HashOfRow(b.GetInt32()) is uint hash
                    && r.TryGetProperty("CurrencyTab_StackSize", out var stack) && stack.ValueKind == JsonValueKind.Number
                    && stack.TryGetInt32(out int size) && size is > 0 and <= ushort.MaxValue)
                    rules._currencyTabStack[hash] = (ushort)size;
        }

        if (File.Exists(Table("MapKeys")))
        {
            using var d = GameTables.Open(Table("MapKeys"));
            foreach (var r in d.RootElement.EnumerateArray())
                if (r.GetProperty("BaseItemType") is { ValueKind: JsonValueKind.Number } b
                    && HashOfRow(b.GetInt32()) is uint hash
                    && r.GetProperty("MapTier") is { ValueKind: JsonValueKind.Number } tier)
                {
                    rules._mapTiers.TryAdd(hash, tier.GetInt32());
                    // A base the client gives the map tab pages to, in tier order (its page keys use its HASH32).
                    if (!rules._mapBases.Contains(hash)) rules._mapBases.Add(hash);
                }
            rules._mapBases.Sort((a, b) => rules._mapTiers[a].CompareTo(rules._mapTiers[b]));
        }

        // The map tab's other group 0 bases: the four special maps of MapStashSpecialSubStashGroup (a "Key" row each).
        if (File.Exists(Table("MapStashSpecialSubStashGroup")))
        {
            using var d = GameTables.Open(Table("MapStashSpecialSubStashGroup"));
            foreach (var r in d.RootElement.EnumerateArray())
                if (r.GetProperty("BaseItemTypes") is { ValueKind: JsonValueKind.Number } b
                    && HashOfRow(b.GetInt32()) is uint hash && !rules._mapBases.Contains(hash))
                    rules._mapBases.Add(hash);
        }

        // A guardian map names its guardian in a modifier, not in its base type: MapConquerorContainsBossN (a conqueror,
        // group 2) and MapElderContainsBossN (an elder guardian, group 3), both numbered 1 to 4 (#382).
        {
            using var d = GameTables.Open(Table("Mods"));
            foreach (var r in d.RootElement.EnumerateArray())
            {
                if (!r.TryGetProperty("Id", out var idValue) || idValue.ValueKind != JsonValueKind.String) continue;
                var match = System.Text.RegularExpressions.Regex.Match(idValue.GetString() ?? "",
                    "^Map(Conqueror|Elder)ContainsBoss([1-9])");
                if (!match.Success || !int.TryParse(match.Groups[2].Value, out int guardian)
                    || guardian > AccountStash.SpecialGroups) continue;
                int group = match.Groups[1].Value == "Conqueror" ? 2 : 3;
                if (r.TryGetProperty("HASH16", out var hash16) && hash16.ValueKind == JsonValueKind.Number)
                    rules._guardianMods[(ushort)hash16.GetInt32()] = (group, guardian);
            }
        }

        // The legendary maps: one page each, keyed by MapStashUniqueMapInfo.HASH16. Rows for a drop-disabled unique, and
        // repeats of a unique already listed, are not among the keys the client expects (23 of the 33 rows).
        if (File.Exists(Table("MapStashUniqueMapInfo")) && File.Exists(Table("UniqueMaps")))
        {
            var names = new List<string?>();
            var disabled = new HashSet<string?>();
            using (var u = GameTables.Open(Table("UniqueMaps")))
                foreach (var r in u.RootElement.EnumerateArray())
                {
                    string? name = r.TryGetProperty("Name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
                    names.Add(name);
                    if (r.TryGetProperty("IsDropDisabled", out var f) && f.ValueKind == JsonValueKind.True) disabled.Add(name);
                }
            var seen = new HashSet<string?>();
            using var mi = GameTables.Open(Table("MapStashUniqueMapInfo"));
            foreach (var r in mi.RootElement.EnumerateArray())
            {
                int row = r.GetProperty("UniqueMap").GetInt32();
                string? name = row >= 0 && row < names.Count ? names[row] : null;
                if (name is not null && disabled.Contains(name)) continue;
                if (!seen.Add(name)) continue;
                if (r.TryGetProperty("HASH16", out var h) && h.ValueKind == JsonValueKind.Number && h.GetInt32() is int hash16 && hash16 > 0)
                    rules._legendaryMaps.Add((uint)hash16);
            }
        }

        using (var d = GameTables.Open(Table("StashTabAffinities")))
            foreach (var r in d.RootElement.EnumerateArray())
                rules._heldBy[r.GetProperty("Id").GetInt32()] = [.. r.GetProperty("__col2").EnumerateArray().Select(t => (byte)t.GetInt32())];
        using (var d = GameTables.Open(Table("StashTabAffinityByBaseItemType")))
            foreach (var r in d.RootElement.EnumerateArray())
                if (HashOfRow(r.GetProperty("BaseItemType").GetInt32()) is uint hash)
                    foreach (var a in r.GetProperty("StashTabAffinityId").EnumerateArray()) Give(hash, a.GetInt32());

        // By the item's class category. Maps (category Map) are not in the table, only map keys: the map tab holds both.
        var byCategory = new Dictionary<int, int> { [25] = Maps };
        using (var d = GameTables.Open(Table("StashtabAffinityByItemClassCategory")))
            foreach (var r in d.RootElement.EnumerateArray())
                byCategory[r.GetProperty("ItemClassCategory").GetInt32()] = r.GetProperty("StashTabAffinityId").GetInt32();
        var categoryOfClass = new Dictionary<string, int>();
        using (var d = GameTables.Open(Table("ItemClasses")))
            foreach (var r in d.RootElement.EnumerateArray())
                if (r.GetProperty("ItemClassCategory") is { ValueKind: JsonValueKind.Number } c) categoryOfClass[r.GetProperty("Id").GetString() ?? ""] = c.GetInt32();
        foreach (var t in rows)
            if (categoryOfClass.TryGetValue(t.Class, out int category) && byCategory.TryGetValue(category, out int affinity)) Give(t.Hash, affinity);

        // The slotted tabs: each layout row's item type (its Id, a currency slot's StoredItem, a fragment slot's StoredItems rows) has that slot.
        void Layout(string table, byte tabType, int affinity)
        {
            var slots = rules._slots[tabType] = [];
            var spare = new List<(int Y, int X, int Slot)>();
            using var d = GameTables.Open(Table(table));
            foreach (var r in d.RootElement.EnumerateArray())
            {
                int slot = r.GetProperty("FirstSlotIndex").GetInt32();
                string id = r.GetProperty("Id").GetString() ?? "";
                if (id == "CraftingSlot" || (tabType == Delve && id.StartsWith("GenericSlot", StringComparison.Ordinal)))
                {
                    (rules._anySlots.TryGetValue(tabType, out var any) ? any : rules._anySlots[tabType] = []).Add(slot);
                    continue;
                }
                if (id.StartsWith("Generic", StringComparison.Ordinal) && r.TryGetProperty("XOffset", out var x) && r.TryGetProperty("YOffset", out var y))
                {
                    spare.Add((y.GetInt32(), x.GetInt32(), slot));
                    continue;
                }
                var types = new List<uint>();
                if (byId.TryGetValue(r.GetProperty("Id").GetString() ?? "", out uint own)) types.Add(own);
                // The currency layout names its item in StoredItem: for the Eldritch Embers and Ichors the row's Id is an old
                // name (CurrencyCleansingFireOrb1 → CurrencyEldritchEmber1), so the Id alone missed them.
                if (r.TryGetProperty("StoredItem", out var one) && one.ValueKind == JsonValueKind.Number && HashOfRow(one.GetInt32()) is uint item)
                    types.Add(item);
                if (r.TryGetProperty("StoredItems", out var stored))
                    foreach (var s in stored.EnumerateArray()) if (HashOfRow(s.GetInt32()) is uint h) types.Add(h);
                foreach (uint hash in types)
                {
                    slots.TryAdd(hash, slot);
                    Give(hash, affinity);
                }
            }
            // Screen order: rows by YOffset (the two rows are 680 and 770, the cells of a row within a few pixels), then X.
            if (spare.Count > 0)
                rules._overflowSlots[tabType] = [.. spare.OrderBy(s => s.Y / 40).ThenBy(s => s.X).Select(s => s.Slot)];
        }
        Layout("CurrencyStashTabLayout", Currency, CurrencyItems);
        Layout("EssenceStashTabLayout", Essence, Essences);
        Layout("FragmentStashTabLayout", Fragment, Fragments);
        Layout("DelveStashTabLayout", Delve, DelveItems);
        Layout("BlightStashTabLayout", Blight, BlightItems);
        Layout("DeliriumStashTabLayout", Delirium, DeliriumItems);
        // Divination cards: the table lists the cards in order, without slot numbers; a card's slot is its row (our guess).
        var cards = rules._slots[Divination] = [];
        using (var d = GameTables.Open(Table("DivinationCardStashTabLayout")))
            foreach (var (r, i) in d.RootElement.EnumerateArray().Select((r, i) => (r, i)))
                if (r.GetProperty("StoredItem") is { ValueKind: JsonValueKind.Number } c && HashOfRow(c.GetInt32()) is uint card)
                {
                    cards.TryAdd(card, i);
                    Give(card, DivinationCards);
                }

        rules.Loaded = true;
        return rules;
    }

    /// <summary>Rules from given data (tests).</summary>
    internal static StashRules For(Dictionary<uint, HashSet<int>> affinities, Dictionary<int, HashSet<byte>> heldBy,
        Dictionary<byte, Dictionary<uint, int>> slots, Dictionary<uint, ushort>? currencyTabStack = null,
        Dictionary<byte, HashSet<int>>? anySlots = null, Dictionary<byte, List<int>>? overflowSlots = null)
    {
        var rules = new StashRules { Loaded = true };
        foreach (var (k, v) in affinities) rules._affinities[k] = v;
        foreach (var (k, v) in heldBy) rules._heldBy[k] = v;
        foreach (var (k, v) in slots) rules._slots[k] = v;
        foreach (var (k, v) in currencyTabStack ?? []) rules._currencyTabStack[k] = v;
        foreach (var (k, v) in anySlots ?? []) rules._anySlots[k] = v;
        foreach (var (k, v) in overflowSlots ?? []) rules._overflowSlots[k] = v;
        return rules;
    }
}
