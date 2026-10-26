using GameServer.Core.Protocol.Instance;

/// <summary>
/// NPC shops (Agents/docs/protocol/17-npcs.md §5). The client files name the shops (NPCShopSets → NPCShop) but not what
/// they sell: the stock is the server's. Ours is a fixed Act 1 assortment per NPC. A shop tab is an inventory in the
/// client's second inventory manager (S→C 0xc0 manager 1, instance +0xca10), opened with S→C 0x8c.
/// </summary>
static class NpcShops
{
    /// <summary>A shop tab: 12 × 12 cells, the size of the vendor window's grid. Inventories.dat row 83
    /// (CraftPredictionScratch, kind 0x0f) only gives the client a generic grid type.</summary>
    public static readonly InstanceInventories.Slot Tab = new(83, 0x0f, 12, 12);

    /// <summary>A sell offer grid (the player's and the NPC's side of the sell window): the main inventory's row and size,
    /// so every item fits (the client checks a drop against the row, 0x141d628f0; row 83 refused everything).</summary>
    public static readonly InstanceInventories.Slot Offer = new(0, 0x00, 12, 5);

    /// <summary>Inventory manager of NPC inventories (0x14216aa30: 0 player, 1 instance +0xca10, 2 +0xcb90).</summary>
    public const byte Manager = 1;

    /// <summary>Shop tab inventory ids, above the player's 1..117.</summary>
    public const uint FirstTabId = 0x400;

    static readonly Dictionary<string, (string Name, int Count)[]> Stock = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Metadata/NPC/Act1/Tarkleigh"] =
        [
            ("Rusted Sword", 1), ("Rusted Hatchet", 1), ("Driftwood Club", 1), ("Glass Shank", 1), ("Crude Bow", 1),
            ("Driftwood Wand", 1), ("Driftwood Sceptre", 1), ("Splintered Tower Shield", 1), ("Plate Vest", 1),
            ("Shabby Jerkin", 1), ("Simple Robe", 1), ("Iron Hat", 1), ("Iron Gauntlets", 1), ("Iron Greaves", 1),
        ],
        ["Metadata/NPC/Act1/Nessa"] =
        [
            ("Small Life Flask", 3), ("Small Mana Flask", 3), ("Medium Life Flask", 1), ("Scroll of Wisdom", 5),
            ("Portal Scroll", 5), ("Orb of Transmutation", 1), ("Fireball", 1), ("Frostblink", 1),
        ],
        // Kirac in the hideout sells maps for the map device (2026-10-21); names the game data lacks are skipped.
        ["Metadata/NPC/Epilogue/KiracHideout"] =
        [
            ("Beach Map", 5), ("Lookout Map", 3), ("Alleyways Map", 3), ("Arid Lake Map", 3), ("Glacier Map", 3),
            ("Strand Map", 3), ("Dunes Map", 3), ("Cemetery Map", 3), ("Jungle Valley Map", 3), ("Toxic Sewer Map", 3),
        ],
        ["Metadata/NPC/Act1/Bestel"] =
        [
            ("Iron Ring", 2), ("Paua Amulet", 1), ("Chain Belt", 1), ("Rustic Sash", 1), ("Rusted Sword", 1), ("Iron Hat", 1),
        ],
    };

    /// <summary>What everything costs for now: our own prices, in Scrolls of Wisdom.</summary>
    public const string Currency = "Scroll of Wisdom";
    public const int PriceInCurrency = 1;


    /// <summary>A shop tab: its name in the buy window (S→C 0x8c) and its items.</summary>
    public sealed record ShopTab(string Name, List<InventoryItem> Items);

    /// <summary>Nessa's gem tabs (2026-10-21): every shop gem (<see cref="ItemCatalog.ShopGems"/>) by colour, actives and
    /// supports apart, one of each.</summary>
    static readonly (string Name, int Colour, bool Support)[] GemTabs =
    [
        ("Red Skills", 1, false), ("Red Support", 1, true), ("Green Skills", 2, false), ("Green Support", 2, true),
        ("Blue Skills", 3, false), ("Blue Support", 3, true), ("White Skills", 4, false),
    ];

    static readonly HashSet<string> SellsGems = new(StringComparer.OrdinalIgnoreCase) { "Metadata/NPC/Act1/Nessa" };

    static readonly HashSet<string> SellsMaps = new(StringComparer.OrdinalIgnoreCase) { "Metadata/NPC/Act1/Nessa" };

    /// <summary>The NPC's tabs: its stock (<see cref="Stock"/>) in the first, then Nessa's gem tabs, each laid out from the
    /// first free cell row by row, with item ids from <paramref name="nextId"/>. Unknown or unwritable item types
    /// and items without room are skipped and named in <paramref name="notes"/>.</summary>
    public static List<ShopTab> Tabs(string npcId, ItemCatalog items, Func<uint> nextId, List<string> notes, string dataFolder = "data")
    {
        var currency = items.Find(Currency);
        if (currency is null) notes.Add($"no item type {Currency}: the stock has no prices");
        Item Priced(Item item) => currency is null ? item : item with { Price = [(currency.Hash, (uint)PriceInCurrency)] };
        var tabs = new List<ShopTab>();
        var stock = new List<(ItemCatalog.BaseType Type, int Count, Item? Template)>();
        var wanted = (Stock.GetValueOrDefault(npcId) ?? []).ToList();
        foreach (var (name, count) in wanted)
        {
            if (items.Find(name) is { Supported: true } type && type.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) stock.Add((type, count, null));
            else notes.Add($"no writable item type {name}");
        }
        if (SellsMaps.Contains(npcId))
            stock.AddRange(NessaMapStock.Build(items, dataFolder).Select(m => (m.Type, 1, (Item?)m.Item)));
        // Goods, then Goods2, Goods3 … for what does not fit.
        for (int page = 1; stock.Count > 0 || page == 1; page++)
            tabs.Add(new(page == 1 ? "Goods" : $"Goods{page}", Lay(stock, items, Priced, nextId, notes)));
        if (SellsGems.Contains(npcId))
            foreach (var (name, colour, support) in GemTabs)
            {
                var gems = items.ShopGems(colour, support).Select(t => (t, 1, (Item?)null)).ToList();
                tabs.Add(new(name, Lay(gems, items, Priced, nextId, notes)));
                notes.AddRange(gems.Select(g => $"no room in {name} for {g.t.Name}"));
            }
        return tabs;
    }

    /// <summary>Lays <paramref name="stock"/> out in one tab and takes what it placed out of the list: what is left did not
    /// fit (the caller puts it in the next tab).</summary>
    static List<InventoryItem> Lay(List<(ItemCatalog.BaseType Type, int Count, Item? Template)> stock, ItemCatalog items, Func<Item, Item> priced,
        Func<uint> nextId, List<string> notes)
    {
        var placed = new List<(InventoryItem Item, byte W, byte H)>();
        var left = new List<(ItemCatalog.BaseType Type, int Count, Item? Template)>();
        foreach (var (type, count, template) in stock)
        {
            var (sockets, links) = items.DefaultSockets(type);
            int i = 0;
            for (; i < count && FreeCell(placed, type.Width, type.Height) is { } cell; i++)
                placed.Add((new InventoryItem(nextId(), cell.X, cell.Y, priced(template ?? ItemCatalog.Create(type, sockets, links))), type.Width, type.Height));
            if (i < count) left.Add((type, count - i, template));
        }
        // An item bigger than a whole tab would never fit: drop it rather than loop.
        if (placed.Count == 0 && left.Count > 0) { notes.AddRange(left.Select(l => $"no room for {l.Type.Name}")); left.Clear(); }
        stock.Clear();
        stock.AddRange(left);
        return [.. placed.Select(p => p.Item)];
    }

    static (byte X, byte Y)? FreeCell(List<(InventoryItem Item, byte W, byte H)> placed, byte width, byte height)
    {
        // Row by row: the first row fills left to right, then the next.
        for (int y = 0; y + height <= Tab.Height; y++)
            for (int x = 0; x + width <= Tab.Width; x++)
                if (!placed.Any(p => p.Item.X < x + width && x < p.Item.X + p.W && p.Item.Y < y + height && y < p.Item.Y + p.H))
                    return ((byte)x, (byte)y);
        return null;
    }
}
