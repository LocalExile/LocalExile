using System.Globalization;
using System.Runtime.CompilerServices;
using GameServer.Core.Protocol.Instance;
using GameServer.Modding;

namespace GameServer.Modules.Deepwater;

/// <summary>Dredge (ClientStrings 8423-8424, the wiki): whatever a player picks up from the ground during a run is lost
/// unless it leaves by the Bathysphere or goes up in a capsule. The picked-up items' ids are kept in the character's
/// save (<see cref="Key"/>, written at once so a crash or a relog counts): cleared on a Bathysphere end, taken off when
/// sent through a capsule, and the items removed on any other end and at the next entry outside the run.
/// <para>G (the mechanism; the rule is H): a pickup is matched by the new item ids of the picked type that appear in the
/// main inventory, the expanded inventory or on the cursor after the ground item is gone. A stackable merged into a stack
/// the player already had is not marked (its id is old), nor is a picked-up gem once it is socketed.</para>
/// <para>H: a Dredged item carries <see cref="DredgeFlags"/> in its header flags (bit 1 the client's contraband gate,
/// 0x141d65f80: only the main/expanded inventories and the capsule's 113 take it, and 113 only with it; bit 2 the Dredge
/// symbol). G: the items keep it while in 113 and lose it on reaching the collection (114) or on a Bathysphere end.</para></summary>
internal static class DeepwaterDredge
{
    /// <summary>The character save's key: the item ids, comma-separated.</summary>
    public const string Key = "deepwater-dredge";
    const double PickupWait = 20;
    /// <summary>Item header flags bit 1 (contraband, +0x29) and bit 2 (the Deepwater marker, +0x2a).</summary>
    public const ushort DredgeFlags = 0x0006;

    public static bool Dredged(Item item) => (item.HeaderFlags & DredgeFlags) == DredgeFlags;

    sealed record Pending(uint GroundId, uint TypeHash, HashSet<uint> Before, double Until);

    static readonly ConditionalWeakTable<Player, List<Pending>> Pickups = new();
    static readonly byte[] Rows = [PlayerInventories.MainInventory, PlayerInventories.ExpandedInventory, PlayerInventories.Cursor];

    static HashSet<uint> Parse(string? text) =>
        [.. (text ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).Select(s => uint.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out uint id) ? id : 0).Where(id => id != 0)];

    public static IReadOnlySet<uint> Ids(Player player) => Parse(WorldOf(player).CharacterData.Get(Key));

    static void Change(Player player, Action<HashSet<uint>> change) =>
        WorldOf(player).CharacterData.Change(Key, old =>
        {
            var ids = Parse(old);
            change(ids);
            return string.Join(',', ids.Order().Select(id => id.ToString(CultureInfo.InvariantCulture)));
        });

    static IEnumerable<InventoryItem> Picked(Player player, uint typeHash)
    {
        var items = ItemsOf(player);
        foreach (byte row in Rows)
            foreach (var it in items.Items(row))
                if (it.Item.TypeHash == typeHash) yield return it;
    }

    /// <summary>GroundItemUse in a running run: notes the pickup (the ids of that type the player holds now); the Game
    /// module then picks it up as ever. Gold goes to the stash and is not Dredge.</summary>
    public static void Noted(Player player, uint groundId, WorldItemObject ground)
    {
        if (global::Game.IsGold(ground)) return;
        var before = Picked(player, ground.Item.TypeHash).Select(it => it.Id).ToHashSet();
        var list = Pickups.GetValue(player, _ => []);
        lock (list) list.Add(new(groundId, ground.Item.TypeHash, before, AreaTime(player) + PickupWait));
    }

    /// <summary>An item picked up in the run but not marked yet (its pickup is matched on the next tick): one of a noted
    /// pickup's type whose ground item is gone, held on the cursor under a new id (any of that type when it is not on the
    /// cursor). Read only when an item is placed.</summary>
    public static bool Unmarked(Player player, Item item)
    {
        if (!Pickups.TryGetValue(player, out var list)) return false;
        uint? held = ItemsOf(player).Items(PlayerInventories.Cursor).FirstOrDefault(i => ReferenceEquals(i.Item, item) || i.Item == item)?.Id;
        lock (list)
            return list.Any(p => p.TypeHash == item.TypeHash && player.Instance.Ground.Get(p.GroundId) is null && (held is not { } id || !p.Before.Contains(id)));
    }

    /// <summary>Per tick: a noted pickup whose ground item is gone marks the new items of its type, in the save's list and
    /// on the item (re-sent as a removal and an add of the same id). Nothing to do (one table lookup) without a pickup in
    /// flight. The ground item cannot carry the flag before the pickup: the Game module takes it by reference.</summary>
    public static async Task Tick(IModuleContext module, Player player)
    {
        if (!Pickups.TryGetValue(player, out var list)) return;
        Pending[] due;
        double now = AreaTime(player);
        lock (list)
        {
            if (list.Count == 0) return;
            due = [.. list.Where(p => player.Instance.Ground.Get(p.GroundId) is null || now > p.Until)];
            list.RemoveAll(due.Contains);
        }
        var marked = new List<uint>();
        foreach (var p in due)
            if (player.Instance.Ground.Get(p.GroundId) is null)
                marked.AddRange(Picked(player, p.TypeHash).Select(it => it.Id).Where(id => !p.Before.Contains(id)));
        if (marked.Count == 0) return;
        Change(player, ids => ids.UnionWith(marked));
        module.Log($"{CharacterName(player)}: Dredge: items {string.Join(", ", marked)} picked up in the run");
        await Flag(player, marked, on: true, live: true);
    }

    /// <summary>Sets or clears <see cref="DredgeFlags"/> on these items wherever the player holds them (the cursor too);
    /// <paramref name="live"/> re-sends each changed row as one 0xbf removing and re-adding the same ids.</summary>
    static async Task Flag(Player player, IEnumerable<uint> ids, bool on, bool live)
    {
        var items = ItemsOf(player);
        var changed = new Dictionary<byte, List<InventoryItem>>();
        foreach (uint id in ids)
        {
            byte? row = items.RowOf(id) ?? (items.Items(PlayerInventories.Cursor).Any(i => i.Id == id) ? PlayerInventories.Cursor : null);
            if (row is not { } r || items.Items(r).FirstOrDefault(i => i.Id == id) is not { } it || Dredged(it.Item) == on) continue;
            if (items.Change(r, id, i => i with { HeaderFlags = (ushort)(on ? i.HeaderFlags | DredgeFlags : i.HeaderFlags & ~DredgeFlags) }) is { } now)
                (changed.TryGetValue(r, out var l) ? l : changed[r] = []).Add(now);
        }
        if (!live) return;
        foreach (var (row, list) in changed)
            await PlayerOf(player).Send(InstanceItems.InventoryChange(PlayerInventories.InventoryId(row), items.NextRevision(row), [.. list.Select(i => i.Id)], list),
                $"0x00bf inventory {PlayerInventories.InventoryId(row)}: {list.Count} items {(on ? "marked" : "no longer")} Dredge (header flags 0x{DredgeFlags:x4})");
    }

    /// <summary>Clears the Dredge mark from these items (the list is the caller's to change).</summary>
    public static Task Unmark(Player player, IEnumerable<uint> ids, bool live) => Flag(player, ids, on: false, live);

    /// <summary>A Bathysphere end: what the player carries is theirs, and unmarked.</summary>
    public static async Task Clear(IModuleContext module, Player player)
    {
        Pickups.Remove(player);
        var held = new HashSet<uint>(Ids(player));
        CollectFlagged(player, held);
        if (held.Count == 0) return;
        Change(player, ids => ids.Clear());
        await Unmark(player, held, live: !player.Closed && !player.LeavingArea);
        module.Log($"{CharacterName(player)}: Dredge cleared (the Bathysphere)");
    }

    /// <summary>Items sent through a capsule are no longer Dredge.</summary>
    public static void Untrack(Player player, IReadOnlyCollection<uint> sent)
    {
        if (sent.Count == 0 || Ids(player).Count == 0) return;
        Change(player, ids => ids.ExceptWith(sent));
    }

    /// <summary>Any end but a ride up (the Bathysphere, or the backup capsule, G): a death, drowning, a portal, a waypoint,
    /// a logout, or a crash found at the next entry. The Dredge items are removed from every inventory and the list is
    /// cleared. <paramref name="live"/> sends the inventories' changes (a leaving player gets them with
    /// its next area).</summary>
    public static async Task Remove(IModuleContext module, Player player, bool live, string why)
    {
        var ids = new HashSet<uint>(Ids(player));
        if (Pickups.TryGetValue(player, out var pending))
        {
            Pending[] due;
            lock (pending) { due = [.. pending]; pending.Clear(); }
            Pickups.Remove(player);
            foreach (var p in due)
                if (player.Instance is { } inst && inst.Ground.Get(p.GroundId) is null)
                    foreach (var it in Picked(player, p.TypeHash))
                        if (!p.Before.Contains(it.Id)) ids.Add(it.Id);
        }
        CollectFlagged(player, ids);
        if (ids.Count == 0) return;
        var items = ItemsOf(player);
        var removed = new Dictionary<byte, List<uint>>();
        foreach (uint id in ids)
        {
            byte? row = items.RowOf(id) ?? (items.Items(PlayerInventories.Cursor).Any(i => i.Id == id) ? PlayerInventories.Cursor : null);
            if (row is not { } r || items.Remove(r, id) is null) continue;
            (removed.TryGetValue(r, out var l) ? l : removed[r] = []).Add(id);
        }
        Change(player, set => set.Clear());
        module.Log($"{CharacterName(player)}: Dredge lost ({why}): {removed.Values.Sum(l => l.Count)} of {ids.Count} items removed"
            + (removed.Count > 0 ? $" from rows {string.Join(", ", removed.Keys)}" : ""));
        if (!live) return;
        foreach (var (row, list) in removed)
            await PlayerOf(player).Send(InstanceItems.InventoryChange(PlayerInventories.InventoryId(row), items.NextRevision(row), list, []),
                $"0x00bf inventory {PlayerInventories.InventoryId(row)}: {list.Count} Dredge items lost");
    }

    // Includes any item still carrying DredgeFlags (such as a stack split via 0x7c whose new id was never added to Ids).
    static void CollectFlagged(Player player, HashSet<uint> target)
    {
        var items = ItemsOf(player);
        foreach (byte row in (ReadOnlySpan<byte>)[PlayerInventories.MainInventory, PlayerInventories.ExpandedInventory, PlayerInventories.Cursor, DeepwaterExtraction.CapsuleRow])
            foreach (var it in items.Items(row))
                if (Dredged(it.Item)) target.Add(it.Id);
    }

    /// <summary>Clears any pending pickups when the Deepwater module unloads.</summary>
    internal static void ClearCache() => Pickups.Clear();
}
