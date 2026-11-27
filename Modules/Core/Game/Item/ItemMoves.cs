using GameServer.Core.Protocol.Instance;


/// <summary>
/// Item changes that touch more than one save (the character's items and the account's stash) or one save more than
/// once (a currency item used up and the item it changed). Each runs in one transaction (<see cref="Saves"/>): it is
/// saved whole or not at all, so an item is never lost between its two places, nor counted in both. Checks come before
/// the first change; the handlers in Program.cs send the client what changed afterwards.
/// </summary>
public static class ItemMoves
{
    const byte Cursor = PlayerInventories.Cursor, Main = PlayerInventories.MainInventory;

    /// <summary>An item out of a stash tab onto the empty cursor (C→S 0x28), keeping its id. Null (nothing changes) when
    /// something is held or the item is not in the tab.</summary>
    public static InventoryItem? StashToCursor(AccountStash stash, PlayerInventories items, int tab, uint itemId) => Saves.Atomically(() =>
    {
        if (items.Items(Cursor).Count > 0 || stash.Take(tab, itemId) is not { } taken) return null;
        stash.SetLastTab(tab);   // the player works in this tab: it is on screen
        return items.Put(Cursor, taken with { X = 0, Y = 0 });
    });

    /// <summary>What a Ctrl + click moved between an inventory row and a stash tab: the inventory side (the row, the item id
    /// that left or changed, and what is left of it there: null when all of it moved) and the tab side (stacks that grew,
    /// items that arrived; or the item id that left the tab and what is left of it there).</summary>
    public sealed record Transfer(byte Row, uint InventoryItemId, InventoryItem? LeftInInventory, int Tab,
        IReadOnlyList<InventoryItem> TabChanged, IReadOnlyList<InventoryItem> TabAdded,
        IReadOnlyList<InventoryItem> InventoryChanged, IReadOnlyList<InventoryItem> InventoryAdded, uint TabItemId = 0, InventoryItem? LeftInTab = null);

    /// <summary>
    /// An item out of a stash tab into the main inventory (Ctrl + click, C→S 0xe3): a stack onto the inventory's stacks of
    /// its type first, then into free cells, at most one normal inventory stack per click (all: Ctrl + right click,
    /// as much of the clicked stack as fits). What doesn't move stays in the tab; any other item into the first free
    /// cell, keeping its id. Null (nothing changes) when it is not there, its type is unknown or nothing fits.
    /// </summary>
    public static Transfer? StashToInventory(AccountStash stash, PlayerInventories items, int tab, uint itemId,
        Func<Item, PlayerInventories.Shape?> shapeOf, bool all = false) => Saves.Atomically(() =>
    {
        if (tab < 0 || tab >= stash.Count || stash.Inventory(tab).Items.FirstOrDefault(i => i.Id == itemId) is not { } inTab
            || shapeOf(inTab.Item) is not { } shape) return null;
        if (shape.MaxStack > 1 || inTab.Item.StackSize > 1)
        {
            ushort requested = all ? inTab.Item.StackSize : Math.Min(inTab.Item.StackSize, shape.MaxStack);
            var stacked = items.AddStacked(Main, inTab.Item with { StackSize = requested }, shapeOf);
            int moved = requested - stacked.Left;
            if (moved == 0) return null;
            var leftInTab = moved == inTab.Item.StackSize ? null : stash.Shrink(tab, itemId, moved);
            if (leftInTab is null) stash.Take(tab, itemId);
            stash.SetLastTab(tab);
            return new Transfer(Main, 0, null, tab, [], [], stacked.Changed, stacked.Added, itemId, leftInTab);
        }
        if (items.FreeCell(Main, shape.Width, shape.Height, shapeOf) is not { } cell || stash.Take(tab, itemId) is not { } taken) return null;
        stash.SetLastTab(tab);
        var placed = items.Put(Main, taken with { X = cell.X, Y = cell.Y });
        return new Transfer(Main, 0, null, tab, [], [], [], [placed], itemId);
    });

    /// <summary>
    /// One oil from a blight tab stack into the first free input cell beside the anoint cell (Ctrl + click, C→S 0xf5, while
    /// the anoint cell holds an item: <see cref="AccountStash.AnointInputFor"/>), with a new id.
    /// Null (nothing changes) when the inputs don't apply; <paramref name="full"/> when they apply but are all taken.
    /// </summary>
    public static Transfer? StashToAnointInput(AccountStash stash, int tab, uint itemId,
        Func<Item, PlayerInventories.Shape?> shapeOf, out bool full)
    {
        bool isFull = false;
        var result = Saves.Atomically(() =>
        {
            if (stash.MoveToAnointInput(tab, itemId, shapeOf, out isFull) is not var (placed, left)) return null;
            stash.SetLastTab(tab);
            return new Transfer(Main, 0, null, tab, [], [placed], [], [], itemId, left);
        });
        full = isFull;
        return result;
    }

    /// <summary>
    /// An item out of whichever inventory holds it into a stash tab (Ctrl + click: C→S 0xe0 into the shown tab, 0xe1 into
    /// the tab of its affinity): a stack onto the tab's stacks of its type first, then into free cells (a slotted tab: its
    /// own slot), and what doesn't fit stays in the inventory; any other item into the first free cell (or its slot) with a
    /// stash id. Null with the reason (nothing changes): the tab doesn't hold it, or it is full.
    /// </summary>
    public static Transfer? InventoryToStash(AccountStash stash, PlayerInventories items, int tab,
        uint itemId, Func<Item, PlayerInventories.Shape?> shapeOf, out string why)
    {
        string reason = "";
        var result = Saves.Atomically(() =>
        {
            if (tab < 0 || tab >= stash.Count) { reason = "no such tab"; return null; }
            if (items.RowOf(itemId) is not byte from || items.Items(from).FirstOrDefault(i => i.Id == itemId) is not { } item)
            { reason = "the item is not in the inventories"; return null; }
            if (shapeOf(item.Item) is not { } shape) { reason = "unknown item type"; return null; }
            tab = stash.UniqueDestination(tab, item.Item);
            if (tab < 0) { reason = "the item has no collection slot"; return null; }
            // The client drops a map on the map tab itself, and the map tab holds nothing directly: its page does (#477).
            tab = stash.MapDestination(tab, item.Item, shapeOf);
            if (!stash.Holds(tab, item.Item)) { reason = $"the {stash.TypeOf(tab)} tab doesn't hold that item"; return null; }
            if (shape.MaxStack > 1 || item.Item.Components.Contains("Stack"))
            {
                if (stash.AddStacked(tab, item.Item, shapeOf, out reason) is not { } stacked) return null;
                if (stacked.Left == item.Item.StackSize) { reason = "the tab is full"; return null; }
                var left = stacked.Left == 0 ? null : items.SetStackSize(from, itemId, stacked.Left);
                if (stacked.Left == 0) items.Remove(from, itemId);
                stash.SetLastTab(tab);
                return new Transfer(from, itemId, left, tab, stacked.Changed, stacked.Added, [], []);
            }
            if (stash.CellFor(tab, item.Item, shapeOf) is not { } cell) { reason = "the tab is full"; return null; }
            var removed = items.Remove(from, itemId)!;
            if (stash.Place(tab, cell.X, cell.Y, removed, shapeOf, out reason) is not var (placed, _))
                throw new InvalidOperationException($"stash tab {tab} refused item {itemId} at its free cell: {reason}");   // CellFor said it fits
            stash.SetLastTab(tab);
            return new Transfer(from, itemId, null, tab, [], [placed], [], []);
        });
        why = result is null && reason == "" ? "refused" : reason;
        return result;
    }

    /// <summary>
    /// Lists an item from <paramref name="sourceRow"/> into a shown merchant <paramref name="tab"/> with
    /// <paramref name="note"/> in one transaction (C→S 0x01a2): the source inventory item is never mutated on refusal,
    /// and any unmoved stack remainder keeps its original note.
    /// </summary>
    public static Transfer? ListInMerchantTab(AccountStash stash, PlayerInventories items, int tab, byte sourceRow,
        uint itemId, string? note, Func<Item, PlayerInventories.Shape?> shapeOf, out string why)
    {
        string? normalized = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        if (normalized is { Length: > 250 }) normalized = normalized[..250];
        string reason = "";
        var result = Saves.Atomically(() =>
        {
            if (!stash.IsShownTab(tab) || stash.TabType(tab) != StashRules.Trade)
            { reason = "no merchant tab"; return null; }
            if (items.RowOf(itemId) != sourceRow || items.Items(sourceRow).FirstOrDefault(i => i.Id == itemId) is not { } item)
            { reason = "not in the inventory"; return null; }
            if (shapeOf(item.Item) is not { } shape) { reason = "unknown item type"; return null; }
            if (!stash.Holds(tab, item.Item)) { reason = $"the {stash.TypeOf(tab)} tab doesn't hold that item"; return null; }
            var listed = item.Item with { Note = normalized };
            if (shape.MaxStack > 1 || listed.Components.Contains("Stack"))
            {
                if (stash.AddStacked(tab, listed, shapeOf, out reason) is not { } stacked) return null;
                if (stacked.Left == listed.StackSize) { reason = "the tab is full"; return null; }
                var left = stacked.Left == 0 ? null : items.SetStackSize(sourceRow, itemId, stacked.Left);
                if (stacked.Left == 0) items.Remove(sourceRow, itemId);
                stash.SetLastTab(tab);
                return new Transfer(sourceRow, itemId, left, tab, stacked.Changed, stacked.Added, [], []);
            }
            if (stash.CellFor(tab, listed, shapeOf) is not { } cell) { reason = "the tab is full"; return null; }
            var removed = items.Remove(sourceRow, itemId)!;
            if (stash.Place(tab, cell.X, cell.Y, removed with { Item = listed }, shapeOf, out reason) is not var (placed, _))
                throw new InvalidOperationException($"stash tab {tab} refused item {itemId} at its free cell: {reason}");
            stash.SetLastTab(tab);
            return new Transfer(sourceRow, itemId, null, tab, [], [placed], [], []);
        });
        why = result is null && reason == "" ? "refused" : reason;
        return result;
    }

    /// <summary>
    /// Quick-places the cursor item into a specialty/Unique/Map stash tab (C→S 0x00e2): routes Unique and Map tabs to
    /// their destination child page, merges stackable items onto existing stacks first, or places non-stackables in the
    /// first matching free cell. Null with reason when refused (nothing changes).
    /// </summary>
    public static Transfer? HeldQuickToStash(AccountStash stash, PlayerInventories items, int tab, uint itemId,
        Func<Item, PlayerInventories.Shape?> shapeOf, out string why)
    {
        string reason = "";
        var result = Saves.Atomically(() =>
        {
            if (tab < 0 || tab >= stash.Count) { reason = "no such tab"; return null; }
            if (items.Items(Cursor) is not [var held, ..]) { reason = "nothing held"; return null; }
            if (itemId != 0 && held.Id != itemId) { reason = "cursor item mismatch"; return null; }
            if (shapeOf(held.Item) is not { } shape) { reason = "unknown item type"; return null; }
            int destination = stash.UniqueDestination(tab, held.Item);
            if (destination < 0) { reason = "the item has no collection slot"; return null; }
            destination = stash.MapDestination(destination, held.Item, shapeOf);
            if (!stash.Holds(destination, held.Item)) { reason = $"the {stash.TypeOf(destination)} tab doesn't hold that item"; return null; }
            if (shape.MaxStack > 1 || held.Item.Components.Contains("Stack"))
            {
                if (stash.AddStacked(destination, held.Item, shapeOf, out reason) is not { } stacked) return null;
                if (stacked.Left == held.Item.StackSize) { reason = "the tab is full"; return null; }
                var left = stacked.Left == 0 ? null : items.SetStackSize(Cursor, held.Id, stacked.Left);
                if (stacked.Left == 0) items.Remove(Cursor, held.Id);
                stash.SetLastTab(destination);
                return new Transfer(Cursor, held.Id, left, destination, stacked.Changed, stacked.Added, [], []);
            }
            if (stash.CellFor(destination, held.Item, shapeOf) is not { } cell) { reason = "the tab is full"; return null; }
            var removed = items.Remove(Cursor, held.Id)!;
            if (stash.Place(destination, cell.X, cell.Y, removed, shapeOf, out reason) is not var (placed, _))
                throw new InvalidOperationException($"stash tab {destination} refused cursor item {held.Id} at its free cell: {reason}");
            stash.SetLastTab(destination);
            return new Transfer(Cursor, held.Id, null, destination, [], [placed], [], []);
        });
        why = result is null && reason == "" ? "refused" : reason;
        return result;
    }

    /// <summary>Quick-moves a Heist equipment item between carried inventory (main/expanded) and one of the nine Heist NPC
    /// equipment rows (rows 45..53, kind 0x22; C→S 0x00fd and controller C→S 0x002d).</summary>
    public static BenchMove? MoveHeistNpcEquipment(PlayerInventories items, byte slot, uint itemId, bool intoTarget,
        bool fromExpanded, Func<Item, PlayerInventories.Shape?> shapeOf) => Saves.Atomically<BenchMove?>(() =>
    {
        if (slot >= PlayerInventories.HeistNpcEquipmentCount) return null;
        byte targetRow = (byte)(PlayerInventories.HeistNpcEquipmentFirst + slot);
        byte from;
        if (intoTarget)
        {
            byte preferred = fromExpanded ? PlayerInventories.ExpandedInventory : Main;
            if (items.Items(preferred).Any(i => i.Id == itemId)) from = preferred;
            else if (items.RowOf(itemId) is byte src && src is Main or PlayerInventories.ExpandedInventory) from = src;
            else return null;
        }
        else from = targetRow;
        byte to = intoTarget ? targetRow : Main;
        if (items.Items(from).FirstOrDefault(i => i.Id == itemId) is not { } item || shapeOf(item.Item) is not { } shape) return null;
        if (intoTarget)
        {
            byte? slotX = shape.Class switch
            {
                "HeistEquipmentTool" => 0,
                "HeistEquipmentWeapon" => 2,
                "HeistEquipmentUtility" => 4,
                "HeistEquipmentReward" => 6,
                _ => null,
            };
            if (slotX is not byte x) return null;
            bool occupied = items.Items(to).Any(i => shapeOf(i.Item) is { } s && i.X < x + shape.Width && x < i.X + s.Width && i.Y < shape.Height);
            if (occupied || items.Remove(from, itemId) is not { } taken) return null;
            return new(from, to, items.Put(to, taken with { X = x, Y = 0 }));
        }
        if (items.FreeCell(to, shape.Width, shape.Height, shapeOf) is not { } cell
            || items.Remove(from, itemId) is not { } outItem) return null;
        return new(from, to, items.Put(to, outItem with { X = cell.X, Y = cell.Y }));
    });

    /// <summary>A price paid: what it took from the main inventory's stacks and from the stash's (either may be null).</summary>
    public sealed record Price(PlayerInventories.Payment? Inventory, AccountStash.Payment? Stash);

    public sealed record VendorPurchase(IReadOnlyList<Price> Payments, IReadOnlyList<InventoryItem> Bought,
        PlayerInventories.Placement? Equipped = null, PlayerInventories.Placement? Placed = null);

    public sealed record VendorSocketPurchase(IReadOnlyList<Price> Payments, InventoryItem Host, InventoryItem? Displaced);

    public static VendorPurchase? PurchaseVendor(PlayerInventories items, AccountStash? stash, AccountStash gold,
        Item offer, ItemCatalog catalog, Func<Item, PlayerInventories.Shape?> shapeOf, Func<bool> consume,
        byte? destination = null, byte x = 0, byte y = 0, bool expandedAllowed = false) => Saves.Atomically<VendorPurchase?>(() =>
    {
        if (offer.Price.Count == 0 || offer.Price.Any(p => p.Amount == 0)) return null;
        byte row = destination ?? Main;
        bool equipment = row is >= 1 and <= 11 or 14 or 15 or 102;
        bool expanded = row == PlayerInventories.ExpandedInventory;
        if (row != Main && row != Cursor && !equipment && !(expanded && expandedAllowed)) return null;
        var bought = offer with { Price = [] };
        if (destination is null)
        {
            if (!items.Fits(Main, bought, shapeOf)) return null;
        }
        else if (row == Cursor)
        {
            if (items.Items(Cursor).Count != 0) return null;
        }
        else
        {
            if (items.PreviewPurchase(row, x, y, bought, shapeOf, expandedAllowed, allowDisplace: equipment) is null) return null;
        }
        var costs = offer.Price.GroupBy(p => p.TypeHash).Select(g => (Currency:g.Key, Amount:g.Sum(p => (long)p.Amount))).ToArray();
        foreach (var (currency, amount) in costs)
        {
            if (amount <= 0 || amount > int.MaxValue || catalog.ByHash(currency) is not { } type) return null;
            if (type.Class == ItemDrops.GoldClass ? gold.Gold < amount
                : (long)items.Count(Main,currency) + (stash?.CountOf(currency) ?? 0) < amount) return null;
        }
        if (!consume()) return null;
        var payments = new List<Price>();
        foreach (var (currency, amount) in costs)
            if (catalog.ByHash(currency)!.Class == ItemDrops.GoldClass)
            {
                if (gold.TrySpendGold(amount) is null) throw new InvalidOperationException("Vendor gold changed during purchase");
            }
            else payments.Add(Pay(items,stash,currency,(int)amount)
                ?? throw new InvalidOperationException("Vendor payment changed during purchase"));
        if (destination is null)
        {
            var into = items.AddStacked(Main,bought,shapeOf);
            if (into.Left != 0) throw new InvalidOperationException("Vendor item no longer fits after payment");
            return new(payments,[.. into.Changed, .. into.Added]);
        }
        if (row == Cursor) return new(payments, [items.Add(Cursor, 0, 0, bought)]);
        var placed = items.PlaceIncoming(row, x, y, bought, shapeOf, out var why, expandedAllowed, allowDisplace: equipment)
            ?? throw new InvalidOperationException($"Vendor placement changed during purchase: {why}");
        return new(payments, [placed.Placed], equipment ? placed : null, placed);
    });

    /// <summary>Buys a vendor item and sockets it directly into <paramref name="targetItemId"/>'s <paramref name="socket"/>
    /// (<c>C→S 0x0089</c>) in one transaction, moving any displaced gem onto the empty cursor.</summary>
    public static VendorSocketPurchase? PurchaseAndSocketVendor(PlayerInventories items, AccountStash? stash, AccountStash gold,
        Item offer, ItemCatalog catalog, byte targetRow, uint targetItemId, int socket,
        Func<Item, PlayerInventories.Shape?> shapeOf, Func<bool> consume, out string why)
    {
        string reason = "";
        var result = Saves.Atomically<VendorSocketPurchase?>(() =>
        {
            if (offer.Price.Count == 0 || offer.Price.Any(p => p.Amount == 0)) { reason = "invalid price"; return null; }
            var bought = offer with { Price = [] };
            if (items.PreviewSocketItem(targetRow, targetItemId, socket, bought, shapeOf, out reason) is null) return null;
            var costs = offer.Price.GroupBy(p => p.TypeHash).Select(g => (Currency: g.Key, Amount: g.Sum(p => (long)p.Amount))).ToArray();
            foreach (var (currency, amount) in costs)
            {
                if (amount <= 0 || amount > int.MaxValue || catalog.ByHash(currency) is not { } type) { reason = "invalid price"; return null; }
                if (type.Class == ItemDrops.GoldClass ? gold.Gold < amount
                    : (long)items.Count(Main, currency) + (stash?.CountOf(currency) ?? 0) < amount) { reason = "insufficient currency"; return null; }
            }
            if (!consume()) { reason = "out of stock"; return null; }
            var payments = new List<Price>();
            foreach (var (currency, amount) in costs)
                if (catalog.ByHash(currency)!.Class == ItemDrops.GoldClass)
                {
                    if (gold.TrySpendGold(amount) is null) throw new InvalidOperationException("Vendor gold changed during purchase");
                }
                else payments.Add(Pay(items, stash, currency, (int)amount)
                    ?? throw new InvalidOperationException("Vendor payment changed during purchase"));
            if (items.SocketItem(targetRow, targetItemId, socket, bought, shapeOf, items.NewItemId, out reason) is not var (host, displaced))
                throw new InvalidOperationException($"Vendor socket placement changed during purchase: {reason}");
            return new(payments, host, displaced);
        });
        why = reason;
        return result;
    }

    public sealed record BenchResult(InventoryItem Changed, IReadOnlyList<Price> Payments);

    /// <summary>Where a bench Ctrl + click (C→S 0xe9) moved an item: out of <see cref="From"/>, now <see cref="Placed"/> in
    /// <see cref="To"/>, keeping its id.</summary>
    public sealed record BenchMove(byte From, byte To, InventoryItem Placed);

    /// <summary>
    /// Ctrl + click with the crafting bench open: an item of the main inventory onto the empty bench slot, or the bench's
    /// item into the main inventory's first free cell. Null (nothing changes) when the item is not there, the slot is taken
    /// or the inventory has no room.
    /// </summary>
    public static BenchMove? MoveToBench(PlayerInventories items, uint itemId, bool intoBench,
        Func<Item, PlayerInventories.Shape?> shapeOf) => Saves.Atomically<BenchMove?>(() =>
    {
        byte from = intoBench ? Main : PlayerInventories.CraftingBench, to = intoBench ? PlayerInventories.CraftingBench : Main;
        if (items.Items(from).FirstOrDefault(i => i.Id == itemId) is not { } item || shapeOf(item.Item) is not { } shape) return null;
        var cell = intoBench ? (items.Items(to).Count == 0 ? ((byte)0, (byte)0) : null)
            : items.FreeCell(to, shape.Width, shape.Height, shapeOf);
        if (cell is null || items.Remove(from, itemId) is not { } taken) return null;
        return new(from, to, items.Put(to, taken with { X = cell.Value.X, Y = cell.Value.Y }));
    });

    /// <summary>Ctrl + click between main/expanded inventory and a single-item crafting slot (e.g. HarvestCraftingItem).</summary>
    public static BenchMove? MoveToSlot(PlayerInventories items, byte slotRow, uint itemId, bool intoSlot,
        Func<Item, PlayerInventories.Shape?> shapeOf) => Saves.Atomically<BenchMove?>(() =>
    {
        byte from;
        if (intoSlot)
        {
            if (items.RowOf(itemId) is not byte src
                || src is not (PlayerInventories.MainInventory or PlayerInventories.ExpandedInventory)) return null;
            from = src;
        }
        else from = slotRow;
        byte to = intoSlot ? slotRow : Main;
        if (items.Items(from).FirstOrDefault(i => i.Id == itemId) is not { } item || shapeOf(item.Item) is not { } shape) return null;
        if (intoSlot && (item.Item.Components.Contains("Quest") || shape.Class == "QuestItem")) return null;
        var cell = intoSlot ? (items.Items(to).Count == 0 ? ((byte)0, (byte)0) : null)
            : items.FreeCell(to, shape.Width, shape.Height, shapeOf);
        if (cell is null || items.Remove(from, itemId) is not { } taken) return null;
        return new(from, to, items.Put(to, taken with { X = cell.Value.X, Y = cell.Value.Y }));
    });

    /// <summary>Moves one item from a stash tab into an empty single-item crafting slot (e.g. HarvestCraftingItem).
    /// Rejects oversized stacks (e.g. 5,000/50,000 currency tab stacks) and quest items.</summary>
    public static InventoryItem? StashToSlot(AccountStash stash, PlayerInventories items, int tab, byte slotRow, uint itemId,
        Func<Item, PlayerInventories.Shape?> shapeOf) =>
        Saves.Atomically<InventoryItem?>(() =>
    {
        if (tab < 0 || tab >= stash.Count || items.Items(slotRow).Count > 0) return null;
        if (stash.Inventory(tab).Items.FirstOrDefault(i => i.Id == itemId) is not { } inTab
            || shapeOf(inTab.Item) is not { } shape) return null;
        if (inTab.Item.StackSize > Math.Max((ushort)1, shape.MaxStack)
            || inTab.Item.Components.Contains("Quest") || shape.Class == "QuestItem") return null;
        if (stash.Take(tab, itemId) is not { } taken) return null;
        stash.SetLastTab(tab);
        return items.Put(slotRow, taken with { X = 0, Y = 0 });
    });

    /// <summary>
    /// Ctrl + click with the map device open (C→S 0xe8): a map/fragment from the main inventory into Map1's first free
    /// cell, or a device item back into the inventory. Null when the item is missing, the class is refused, or there is
    /// no room. The item's current inventory wins if the packet's direction flag disagrees.
    /// </summary>
    public static BenchMove? MoveToMapDevice(PlayerInventories items, uint itemId,
        Func<Item, PlayerInventories.Shape?> shapeOf, Player? player = null) => Saves.Atomically<BenchMove?>(() =>
    {
        byte main = Main, device = PlayerInventories.MapDevice;
        byte from;
        InventoryItem item;
        if (items.Items(main).FirstOrDefault(i => i.Id == itemId) is { } inMain) { from = main; item = inMain; }
        else if (items.Items(device).FirstOrDefault(i => i.Id == itemId) is { } inDevice) { from = device; item = inDevice; }
        else return null;
        byte to = from == main ? device : main;
        if (shapeOf(item.Item) is not { } shape) return null;
        if (to == device && !MapDevice.AcceptsItem(item.Item.TypeHash, shape.Class, player)) return null;
        if (items.FreeCell(to, shape.Width, shape.Height, shapeOf) is not { } cell
            || items.Remove(from, itemId) is not { } taken) return null;
        return new(from, to, items.Put(to, taken with { X = cell.X, Y = cell.Y }));
    });

    /// <summary>
    /// C→S 0xf6: a Voidstone between the main inventory (or the cursor) and AtlasUpgrades1 (row 39, 4×1).
    /// Into the Atlas it sits at <paramref name="slot"/>; back out it takes the first free inventory cell.
    /// Null when the item is missing, the slot is taken, or there is no room.
    /// </summary>
    public static BenchMove? MoveToAtlasUpgrade(PlayerInventories items, uint itemId, byte slot,
        Func<Item, PlayerInventories.Shape?> shapeOf) => Saves.Atomically<BenchMove?>(() =>
    {
        byte main = Main, atlas = PlayerInventories.AtlasUpgrades, cursor = Cursor;
        byte from;
        InventoryItem item;
        if (items.Items(main).FirstOrDefault(i => i.Id == itemId) is { } inMain) { from = main; item = inMain; }
        else if (items.Items(atlas).FirstOrDefault(i => i.Id == itemId) is { } inAtlas) { from = atlas; item = inAtlas; }
        else if (items.Items(cursor).FirstOrDefault(i => i.Id == itemId) is { } held) { from = cursor; item = held; }
        else return null;
        byte to = from == atlas ? main : atlas;
        if (shapeOf(item.Item) is not { } shape) return null;
        (byte X, byte Y)? cell = to == atlas
            ? (slot > 3 || items.Items(to).Any(i => i.X == slot) ? null : ((byte)slot, (byte)0))
            : items.FreeCell(to, shape.Width, shape.Height, shapeOf);
        if (cell is null || items.Remove(from, itemId) is not { } taken) return null;
        return new(from, to, items.Put(to, taken with { X = cell.Value.X, Y = cell.Value.Y }));
    });

    /// <summary>All currencies and the crafted item commit together, including multi-currency flask crafts.</summary>
    public static BenchResult? CraftAtBench(PlayerInventories items, AccountStash stash, BenchCrafting bench,
        int recipe, Random random) => Saves.Atomically<BenchResult?>(() =>
    {
        var targets = items.Items(PlayerInventories.CraftingBench);
        if (targets.Count != 1 || bench.Apply(recipe, targets[0].Item, random) is not { } craft) return null;
        foreach (var (currency, amount) in craft.Costs)
            if (amount < 0 || (long)items.Count(Main, currency) + stash.CountOf(currency) < amount) return null;
        var payments = new List<Price>();
        foreach (var (currency, amount) in craft.Costs)
            if (amount > 0) payments.Add(Pay(items, stash, currency, amount)
                ?? throw new InvalidOperationException("Bench payment changed after validation"));
        var changed = items.Change(PlayerInventories.CraftingBench, targets[0].Id, _ => craft.Item)
            ?? throw new InvalidOperationException("Bench item disappeared after payment");
        return new(changed, payments);
    });

    public sealed record SlotCraft(InventoryItem Changed, IReadOnlyList<Price> Payments);

    /// <summary>The one item in a slot row and every cost commit together.</summary>
    public static SlotCraft? CraftInSlot(PlayerInventories items, AccountStash stash, byte row,
        Func<Item, CraftResult?> craftIt) => Saves.Atomically<SlotCraft?>(() =>
    {
        var targets = items.Items(row);
        if (targets.Count != 1 || craftIt(targets[0].Item) is not { } craft) return null;
        var totals = new Dictionary<uint, long>();
        foreach (var (currency, amount) in craft.Costs)
        {
            if (amount < 0) return null;
            if (amount == 0) continue;
            long sum = totals.GetValueOrDefault(currency) + amount;
            if (sum > int.MaxValue) return null;
            totals[currency] = sum;
        }
        foreach (var (currency, amount) in totals)
            if ((long)items.Count(Main, currency) + stash.CountOf(currency) < amount) return null;
        var payments = new List<Price>();
        foreach (var (currency, amount) in totals)
            payments.Add(Pay(items, stash, currency, (int)amount)
                ?? throw new InvalidOperationException("Craft payment changed after validation"));
        var changed = items.Change(row, targets[0].Id, _ => craft.Item)
            ?? throw new InvalidOperationException("Crafted item disappeared after payment");
        return new(changed, payments);
    });

    /// <summary>
    /// Pays <paramref name="amount"/> of a currency type: from the main inventory's stacks first, the rest from the stash's
    /// (any tab), as a vendor takes it. Null (nothing changes) when the two together hold fewer. Call it inside a
    /// transaction with what the price buys.
    /// </summary>
    static Price? Pay(PlayerInventories items, AccountStash? stash, uint currency, int amount)
    {
        int inInventory = items.Count(Main, currency), inStash = stash?.CountOf(currency) ?? 0;
        if (inInventory + inStash < amount) return null;
        int fromInventory = Math.Min(amount, inInventory);
        var paid = fromInventory > 0 ? items.Pay(Main, currency, fromInventory) : null;
        var fromStash = amount > fromInventory ? stash!.Pay(currency, amount - fromInventory) : null;
        return new Price(paid, fromStash);
    }

    /// <summary>A currency (<see cref="Pay"/>), gold and what they buy, together. Null (nothing changes) when the player holds too little.</summary>
    public static Price? PayCurrency(PlayerInventories items, AccountStash stash, uint currency, int amount, uint gold, Func<bool> commit) =>
        Saves.Atomically<Price?>(() =>
        {
            if (amount <= 0 || stash.Gold < gold || (long)items.Count(Main, currency) + stash.CountOf(currency) < amount) return null;
            var paid = Pay(items, stash, currency, amount) ?? throw new InvalidOperationException("Currency payment changed after validation");
            if (gold > 0 && stash.TrySpendGold(gold) is null) throw new InvalidOperationException("Gold changed after validation");
            if (!commit()) throw new InvalidOperationException("What the payment buys could not be saved");
            return paid;
        });

    /// <summary>As much of a stackable item as fits into the main inventory, and the record of how much, together.</summary>
    public static (int Placed, IReadOnlyList<PlayerInventories.Stacking> Stacks) ReceiveCurrency(PlayerInventories items, Item item, int amount,
        Func<Item, PlayerInventories.Shape?> shapeOf, Func<int, bool> commit) => Saves.Atomically(() =>
    {
        var stacks = new List<PlayerInventories.Stacking>();
        int placed = 0;
        while (placed < amount)
        {
            int chunk = Math.Min(amount - placed, ushort.MaxValue);
            var into = items.AddStacked(Main, item with { StackSize = (ushort)chunk }, shapeOf);
            if (into.Any) stacks.Add(into);
            placed += chunk - into.Left;
            if (into.Left > 0) break;
        }
        if (placed > 0 && !commit(placed)) throw new InvalidOperationException("What was received could not be recorded");
        return (placed, (IReadOnlyList<PlayerInventories.Stacking>)stacks);
    });

    /// <summary>
    /// Ctrl + click on a shop item (C→S 0x8a): the price (or nothing, when free) paid (<see cref="Pay"/>: the inventory, then
    /// the stash) and the item into the main inventory (a stack onto stacks of its type, then free cells), together. Null
    /// with the reason (nothing changes) when there is no room or the price can't be paid.
    /// </summary>
    public static (Price? Paid, PlayerInventories.Stacking Into)? BuyToInventory(PlayerInventories items, AccountStash? stash, Item item,
        (uint Currency, int Amount)? price, Func<Item, PlayerInventories.Shape?> shapeOf, out string why)
    {
        // Checked before anything is paid: the room as the inventory is now (paying first could free a cell, never take one).
        if (!items.Fits(Main, item, shapeOf)) { why = "no room in the inventory"; return null; }
        string reason = "";
        var result = Saves.Atomically<(Price?, PlayerInventories.Stacking)?>(() =>
        {
            Price? paid = null;
            if (price is var (currency, amount) && (paid = Pay(items, stash, currency, amount)) is null) { reason = "not enough to pay"; return null; }
            var into = items.AddStacked(Main, item, shapeOf);
            if (into.Left > 0) throw new InvalidOperationException("a bought item that fit before the payment didn't after it");
            return (paid, into);
        });
        why = reason;
        return result;
    }

    /// <summary>What <see cref="HeldToStash"/> did. <see cref="Merged"/>: the held stack went onto <see cref="InTab"/>
    /// (same id, larger). <see cref="One"/>: one of the held stack went into an empty cell as <see cref="InTab"/>.
    /// Otherwise the held item is <see cref="InTab"/> and <see cref="Displaced"/> (the one it was dropped on) is held now.
    /// <see cref="Held"/> is what the cursor holds after (null: nothing); <see cref="StashPlacement.Tab"/> is the
    /// destination tab, including a map's hidden child page.</summary>
    public sealed record StashPlacement(InventoryItem InTab, InventoryItem HeldBefore, InventoryItem? Held, InventoryItem? Displaced,
        bool Merged, bool One, int Tab);

    /// <summary>
    /// The held item into a stash tab at (x, y) (C→S 0x2a): onto a stack of its type with room (with <paramref name="one"/>,
    /// one of it), else with <paramref name="one"/> one of the held stack into an empty cell, else the whole item, swapping
    /// the one item it was dropped on onto the cursor. Null with the reason (nothing changes).
    /// </summary>
    public static StashPlacement? HeldToStash(AccountStash stash, PlayerInventories items, int tab, int x, int y, bool one,
        Func<Item, PlayerInventories.Shape?> shapeOf, out string why)
    {
        string reason = "";
        var result = Saves.Atomically(() =>
        {
            if (items.Items(Cursor) is not [var held, ..]) { reason = "nothing held"; return null; }
            // A map held over the map tab goes to its page, the same routing the inventory path uses (#382/#605).
            int destination = stash.UniqueDestination(tab, held.Item);
            if (destination < 0) { reason = "the item has no collection slot"; return null; }
            destination = stash.MapDestination(destination, held.Item, shapeOf);
            if (destination != tab)
            {
                if (stash.CellFor(destination, held.Item, shapeOf) is not { } cell)
                { reason = "the tab is full"; return null; }
                (x, y) = (cell.X, cell.Y);
            }
            tab = destination;
            one &= held.Item.StackSize > 1;
            if (stash.Merge(tab, x, y, held, shapeOf, one ? 1 : int.MaxValue) is var (stack, moved))
                return new StashPlacement(stack, held, items.SetStackSize(Cursor, held.Id, held.Item.StackSize - moved), null, Merged: true, One: one, Tab: tab);
            if (one)
            {
                // Into an empty place only (a slotted tab: its slot), so nothing is swapped out.
                if (!stash.IsFree(tab, x, y, held.Item, shapeOf)) { reason = "something in the way, or the tab doesn't hold it"; return null; }
                var single = new InventoryItem(items.NewItemId(), 0, 0, held.Item with { StackSize = 1 });
                if (stash.Place(tab, x, y, single, shapeOf, out reason) is not (var put, null)) throw new InvalidOperationException($"stash tab {tab}: one item into a free place refused: {reason}");
                return new StashPlacement(put, held, items.SetStackSize(Cursor, held.Id, held.Item.StackSize - 1), null, Merged: false, One: true, Tab: tab);
            }
            if (stash.Place(tab, x, y, held, shapeOf, out reason) is not var (placed, displaced)) return null;
            stash.SetLastTab(tab);
            items.Remove(Cursor, held.Id);
            if (displaced is not null) items.Put(Cursor, displaced);
            return new StashPlacement(placed, held, displaced, displaced, Merged: false, One: false, Tab: tab);
        });
        why = reason;
        return result;
    }

    /// <summary>Splits <paramref name="amount"/> off a stash tab's stack onto the empty cursor (C→S 0x7c), as a new
    /// character item, at most the item's max stack (a currency slot holds more). Returns the tab's stack as it is now and
    /// the held part, or null (nothing changes).</summary>
    public static (InventoryItem? Left, InventoryItem Part)? SplitFromStash(AccountStash stash, PlayerInventories items, int tab, uint itemId, int amount,
        Func<Item, PlayerInventories.Shape?> shapeOf) =>
        Saves.Atomically<(InventoryItem?, InventoryItem)?>(() =>
        {
            if (items.Items(Cursor).Count > 0 || tab < 0 || tab >= stash.Count || amount < 1
                || stash.Inventory(tab).Items.FirstOrDefault(i => i.Id == itemId) is not { } source
                || shapeOf(source.Item) is not { } shape) return null;
            amount = Math.Min(amount, Math.Max((int)shape.MaxStack, 1));
            // The whole stack: a plain click on a special tab's item sends it this way (a currency slot, live 2026-10-20). It
            // goes onto the cursor as it is, keeping its id; a bigger slot gives a full stack and keeps the rest.
            if (amount >= source.Item.StackSize)
                return stash.Take(tab, itemId) is { } taken ? (null, items.Put(Cursor, taken with { X = 0, Y = 0 })) : null;
            if (stash.Shrink(tab, itemId, amount) is not { } left) return null;
            var part = items.Put(Cursor, PlayerInventories.Completed(new InventoryItem(items.NewItemId(), 0, 0, source.Item with { StackSize = (ushort)amount }), shape));
            return (left, part);
        });

    /// <summary>Where an item is for <see cref="Use"/>: a stash tab (<paramref name="Tab"/>) or a character
    /// inventory row (<paramref name="Row"/>), and its id.</summary>
    public readonly record struct Spot(int? Tab, byte Row, uint Id);

    /// <summary>A currency item used on an item (C→S 0x71 / Shift 0x72), each in a character inventory or a stash tab (the
    /// currency tab's crafting slot included, live 2026-10-23): one leaves the currency's stack (the last one removes it)
    /// and, together, the target becomes <paramref name="change"/> and/or <paramref name="held"/> goes onto the empty cursor
    /// as a new character item (a Mirror of Kalandra's copy, the target unchanged). Returns the currency's stack as it is now
    /// (null when it is gone), the changed target and the held item, or null (nothing changes) when either item is gone or
    /// something is held while <paramref name="held"/> is set.</summary>
    public static (InventoryItem? Left, InventoryItem? Changed, InventoryItem? Held)? Use(PlayerInventories items, AccountStash stash,
        Spot used, Spot target, Item? change, Item? held) =>
        Saves.Atomically<(InventoryItem?, InventoryItem?, InventoryItem?)?>(() =>
        {
            if (used == target) return null;
            bool there(Spot s) => s.Tab is int t ? t >= 0 && t < stash.Count && stash.Inventory(t).Items.Any(i => i.Id == s.Id)
                : items.Items(s.Row).Any(i => i.Id == s.Id);
            if (change is null && held is null || !there(used) || !there(target)) return null;
            if (held is not null && items.Items(Cursor).Count > 0) return null;
            if (!TakeOne(items, stash, used, out var left)) return null;
            InventoryItem? changed = null;
            if (change is not null)
            {
                changed = target.Tab is int targetTab ? stash.Change(targetTab, target.Id, _ => change) : items.Change(target.Row, target.Id, _ => change);
                if (changed is null) return null;
            }
            return (left, changed, held is null ? null : items.Add(Cursor, 0, 0, held));
        });

    // One currency item leaves its stack, in a stash tab or a character inventory row (the last one removes the stack).
    // False when the stack is gone or offered in a trade; nothing changes then.
    static bool TakeOne(PlayerInventories items, AccountStash stash, Spot used, out InventoryItem? left)
    {
        left = null;
        if (used.Tab is int tab)
        {
            if (tab < 0 || tab >= stash.Count || stash.Inventory(tab).Items.FirstOrDefault(i => i.Id == used.Id) is not { } stack) return false;
            if (stack.Item.StackSize <= 1) return stash.Take(tab, used.Id) is not null;
            return (left = stash.Shrink(tab, used.Id, 1)) is not null;
        }
        if (items.ConsumeOne(used.Row, used.Id) is not { } consumed) return false;
        left = consumed.Left;
        return true;
    }

    /// <summary>
    /// A purchase (C→S 0x88): the price (<paramref name="price"/>: currency type and amount, or null when free) paid from the
    /// main inventory and then the stash, and the item goes to <paramref name="row"/> at its cell, together. Null when the
    /// price can't be paid (nothing changes).
    /// </summary>
    public static (Price? Paid, InventoryItem Bought)? Buy(PlayerInventories items, AccountStash? stash, byte row, InventoryItem item,
        (uint Currency, int Amount)? price) =>
        Saves.Atomically<(Price?, InventoryItem)?>(() =>
        {
            Price? paid = null;
            if (price is var (currency, amount) && (paid = Pay(items, stash, currency, amount)) is null) return null;
            return (paid, items.Put(row, item));
        });

    /// <summary>An Orb of Regret used (C→S 0x58): one leaves its stack and the character has <paramref name="refundPoints"/>
    /// refund points, together. Null when the item is gone (nothing changes).</summary>
    public static PlayerInventories.Consumption? UseForRefundPoint(PlayerInventories items, string character, byte row, uint itemId, ushort refundPoints) =>
        Saves.Atomically(() =>
        {
            if (items.ConsumeOne(row, itemId) is not { } taken) return null;
            CharacterSaves.SetRefundPoints(character, refundPoints);
            return taken;
        });

    /// <summary>A Book of Skill used (C→S 0x58): the book is gone and the character has <paramref name="extraPoints"/>
    /// extra passive skill points, together. Null when the item is gone (nothing changes).</summary>
    public static PlayerInventories.Consumption? UseForPassivePoint(PlayerInventories items, string character, byte row, uint itemId, ushort extraPoints) =>
        Saves.Atomically(() =>
        {
            if (items.ConsumeOne(row, itemId) is not { } taken) return null;
            CharacterSaves.SetExtraPassivePoints(character, extraPoints);
            return taken;
        });
}
