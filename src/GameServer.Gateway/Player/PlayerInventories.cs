using System.Collections.Concurrent;
using System.Text.Json;
using GameServer.Core.Protocol.Instance;
using GameServer.Core;

/// <summary>
/// A character's items, by Inventories.dat row (the Gateway registers inventory id = row + 1 for every row, 05 §2). Kept
/// in memory per character name and saved after every change (<see cref="CharacterSaves"/>, data/saves/), so they follow
/// the player through area changes and Gateway restarts.
/// </summary>
public sealed partial class PlayerInventories
{
    public const byte MainInventory = 0, BodyArmour = 1, Weapon1 = 2, Offhand1 = 3, Helmet = 4, Amulet = 5,
        Ring1 = 6, Ring2 = 7, Gloves = 8, Boots = 9, Belt = 10, Flask = 11, MapDevice = 13, Weapon2 = 14,
        Offhand2 = 15, CraftingBench = 16, PassiveJewels = 23, AtlasUpgrades = 39, HarvestCrafting = 43,
        HeistNpcEquipmentFirst = 45, HeistNpcEquipmentCount = 9,
        ExpandedInventory = 84, MavenInvitations = 86, KalguurDisenchanting = 87, KalguurMapping = 88,
        RecombinatorInput = 92, RecombinatorOutput = 93, MemoryLineMaps = 94, Ring3 = 102, DeepwaterCollection = 114,
        Cursor = InstanceInventories.CursorRow;

    private static readonly ConcurrentDictionary<string, PlayerInventories> ByCharacter = new(StringComparer.OrdinalIgnoreCase);

    // Persistent gem edits replace immutable records without replacing the physical socketed gem.
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Item, object> GemIdentities = new();
    public static object GemIdentity(Item gem) => GemIdentities.GetValue(gem, _ => new());
    public static Item PreserveGemIdentity(Item before, Item after)
    {
        if (!ReferenceEquals(before, after)) GemIdentities.AddOrUpdate(after, GemIdentity(before));
        return after;
    }

    private readonly Dictionary<byte, List<InventoryItem>> _items = [];
    private readonly Dictionary<byte, uint> _revisions = [];
    private uint _nextItemId = 1;
    private readonly Lock _lock = new();
    private readonly GemSourceLifetimes.SourceStore _gemSourceLifetimes = new();
    private string? _owner;   // the character whose save this is; null while the start kit is being built
    private ModCatalog? _mods;  // to save mods by their Mods.dat Id
    private Item? _startWeapon;
    CharacterSaves.OpeningTutorial? newTutorial;
    private bool _stale;  // dropped after a failed save: the database has the items as they are
    private bool _unsaved;  // ChangeUnsaved changed something the database doesn't have yet
    // Items offered to an NPC while the sell window is open (id → its cell in the offer). As in 3.29 an offered item stays
    // in its inventory cell, a greyed imprint that keeps its size and collision, until the sale ends: nothing can take its
    // cell, so a cancel always puts every item back where it was. In memory only: a sale that ends any other way (a
    // disconnect, a crash) is a cancel, and the save already has every item in its place.
    private readonly Dictionary<uint, (byte X, byte Y)> _offered = [];
    private (byte Row, byte X, byte Y)? _heldFrom;  // where the held item was picked up from (PickUp)

    /// <summary>The class's start weapon while it waits on the ground for the character (as on the real game's Twilight
    /// Strand: the Gateway puts it near the spawn of each instance the character enters outside towns); null once picked
    /// up. New characters receive it on the ground; existing saved equipment is preserved.</summary>
    public Item? StartWeapon
    {
        get { lock (_lock) return _startWeapon; }
    }

    /// <summary>Makes <paramref name="weapon"/> the start weapon waiting on the ground, and saves it.</summary>
    public void SetStartWeapon(Item weapon)
    {
        lock (_lock) _startWeapon = weapon;
        Persist();
    }

    /// <summary>The start weapon was picked up: true the first time. It leaves the save with the pickup's own change (the
    /// <see cref="Put"/> that follows), so a crash never keeps it both waiting and in an inventory.</summary>
    public bool ClaimStartWeapon()
    {
        lock (_lock)
        {
            if (_startWeapon is null) return false;
            _startWeapon = null;
            return true;
        }
    }

    /// <summary>Writes the character's save (every inventory, with item ids and cells, and the start weapon), in a
    /// transaction of its own or the one it runs in (<see cref="Saves"/>).</summary>
    long _changes;
    long _mutations;
    (byte Row, InventoryItem Item)[]? _equipmentSnapshot;
    long _equipmentSnapshotMutation = -1;

    /// <summary>How many saved changes the items had (each Persist): a reader that saw the count sees whether they changed.
    /// Unsaved gem experience is not counted.</summary>
    public long Changes => Interlocked.Read(ref _changes);

    /// <summary>Monotonic count of all inventory mutations (saved and unsaved).</summary>
    public long Mutations => Interlocked.Read(ref _mutations);

    void Persist()
    {
        Interlocked.Increment(ref _changes);
        Interlocked.Increment(ref _mutations);
        if (_owner is not { } owner) return;
        Saves.Atomically(() =>
        {
            if (_stale) throw new InvalidOperationException($"items of {owner}: changed on a copy dropped after a failed save");
            // A failed save leaves this copy ahead of the database: drop it, the next use loads the items as saved.
            Saves.Db.OnRollback(() =>
            {
                _stale = true;
                _gemSourceLifetimes.RetireOwner();
                ByCharacter.TryRemove(new KeyValuePair<string, PlayerInventories>(owner, this));
            });
            // Copied under the items' lock, inside the transaction: an older copy never lands after a newer one. The rest
            // of the save (skill bar, experience, passives) stays as it is.
            Dictionary<byte, List<CharacterSaves.SavedPlacement>> saved;
            CharacterSaves.SavedItem? weapon;
            lock (_lock)
            {
                _unsaved = false;   // this copy has it (a rollback drops the whole object)
                saved = _items.Where(kv => kv.Value.Count > 0).ToDictionary(kv => kv.Key,
                    kv => kv.Value.Select(i => new CharacterSaves.SavedPlacement(i.Id, i.X, i.Y, CharacterSaves.ToSaved(i.Item, _mods))).ToList());
                weapon = _startWeapon is null ? null : CharacterSaves.ToSaved(_startWeapon, _mods);
            }
            CharacterSaves.SetInventories(owner, saved, weapon);
        });
    }

    public static uint InventoryId(byte row) => (uint)row + 1;
    public static byte? Row(uint inventoryId) => inventoryId >= 1 && inventoryId <= InstanceInventories.PlayerDefaults.Length ? (byte)(inventoryId - 1) : null;

    public IReadOnlyList<InventoryItem> Items(byte row)
    {
        lock (_lock) return _items.TryGetValue(row, out var l) ? [.. l] : [];
    }

    public bool AnyOwnedItem(Func<Item, bool> match)
    {
        lock (_lock) return _items.Values.Any(row => row.Any(item => match(item.Item)));
    }

    public InventoryItem Add(byte row, byte x, byte y, Item item)
    {
        InventoryItem placed;
        lock (_lock)
        {
            if (row == Cursor) _heldFrom = null;
            placed = new InventoryItem(_nextItemId++, x, y, item);
            (_items.TryGetValue(row, out var l) ? l : _items[row] = []).Add(placed);
        }
        Persist();
        return placed;
    }

    /// <summary>Puts an item (keeping its id) at a cell.</summary>
    public InventoryItem Put(byte row, InventoryItem item)
    {
        lock (_lock)
        {
            if (row == Cursor) _heldFrom = null;
            (_items.TryGetValue(row, out var l) ? l : _items[row] = []).Add(item);
        }
        Persist();
        return item;
    }

    /// <summary>Sets or clears an item's price note (e.g. C→S 0x1a1 with Flag=1 pricing a held cursor item before
    /// placement into an async-trade tab). Returns the updated item or null if not found.</summary>
    public InventoryItem? SetItemNote(byte row, uint itemId, string? note)
    {
        string? normalized = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        if (normalized is { Length: > 250 }) normalized = normalized[..250];
        InventoryItem updated;
        lock (_lock)
        {
            if (!_items.TryGetValue(row, out var list)) return null;
            int at = list.FindIndex(i => i.Id == itemId);
            if (at < 0) return null;
            updated = list[at] = list[at] with { Item = list[at].Item with { Note = normalized } };
        }
        Persist();
        return updated;
    }

    /// <summary>What an item needs for placement: its grid size, ItemClasses Id, how many share a cell (1: no stack),
    /// what a full stack becomes (null: it stays; 5 Scroll Fragments → a Scroll of Wisdom) and whether it is a Delve Fossil.</summary>
    public readonly record struct Shape(byte Width, byte Height, string Class, ushort MaxStack = 1, Func<Item>? FullStack = null,
        bool IsFossil = false);

    /// <summary>A stack that reached its max becomes its full-stack item (one of it, same id and cell).</summary>
    public static InventoryItem Completed(InventoryItem it, Shape shape) =>
        shape.FullStack is { } full && shape.MaxStack > 1 && it.Item.StackSize >= shape.MaxStack ? it with { Item = full() } : it;

    /// <summary>The held item went to <see cref="Row"/> as <see cref="Placed"/>; <see cref="Displaced"/> (the one it was
    /// dropped on) is now held. <see cref="Merged"/>: the held stack went onto a stack of its type instead, which is
    /// <see cref="Placed"/> now (same id, larger); <see cref="Rest"/> is what is still held (same id as <see cref="Held"/>),
    /// or null. <see cref="FromRow"/> is the inventory row the held item came from, if picked up from one.</summary>
    public sealed record Placement(byte Row, InventoryItem Placed, InventoryItem Held, InventoryItem? Displaced,
        bool Merged = false, InventoryItem? Rest = null, byte? FromRow = null);

    /// <summary>
    /// Puts the item held on the cursor into inventory <paramref name="row"/> at (x, y). Equipment and other single-item
    /// inventories take it at (0, 0) when its class fits (<see cref="Accepts"/>); grids need it inside the grid. When exactly
    /// one item is in the way it is swapped onto the cursor, unless it is a stack of the held stack's type with room: then
    /// the held stack fills it and the rest stays held. Two or more refuse the move. Returns null with the reason.
    /// </summary>
    /// <remarks>With <paramref name="one"/> (Shift + click, 0x2a's first flag) only one of a held stack goes: into an
    /// empty cell as a new item, or onto a stack of its type with room; the rest stays held.</remarks>
    public Placement? Place(byte row, int x, int y, Func<Item, Shape?> shapeOf, out string why, bool one = false,
        bool expandedAllowed = false, Player? player = null)
    {
        var result = PlaceCore(row, x, y, shapeOf, out why, one, expandedAllowed, player: player);
        if (result is not null) Persist();
        return result;
    }

    // Vendor and quest-reward preflight uses the same placement checks without putting an unpaid item on the cursor.
    public Placement? PreviewPurchase(byte row, int x, int y, Item item, Func<Item, Shape?> shapeOf,
        bool expandedAllowed = false, bool allowDisplace = true) =>
        PlaceCore(row, x, y, shapeOf, out _, expandedAllowed: expandedAllowed, incoming: new InventoryItem(0, 0, 0, item),
            preview: true, allowDisplace: allowDisplace);

    /// <summary>Places a newly acquired item (quest reward or vendor purchase) directly at (<paramref name="x"/>, <paramref name="y"/>)
    /// in <paramref name="row"/> using the same placement, stack-merge and single-item swap rules as <see cref="Place"/>.</summary>
    public Placement? PlaceIncoming(byte row, int x, int y, Item item, Func<Item, Shape?> shapeOf, out string why,
        bool expandedAllowed = false, bool allowDisplace = true)
    {
        var result = PlaceCore(row, x, y, shapeOf, out why, expandedAllowed: expandedAllowed,
            incoming: new InventoryItem(0, 0, 0, item), allowDisplace: allowDisplace);
        if (result is not null) Persist();
        return result;
    }

    /// <summary>The single item that placing the held item at (<paramref name="x"/>, <paramref name="y"/>) in
    /// <paramref name="row"/> would swap out onto the cursor, or null when nothing (or more than one item) is in the way,
    /// or when the move would merge into a stack or split one off.</summary>
    public InventoryItem? DisplacedByPlace(byte row, int x, int y, Func<Item, Shape?> shapeOf, bool one = false)
    {
        lock (_lock)
        {
            // PlaceCore's own checks, changing nothing: what it would swap out, before its stack rules.
            if (PlaceCore(row, x, y, shapeOf, out _, expandedAllowed: true, preview: true) is not { Displaced: { } under, Held: var held }) return null;
            if (one && held.Item.StackSize > 1) return null;
            return under.Item.TypeHash == held.Item.TypeHash && shapeOf(held.Item) is { MaxStack: > 1 } shape
                && under.Item.StackSize < shape.MaxStack ? null : under;
        }
    }

    /// <summary>The single item that clicking the held item onto <paramref name="itemId"/> in <paramref name="row"/>
    /// would swap out onto the cursor, or null when it would not swap a single item.</summary>
    public InventoryItem? DisplacedByMerge(byte row, uint itemId, Func<Item, Shape?> shapeOf, bool one = false)
    {
        lock (_lock)
            return ItemsIn(row).FirstOrDefault(i => i.Id == itemId) is { } target ? DisplacedByPlace(row, target.X, target.Y, shapeOf, one) : null;
    }

    Placement? PlaceCore(byte row, int x, int y, Func<Item, Shape?> shapeOf, out string why, bool one = false, bool expandedAllowed = false,
        InventoryItem? incoming = null, bool preview = false, bool allowDisplace = true, Player? player = null)
    {
        lock (_lock)
        {
            why = "";
            if (row == ExpandedInventory && !expandedAllowed)
            { why = "extra backpack is disabled; existing items can still be removed"; return null; }
            if (row == Cursor || row >= InstanceInventories.PlayerDefaults.Length) { why = "not an inventory to place into"; return null; }
            var cursor = ItemsIn(Cursor);
            if (incoming is null && cursor.Count == 0) { why = "nothing held"; return null; }
            if (incoming is not null && cursor.Count != 0) { why = "something held"; return null; }
            var held = incoming ?? cursor[0];
            if (shapeOf(held.Item) is not { } shape) { why = "unknown item type"; return null; }
            var slot = InstanceInventories.PlayerDefaults[row];
            List<InventoryItem> inTheWay;
            // A one-handed weapon put into an off hand while that side's main hand is empty goes to the main hand.
            if ((row == 3 || row == 15) && OneHand.Contains(shape.Class) && (!_items.TryGetValue((byte)(row - 1), out var main) || main.Count == 0))
                row = (byte)(row - 1);
            if (slot.SingleItem)
            {
                slot = InstanceInventories.PlayerDefaults[row];
                if (!Accepts(row, shape.Class)) { why = $"{shape.Class} does not go in row {row}"; return null; }
                if (HandConflict(row, shape.Class, shapeOf) is { } conflict) { why = conflict; return null; }
                (x, y) = (0, 0);
                inTheWay = [.. ItemsIn(row)];
            }
            else
            {
                if (x < 0 || y < 0 || x + shape.Width > slot.Width || y + shape.Height > slot.Height) { why = "outside the grid"; return null; }
                if (row == Flask && !Accepts(row, shape.Class)) { why = $"{shape.Class} does not go in the flask slots"; return null; }
                if (row == MapDevice && !global::MapDevice.AcceptsItem(held.Item.TypeHash, shape.Class, player)) { why = $"{shape.Class} does not go in the map device"; return null; }
                if (row == MavenInvitations && !AcceptsItem(row, held.Item.TypeHash, shape.Class)) { why = "only Maven invitations go in the Maven invitation slots"; return null; }
                if (row == MemoryLineMaps && !AcceptsItem(row, held.Item.TypeHash, shape.Class)) { why = $"{shape.Class} does not go in the memory line map device"; return null; }
                inTheWay = [.. ItemsIn(row).Where(i => shapeOf(i.Item) is { } s && i.X < x + shape.Width && x < i.X + s.Width && i.Y < y + shape.Height && y < i.Y + s.Height)];
            }
            if (inTheWay.Count > 1) { why = $"{inTheWay.Count} items in the way"; return null; }
            if (inTheWay.Any(i => _offered.ContainsKey(i.Id))) { why = "an item offered for sale is in the way"; return null; }
            bool canMerge = inTheWay is [var mergeTarget] && mergeTarget.Item.TypeHash == held.Item.TypeHash
                && shape.MaxStack > 1 && mergeTarget.Item.StackSize < shape.MaxStack;
            if (!allowDisplace && inTheWay.Count > 0 && !canMerge) { why = "cell is occupied"; return null; }
            byte? fromRow = incoming is null ? _heldFrom?.Row : null;
            if (preview) return new(row, held with { X = (byte)x, Y = (byte)y }, held, inTheWay.FirstOrDefault(), FromRow: fromRow);
            var items = ItemsIn(row);
            int amount = one ? 1 : held.Item.StackSize;
            if (inTheWay is [var under] && under.Item.TypeHash == held.Item.TypeHash && shape.MaxStack > 1 && under.Item.StackSize < shape.MaxStack)
            {
                int moved = Math.Min(amount, shape.MaxStack - under.Item.StackSize);
                var filled = Completed(under with { Item = under.Item with { StackSize = (ushort)(under.Item.StackSize + moved) } }, shape);
                items[items.IndexOf(under)] = filled;
                InventoryItem? rest = null;
                if (moved < held.Item.StackSize)
                {
                    if (incoming is null) cursor[0] = rest = held with { Item = held.Item with { StackSize = (ushort)(held.Item.StackSize - moved) } };
                    else { rest = new InventoryItem(_nextItemId++, 0, 0, held.Item with { StackSize = (ushort)(held.Item.StackSize - moved) }); cursor.Add(rest); _heldFrom = null; }
                }
                else
                {
                    if (incoming is null) cursor.RemoveAt(0);
                    _heldFrom = null;
                }
                return new(row, filled, held, null, Merged: true, rest, fromRow);
            }
            if (amount < held.Item.StackSize)
            {
                // One of the stack into an empty cell, as a new item.
                if (inTheWay.Count > 0) { why = "only one of the stack, and something is in the way"; return null; }
                var single = new InventoryItem(_nextItemId++, (byte)x, (byte)y, held.Item with { StackSize = (ushort)amount });
                items.Add(single);
                var left = cursor[0] = held with { Item = held.Item with { StackSize = (ushort)(held.Item.StackSize - amount) } };
                return new(row, single, held, null, Rest: left, FromRow: fromRow);
            }
            if (incoming is null) cursor.RemoveAt(0);
            InventoryItem? displaced = null;
            if (inTheWay.Count == 1)
            {
                items.Remove(inTheWay[0]);
                displaced = inTheWay[0] with { X = 0, Y = 0 };
                cursor.Add(displaced);
                _heldFrom = (row, inTheWay[0].X, inTheWay[0].Y);
            }
            else _heldFrom = null;
            var placed = incoming is null ? held with { X = (byte)x, Y = (byte)y } : new InventoryItem(_nextItemId++, (byte)x, (byte)y, held.Item);
            items.Add(placed);
            return new(row, placed, held, displaced, FromRow: fromRow);
        }
    }

    /// <summary>
    /// The held item clicked on item <paramref name="itemId"/> in <paramref name="row"/> (C→S 0x4a): <see cref="Place"/> on
    /// that item's cell. A stack of the same type with room takes what fits (with <paramref name="one"/>, one) and the
    /// rest stays held; anything else is swapped (refused with <paramref name="one"/>). Null with the reason; nothing changes.
    /// </summary>
    public Placement? MergeOnto(byte row, uint itemId, Func<Item, Shape?> shapeOf, out string why, bool one = false,
        bool expandedAllowed = false, Player? player = null)
    {
        Placement? result;
        lock (_lock)
        {
            why = "";
            if (!_items.TryGetValue(Cursor, out var cursor) || cursor.Count == 0) { why = "nothing held"; return null; }
            var held = cursor[0];
            if (ItemsIn(row).FirstOrDefault(i => i.Id == itemId) is not { } target) { why = "no such item"; return null; }
            result = PlaceCore(row, target.X, target.Y, shapeOf, out why, one && held.Item.StackSize > 1, expandedAllowed, player: player);
        }
        if (result is not null) Persist();
        return result;
    }

    /// <summary>After the main hand (Weapon1 / Weapon2) was emptied: a one-handed weapon in that side's off hand moves to
    /// the main hand. Returns it (same id, now in the main hand), or null.</summary>
    public InventoryItem? PromoteOffhand(byte mainRow, Func<Item, Shape?> shapeOf)
    {
        var result = PromoteOffhandCore(mainRow, shapeOf);
        if (result is not null) Persist();
        return result;
    }

    InventoryItem? PromoteOffhandCore(byte mainRow, Func<Item, Shape?> shapeOf)
    {
        lock (_lock)
        {
            if (mainRow is not (2 or 14) || ItemsIn(mainRow).Count > 0) return null;
            var off = ItemsIn((byte)(mainRow + 1));
            if (off.Count == 0 || shapeOf(off[0].Item) is not { } s || !OneHand.Contains(s.Class)) return null;
            var moved = off[0] with { X = 0, Y = 0 };
            off.RemoveAt(0);
            ItemsIn(mainRow).Add(moved);
            return moved;
        }
    }

    /// <summary>The row an item is in (not the cursor), or null; null for an item offered for sale too, which nothing may
    /// move or use until the sale ends.</summary>
    public byte? RowOf(uint itemId)
    {
        lock (_lock)
        {
            if (_offered.ContainsKey(itemId)) return null;
            foreach (var (row, items) in _items)
                if (row != Cursor && items.Any(i => i.Id == itemId)) return row;
        }
        return null;
    }

    List<InventoryItem> ItemsIn(byte row) => _items.TryGetValue(row, out var l) ? l : _items[row] = [];

    /// <summary>
    /// Puts the held gem into socket <paramref name="socket"/> of item <paramref name="itemId"/> in <paramref name="row"/>.
    /// A gem already in the socket goes to the cursor. Returns the item as it is now and the gem now held (or null), or
    /// null with the reason.
    /// </summary>
    public (InventoryItem Item, InventoryItem HeldBefore, InventoryItem? HeldNow)? Socket(byte row, uint itemId, int socket,
        Func<Item, Shape?> shapeOf, Func<uint> newId, out string why)
    {
        var result = SocketCore(row, itemId, socket, shapeOf, newId, out why);
        if (result is not null) Persist();
        return result;
    }

    /// <summary>Checks whether <paramref name="incoming"/> can be socketed directly into <paramref name="itemId"/>'s
    /// <paramref name="socket"/> without mutating state (for <c>C→S 0x0089</c> buy/reward direct socketing).</summary>
    public (InventoryItem Item, InventoryItem? Displaced)? PreviewSocketItem(byte row, uint itemId, int socket, Item incoming,
        Func<Item, Shape?> shapeOf, out string why) =>
        SocketCore(row, itemId, socket, shapeOf, () => 0, out why, incoming, preview: true) is { } r ? (r.Item, r.HeldNow) : null;

    /// <summary>Sockets a newly acquired item (quest reward or vendor gem) directly into <paramref name="itemId"/>'s
    /// <paramref name="socket"/>; any gem already in that socket moves onto the empty cursor.</summary>
    public (InventoryItem Item, InventoryItem? Displaced)? SocketItem(byte row, uint itemId, int socket, Item incoming,
        Func<Item, Shape?> shapeOf, Func<uint> newId, out string why)
    {
        var result = SocketCore(row, itemId, socket, shapeOf, newId, out why, incoming);
        if (result is not null) Persist();
        return result is { } r ? (r.Item, r.HeldNow) : null;
    }

    (InventoryItem Item, InventoryItem HeldBefore, InventoryItem? HeldNow)? SocketCore(byte row, uint itemId, int socket,
        Func<Item, Shape?> shapeOf, Func<uint> newId, out string why, Item? incoming = null, bool preview = false)
    {
        lock (_lock)
        {
            why = "";
            var cursor = ItemsIn(Cursor);
            if (incoming is null && cursor.Count == 0) { why = "nothing held"; return null; }
            if (incoming is not null && cursor.Count != 0) { why = "something is held"; return null; }
            var held = incoming is null ? cursor[0] : new InventoryItem(0, 0, 0, incoming);
            var heldShape = shapeOf(held.Item);
            var heldClass = heldShape?.Class;
            var items = ItemsIn(row);
            int at = items.FindIndex(i => i.Id == itemId);
            if (at < 0) { why = "no such item"; return null; }
            if (_offered.ContainsKey(itemId)) { why = "offered for sale"; return null; }
            var item = items[at];
            if (socket < 0 || socket >= item.Item.Sockets.Count) { why = "no such socket"; return null; }
            var sockets = item.Item.Sockets.ToList();
            var colour = sockets[socket].Colour;
            if (colour == SocketColour.Abyss)
            {
                if (heldClass != "AbyssJewel") { why = "an Abyss socket requires an Abyss jewel"; return null; }
            }
            else if (colour == SocketColour.Delve)
            {
                if (heldShape?.IsFossil != true) { why = "a Delve socket requires a Fossil"; return null; }
                if (item.Item.StackSize > 1) { why = "split the resonator stack before socketing a Fossil"; return null; }
                for (int i = 0; i < sockets.Count; i++)
                    if (i != socket && sockets[i].Gem?.TypeHash == held.Item.TypeHash)
                    { why = "cannot socket identical Fossils into the same Resonator"; return null; }
                if (held.Item.StackSize > 1 && sockets[socket].Gem is not null)
                { why = "cannot swap a stack of Fossils into an occupied socket"; return null; }
            }
            else if (heldClass is not ("Active Skill Gem" or "Support Skill Gem"))
            {
                why = "the held item is not a gem";
                return null;
            }
            var old = sockets[socket].Gem;
            if (preview) return (item, held, old is null ? null : new InventoryItem(0, 0, 0, old));
            bool splitStack = incoming is null && colour == SocketColour.Delve && held.Item.StackSize > 1;
            sockets[socket] = sockets[socket] with { Gem = splitStack ? held.Item with { StackSize = 1 } : held.Item };
            items[at] = item with { Item = item.Item with { Sockets = sockets } };
            _gemSourceLifetimes.RetireSocket(item.Id, socket);
            InventoryItem? now;
            if (splitStack)
            {
                now = held with { Item = held.Item with { StackSize = (ushort)(held.Item.StackSize - 1) } };
                cursor[0] = now;
            }
            else
            {
                if (incoming is null) cursor.RemoveAt(0);
                _heldFrom = null;
                now = old is null ? null : new InventoryItem(newId(), 0, 0, old);
                if (now is not null) cursor.Add(now);
            }
            return (items[at], held, now);
        }
    }

    /// <summary>Takes the gem out of socket <paramref name="socket"/> onto the (empty) cursor. Returns the item as it is
    /// now and the gem held, or null with the reason.</summary>
    public (InventoryItem Item, InventoryItem Held)? Unsocket(byte row, uint itemId, int socket, Func<uint> newId, out string why)
    {
        var result = UnsocketCore(row, itemId, socket, newId, out why);
        if (result is not null) Persist();
        return result;
    }

    (InventoryItem Item, InventoryItem Held)? UnsocketCore(byte row, uint itemId, int socket, Func<uint> newId, out string why)
    {
        lock (_lock)
        {
            why = "";
            var cursor = ItemsIn(Cursor);
            if (cursor.Count > 0) { why = "something is held"; return null; }
            var items = ItemsIn(row);
            int at = items.FindIndex(i => i.Id == itemId);
            if (at < 0) { why = "no such item"; return null; }
            if (_offered.ContainsKey(itemId)) { why = "offered for sale"; return null; }
            var item = items[at];
            if (socket < 0 || socket >= item.Item.Sockets.Count || item.Item.Sockets[socket].Gem is not { } gem) { why = "no gem in that socket"; return null; }
            var sockets = item.Item.Sockets.ToList();
            sockets[socket] = sockets[socket] with { Gem = null };
            items[at] = item with { Item = item.Item with { Sockets = sockets } };
            _gemSourceLifetimes.RetireSocket(item.Id, socket);
            _heldFrom = null;
            var held = new InventoryItem(newId(), 0, 0, gem);
            cursor.Add(held);
            return (items[at], held);
        }
    }

    /// <summary>The gems socketed in the items of the visible equipment rows, in row and socket order.</summary>
    public IReadOnlyList<Item> SocketedGems() => [.. SocketedGemsByItem().Select(g => g.Gem)];

    /// <summary><see cref="SocketedGems"/> with the id of the item each gem is in.</summary>
    public IReadOnlyList<(uint ItemId, Item Gem)> SocketedGemsByItem() => [.. SocketedGemPlaces().Select(g => (g.ItemId, g.Gem))];

    // Immutable item records from ONE inventory read, including attribute gear and all active socket hosts.
    public IReadOnlyList<(byte Row, InventoryItem Item)> EquipmentSnapshot()
    {
        // Asked for many times in one stat-sources lookup: cache the immutable array while no inventory mutation occurs.
        lock (_lock)
        {
            long version = Interlocked.Read(ref _mutations);
            if (_equipmentSnapshot is not null && _equipmentSnapshotMutation == version) return _equipmentSnapshot;
            int count = 0;
            foreach (byte row in ItemModStats.EquipmentRows)
                if (_items.TryGetValue(row, out var items)) count += items.Count;
            var snapshot = new (byte Row, InventoryItem Item)[count];
            int at = 0;
            foreach (byte row in ItemModStats.EquipmentRows)
                if (_items.TryGetValue(row, out var items))
                    foreach (var item in items) snapshot[at++] = (row, item);
            _equipmentSnapshot = snapshot;
            _equipmentSnapshotMutation = version;
            return snapshot;
        }
    }

    /// <summary>Persists an id for the same physical gem selected by the bind request.</summary>
    public Guid? EnsureSkillBarGemId(uint hostId, int socket, Item selected)
    {
        Guid id;
        lock (_lock)
        {
            var hostRow = ItemModStats.EquipmentRows.FirstOrDefault(row =>
                _items.TryGetValue(row, out var list) && list.Any(item => item.Id == hostId));
            if (hostRow == MainInventory) return null;
            var items = _items[hostRow];
            int index = items.FindIndex(item => item.Id == hostId);
            var host = items[index];
            if (socket < 0 || socket >= host.Item.Sockets.Count || host.Item.Sockets[socket].Gem is not { } gem
                || !ReferenceEquals(GemIdentity(selected), GemIdentity(gem))) return null;
            if (gem.SkillBarGemId is Guid saved) return saved;
            id = Guid.NewGuid();
            var sockets = host.Item.Sockets.ToArray();
            sockets[socket] = sockets[socket] with { Gem = PreserveGemIdentity(gem, gem with { SkillBarGemId = id }) };
            items[index] = host with { Item = host.Item with { Sockets = sockets } };
            Interlocked.Increment(ref _mutations);
        }
        Persist();
        return id;
    }

    public Item? SocketHost(uint id)
    {
        lock (_lock) return _items.Values.SelectMany(v => v).FirstOrDefault(i => i.Id == id)?.Item;
    }

    /// <summary><see cref="SocketedGems"/> with the row and id of the item each gem is in and its socket.</summary>
    public IReadOnlyList<(byte Row, uint ItemId, int Socket, Item Gem)> SocketedGemPlaces()
    {
        lock (_lock)
            return [.. VisibleSlots.Keys.Order().SelectMany(r => (_items.TryGetValue(r, out var l) ? l : []).Select(i => (Row: r, Item: i)))
                .SelectMany(x => x.Item.Item.Sockets.Select((s, socket) => (x.Row, x.Item.Id, socket, s.Gem, s.Colour)).Where(g => g.Colour is not (SocketColour.Abyss or SocketColour.Delve) && g.Gem is not null)
                    .Select(g => (g.Row, g.Id, g.socket, g.Gem!)))];
    }

    /// <summary>Whether any gem socketed in <see cref="GemExperienceRows"/> matches <paramref name="predicate"/>, without allocating.</summary>
    public bool AnySocketedGem(Func<uint, Item, bool> predicate) =>
        AnySocketedGem(predicate, static (match, id, _, socket) => match(id, socket.Gem!));

    /// <summary>Checks each socket with the owning item during the same inventory read.</summary>
    public bool AnySocketedGem(Func<uint, Item, ItemSocket, bool> predicate) =>
        AnySocketedGem(predicate, static (match, id, item, socket) => match(id, item, socket));

    bool AnySocketedGem<T>(T predicate, Func<T, uint, Item, ItemSocket, bool> matches)
    {
        lock (_lock)
            foreach (var row in GemExperienceRows)
            {
                if (!_items.TryGetValue(row, out var items)) continue;
                for (int at = 0; at < items.Count; at++)
                {
                    if (items[at].Item.Sockets is not { Count: > 0 } held) continue;
                    for (int socket = 0; socket < held.Count; socket++)
                        if (held[socket] is { Colour: not SocketColour.Abyss, Gem: not null } entry
                            && matches(predicate, items[at].Id, items[at].Item, entry))
                            return true;
                }
            }
        return false;
    }

    /// <summary>
    /// Changes the gems socketed in the <see cref="GemExperienceRows"/> items in one step (a gem moved meanwhile is never
    /// changed with another's values), without saving it now (like <see cref="ChangeUnsaved"/>). <paramref name="change"/>
    /// gets the item's id and the gem and returns the changed gem, or null to keep it. Returns each changed item once, as
    /// it is now, with its row and each changed gem before and after. Only for persistent values of a socketed gem, its
    /// experience.
    /// </summary>
    public IReadOnlyList<(byte Row, InventoryItem Item, IReadOnlyList<(Item Before, Item After)> Gems)> ChangeSocketedGems(
        Func<uint, Item, Item?> change) =>
        ChangeSocketedGems((id, _, socket) => change(id, socket.Gem!));

    /// <summary>Updates socketed gems using the owning item and original socket under the inventory lock.</summary>
    public IReadOnlyList<(byte Row, InventoryItem Item, IReadOnlyList<(Item Before, Item After)> Gems)> ChangeSocketedGems(
        Func<uint, Item, ItemSocket, Item?> change)
    {
        var changed = new List<(byte, InventoryItem, IReadOnlyList<(Item, Item)>)>();
        lock (_lock)
            foreach (var row in GemExperienceRows)
            {
                if (!_items.TryGetValue(row, out var items)) continue;
                for (int at = 0; at < items.Count; at++)
                {
                    if (items[at].Item.Sockets is not { Count: > 0 } held) continue;
                    var sockets = held.ToArray();
                    var gems = new List<(Item, Item)>();
                    for (int socket = 0; socket < sockets.Length; socket++)
                        if (sockets[socket] is { Colour: not (SocketColour.Abyss or SocketColour.Delve), Gem: { } gem } entry
                            && change(items[at].Id, items[at].Item, entry) is { } after)
                        {
                            after = PreserveGemIdentity(gem, after);
                            gems.Add((gem, after));
                            sockets[socket] = sockets[socket] with { Gem = after };
                        }
                    if (gems.Count == 0) continue;
                    _unsaved = true;
                    Interlocked.Increment(ref _mutations);
                    items[at] = items[at] with { Item = items[at].Item with { Sockets = sockets } };
                    changed.Add((row, items[at], gems));
                }
            }
        return changed;
    }

    static readonly string[] OneHand = ["Claw", "Dagger", "Wand", "One Hand Sword", "Thrusting One Hand Sword", "One Hand Axe", "One Hand Mace", "Sceptre", "Rune Dagger"];
    static readonly string[] TwoHand = ["Bow", "Staff", "Two Hand Sword", "Two Hand Axe", "Two Hand Mace", "Warstaff", "FishingRod"];

    /// <summary>Whether an item class goes into an equipment row (Inventories.dat rows 1-11, 14, 15, 102) or the map device's
    /// slots (row 13: maps, fragments, MiscMapItems and vault keys). Other single-item
    /// rows (crafting benches …) take anything.</summary>
    public static bool Accepts(byte row, string itemClass) => row switch
    {
        1 => itemClass == "Body Armour",
        2 or 14 => OneHand.Contains(itemClass) || TwoHand.Contains(itemClass),
        3 or 15 => OneHand.Contains(itemClass) || itemClass is "Shield" or "Quiver",
        4 => itemClass == "Helmet",
        5 => itemClass == "Amulet",
        6 or 7 or 102 => itemClass == "Ring",
        8 => itemClass == "Gloves",
        9 => itemClass == "Boots",
        10 => itemClass == "Belt",
        MapDevice => global::MapDevice.AcceptsClass(itemClass),
        MavenInvitations => itemClass is global::MapDevice.MiscClass or "QuestItem",
        MemoryLineMaps => global::MapDevice.AcceptsMemoryLineClass(itemClass),
        Flask => itemClass is "LifeFlask" or "ManaFlask" or "HybridFlask" or "UtilityFlask" or "UtilityFlaskCritical" or "Tincture",
        _ => true,
    };

    /// <summary>Item-specific placement check for grid receptacles that restrict beyond ItemClass (Map1, MavenInvitations, MemoryLineMaps).</summary>
    public static bool AcceptsItem(byte row, uint typeHash, string itemClass, Player? player = null) => row switch
    {
        MapDevice => global::MapDevice.AcceptsItem(typeHash, itemClass, player),
        MavenInvitations => global::MapDevice.IsMavenInvitation(typeHash),
        MemoryLineMaps => global::MapDevice.AcceptsMemoryLineClass(itemClass),
        _ => Accepts(row, itemClass),
    };

    /// <summary>
    /// Why an item of this class can't go into a hand row next to what the other hand of the same weapon set holds, or
    /// null: a bow goes with an empty off hand or a quiver, another two-handed weapon only with an empty off hand; a quiver
    /// goes with an empty main hand or a bow, never with a one-handed weapon; a shield or a one-handed weapon in the off hand
    /// not with a two-handed weapon (a bow included); a wand only dual-wields with another wand, not a melee weapon. Call
    /// under the lock.
    /// </summary>
    string? HandConflict(byte row, string itemClass, Func<Item, Shape?> shapeOf)
    {
        if (row is not (Weapon1 or Offhand1 or Weapon2 or Offhand2)) return null;
        bool main = row is Weapon1 or Weapon2;
        byte other = main ? (byte)(row + 1) : (byte)(row - 1);
        string? otherClass = _items.TryGetValue(other, out var l) && l.Count > 0 ? shapeOf(l[0].Item)?.Class : null;
        if (main)
        {
            if (itemClass == "Bow") return otherClass is null or "Quiver" ? null : "a bow goes only with an empty off hand or a quiver";
            if (TwoHand.Contains(itemClass)) return otherClass is null ? null : "a two-handed weapon needs an empty off hand";
            if (otherClass == "Quiver") return "a quiver goes only with a bow (or an empty main hand)";
            if (otherClass is not null && OneHand.Contains(otherClass) && (itemClass == "Wand") != (otherClass == "Wand"))
                return "a wand cannot be dual-wielded with a melee weapon";
            return null;
        }
        if (itemClass == "Quiver") return otherClass is null or "Bow" ? null : "a quiver goes only with a bow or an empty main hand";
        if (otherClass is not null && TwoHand.Contains(otherClass)) return $"the main hand holds a two-handed weapon ({otherClass})";
        if (otherClass is not null && OneHand.Contains(itemClass) && OneHand.Contains(otherClass) && (itemClass == "Wand") != (otherClass == "Wand"))
            return "a wand cannot be dual-wielded with a melee weapon";
        return null;
    }

    /// <summary>
    /// Finds the first empty compatible slot among <paramref name="candidateRows"/> for an item of <paramref name="shape"/>:
    /// a single-item row must be empty, accept its class (<see cref="Accepts"/>) and have no <see cref="HandConflict"/>; a
    /// grid row (such as <see cref="Flask"/>) must accept its class and have a free cell (<see cref="FreeCell"/>). Returns
    /// null when no candidate row can take it without displacing anything.
    /// </summary>
    public (byte Row, (byte X, byte Y) Cell)? FindEmptySlot(IEnumerable<byte> candidateRows, Shape shape, Func<Item, Shape?> shapeOf)
    {
        lock (_lock)
        {
            foreach (byte row in candidateRows)
            {
                if (row == Cursor || row >= InstanceInventories.PlayerDefaults.Length) continue;
                if (!Accepts(row, shape.Class)) continue;
                if (InstanceInventories.PlayerDefaults[row].SingleItem)
                {
                    if (ItemsIn(row).Count != 0) continue;
                    if (HandConflict(row, shape.Class, shapeOf) is not null) continue;
                    return (row, (0, 0));
                }
                if (FreeCellCore(row, shape.Width, shape.Height, shapeOf) is { } cell)
                    return (row, cell);
            }
            return null;
        }
    }

    static readonly byte[] AutoEquipRows = [BodyArmour, Weapon1, Offhand1, Helmet, 5, 6, 7, Gloves, 9, 10];

    /// <summary>First empty compatible active equipment or flask slot for an identified picked-up item, or null.</summary>
    public (byte Row, byte X, byte Y)? FindAutoEquipSlot(Item item, Func<Item, Shape?> shapeOf)
    {
        if (!item.Identified || shapeOf(item) is not { } shape) return null;
        lock (_lock)
        {
            foreach (byte row in AutoEquipRows)
                if (Accepts(row, shape.Class) && ItemsIn(row).Count == 0 && HandConflict(row, shape.Class, shapeOf) is null)
                    return (row, 0, 0);
            if (Accepts(Flask, shape.Class) && FreeCell(Flask, shape.Width, shape.Height, shapeOf) is { } flaskCell)
                return (Flask, flaskCell.X, flaskCell.Y);
        }
        return null;
    }

    /// <summary>Inventories.dat column 7 by row: the slot the player's model shows an equipped item in (17 = none).</summary>
    static readonly Dictionary<byte, byte> VisibleSlots = new()
    {
        [1] = 2, [2] = 0, [3] = 1, [4] = 4, [5] = 11, [6] = 8, [7] = 9, [8] = 5, [9] = 6, [10] = 10,
    };

    /// <summary>The rows whose socketed gems gain experience: the visible equipment rows and the second weapon set.</summary>
    static readonly byte[] GemExperienceRows = [.. VisibleSlots.Keys.Order(), Weapon2, Offhand2];

    public static bool ShowsOnModel(byte row) => VisibleSlots.ContainsKey(row);

    /// <summary>What the player's model wears: every visible equipment slot (Weapon1, Offhand1, armour …), with its item
    /// or empty, so an unequipped item disappears from the model.</summary>
    public IReadOnlyList<VisibleItem> Equipped(Func<Item, VisibleItem?> look)
    {
        lock (_lock)
            return [.. VisibleSlots.OrderBy(e => e.Value).Select(e =>
                _items.TryGetValue(e.Key, out var l) && l.Count > 0 && look(l[0].Item) is { } v
                    ? v with { Slot = e.Value, SkinVisual = l[0].Item.SkinTransferSource is { } source
                        && look(source) is { } donor ? donor.VisualIdentity : v.SkinVisual }
                    : VisibleItem.Empty(e.Value))];
    }

    /// <summary>Which weapon set is in the hands: 0 the first, 1 the second (the 0x21 / 0xde flag the panel's I / II tab
    /// shows). Kept for the character while the server runs (C→S 0xdd, 13-items.md "Weapon swap").</summary>
    public byte WeaponSet { get; set; }

    /// <summary>The weapon swap: the hands' rows (Weapon1, Offhand1) and the other set's (Weapon2, Offhand2, rows 14, 15)
    /// trade their items, keeping ids and cells, as the client shows the active set in the hands' rows. Saved.</summary>
    public void SwapWeaponSets()
    {
        lock (_lock)
            foreach (var (hand, other) in new[] { (Weapon1, Weapon2), (Offhand1, Offhand2) })
            {
                var inHand = _items.TryGetValue(hand, out var a) ? a : [];
                var away = _items.TryGetValue(other, out var b) ? b : [];
                _items[hand] = away;
                _items[other] = inHand;
            }
        Persist();
    }

    /// <summary>A free cell for an item of this size in a grid inventory, row by row; null when it is full.</summary>
    public (byte X, byte Y)? FreeCell(byte row, byte width, byte height, Func<Item, Shape?> shapeOf)
    {
        lock (_lock) return FreeCellCore(row, width, height, shapeOf);
    }

    (byte X, byte Y)? FreeCellCore(byte row, byte width, byte height, Func<Item, Shape?> shapeOf) =>
        FreeCellIn(row, _items.TryGetValue(row, out var l) ? l : [], width, height, shapeOf);

    static (byte X, byte Y)? FreeCellIn(byte row, List<InventoryItem> items, byte width, byte height, Func<Item, Shape?> shapeOf)
    {
        var slot = InstanceInventories.PlayerDefaults[row];
        for (int x = 0; x + width <= slot.Width; x++)
            for (int y = 0; y + height <= slot.Height; y++)
                if (!items.Any(i => shapeOf(i.Item) is { } s && i.X < x + width && x < i.X + s.Width && i.Y < y + height && y < i.Y + s.Height))
                    return ((byte)x, (byte)y);
        return null;
    }

    /// <summary>
    /// Splits <paramref name="amount"/> off the stack <paramref name="itemId"/> in <paramref name="row"/> onto the empty
    /// cursor, as a new item, in one save. Returns the stack as it is now (same id) and the held part, or null with the
    /// reason (something held, no such stack, an amount of 0 or the whole stack).
    /// </summary>
    public (InventoryItem Stack, InventoryItem Held)? Split(byte row, uint itemId, int amount, out string why)
    {
        var result = SplitCore(row, itemId, amount, out why);
        if (result is not null) Persist();
        return result;
    }

    (InventoryItem Stack, InventoryItem Held)? SplitCore(byte row, uint itemId, int amount, out string why)
    {
        lock (_lock)
        {
            why = "";
            if (row == Cursor) { why = "the cursor"; return null; }
            var cursor = ItemsIn(Cursor);
            if (cursor.Count > 0) { why = "something is held"; return null; }
            var items = ItemsIn(row);
            int at = items.FindIndex(i => i.Id == itemId);
            if (at < 0) { why = "no such item"; return null; }
            if (_offered.ContainsKey(itemId)) { why = "offered for sale"; return null; }
            var stack = items[at];
            if (amount < 1 || amount >= stack.Item.StackSize) { why = $"cannot split {amount} off a stack of {stack.Item.StackSize}"; return null; }
            items[at] = stack with { Item = stack.Item with { StackSize = (ushort)(stack.Item.StackSize - amount) } };
            _heldFrom = (row, stack.X, stack.Y);
            var held = new InventoryItem(_nextItemId++, 0, 0, stack.Item with { StackSize = (ushort)amount });
            cursor.Add(held);
            return (items[at], held);
        }
    }

    /// <summary>Replaces an item with a changed copy (same id and cell) in one save; null when it is not there.</summary>
    public InventoryItem? Change(byte row, uint itemId, Func<Item, Item> change)
    {
        var result = ChangeUnsaved(row, itemId, change);
        if (result is not null) Persist();
        return result;
    }

    /// <summary>
    /// <see cref="Change"/> without saving it now: for flask charges from kills, which come by the hundred per second. It
    /// is saved with the character's next save, or by <see cref="SaveUnsaved()"/> within
    /// <see cref="CharacterSaves.ExperienceSaveInterval"/>; a crash loses at most those seconds of charges. Never for an
    /// item's existence, place or mods.
    /// </summary>
    public InventoryItem? ChangeUnsaved(byte row, uint itemId, Func<Item, Item> change)
    {
        lock (_lock)
        {
            var items = ItemsIn(row);
            int at = items.FindIndex(i => i.Id == itemId);
            if (at < 0) return null;
            _unsaved = true;
            Interlocked.Increment(ref _mutations);
            return items[at] = items[at] with { Item = change(items[at].Item) };
        }
    }

    /// <summary>Saves what <see cref="ChangeUnsaved"/> changed, if anything.</summary>
    public void SaveUnsaved()
    {
        bool unsaved;
        lock (_lock) unsaved = _unsaved;
        if (unsaved && !_stale) Persist();
    }

    /// <summary>Saves every loaded character's unsaved changes (the Gateway's timer, its shutdown). One that can't be
    /// saved doesn't stop the others.</summary>
    public static void SaveAllUnsaved()
    {
        foreach (var (character, items) in ByCharacter)
            try { items.SaveUnsaved(); }
            catch (Exception e) when (e is IOException or InvalidOperationException) { Console.WriteLine($"[saves] items of {character} not saved yet: {e.Message}"); }
    }

    /// <summary>Identifies every unidentified item with mods in a row (an NPC's Identify Items) in one save. Returns them
    /// as they are now (same ids and cells).</summary>
    public IReadOnlyList<InventoryItem> IdentifyAll(byte row)
    {
        var identified = new List<InventoryItem>();
        lock (_lock)
        {
            var items = ItemsIn(row);
            for (int i = 0; i < items.Count; i++)
                if (!items[i].Item.Identified && items[i].Item.Components.Contains("Mods"))
                    identified.Add(items[i] = items[i] with { Item = items[i].Item with { Identified = true } });
        }
        if (identified.Count > 0) Persist();
        return identified;
    }

    /// <summary>Sets the size of a stack (same id), or removes it at 0, in one save. Returns it as it is now (null when
    /// removed or not there).</summary>
    public InventoryItem? SetStackSize(byte row, uint itemId, int size)
    {
        InventoryItem? result = null;
        lock (_lock)
        {
            var items = ItemsIn(row);
            int at = items.FindIndex(i => i.Id == itemId);
            if (at < 0) return null;
            if (size <= 0) items.RemoveAt(at);
            else result = items[at] = items[at] with { Item = items[at].Item with { StackSize = (ushort)size } };
        }
        Persist();
        return result;
    }

    /// <summary>What <see cref="AddStacked"/> did: stacks that grew (same ids), new items, and how many did not fit.</summary>
    public sealed record Stacking(IReadOnlyList<InventoryItem> Changed, IReadOnlyList<InventoryItem> Added, int Left)
    {
        /// <summary>For the inventory's 0xbf: the grown stacks are removed and added again, the new items added.</summary>
        public IReadOnlyList<uint> RemovedIds => [.. Changed.Select(c => c.Id)];
        public IReadOnlyList<InventoryItem> AddedItems => [.. Changed, .. Added];
        public bool Any => Changed.Count + Added.Count > 0;
    }

    /// <summary>
    /// Puts <paramref name="item"/> (a stack of <see cref="Item.StackSize"/>) into the grid <paramref name="row"/>: first
    /// onto the stacks of its type that have room (fullest first), then as new stacks of at most its max size in free
    /// cells, in one save. What does not fit is <see cref="Stacking.Left"/>.
    /// </summary>
    public Stacking AddStacked(byte row, Item item, Func<Item, Shape?> shapeOf)
    {
        Stacking result;
        lock (_lock) result = StackInto(row, ItemsIn(row), item, shapeOf, ref _nextItemId);
        if (result.Any) Persist();
        return result;
    }

    /// <summary>Whether all of <paramref name="item"/> would fit into <paramref name="row"/> (<see cref="AddStacked"/>),
    /// changing nothing.</summary>
    public bool Fits(byte row, Item item, Func<Item, Shape?> shapeOf)
    {
        lock (_lock)
        {
            uint id = _nextItemId;
            return StackInto(row, [.. ItemsIn(row)], item, shapeOf, ref id).Left == 0;
        }
    }

    /// <summary>Stacks across the requested grids before using their empty cells. A preview changes nothing.</summary>
    public IReadOnlyDictionary<byte, Stacking> StackAcross(IReadOnlyList<byte> rows, Item item,
        Func<Item, Shape?> shapeOf, bool preview = false)
    {
        Dictionary<byte, Stacking> result;
        lock (_lock)
        {
            var grids = rows.Distinct().ToDictionary(row => row, row => ItemsIn(row).ToList());
            var before = grids.ToDictionary(p => p.Key, p => p.Value.ToDictionary(i => i.Id));
            uint next = _nextItemId;
            int left = item.StackSize;
            foreach (bool stacksOnly in new[] { true, false })
                foreach (var (row, grid) in grids)
                {
                    if (left == 0) break;
                    left = StackInto(row, grid, item with { StackSize = (ushort)left }, shapeOf, ref next, stacksOnly).Left;
                }
            result = grids.ToDictionary(p => p.Key, p => new Stacking(
                p.Value.Where(i => before[p.Key].TryGetValue(i.Id, out var old) && old != i).ToArray(),
                p.Value.Where(i => !before[p.Key].ContainsKey(i.Id)).ToArray(), left));
            if (!preview)
            {
                foreach (var (row, grid) in grids) _items[row] = grid;
                _nextItemId = next;
            }
        }
        if (!preview && result.Values.Any(s => s.Any)) Persist();
        return result;
    }

    Stacking StackInto(byte row, List<InventoryItem> items, Item item, Func<Item, Shape?> shapeOf, ref uint nextId,
        bool stacksOnly = false)
    {
        var shape = shapeOf(item) ?? new(1, 1, "");
        int max = Math.Max((int)shape.MaxStack, 1), left = item.StackSize;
        var changed = new List<InventoryItem>();
        var added = new List<InventoryItem>();
        if (max > 1)
            foreach (var s in items.Where(i => i.Item.TypeHash == item.TypeHash && i.Item.StackSize < max).OrderByDescending(i => i.Item.StackSize).ToList())
            {
                if (left == 0) break;
                int take = Math.Min(left, max - s.Item.StackSize);
                left -= take;
                var grown = Completed(s with { Item = s.Item with { StackSize = (ushort)(s.Item.StackSize + take) } }, shape);
                items[items.IndexOf(s)] = grown;
                changed.Add(grown);
            }
        while (!stacksOnly && left > 0 && FreeCellIn(row, items, shape.Width, shape.Height, shapeOf) is { } cell)
        {
            int take = Math.Min(left, max);
            left -= take;
            var placed = Completed(new InventoryItem(nextId++, cell.X, cell.Y, item with { StackSize = (ushort)take }), shape);
            items.Add(placed);
            added.Add(placed);
        }
        return new(changed, added, left);
    }

    /// <summary>A fresh id for an item that enters the inventories (picked up from the ground, out of a socket).</summary>
    public uint NewItemId()
    {
        lock (_lock) return _nextItemId++;
    }

    /// <summary>
    /// Picks an item up from inventory <paramref name="row"/> onto the empty cursor, keeping its id, in one step and one
    /// save, so a crash at any moment leaves the item in exactly one place. Null (nothing changes) when the cursor
    /// already holds something or the item is not in that row.
    /// </summary>
    public InventoryItem? PickUp(byte row, uint itemId)
    {
        var result = PickUpCore(row, itemId);
        if (result is not null) Persist();
        return result;
    }

    InventoryItem? PickUpCore(byte row, uint itemId)
    {
        lock (_lock)
        {
            if (row == Cursor || ItemsIn(Cursor).Count > 0 || RemoveCore(row, itemId) is not { } it) return null;
            _heldFrom = (row, it.X, it.Y);   // where the held item goes back to as a sale imprint (Offer)
            var held = it with { X = 0, Y = 0 };
            ItemsIn(Cursor).Add(held);
            return held;
        }
    }

    /// <summary>Takes an item out of an inventory; null when it is not there.</summary>
    public InventoryItem? Remove(byte row, uint itemId)
    {
        var result = RemoveCore(row, itemId);
        if (result is not null) Persist();
        return result;
    }

    InventoryItem? RemoveCore(byte row, uint itemId)
    {
        lock (_lock)
        {
            if (_offered.ContainsKey(itemId)) return null;   // stays where it is until the sale ends (Sell, TakeBack)
            if (!_items.TryGetValue(row, out var l) || l.FindIndex(i => i.Id == itemId) is var at && at < 0) return null;
            var it = l[at];
            l.RemoveAt(at);
            if (row == Cursor) _heldFrom = null;
            return it;
        }
    }

    /// <summary>What <see cref="ConsumeOne"/> did: the stack left in place (same id), or null when the last one was taken.</summary>
    public sealed record Consumption(InventoryItem? Left, bool Removed);

    /// <summary>Takes one item from the stack with this id (an Orb of Regret's use), in one save.</summary>
    public Consumption? ConsumeOne(byte row, uint itemId)
    {
        Consumption result;
        lock (_lock)
        {
            var list = ItemsIn(row);
            int at = list.FindIndex(i => i.Id == itemId);
            if (at < 0 || _offered.ContainsKey(itemId)) return null;
            var it = list[at];
            if (it.Item.StackSize > 1)
            {
                var left = it with { Item = it.Item with { StackSize = (ushort)(it.Item.StackSize - 1) } };
                list[at] = left;
                result = new(left, false);
            }
            else
            {
                list.RemoveAt(at);
                result = new(null, true);
            }
        }
        Persist();
        return result;
    }

    /// <summary>What <see cref="Pay"/> changed in the row: emptied stacks (ids), and stacks left smaller (same ids).</summary>
    public sealed record Payment(IReadOnlyList<uint> Removed, IReadOnlyList<InventoryItem> Changed);

    /// <summary>
    /// Takes <paramref name="amount"/> of the item type <paramref name="typeHash"/> from the stacks in <paramref name="row"/>
    /// (smallest stacks first), in one step and one save. Null (nothing changes) when the row holds fewer.
    /// </summary>
    /// <summary>How many of an item type the row's stacks hold (not counting items offered for sale).</summary>
    public int Count(byte row, uint typeHash)
    {
        lock (_lock) return ItemsIn(row).Where(i => i.Item.TypeHash == typeHash && !_offered.ContainsKey(i.Id)).Sum(i => (int)i.Item.StackSize);
    }

    public Payment? Pay(byte row, uint typeHash, int amount)
    {
        Payment payment;
        lock (_lock)
        {
            var stacks = ItemsIn(row).Where(i => i.Item.TypeHash == typeHash && !_offered.ContainsKey(i.Id)).OrderBy(i => i.Item.StackSize).ToList();
            if (stacks.Sum(i => (int)i.Item.StackSize) < amount) return null;
            var removed = new List<uint>();
            var changed = new List<InventoryItem>();
            var list = ItemsIn(row);
            foreach (var s in stacks)
            {
                if (amount <= 0) break;
                int take = Math.Min(amount, s.Item.StackSize);
                amount -= take;
                int at = list.FindIndex(i => i.Id == s.Id);
                if (take == s.Item.StackSize) { list.RemoveAt(at); removed.Add(s.Id); }
                else { list[at] = s with { Item = s.Item with { StackSize = (ushort)(s.Item.StackSize - take) } }; changed.Add(list[at]); }
            }
            payment = new(removed, changed);
        }
        Persist();
        return payment;
    }

    /// <summary>The next revision number of an inventory's contents (sent with each S→C 0xbf).</summary>
    public uint NextRevision(byte row)
    {
        uint revision;
        lock (_lock) revision = _revisions[row] = _revisions.GetValueOrDefault(row) + 1;
        Changed?.Invoke();
        return revision;
    }

    /// <summary>An inventory's contents changed (a new revision was taken for the client).</summary>
    public Action? Changed;

    private static readonly ConcurrentDictionary<string, int> ClassRows = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The character's class (Characters.dat row), for its start kit: set when it connects, before its items
    /// are first used. Items kept in memory without a save file belonged to a deleted character of the same name (Auth
    /// sets the save aside, and every character's items are saved as soon as they exist), so they are dropped.</summary>
    public static void SetClass(string character, int characterRow)
    {
        ClassRows[character] = characterRow;
        if (CharacterSaves.Load(character) is not null) return;
        CharacterSaves.ForgetExperience(character);   // a deleted character's, if any
        if (ByCharacter.TryRemove(character, out var gone))
        {
            gone._stale = true;
            gone._gemSourceLifetimes.RetireOwner();
            Console.WriteLine($"[gateway] {character}: no save any more (deleted?), items start over");
        }
    }

    /// <summary>Forgets the character's loaded items: the next use loads them from the database (tests: a restart).</summary>
    public static void Forget(string character)
    {
        if (ByCharacter.TryRemove(character, out var old)) old._gemSourceLifetimes.RetireOwner();
    }

    // A confirmed build replacement retires old references, including the periodic unsaved-items writer.
    public static void Invalidate(string character)
    {
        if (ByCharacter.TryRemove(character, out var old))
        {
            old._stale = true;
            old._gemSourceLifetimes.RetireOwner();
        }
    }

    /// <summary>The physical shared marker for a source in this loaded inventory, or null after this owner was retired.
    /// Taking the inventory lock makes capture linearizable with the accepted socket writes below.</summary>
    public GemSourceLifetimes.SourceMarker? CaptureGemSourceLifetime(GemSkillIds.Gem key)
    {
        lock (_lock) return _gemSourceLifetimes.Capture(key);
    }

    /// <summary>The loaded inventory currently registered for a character, without constructing or loading another one.</summary>
    public static PlayerInventories? Current(string character) =>
        ByCharacter.TryGetValue(character, out var inventory) ? inventory : null;

    /// <summary>Claims the current saved activation of one source. Takes only the neutral store lock.</summary>
    public bool ClaimGemActivation(string saveNamespace, ushort skill,
        GemSourceLifetimes.SourceMarker marker, Guid owner) =>
        _gemSourceLifetimes.ClaimActivation(saveNamespace, skill, marker, owner);

    /// <summary>Looks up one source's activation writer using only the neutral store lock.</summary>
    public GemSourceLifetimes.SourceStore.ActivationOwner? GemActivationWriter(
        string saveNamespace, ushort skill) =>
        _gemSourceLifetimes.ActivationWriter(saveNamespace, skill);

    /// <summary>The character's items: as saved, or the class's start kit (saved at once) when it has no save. A saved item
    /// the game data doesn't know stops the load (<see cref="UnknownSavedItemException"/>) and the save stays as it is.</summary>
    public static PlayerInventories For(string character, ItemCatalog catalog, string folder = "data") =>
        ByCharacter.GetOrAdd(character, name =>
        {
            var saved = FromSave(name, catalog);
            var inv = saved ?? StartKit(catalog, folder, ClassRows.GetValueOrDefault(name, 1));
            inv._owner = name;
            inv._mods = catalog.Mods;
            if (saved is null) Saves.Atomically(() =>
            {
                inv.Persist();
                if (inv.newTutorial is { } tutorial) CharacterSaves.SetTutorial(name, tutorial);
            });
            return inv;
        });

    /// <summary>The character's saved items (ids and cells kept), or null without a save.</summary>
    static PlayerInventories? FromSave(string character, ItemCatalog catalog)
    {
        if (CharacterSaves.Load(character) is not { } save) return null;
        var inv = new PlayerInventories();
        foreach (var (row, items) in save.Inventories)
            foreach (var p in items)
            {
                inv.ItemsIn(row).Add(new InventoryItem(p.Id, p.X, p.Y, CharacterSaves.FromSaved(p.Item, catalog)));
                // Stash ids (AccountStash.FirstItemId up) are the account's; the character's own stay below them.
                if (p.Id < AccountStash.FirstItemId) inv._nextItemId = Math.Max(inv._nextItemId, p.Id + 1);
            }
        if (save.StartWeapon is { } weapon) inv._startWeapon = CharacterSaves.FromSaved(weapon, catalog);
        return inv;
    }

    /// <summary>The items offered for sale, each at its cell in the offer window (its inventory cell keeps its imprint).</summary>
    public IReadOnlyList<InventoryItem> Offered()
    {
        lock (_lock)
            return [.. _items.Values.SelectMany(l => l).Where(i => _offered.ContainsKey(i.Id))
                .Select(i => i with { X = _offered[i.Id].X, Y = _offered[i.Id].Y })];
    }

    /// <summary>
    /// Offers an item for sale at (x, y) of the offer window. An item in an inventory stays in its cell, as the greyed
    /// imprint; the held item goes back to the main inventory cell it was picked up from (or the first free one) and waits
    /// there the same way. Returns the row and the item where it waits, or null (nothing changes): it is not there, it is
    /// offered already, or there is no room for the held item.
    /// </summary>
    public (byte Row, InventoryItem Item)? Offer(byte row, uint itemId, byte x, byte y, Func<Item, Shape?> shapeOf)
    {
        (byte, InventoryItem) result;
        bool moved = false;
        lock (_lock)
        {
            if (_offered.ContainsKey(itemId) || ItemsIn(row).FirstOrDefault(i => i.Id == itemId) is not { } it
                || it.Item.SkinTransferSource is not null || it.Item.Sockets.Any(s => s.Gem is not null)) return null;
            if (row == Cursor)
            {
                var shape = shapeOf(it.Item) ?? new(1, 1, "");
                var main = ItemsIn(MainInventory);
                bool FreeAt(byte cx, byte cy) => cx + shape.Width <= InstanceInventories.PlayerDefaults[MainInventory].Width
                    && cy + shape.Height <= InstanceInventories.PlayerDefaults[MainInventory].Height
                    && !main.Any(i => shapeOf(i.Item) is { } s && i.X < cx + shape.Width && cx < i.X + s.Width && i.Y < cy + shape.Height && cy < i.Y + s.Height);
                (byte X, byte Y)? cell = _heldFrom is (MainInventory, var hx, var hy) && FreeAt(hx, hy) ? (hx, hy)
                    : FreeCellIn(MainInventory, main, shape.Width, shape.Height, shapeOf);
                if (cell is not { } at) return null;
                ItemsIn(Cursor).Remove(it);
                _heldFrom = null;
                it = it with { X = at.X, Y = at.Y };
                main.Add(it);
                (row, moved) = (MainInventory, true);
            }
            _offered[itemId] = (x, y);
            result = (row, it);
        }
        if (moved) Persist();
        return result;
    }

    /// <summary>The items offered for sale where they wait in the inventories (their rows and cells).</summary>
    public IReadOnlyList<(byte Row, InventoryItem Item)> OfferedInPlace()
    {
        lock (_lock) return [.. _items.SelectMany(kv => kv.Value.Where(i => _offered.ContainsKey(i.Id)).Select(i => (kv.Key, i)))];
    }

    /// <summary>An offered item taken back out of the offer: it is in its cell again. Returns its row and the item, or
    /// null when it is not offered.</summary>
    public (byte Row, InventoryItem Item)? TakeBack(uint itemId)
    {
        lock (_lock)
        {
            if (!_offered.Remove(itemId)) return null;
            foreach (var (row, items) in _items)
                if (items.FirstOrDefault(i => i.Id == itemId) is { } it) return (row, it);
            return null;
        }
    }

    /// <summary>The sale cancelled (or ended any other way): every offered item is in its cell again. Returns them with
    /// their rows; nothing to save, the save has them there.</summary>
    public IReadOnlyList<(byte Row, InventoryItem Item)> CancelOffers()
    {
        lock (_lock)
        {
            var back = _items.SelectMany(kv => kv.Value.Where(i => _offered.ContainsKey(i.Id)).Select(i => (kv.Key, i))).ToList();
            _offered.Clear();
            return back;
        }
    }

    /// <summary>
    /// The sale accepted: every offered item is gone and <paramref name="reward"/> (one stack of it) goes into the main
    /// inventory, in one save. Null (nothing changes) when the reward doesn't all fit: the sale stays open.
    /// </summary>
    public Stacking? Sell(Item reward, Func<Item, Shape?> shapeOf) => Sell([reward], shapeOf);

    /// <summary>Stage the entire multi-item reward before consuming any offered item; persist once.</summary>
    public Stacking? Sell(IReadOnlyList<Item> rewards, Func<Item, Shape?> shapeOf)
    {
        Stacking result;
        lock (_lock)
        {
            if (_offered.Count == 0) return null;
            uint probe = _nextItemId;
            var before = ItemsIn(MainInventory).Where(i => !_offered.ContainsKey(i.Id)).ToList();
            var after = before.ToList();
            foreach (var reward in rewards)
                if (reward.StackSize > 0 && StackInto(MainInventory, after, reward, shapeOf, ref probe).Left > 0) return null;
            foreach (var items in _items.Values) items.RemoveAll(i => _offered.ContainsKey(i.Id));
            _offered.Clear();
            _items[MainInventory] = after;
            _nextItemId = probe;
            var old = before.ToDictionary(i => i.Id);
            result = new([.. after.Where(i => old.TryGetValue(i.Id, out var previous) && previous != i)],
                [.. after.Where(i => !old.ContainsKey(i.Id))], 0);
        }
        Persist();
        return result;
    }

    /// <summary>Whether <paramref name="received"/> fits the main inventory once every offered item is gone (a player
    /// trade checks both sides before either changes). Nothing changes.</summary>
    public bool CanExchange(IReadOnlyList<Item> received, Func<Item, Shape?> shapeOf)
    {
        lock (_lock)
        {
            uint probe = _nextItemId;
            var after = ItemsIn(MainInventory).Where(i => !_offered.ContainsKey(i.Id)).ToList();
            foreach (var item in received)
                if (item.StackSize > 0 && StackInto(MainInventory, after, item, shapeOf, ref probe).Left > 0) return false;
            return true;
        }
    }

    /// <summary>A player trade completed: every offered item is gone and <paramref name="received"/> goes into the main
    /// inventory, in one save. Unlike <see cref="Sell(IReadOnlyList{Item}, Func{Item, Shape?})"/> either side may be
    /// empty. Null (nothing changes) when the received items do not fit.</summary>
    public Stacking? Exchange(IReadOnlyList<Item> received, Func<Item, Shape?> shapeOf)
    {
        Stacking result;
        lock (_lock)
        {
            uint probe = _nextItemId;
            var before = ItemsIn(MainInventory).Where(i => !_offered.ContainsKey(i.Id)).ToList();
            var after = before.ToList();
            foreach (var item in received)
                if (item.StackSize > 0 && StackInto(MainInventory, after, item, shapeOf, ref probe).Left > 0) return null;
            foreach (var items in _items.Values) items.RemoveAll(i => _offered.ContainsKey(i.Id));
            _offered.Clear();
            _items[MainInventory] = after;
            _nextItemId = probe;
            var old = before.ToDictionary(i => i.Id);
            result = new([.. after.Where(i => old.TryGetValue(i.Id, out var previous) && previous != i)],
                [.. after.Where(i => !old.ContainsKey(i.Id))], 0);
        }
        Persist();
        return result;
    }

    /// <summary>
    /// The class's opening kit from Characters.dat: its start weapon waits on the Strand's ground, while the
    /// skill and support gems are saved as pending tutorial rewards (the biting zombie and large chest).
    /// Two Small Life Flasks (keys 1/2), one Small Mana Flask (key 5) and a stack of three Scrolls of Wisdom are
    /// available immediately.
    /// Existing saves are loaded unchanged. Game_START_ITEMS=0 starts empty.
    /// </summary>
    static PlayerInventories StartKit(ItemCatalog catalog, string folder, int characterRow = 1)
    {
        var inv = new PlayerInventories();
        if (Environment.GetEnvironmentVariable("Game_START_ITEMS") == "0") return inv;
        if (catalog.Error is not null || !ClientTables.Has("Characters") || !ClientTables.Has("SkillGems"))
        {
            // The empty kit is saved like any other, so this character stays empty: say why.
            Console.WriteLine($"[gateway] start kit is empty: {catalog.Error ?? $"no Characters or SkillGems client table in {ClientTables.DefaultPath}"}");
            return inv;
        }
        var c = ClientTables.Table("Characters");
        var g = ClientTables.Table("SkillGems");
        var row = c[characterRow];
        void Put(byte inventory, byte x, byte y, ItemCatalog.BaseType? t)
        {
            if (t is { Supported: true }) inv.Add(inventory, x, y, ItemCatalog.Create(t));
        }
        ItemCatalog.BaseType? Gem(string column) =>
            row.IntOrNull(column) is int k
                ? catalog.Row(g[k].Int("BaseItemTypesKey")) : null;
        // The start weapon with two linked white sockets (3.29: gems fit any colour), empty, for its gems.
        if (row.Ints("StartWeapons") is [var w, ..] && catalog.Row(w) is { Supported: true } weapon)
        {
            var item = ItemCatalog.Create(weapon, [new(SocketColour.White), new(SocketColour.White)], [2]);
            // Pending until the character picks it up from the beach.
            inv._startWeapon = item;
        }
        if (Gem("StartSkillGem") is { Supported: true } skill && Gem("StartSupportGem") is { Supported: true } support)
            inv.newTutorial = new(CharacterSaves.ToSaved(ItemCatalog.Create(skill)), CharacterSaves.ToSaved(ItemCatalog.Create(support)), Id: Guid.NewGuid().ToString("N"));
        // Keys 1 and 2: life; key 5: mana, as players set the bar up.
        Put(Flask, 0, 0, catalog.Find("Metadata/Items/Flasks/FlaskLife1"));
        Put(Flask, 1, 0, catalog.Find("Metadata/Items/Flasks/FlaskLife1"));
        Put(Flask, 4, 0, catalog.Find("Metadata/Items/Flasks/FlaskMana1"));
        // Three Scrolls of Wisdom, one stack in the main inventory's first cell, to identify the first drops.
        if (catalog.Find("Metadata/Items/Currency/CurrencyIdentification") is { Supported: true } wisdom)
            inv.Add(MainInventory, 0, 0, ItemCatalog.Create(wisdom) with { StackSize = 3 });
        return inv;
    }
}
