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
/// the player already had is not marked (its id is old), nor is a picked-up gem once it is socketed. The client's Dredge
/// marker on the item is not sent (its field is unknown).</para></summary>
internal static class DeepwaterDredge
{
    /// <summary>The character save's key: the item ids, comma-separated.</summary>
    public const string Key = "deepwater-dredge";
    const double PickupWait = 20;

    sealed record Pending(uint GroundId, uint TypeHash, HashSet<uint> Before, double Until);

    static readonly ConditionalWeakTable<ClientState, List<Pending>> Pickups = new();
    static readonly byte[] Rows = [PlayerInventories.MainInventory, PlayerInventories.ExpandedInventory, PlayerInventories.Cursor];

    static HashSet<uint> Parse(string? text) =>
        [.. (text ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).Select(s => uint.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out uint id) ? id : 0).Where(id => id != 0)];

    public static IReadOnlySet<uint> Ids(ClientState conn) => Parse(WorldOf(conn).CharacterData.Get(Key));

    static void Change(ClientState conn, Action<HashSet<uint>> change) =>
        WorldOf(conn).CharacterData.Change(Key, old =>
        {
            var ids = Parse(old);
            change(ids);
            return string.Join(',', ids.Order().Select(id => id.ToString(CultureInfo.InvariantCulture)));
        });

    static IEnumerable<InventoryItem> Picked(ClientState conn, uint typeHash)
    {
        var items = ItemsOf(conn);
        foreach (byte row in Rows)
            foreach (var it in items.Items(row))
                if (it.Item.TypeHash == typeHash) yield return it;
    }

    /// <summary>GroundItemUse in a running run: notes the pickup (the ids of that type the player holds now); the Game
    /// module then picks it up as ever. Gold goes to the stash and is not Dredge.</summary>
    public static void Noted(ClientState conn, uint groundId, WorldItemObject ground)
    {
        if (global::Game.IsGold(ground)) return;
        var before = Picked(conn, ground.Item.TypeHash).Select(it => it.Id).ToHashSet();
        var list = Pickups.GetValue(conn, _ => []);
        lock (list) list.Add(new(groundId, ground.Item.TypeHash, before, conn.Clock.Elapsed.TotalSeconds + PickupWait));
    }

    /// <summary>Per tick: a noted pickup whose ground item is gone marks the new items of its type. Nothing to do (one
    /// table lookup) without a pickup in flight.</summary>
    public static void Tick(IModuleContext module, ClientState conn)
    {
        if (!Pickups.TryGetValue(conn, out var list)) return;
        Pending[] due;
        double now = conn.Clock.Elapsed.TotalSeconds;
        lock (list)
        {
            if (list.Count == 0) return;
            due = [.. list.Where(p => conn.Instance.Ground.Get(p.GroundId) is null || now > p.Until)];
            list.RemoveAll(due.Contains);
        }
        var marked = new List<uint>();
        foreach (var p in due)
            if (conn.Instance.Ground.Get(p.GroundId) is null)
                marked.AddRange(Picked(conn, p.TypeHash).Select(it => it.Id).Where(id => !p.Before.Contains(id)));
        if (marked.Count == 0) return;
        Change(conn, ids => ids.UnionWith(marked));
        module.Log($"{CharacterName(conn)}: Dredge: items {string.Join(", ", marked)} picked up in the run");
    }

    /// <summary>A Bathysphere end: what the player carries is theirs.</summary>
    public static void Clear(IModuleContext module, ClientState conn)
    {
        Pickups.Remove(conn);
        if (Ids(conn).Count == 0) return;
        Change(conn, ids => ids.Clear());
        module.Log($"{CharacterName(conn)}: Dredge cleared (the Bathysphere)");
    }

    /// <summary>Items sent through a capsule are no longer Dredge.</summary>
    public static void Untrack(ClientState conn, IReadOnlyCollection<uint> sent)
    {
        if (sent.Count == 0 || Ids(conn).Count == 0) return;
        Change(conn, ids => ids.ExceptWith(sent));
    }

    /// <summary>Any end but a ride up (the Bathysphere, or the backup capsule, G): a death, drowning, a portal, a waypoint,
    /// a logout, or a crash found at the next entry. The Dredge items are removed from every inventory and the list is
    /// cleared. <paramref name="live"/> sends the inventories' changes (a leaving player gets them with
    /// its next area).</summary>
    public static async Task Remove(IModuleContext module, ClientState conn, bool live, string why)
    {
        Pickups.Remove(conn);
        var ids = Ids(conn);
        if (ids.Count == 0) return;
        var items = ItemsOf(conn);
        var removed = new Dictionary<byte, List<uint>>();
        foreach (uint id in ids)
        {
            byte? row = items.RowOf(id) ?? (items.Items(PlayerInventories.Cursor).Any(i => i.Id == id) ? PlayerInventories.Cursor : null);
            if (row is not { } r || items.Remove(r, id) is null) continue;
            (removed.TryGetValue(r, out var l) ? l : removed[r] = []).Add(id);
        }
        Change(conn, set => set.Clear());
        module.Log($"{CharacterName(conn)}: Dredge lost ({why}): {removed.Values.Sum(l => l.Count)} of {ids.Count} items removed"
            + (removed.Count > 0 ? $" from rows {string.Join(", ", removed.Keys)}" : ""));
        if (!live) return;
        foreach (var (row, list) in removed)
            await PlayerOf(conn).Send(InstanceItems.InventoryChange(PlayerInventories.InventoryId(row), items.NextRevision(row), list, []),
                $"0x00bf inventory {PlayerInventories.InventoryId(row)}: {list.Count} Dredge items lost");
    }
}
