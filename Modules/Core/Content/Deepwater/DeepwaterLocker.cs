using GameServer.Core;
using GameServer.Core.Protocol.Instance;
using GameServer.Modding;

namespace GameServer.Modules.Deepwater;

/// <summary>The deck's chart inventories (05-inventories-and-ui.md): the Chart window's slot 103, the charted charts 104
/// the Voyage window shows, the running chart 109, and the Chart Locker 112, whose tabs are regions of the one 12 × 79 grid
/// that DeepwaterLockerLayout gives (H, the locker window <c>0x140427f30</c>): <c>__col7</c>/<c>__col8</c> the first column
/// and row, <c>__col5</c> × <c>__col6</c> a page, <c>__col12</c> the pages (0 = one). They are saved with the character, as
/// the other inventory rows (G: <c>Inventories.__col6</c> = 1 suggests the real game kept 104 and 112 per account and
/// league).</summary>
internal static class DeepwaterLocker
{
    /// <summary>Inventories rows: DeepwaterUnchartedMission (1 × 1), DeepwaterChartedMissionStorage (120 × 1),
    /// DeepwaterActiveMission (1 × 1), DeepwaterLockerInventory1 (12 × 79).</summary>
    public const byte UnchartedRow = 103, ChartedRow = 104, ActiveRow = 109, LockerRow = 112;
    /// <summary>S→C 0x7b kind DeepwaterLockerWindow; its u32 goes to the window's listeners (M: the locker's object id, as for
    /// the stash and the capsule).</summary>
    public const byte LockerUi = 0x6e;
    /// <summary>The locker window's owner (ClientState.ModuleWindow).</summary>
    public const string LockerWindow = "deepwater-locker";
    public const string ChartLocker = "Metadata/MiscellaneousObjects/Deepwater/ChartLocker";
    public const string Sulphur = "Metadata/Items/Currency/CurrencyDeepwater";
    public static uint SulphurHash => InstanceEntities.ObjectTypeHash(Sulphur);

    /// <summary>A locker tab's cells for one base type.</summary>
    public readonly record struct Region(uint TypeHash, string Name, int X, int Y, int Width, int Height)
    {
        public bool Holds(int x, int y, int width, int height) => x >= X && y >= Y && x + width <= X + Width && y + height <= Y + Height;
    }

    public static readonly Lazy<Region[]> Regions = new(() =>
    {
        var bases = ClientTables.TableOrEmpty("BaseItemTypes");
        return [.. ClientTables.TableOrEmpty("DeepwaterLockerLayout")
            .Where(r => r.IntOrNull("BaseItemType") is int b && b < bases.Count && bases[b].Text("Id") is not null)
            .Select(r => new Region(InstanceEntities.ObjectTypeHash(bases[r.Int("BaseItemType")].Text("Id")!), r.Text("__col13") ?? bases[r.Int("BaseItemType")].Text("Name") ?? "",
                r.IntOrNull("__col7") ?? 0, r.IntOrNull("__col8") ?? 0, r.IntOrNull("__col5") ?? 1,
                (r.IntOrNull("__col6") ?? 1) * Math.Max(1, r.IntOrNull("__col12") ?? 0)))];
    });

    public static Region? RegionOf(uint typeHash)
    {
        foreach (var r in Regions.Value) if (r.TypeHash == typeHash) return r;
        return null;
    }

    /// <summary>A charted chart: one with an Adjacent or Voyage implicit (Mods domain 39, generation type 37).</summary>
    public static bool Charted(ItemCatalog catalog, Item item) => DeepwaterCharts.IsChart(catalog, item)
        && item.Mods.Any(m => m.Section == ModSection.Implicit && catalog.Mods.ByHash(m.Hash) is { Domain: 39, GenerationType: 37 });

    /// <summary>GameEvents.InventoryRefusal for the chart rows: 103 takes a chart (the client's own gate, <c>0x141d65f80</c>
    /// wants the DeepwaterChart component); 104 only a charted chart (G: the real server may have filled it only itself);
    /// 109 nothing (the server moves the running chart); 112 only the layout's base types.</summary>
    public static string? Refusal(ClientState conn, byte row, Item item) => row switch
    {
        UnchartedRow => DeepwaterCharts.IsChart(ItemsFor(conn), item) ? null : "only a Chart goes into the chart device",
        ChartedRow => Charted(ItemsFor(conn), item) ? null : "only a charted Chart goes into the charted inventory (G)",
        ActiveRow => "the active chart slot is the server's",
        LockerRow => RegionOf(item.TypeHash) is null ? "the Chart Locker takes only Charts and Dead Man's Sulphur" : null,
        _ => null,
    };

    /// <summary>GameEvents.InventoryPlace: a drop into the locker outside the held item's tab region is refused (true:
    /// handled, nothing changes); anything else goes on to the Game module.</summary>
    public static bool RefusedPlace(IModuleContext module, ClientState conn, InstanceItems.PlaceItem place)
    {
        if (place.InventoryId != PlayerInventories.InventoryId(LockerRow)) return false;
        if (ItemsOf(conn).Items(PlayerInventories.Cursor).FirstOrDefault() is not { } held || RegionOf(held.Item.TypeHash) is not { } region) return false;
        var shape = ShapeOf(held.Item) ?? new(1, 1, "");
        if (region.Holds((int)place.X, (int)place.Y, shape.Width, shape.Height)) return false;
        module.Log($"{PlayerOf(conn).Character} <= 0x002a place item {held.Id} in the Chart Locker at ({place.X}, {place.Y}): refused (outside the {region.Name} tab)");
        return true;
    }

    /// <summary>C→S 0x0111, Ctrl + click with the locker open: an item from the main (or expanded) inventory into its tab
    /// (Sulphur onto the tab's stacks first, then the tab's first free cell), or one out of the locker into the main
    /// inventory. The client waits for the 0xbf pair (as for 0x0112), so a refusal is only logged.</summary>
    public static async Task QuickMove(IModuleContext module, ClientState conn, InstanceItems.LeagueWindowMove move)
    {
        var player = PlayerOf(conn);
        var items = ItemsOf(conn);
        byte from = move.Into ? move.FromExpanded ? PlayerInventories.ExpandedInventory : PlayerInventories.MainInventory : LockerRow;
        string what = $"0x0111 Ctrl + click item {move.ItemId} {(move.Into ? "into" : "out of")} the Chart Locker";
        var it = items.Items(from).FirstOrDefault(i => i.Id == move.ItemId);
        string? why = it is null ? $"not in row {from}" : conn.ModuleWindow != LockerWindow ? "the locker is not open" : null;
        if (why is null && move.Into && RegionOf(it!.Item.TypeHash) is null) why = "the Chart Locker takes only Charts and Dead Man's Sulphur";
        if (why is not null)
        {
            module.Log($"{player.Character} <= {what}: refused ({why})");
            return;
        }
        bool done = move.Into ? await Store(conn, it!, from) : await Take(conn, it!);
        module.Log($"{player.Character} <= {what}: {(done ? "moved" : "refused (no room)")}");
    }

    // Into the item's tab: a stack first fills the tab's stacks of its type, the rest takes a free cell of the tab.
    static async Task<bool> Store(ClientState conn, InventoryItem it, byte from)
    {
        var items = ItemsOf(conn);
        var region = RegionOf(it.Item.TypeHash)!.Value;
        var shape = ShapeOf(it.Item) ?? new(1, 1, "");
        var changed = new List<InventoryItem>();
        InventoryItem? placed = null;
        bool emptied = false;
        InventoryItem? left = null;
        Saves.Atomically(() =>
        {
            int count = it.Item.StackSize;
            if (shape.MaxStack > 1)
                foreach (var stack in items.Items(LockerRow).Where(s => s.Item.TypeHash == it.Item.TypeHash && s.Item.StackSize < shape.MaxStack
                    && region.Holds(s.X, s.Y, shape.Width, shape.Height)).ToList())
                {
                    if (count == 0) break;
                    int put = Math.Min(count, shape.MaxStack - stack.Item.StackSize);
                    if (items.SetStackSize(LockerRow, stack.Id, stack.Item.StackSize + put) is { } grown) { changed.Add(grown); count -= put; }
                }
            if (count > 0 && FreeCell(items, region, shape) is { } cell && items.Remove(from, it.Id) is { } taken)
            {
                placed = items.Put(LockerRow, taken with { X = cell.X, Y = cell.Y, Item = taken.Item with { StackSize = (ushort)count } });
                emptied = true;
            }
            else if (count == 0) emptied = items.Remove(from, it.Id) is not null;
            else if (count < it.Item.StackSize) left = items.SetStackSize(from, it.Id, count);
        });
        if (changed.Count == 0 && placed is null) return false;
        var player = PlayerOf(conn);
        await player.Send(InstanceItems.InventoryChange(PlayerInventories.InventoryId(from), items.NextRevision(from), [it.Id], left is null ? [] : [left]),
            $"0x00bf inventory {PlayerInventories.InventoryId(from)}: " + (left is null ? $"remove item {it.Id}" : $"stack {it.Id} now {left.Item.StackSize}"));
        List<InventoryItem> added = [.. changed, .. placed is null ? [] : new[] { placed }];
        await player.Send(InstanceItems.InventoryChange(PlayerInventories.InventoryId(LockerRow), items.NextRevision(LockerRow), [.. changed.Select(c => c.Id)], added),
            $"0x00bf inventory {PlayerInventories.InventoryId(LockerRow)}: " + string.Join(", ", added.Select(a => $"item {a.Id} at ({a.X}, {a.Y}) x{a.Item.StackSize}")));
        return emptied || changed.Count > 0;
    }

    // Out of the locker: the first free cell of the main inventory.
    static async Task<bool> Take(ClientState conn, InventoryItem it)
    {
        var items = ItemsOf(conn);
        var shape = ShapeOf(it.Item) ?? new(1, 1, "");
        if (items.FreeCell(PlayerInventories.MainInventory, shape.Width, shape.Height, ShapeOf) is not { } cell) return false;
        InventoryItem? placed = null;
        Saves.Atomically(() =>
        {
            if (items.Remove(LockerRow, it.Id) is { } taken) placed = items.Put(PlayerInventories.MainInventory, taken with { X = cell.X, Y = cell.Y });
        });
        if (placed is null) return false;
        var player = PlayerOf(conn);
        await player.Send(InstanceItems.InventoryChange(PlayerInventories.InventoryId(LockerRow), items.NextRevision(LockerRow), [placed.Id], []),
            $"0x00bf inventory {PlayerInventories.InventoryId(LockerRow)}: remove item {placed.Id}");
        await player.Send(InstanceItems.InventoryChange(PlayerInventories.InventoryId(PlayerInventories.MainInventory), items.NextRevision(PlayerInventories.MainInventory), [], [placed]),
            $"0x00bf inventory {PlayerInventories.InventoryId(PlayerInventories.MainInventory)}: add item {placed.Id} at ({placed.X}, {placed.Y})");
        return true;
    }

    static (byte X, byte Y)? FreeCell(PlayerInventories items, Region region, PlayerInventories.Shape shape)
    {
        var taken = items.Items(LockerRow).Select(i => (i, s: ShapeOf(i.Item) ?? new(1, 1, ""))).ToList();
        for (int y = region.Y; y + shape.Height <= region.Y + region.Height; y++)
            for (int x = region.X; x + shape.Width <= region.X + region.Width; x++)
                if (!taken.Any(t => t.i.X < x + shape.Width && x < t.i.X + t.s.Width && t.i.Y < y + shape.Height && y < t.i.Y + t.s.Height))
                    return ((byte)x, (byte)y);
        return null;
    }

    /// <summary>C→S 0x010e, Ctrl + click with the Chart window (inventory 103) open: a chart from the main (or expanded)
    /// inventory into slot 103 at (0, 0) (swapping with an existing chart in 103 if it fits back), or a chart out of 103
    /// into the first free cell of the main inventory (or expanded if granted).</summary>
    public static async Task QuickMoveChart(IModuleContext module, ClientState conn, InstanceItems.LeagueWindowMove move)
    {
        var player = PlayerOf(conn);
        var items = ItemsOf(conn);
        byte from = move.Into ? move.FromExpanded ? PlayerInventories.ExpandedInventory : PlayerInventories.MainInventory : UnchartedRow;
        string what = $"0x010e Ctrl + click item {move.ItemId} {(move.Into ? "into" : "out of")} the Chart window";
        var it = items.Items(from).FirstOrDefault(i => i.Id == move.ItemId);
        string? why = it is null ? $"not in row {from}" : move.Into ? Refusal(conn, UnchartedRow, it.Item) : null;
        if (why is not null)
        {
            module.Log($"{player.Character} <= {what}: refused ({why})");
            return;
        }
        if (move.Into)
        {
            var src = it!;
            var existing = items.Items(UnchartedRow).FirstOrDefault();
            if (existing is not null)
            {
                var itShape = ShapeOf(src.Item) ?? new(1, 1, "");
                var existingShape = ShapeOf(existing.Item) ?? new(1, 1, "");
                if (existingShape.Width > itShape.Width || existingShape.Height > itShape.Height)
                {
                    module.Log($"{player.Character} <= {what}: refused (existing chart does not fit back into cell ({src.X}, {src.Y}))");
                    return;
                }
            }
            InventoryItem? placed = null, swapped = null;
            Saves.Atomically(() =>
            {
                if (items.Remove(from, src.Id) is not { } taken) return;
                var prev = existing is null ? null : items.Remove(UnchartedRow, existing.Id);
                placed = items.Put(UnchartedRow, taken with { X = 0, Y = 0 });
                if (prev is not null) swapped = items.Put(from, prev with { X = src.X, Y = src.Y });
            });
            if (placed is null)
            {
                module.Log($"{player.Character} <= {what}: refused (item moved)");
                return;
            }
            await player.Send(InstanceItems.InventoryChange(PlayerInventories.InventoryId(from), items.NextRevision(from),
                    [src.Id], swapped is null ? [] : [swapped]),
                $"0x00bf inventory {PlayerInventories.InventoryId(from)}: remove item {src.Id}" + (swapped is null ? "" : $", swap in {swapped.Id}"));
            await player.Send(InstanceItems.InventoryChange(PlayerInventories.InventoryId(UnchartedRow), items.NextRevision(UnchartedRow),
                    existing is null ? [] : [existing.Id], [placed]),
                $"0x00bf inventory {PlayerInventories.InventoryId(UnchartedRow)}: place chart {placed.Id}");
            module.Log($"{player.Character} <= {what}: moved row {from} → {UnchartedRow}" + (swapped is null ? "" : $" (swapped with {swapped.Id})"));
            return;
        }
        var shape = ShapeOf(it!.Item) ?? new(1, 1, "");
        byte to = PlayerInventories.MainInventory;
        var cell = items.FreeCell(to, shape.Width, shape.Height, ShapeOf);
        if (cell is null && ExpandedInventoryGranted(conn))
        {
            to = PlayerInventories.ExpandedInventory;
            cell = items.FreeCell(to, shape.Width, shape.Height, ShapeOf);
        }
        if (cell is not { } dest)
        {
            module.Log($"{player.Character} <= {what}: refused (no room)");
            return;
        }
        InventoryItem? moved = null;
        Saves.Atomically(() =>
        {
            if (items.Remove(UnchartedRow, it.Id) is { } taken) moved = items.Put(to, taken with { X = dest.X, Y = dest.Y });
        });
        if (moved is null)
        {
            module.Log($"{player.Character} <= {what}: refused (item moved)");
            return;
        }
        await player.Send(InstanceItems.InventoryChange(PlayerInventories.InventoryId(UnchartedRow), items.NextRevision(UnchartedRow), [moved.Id], []),
            $"0x00bf inventory {PlayerInventories.InventoryId(UnchartedRow)}: remove item {moved.Id}");
        await player.Send(InstanceItems.InventoryChange(PlayerInventories.InventoryId(to), items.NextRevision(to), [], [moved]),
            $"0x00bf inventory {PlayerInventories.InventoryId(to)}: add item {moved.Id} at ({moved.X}, {moved.Y})");
        module.Log($"{player.Character} <= {what}: moved row {UnchartedRow} → {to} at ({moved.X}, {moved.Y})");
    }

    /// <summary>Sulphur the player holds across the inventories the client sums (<c>0x140410110</c>, <c>0x141d6b020</c>):
    /// the Chart Locker (112), main inventory (0), expanded inventory (84) and personal stash tabs.</summary>
    public static int SulphurHeld(ClientState conn)
    {
        var items = ItemsOf(conn);
        long total = (long)items.Count(LockerRow, SulphurHash)
            + items.Count(PlayerInventories.MainInventory, SulphurHash)
            + items.Count(PlayerInventories.ExpandedInventory, SulphurHash)
            + PersonalStashOf(conn).CountOf(SulphurHash);
        return (int)Math.Clamp(total, 0, int.MaxValue);
    }

    /// <summary>The inventory and stash changes recorded by <see cref="TryPaySulphurInTransaction"/>, ready to send to the client.</summary>
    internal readonly record struct SulphurPayment(List<(byte Row, PlayerInventories.Payment Paid)> Rows, AccountStash? Stash, AccountStash.Payment? StashPaid);

    /// <summary>Deducts <paramref name="amount"/> Sulphur inside an active <see cref="Saves.Atomically"/> transaction in
    /// priority order: Chart Locker (112), main inventory (0), expanded inventory (84), then personal stash (G: order).
    /// Returns false (nothing taken) when the player holds less.</summary>
    internal static bool TryPaySulphurInTransaction(ClientState conn, int amount, out SulphurPayment payment)
    {
        var rows = new List<(byte Row, PlayerInventories.Payment Paid)>();
        AccountStash? touchedStash = null;
        AccountStash.Payment? stashPaid = null;
        if (amount <= 0)
        {
            payment = new(rows, null, null);
            return true;
        }
        var items = ItemsOf(conn);
        var stash = PersonalStashOf(conn);
        int inLocker = items.Count(LockerRow, SulphurHash);
        int inMain = items.Count(PlayerInventories.MainInventory, SulphurHash);
        int inExpanded = items.Count(PlayerInventories.ExpandedInventory, SulphurHash);
        int inStash = stash.CountOf(SulphurHash);
        if ((long)inLocker + inMain + inExpanded + inStash < amount)
        {
            payment = new(rows, null, null);
            return false;
        }
        int remaining = amount;
        int fromLocker = Math.Min(inLocker, remaining);
        if (fromLocker > 0 && items.Pay(LockerRow, SulphurHash, fromLocker) is { } a) { rows.Add((LockerRow, a)); remaining -= fromLocker; }
        int fromMain = Math.Min(inMain, remaining);
        if (fromMain > 0 && items.Pay(PlayerInventories.MainInventory, SulphurHash, fromMain) is { } b) { rows.Add((PlayerInventories.MainInventory, b)); remaining -= fromMain; }
        int fromExpanded = Math.Min(inExpanded, remaining);
        if (fromExpanded > 0 && items.Pay(PlayerInventories.ExpandedInventory, SulphurHash, fromExpanded) is { } c) { rows.Add((PlayerInventories.ExpandedInventory, c)); remaining -= fromExpanded; }
        if (remaining > 0)
        {
            stashPaid = stash.Pay(SulphurHash, remaining);
            if (stashPaid is null)
            {
                payment = new(rows, null, null);
                return false;
            }
            touchedStash = stash;
        }
        payment = new(rows, touchedStash, stashPaid);
        return true;
    }

    /// <summary>Sends the 0x00bf updates for a completed <see cref="SulphurPayment"/>.</summary>
    internal static async Task SendSulphurPayment(ClientState conn, SulphurPayment payment)
    {
        var items = ItemsOf(conn);
        var player = PlayerOf(conn);
        foreach (var (row, p) in payment.Rows)
            await player.Send(InstanceItems.InventoryChange(PlayerInventories.InventoryId(row), items.NextRevision(row), [.. p.Removed, .. p.Changed.Select(c => c.Id)], p.Changed),
                $"0x00bf inventory {PlayerInventories.InventoryId(row)}: paid Sulphur");
        if (payment.Stash is { } stash && payment.StashPaid is { } stashPaid)
        {
            var (remote, stream, ciphers) = conn.Wire;
            await SendStashPayment(remote, stream, ciphers, conn, stash, stashPaid);
        }
    }

    /// <summary>Pays <paramref name="amount"/> Sulphur from the locker (112), main inventory (0), expanded inventory (84),
    /// then personal stash (G: priority order), and sends every modified row's or tab's 0x00bf; false (nothing taken) when
    /// the player holds less.</summary>
    public static async Task<bool> PaySulphur(ClientState conn, int amount)
    {
        SulphurPayment payment = default;
        bool paid = false;
        Saves.Atomically(() => paid = TryPaySulphurInTransaction(conn, amount, out payment));
        if (paid) await SendSulphurPayment(conn, payment);
        return paid;
    }
}
