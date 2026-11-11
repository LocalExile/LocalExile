using GameServer.Core.Protocol.Instance;

public sealed partial class PlayerInventories
{
    public sealed record SkinTransferChange(byte Row, InventoryItem Target, InventoryItem Source, byte SourceRow = Cursor);

    // The donor moves from the cursor into A, in the same inventory snapshot and save. No second usable B exists.
    public SkinTransferChange? BindSkin(uint targetId, Func<Item, Shape?> shapeOf, out string why)
        => BindSkin(targetId, null, Cursor, null, shapeOf, out why);

    // The native picker names both inventories and items; an item moved since the pick must not be rebound elsewhere.
    public SkinTransferChange? BindSkin(uint targetId, byte? targetRow, byte sourceRow, uint? sourceId,
        Func<Item, Shape?> shapeOf, out string why)
    {
        SkinTransferChange result;
        lock (_lock)
        {
            why = "";
            var donor = sourceId is uint id ? ItemsIn(sourceRow).FirstOrDefault(i => i.Id == id)
                : ItemsIn(sourceRow) is [var held] ? held : null;
            if (donor is null) { why = "Source B is missing"; return null; }
            if (RowOf(targetId) is not byte row || row == Cursor || _offered.ContainsKey(targetId))
            { why = "Target A is missing or offered for sale"; return null; }
            if (targetRow is byte expected && expected != row || targetId == donor.Id
                || !SkinInventory(row) || sourceRow != Cursor && (!SkinInventory(sourceRow) || ItemsIn(Cursor).Count != 0)
                || _offered.ContainsKey(donor.Id))
            { why = "Items moved, are offered for sale, or are not in an available equipment inventory"; return null; }
            var target = ItemsIn(row).Single(i => i.Id == targetId);
            if (target.Item.SkinTransferSource is not null || donor.Item.SkinTransferSource is not null)
            { why = "Remove the existing skin transfer first"; return null; }
            if (!target.Item.Identified || !donor.Item.Identified || target.Item.StackSize != 1 || donor.Item.StackSize != 1
                || shapeOf(target.Item) is not { } a || shapeOf(donor.Item) is not { } b
                || EquipmentFamily(a.Class) is not { } family || family != EquipmentFamily(b.Class))
            { why = "A and B must be identified equipment for the same slot (weapons may differ in type)"; return null; }
            var bound = target with { Item = target.Item with { SkinTransferSource = donor.Item } };
            var list = ItemsIn(row);
            list[list.IndexOf(target)] = bound;
            ItemsIn(sourceRow).Remove(donor);
            result = new(row, bound, donor, sourceRow);
        }
        Persist();
        return result;
    }

    static bool SkinInventory(byte row) => row <= 10 || row is Weapon2 or Offhand2;

    public SkinTransferChange? UnbindSkin(uint targetId, out string why, byte? targetRow = null)
    {
        SkinTransferChange result;
        lock (_lock)
        {
            why = "";
            if (ItemsIn(Cursor).Count != 0) { why = "Empty the cursor before reclaiming B"; return null; }
            if (RowOf(targetId) is not byte row || row == Cursor || _offered.ContainsKey(targetId))
            { why = "Target A is missing or offered for sale"; return null; }
            if (targetRow is byte expected && expected != row)
            { why = "Target A moved to another inventory"; return null; }
            var target = ItemsIn(row).Single(i => i.Id == targetId);
            if (target.Item.SkinTransferSource is not { } source) { why = "A has no skin transfer"; return null; }
            // Returned items get a fresh placement id, including after A crossed a stash/account boundary.
            var returned = new InventoryItem(NewItemId(), 0, 0, source);
            var unbound = target with { Item = target.Item with { SkinTransferSource = null } };
            var list = ItemsIn(row);
            list[list.IndexOf(target)] = unbound;
            ItemsIn(Cursor).Add(returned);
            result = new(row, unbound, returned);
        }
        Persist();
        return result;
    }

    static string? EquipmentFamily(string itemClass) => OneHand.Contains(itemClass) || TwoHand.Contains(itemClass)
        ? "Weapon" : itemClass is "Body Armour" or "Helmet" or "Gloves" or "Boots" or "Shield" or "Quiver"
            or "Amulet" or "Ring" or "Belt" ? itemClass : null;
}
