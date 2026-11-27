using GameServer.Core;
using GameServer.Core.Protocol.Instance;
using GameServer.Modding;

namespace GameServer.Modules.Breach;

/// <summary>
/// The Wombgift Locker (<c>BrequelStorage1</c>, <c>Inventories</c> row 101, <c>InventoryIdKey</c> 63, 24 × 12 grid):
/// deposit, withdraw, Ctrl+click quick-move, item-class validation (<c>BrequelLockerLayout</c> -> <c>ItemClasses#96
/// BrequelFruit</c>), and character/account persistence via <see cref="PlayerInventories"/>.
/// </summary>
public static class BreachLocker
{
    /// <summary><c>Inventories</c> row 101 (<c>BrequelStorage1</c>, 24 × 12, wire inventory id 102).</summary>
    public const byte LockerRow = 101;

    /// <summary>Owner token recorded in <c>Player.ModuleWindow</c> while the Wombgift Locker is open.</summary>
    public const string LockerWindow = "breach-locker";

    /// <summary>Monastery Wombgift Locker object type (<c>hub.arm</c>).</summary>
    public const string GraftLockerType = "Metadata/MiscellaneousObjects/Brequel/GraftLocker";

    /// <summary>Hideout Wombgift Locker station object type.</summary>
    public const string HideoutLockerType = "Metadata/MiscellaneousObjects/Brequel/GraftLocker_Hideout";

    /// <summary>S→C 0x7b OpenUi kind for the Wombgift Locker (updated once @re-protocol Drop 1 confirms the exact
    /// 3.29.3c constant in <c>InstanceWaypoints</c>).</summary>
    public static byte LockerUiKind { get; set; }

    /// <summary>Item class id accepted by the Wombgift Locker (<c>BrequelLockerLayout</c> -> <c>ItemClasses#96</c>).</summary>
    public const string WombgiftItemClass = "BrequelFruit";

    static HashSet<uint> AcceptedTypeHashes = [];
    static int GridWidth = 24, GridHeight = 12;

    /// <summary>Loads <c>BrequelLockerLayout</c> and <c>BrequelFruitTypes</c> from <c>client.db</c>.</summary>
    public static void LoadGameData()
    {
        var accepted = new HashSet<uint>();
        var acceptedClasses = new HashSet<int>();

        foreach (var row in ClientTables.TableOrEmpty("BrequelLockerLayout"))
        {
            if (row.IntOrNull("ItemClass") is int cls) acceptedClasses.Add(cls);
            if (row.IntOrNull("__col1") is int w and > 0 && row.IntOrNull("__col2") is int h and > 0)
            {
                // Inventories#101 BrequelStorage1 is 24 x 12 (two 12 x 12 pages or one 24 x 12 grid).
                GridWidth = Math.Max(w, 24);
                GridHeight = Math.Max(h, 12);
            }
        }
        if (acceptedClasses.Count == 0) acceptedClasses.Add(96);

        var bases = ClientTables.TableOrEmpty("BaseItemTypes");
        foreach (var row in bases)
        {
            if (row.IntOrNull("ItemClassesKey") is int cls && acceptedClasses.Contains(cls)
                && row.TextOrNull("Id") is { Length: > 0 } id)
                accepted.Add(InstanceEntities.ObjectTypeHash(id));
        }
        foreach (var row in ClientTables.TableOrEmpty("BrequelFruitTypes"))
        {
            if (row.IntOrNull("BaseItemType") is int b && b >= 0 && b < bases.Count
                && bases[b].TextOrNull("Id") is { Length: > 0 } id)
                accepted.Add(InstanceEntities.ObjectTypeHash(id));
        }

        if (ClientTables.TableOrNull("Inventories") is { } invs && LockerRow < invs.Count)
        {
            int w = invs[LockerRow].IntOrNull("Width") ?? 24;
            int h = invs[LockerRow].IntOrNull("Height") ?? 12;
            if (w > 0) GridWidth = w;
            if (h > 0) GridHeight = h;
        }

        AcceptedTypeHashes = accepted;
    }

    /// <summary>Whether <paramref name="item"/> is a Wombgift eligible for the Wombgift Locker.</summary>
    public static bool IsWombgift(ItemCatalog catalog, Item item) =>
        AcceptedTypeHashes.Contains(item.TypeHash)
        || catalog.ByHash(item.TypeHash)?.Class == WombgiftItemClass;

    /// <summary><c>GameEvents.InventoryRefusal</c> for <see cref="LockerRow"/>: only Wombgifts (<c>BrequelFruit</c>)
    /// may enter <c>BrequelStorage1</c>.</summary>
    public static string? Refusal(Player player, byte row, Item item) =>
        row == LockerRow && !IsWombgift(ItemsFor(player), item)
            ? "the Wombgift Locker takes only Wombgifts"
            : null;

    /// <summary>Ctrl+click quick-move between Main/Expanded inventory and <see cref="LockerRow"/>.
    /// Returns <see langword="false"/> when the Wombgift Locker is not the active window so other lockers (e.g.
    /// Deepwater's Chart Locker on <c>0x0111</c>) still receive their packets.</summary>
    public static async Task<bool> QuickMoveAsync(IModuleContext module, Player player, InstanceItems.LeagueWindowMove move)
    {
        if (player.ModuleWindow != LockerWindow) return false;

        var modPlayer = PlayerOf(player);
        var items = ItemsOf(player);
        byte from = move.Into
            ? (move.FromExpanded ? PlayerInventories.ExpandedInventory : PlayerInventories.MainInventory)
            : LockerRow;
        string what = $"Ctrl+click item {move.ItemId} {(move.Into ? "into" : "out of")} the Wombgift Locker";
        var it = items.Items(from).FirstOrDefault(i => i.Id == move.ItemId);
        if (it is null)
        {
            module.Log($"{modPlayer.Character} <= {what}: refused (not in row {from})");
            return true;
        }
        if (move.Into && !IsWombgift(ItemsFor(player), it.Item))
        {
            module.Log($"{modPlayer.Character} <= {what}: refused (not a Wombgift)");
            return true;
        }

        bool done = move.Into ? await StoreAsync(player, it, from) : await TakeAsync(player, it);
        module.Log($"{modPlayer.Character} <= {what}: {(done ? "moved" : "refused (no room)")}");
        return true;
    }

    static async Task<bool> StoreAsync(Player player, InventoryItem it, byte from)
    {
        var items = ItemsOf(player);
        var shape = ShapeOf(it.Item) ?? new(1, 1, WombgiftItemClass);
        InventoryItem? placed = null;

        Saves.Atomically(() =>
        {
            var live = items.Items(from).FirstOrDefault(i => i.Id == it.Id);
            if (live is null) return;
            if (FreeLockerCell(items, shape) is not { } cell) return;
            if (items.Remove(from, live.Id) is { } taken)
                placed = items.Put(LockerRow, taken with { X = cell.X, Y = cell.Y });
        });

        if (placed is null) return false;
        var modPlayer = PlayerOf(player);
        await modPlayer.Send(
            InstanceItems.InventoryChange(PlayerInventories.InventoryId(from), items.NextRevision(from), [placed.Id], []),
            $"0x00bf inventory {PlayerInventories.InventoryId(from)}: remove Wombgift {placed.Id}");
        await modPlayer.Send(
            InstanceItems.InventoryChange(PlayerInventories.InventoryId(LockerRow), items.NextRevision(LockerRow), [], [placed]),
            $"0x00bf inventory {PlayerInventories.InventoryId(LockerRow)}: store Wombgift {placed.Id} at ({placed.X}, {placed.Y})");
        return true;
    }

    static async Task<bool> TakeAsync(Player player, InventoryItem it)
    {
        var items = ItemsOf(player);
        var shape = ShapeOf(it.Item) ?? new(1, 1, WombgiftItemClass);
        byte to = PlayerInventories.MainInventory;
        var cell = items.FreeCell(to, shape.Width, shape.Height, ShapeOf);
        if (cell is null && ExpandedInventoryGranted(player))
        {
            to = PlayerInventories.ExpandedInventory;
            cell = items.FreeCell(to, shape.Width, shape.Height, ShapeOf);
        }
        if (cell is not { } dest) return false;

        InventoryItem? placed = null;
        Saves.Atomically(() =>
        {
            if (items.Remove(LockerRow, it.Id) is { } taken)
                placed = items.Put(to, taken with { X = dest.X, Y = dest.Y });
        });

        if (placed is null) return false;
        var modPlayer = PlayerOf(player);
        await modPlayer.Send(
            InstanceItems.InventoryChange(PlayerInventories.InventoryId(LockerRow), items.NextRevision(LockerRow), [placed.Id], []),
            $"0x00bf inventory {PlayerInventories.InventoryId(LockerRow)}: remove Wombgift {placed.Id}");
        await modPlayer.Send(
            InstanceItems.InventoryChange(PlayerInventories.InventoryId(to), items.NextRevision(to), [], [placed]),
            $"0x00bf inventory {PlayerInventories.InventoryId(to)}: add Wombgift {placed.Id} at ({placed.X}, {placed.Y})");
        return true;
    }

    static (byte X, byte Y)? FreeLockerCell(PlayerInventories items, PlayerInventories.Shape shape)
    {
        var taken = items.Items(LockerRow).Select(i => (Item: i, Shape: ShapeOf(i.Item) ?? new(1, 1, WombgiftItemClass))).ToList();
        for (int y = 0; y + shape.Height <= GridHeight; y++)
            for (int x = 0; x + shape.Width <= GridWidth; x++)
                if (!taken.Any(t => t.Item.X < x + shape.Width && x < t.Item.X + t.Shape.Width
                    && t.Item.Y < y + shape.Height && y < t.Item.Y + t.Shape.Height))
                    return ((byte)x, (byte)y);
        return null;
    }
}
