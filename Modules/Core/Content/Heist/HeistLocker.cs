using GameServer.Core;
using GameServer.Core.Protocol.Instance;
using GameServer.Modding;

namespace GameServer.Modules.Heist;

/// <summary>
/// Heist Locker inventory (<c>HeistStorage1</c>, row <c>56</c>, 107 × 12 grid) partitioned by <c>HeistStorageLayout</c>
/// + <c>HeistAreas</c>: Contracts (y = 0, 9 × 11 × 6 tabs), Blueprints (y = 6, 9 × 11 × 6 tabs), and Rogue's Markers
/// (x = 99, y = 0, 7 × 3). Handles C→S <c>0x00fc</c> quick-moves and <c>0x002a</c> region guards.
/// </summary>
public static class HeistLocker
{
    public const string LockerWindow = "heist";

    /// <summary>A Heist Locker tab's grid cells for one base item type.</summary>
    public readonly record struct Region(int X, int Y, int Width, int Height)
    {
        public bool Holds(int x, int y, int width, int height) =>
            x >= X && y >= Y && (long)x + width <= (long)X + Width && (long)y + height <= (long)Y + Height;
    }

    public static Dictionary<uint, Region> Regions { get; private set; } = [];

    public static void LoadGameData()
    {
        var bases = ClientTables.TableOrEmpty("BaseItemTypes");
        var itemClasses = ClientTables.TableOrEmpty("ItemClasses");
        var areas = ClientTables.TableOrEmpty("HeistAreas");
        var regions = new Dictionary<uint, Region>();
        foreach (var r in ClientTables.TableOrEmpty("HeistStorageLayout"))
        {
            int x = r.IntOrNull("__col8") ?? 0;
            int y = r.IntOrNull("__col9") ?? 0;
            int width = r.IntOrNull("Width") ?? 1;
            int height = r.IntOrNull("Height") ?? 1;
            int? baseRow = r.IntOrNull("StoredItem");
            if (baseRow is null && r.IntOrNull("HeistJobsKey") is int areaRow && areaRow >= 0 && areaRow < areas.Count
                && r.IntOrNull("ItemClass") is int classRow && classRow >= 0 && classRow < itemClasses.Count)
            {
                baseRow = itemClasses[classRow].Text("Id") switch
                {
                    "HeistContract" => areas[areaRow].IntOrNull("Contract_BaseItemTypesKey"),
                    "HeistBlueprint" => areas[areaRow].IntOrNull("Blueprint_BaseItemTypesKey"),
                    _ => null,
                };
            }
            if (baseRow is int b && b >= 0 && b < bases.Count && bases[b].Text("Id") is { Length: > 0 } baseId)
                regions[InstanceEntities.ObjectTypeHash(baseId)] = new(x, y, width, height);
        }
        Regions = regions;
    }

    public static Region? RegionOf(uint typeHash) =>
        Regions.TryGetValue(typeHash, out var region) ? region : null;

    /// <summary>GameEvents.InventoryPlace: refuses manual 0x002a drops into row 56 outside the item's HeistStorageLayout region.</summary>
    public static bool RefusedPlace(IModuleContext module, Player player, InstanceItems.PlaceItem place)
    {
        if (place.InventoryId != PlayerInventories.InventoryId(HeistTables.HeistLockerRow)) return false;
        if (ItemsOf(player).Items(PlayerInventories.Cursor).FirstOrDefault() is not { } held
            || RegionOf(held.Item.TypeHash) is not { } region) return false;
        var shape = ShapeOf(held.Item) ?? new(1, 1, "");
        if (place.X <= int.MaxValue && place.Y <= int.MaxValue
            && region.Holds((int)place.X, (int)place.Y, shape.Width, shape.Height)) return false;
        module.Log($"{PlayerOf(player).Character} <= 0x002a place item {held.Id} in the Heist Locker at ({place.X}, {place.Y}): refused (outside its tab region)");
        return true;
    }

    /// <summary>C→S 0x00fc packet handler: Ctrl + click into or out of the open Heist Locker (row 56).</summary>
    public static async Task<bool> HandleLockerMove(IModuleContext module, Player player, byte[] bytes)
    {
        if (bytes.Length < InstanceHeist.LockerMoveLength) return false;
        await QuickMove(module, player, InstanceHeist.ReadLockerMove(bytes));
        return true;
    }

    /// <summary>C→S 0x00fc, Ctrl + click with the Heist Locker open: into its HeistStorageLayout tab (stacking Rogue's
    /// Markers onto existing stacks in that region first), or out of the locker into MainInventory / ExpandedInventory.</summary>
    public static async Task QuickMove(IModuleContext module, Player player, InstanceItems.LeagueWindowMove move)
    {
        var modPlayer = PlayerOf(player);
        var items = ItemsOf(player);
        byte from = move.Into
            ? (move.FromExpanded ? PlayerInventories.ExpandedInventory : PlayerInventories.MainInventory)
            : HeistTables.HeistLockerRow;
        string what = $"0x00fc Ctrl + click item {move.ItemId} {(move.Into ? "into" : "out of")} the Heist Locker";
        var it = items.Items(from).FirstOrDefault(i => i.Id == move.ItemId);
        string? why = it is null ? $"not in row {from}" : player.ModuleWindow != LockerWindow ? "the locker is not open" : null;
        if (why is null && move.Into && RegionOf(it!.Item.TypeHash) is null)
            why = "only Heist Contracts, Blueprints, and Rogue's Markers go into the Heist Locker";
        if (why is not null)
        {
            module.Log($"{modPlayer.Character} <= {what}: refused ({why})");
            return;
        }
        bool done = move.Into ? await Store(player, it!, from) : await Take(player, it!);
        module.Log($"{modPlayer.Character} <= {what}: {(done ? "moved" : "refused (no room)")}");
    }

    static async Task<bool> Store(Player player, InventoryItem it, byte from)
    {
        var items = ItemsOf(player);
        var region = RegionOf(it.Item.TypeHash)!.Value;
        var shape = ShapeOf(it.Item) ?? new(1, 1, "");
        var changed = new List<InventoryItem>();
        InventoryItem? placed = null;
        bool emptied = false;
        InventoryItem? left = null;
        Saves.Atomically(() =>
        {
            var live = items.Items(from).FirstOrDefault(i => i.Id == it.Id);
            if (live is null) return;
            int count = live.Item.StackSize;
            if (shape.MaxStack > 1)
                foreach (var stack in items.Items(HeistTables.HeistLockerRow)
                    .Where(s => s.Item.TypeHash == live.Item.TypeHash && s.Item.StackSize < shape.MaxStack
                        && region.Holds(s.X, s.Y, shape.Width, shape.Height))
                    .ToList())
                {
                    if (count == 0) break;
                    int put = Math.Min(count, shape.MaxStack - stack.Item.StackSize);
                    if (items.SetStackSize(HeistTables.HeistLockerRow, stack.Id, stack.Item.StackSize + put) is { } grown)
                    {
                        changed.Add(grown);
                        count -= put;
                    }
                }
            if (count > 0 && FreeCell(items, region, shape) is { } cell && items.Remove(from, live.Id) is { } taken)
            {
                placed = items.Put(HeistTables.HeistLockerRow,
                    taken with { X = cell.X, Y = cell.Y, Item = taken.Item with { StackSize = (ushort)count } });
                emptied = true;
            }
            else if (count == 0) emptied = items.Remove(from, live.Id) is not null;
            else if (count < live.Item.StackSize) left = items.SetStackSize(from, live.Id, count);
        });
        if (changed.Count == 0 && placed is null) return false;
        var modPlayer = PlayerOf(player);
        await modPlayer.Send(InstanceItems.InventoryChange(PlayerInventories.InventoryId(from), items.NextRevision(from), [it.Id], left is null ? [] : [left]),
            $"0x00bf inventory {PlayerInventories.InventoryId(from)}: " + (left is null ? $"remove item {it.Id}" : $"stack {it.Id} now {left.Item.StackSize}"));
        List<InventoryItem> added = [.. changed, .. placed is null ? [] : new[] { placed }];
        await modPlayer.Send(InstanceItems.InventoryChange(PlayerInventories.InventoryId(HeistTables.HeistLockerRow), items.NextRevision(HeistTables.HeistLockerRow),
                [.. changed.Select(c => c.Id)], added),
            $"0x00bf inventory {PlayerInventories.InventoryId(HeistTables.HeistLockerRow)}: " + string.Join(", ", added.Select(a => $"item {a.Id} at ({a.X}, {a.Y}) x{a.Item.StackSize}")));
        return emptied || changed.Count > 0;
    }

    static async Task<bool> Take(Player player, InventoryItem it)
    {
        var items = ItemsOf(player);
        var shape = ShapeOf(it.Item) ?? new(1, 1, "");
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
            if (items.Remove(HeistTables.HeistLockerRow, it.Id) is { } taken)
                placed = items.Put(to, taken with { X = dest.X, Y = dest.Y });
        });
        if (placed is null) return false;
        var modPlayer = PlayerOf(player);
        await modPlayer.Send(InstanceItems.InventoryChange(PlayerInventories.InventoryId(HeistTables.HeistLockerRow), items.NextRevision(HeistTables.HeistLockerRow),
                [placed.Id], []),
            $"0x00bf inventory {PlayerInventories.InventoryId(HeistTables.HeistLockerRow)}: remove item {placed.Id}");
        await modPlayer.Send(InstanceItems.InventoryChange(PlayerInventories.InventoryId(to), items.NextRevision(to), [], [placed]),
            $"0x00bf inventory {PlayerInventories.InventoryId(to)}: add item {placed.Id} at ({placed.X}, {placed.Y})");
        return true;
    }

    static (byte X, byte Y)? FreeCell(PlayerInventories items, Region region, PlayerInventories.Shape shape)
    {
        var taken = items.Items(HeistTables.HeistLockerRow).Select(i => (i, s: ShapeOf(i.Item) ?? new(1, 1, ""))).ToList();
        for (int y = region.Y; y + shape.Height <= region.Y + region.Height; y++)
            for (int x = region.X; x + shape.Width <= region.X + region.Width; x++)
                if (!taken.Any(t => t.i.X < x + shape.Width && x < t.i.X + t.s.Width && t.i.Y < y + shape.Height && y < t.i.Y + t.s.Height))
                    return ((byte)x, (byte)y);
        return null;
    }
}
