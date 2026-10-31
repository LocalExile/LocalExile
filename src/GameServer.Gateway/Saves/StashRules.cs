using System.Text.Json;

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
            "DeliriumStashTabLayout", "DivinationCardStashTabLayout"];
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
