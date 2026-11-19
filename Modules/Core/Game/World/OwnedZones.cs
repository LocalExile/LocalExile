using System.Runtime.CompilerServices;

/// <summary>
/// The zones (AreaZones) the game keeps for an owner, by kind and key: one per aura, ground, minion aura or sight range.
/// A feature says which zones it has now (SyncZones) and asks who is inside (InZone); the zone tells it when someone
/// entered or left, so that what it shows follows the walkers' moves instead of a scan. Under the instance's Monsters
/// lock, as the movement phase that updates them.
/// </summary>
public static partial class Game
{
    /// <summary>How a kind of zone is placed and whom it reaches. <see cref="Follows"/> null: the zone stays at the point
    /// its key gives (SyncZones' At).</summary>
    public sealed record ZoneRules(Func<EntityId, int?> Reach, EntityId? Follows = null, Action<EntityId>? Changed = null);

    sealed class KeptZone(Zone zone) { public readonly Zone Zone = zone; public int Radius; }
    sealed class ZoneTable { public readonly Dictionary<(uint Owner, string Kind, object Key), KeptZone> Zones = []; }
    static readonly ConditionalWeakTable<GameArea, ZoneTable> ZoneTables = new();

    static Dictionary<(uint Owner, string Kind, object Key), KeptZone>? ZonesOf(Player conn) =>
        conn.Frames.Area is { } area ? ZoneTables.GetValue(area, _ => new()).Zones : null;

    /// <summary>
    /// Makes the owner's zones of one kind those of <paramref name="wanted"/> (a key, its radius and, for a zone that
    /// follows nothing, its point): new ones are added, radii follow, the others go. Under the Monsters lock.
    /// </summary>
    public static void SyncZones(Player conn, uint owner, string kind, IEnumerable<(object Key, int Radius, (int X, int Y) At)> wanted,
        ZoneRules rules)
    {
        if (ZonesOf(conn) is not { } zones || conn.Frames.Area is not { } area) return;
        var keep = new HashSet<object>();
        foreach (var (key, radius, at) in wanted)
        {
            keep.Add(key);
            if (!zones.TryGetValue((owner, kind, key), out var kept))
            {
                KeptZone? made = null;
                var zone = new Zone
                {
                    Follows = rules.Follows, At = at, Radius = () => made!.Radius, Reach = rules.Reach,
                    Entered = rules.Changed, Left = rules.Changed,
                };
                zones[(owner, kind, key)] = kept = made = new(zone);
                area.Zones.Add(zone, CombatLifetime);
            }
            kept.Radius = radius;
            kept.Zone.At = at;
        }
        foreach (var entry in zones.Where(z => z.Key.Owner == owner && z.Key.Kind == kind && !keep.Contains(z.Key.Key)).ToArray())
        {
            area.Zones.Remove(entry.Value.Zone);
            zones.Remove(entry.Key);
            foreach (var id in entry.Value.Zone.Inside) rules.Changed?.Invoke(id);
        }
    }

    /// <summary>Removes every zone of one kind of the owner (an aura's owner left, a minion ended). Under the Monsters lock.</summary>
    public static void DropZones(Player conn, uint owner, string kind) =>
        SyncZones(conn, owner, kind, [], new(_ => null));

    /// <summary>Whether <paramref name="id"/> is inside the owner's zone of that kind and key. Under the Monsters lock.</summary>
    public static bool InZone(Player conn, uint owner, string kind, object key, uint id) =>
        ZonesOf(conn)?.GetValueOrDefault((owner, kind, key)) is { } kept && kept.Zone.Contains(new(id));

    /// <summary>The entities inside the owner's zone of that kind and key.</summary>
    public static IReadOnlyCollection<EntityId> ZoneMembers(Player conn, uint owner, string kind, object key) =>
        ZonesOf(conn)?.GetValueOrDefault((owner, kind, key))?.Zone.Inside ?? [];

    // Who a zone reaches: the player's hostile living monsters (with their body), the players but one, one entity.
    public static Func<EntityId, int?> ReachesHostileMonsters(Player conn, bool body = true) =>
        id => conn.Monsters.GetValueOrDefault(id.Value) is { Dead: false, Life: > 0, Hostile: true } m ? body ? m.StopRange : 0 : null;
    public static Func<EntityId, int?> ReachesPlayersBut(uint except) => id => id.Value != except && IsPlayerId(id.Value) ? 0 : null;
    public static Func<EntityId, int?> ReachesAlliesBut(Player conn, uint except) =>
        id => id.Value != except && (IsPlayerId(id.Value)
            || conn.Monsters.GetValueOrDefault(id.Value) is { Dead: false, Life: > 0, Hostile: false }) ? 0 : null;
    // Players' ids are 0x20000000 on (Entities.cs).
    public static bool IsPlayerId(uint id) => (id & 0xF0000000) == 0x20000000;
    public static Func<EntityId, int?> ReachesOnly(uint only) => id => id.Value == only ? 0 : null;

    /// <summary>The connection of a player of the area, by id.</summary>
    public static Player? AreaPlayer(Player conn, uint id) => conn.Frames.Players().FirstOrDefault(p => p.PeerId == id);
}
