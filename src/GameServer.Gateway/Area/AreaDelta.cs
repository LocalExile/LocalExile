/// <summary>
/// What changed in the area's world in one frame, made once: records about its entities, in the order they were made.
/// At the frame's end every connection in the area is written the same bytes (Program.EndWorldFrame): an entity has one
/// id for every client, a player's own client too (S→C 0x22). Owned by the area's frame.
/// </summary>
public sealed class AreaDelta(GameArea area)
{
    public enum Kind : byte
    {
        /// <summary>Not about one object (S→C 0x20 monster types): first in the frame.</summary>
        Message,
        World,
        Create,
        Remove,
    }

    /// <param name="Entity">The object the record is about (0 for a message).</param>
    /// <param name="Order">Where the client applies it in the frame (the record's own header).</param>
    /// <param name="Except">A player whose connection does not get it (it has the object already: its own player).</param>
    /// <param name="Only">The one player whose connection gets it (what a joining client is missing).</param>
    public readonly record struct Record(uint Entity, byte[] Bytes, string? Log, WorldOrder Order, Kind Kind,
        Player? Except = null, Player? Only = null)
    {
        public bool For(Player player) => !ReferenceEquals(Except, player) && (Only is null || ReferenceEquals(Only, player));
    }

    List<Record> records = [], held = [];
    readonly HashSet<uint> created = [];
    // Entities created for players alone (each one's quest view, a private ground item; a quest object with a fixed id may
    // be in several players' views): every record about them is their viewers' only.
    readonly Dictionary<uint, List<Player>> viewers = [];

    /// <summary>The player a record about a private entity goes to when <paramref name="sender"/> made it: the sender when
    /// it is one of its viewers (null otherwise: the record goes to each viewer).</summary>
    public Player? ViewerOf(uint entity, Player sender)
    {
        lock (viewers) return viewers.TryGetValue(entity, out var those) && those.Contains(sender) ? sender : null;
    }

    public int Count => records.Count;

    public void Add(Record record)
    {
        area.AssertOwner();
        if (record.Kind == Kind.Create && record.Only is { } owner && record.Except is null && !area.Monsters.ContainsKey(record.Entity))
        {
            lock (viewers)
            {
                if (!viewers.TryGetValue(record.Entity, out var those)) viewers[record.Entity] = those = [];
                if (!those.Contains(owner)) those.Add(owner);
            }
        }
        else if (record.Only is null && record.Entity != 0)
        {
            Player[]? snapshot;
            lock (viewers) snapshot = viewers.TryGetValue(record.Entity, out var all) ? all.ToArray() : null;
            if (snapshot is not null)
            {
                foreach (var viewer in snapshot) Add(record with { Only = viewer, Except = null });
                return;
            }
        }
        if (record.Kind == Kind.Remove && record.Only is { } gone)
        {
            lock (viewers)
                if (viewers.TryGetValue(record.Entity, out var left))
                {
                    left.Remove(gone);
                    if (left.Count == 0) viewers.Remove(record.Entity);
                }
        }
        area.Objects.Written(record);
        if (record.Kind == Kind.Create) created.Add(record.Entity);
        // An object removed takes its components with it (its id may come back as another object).
        else if (record.Kind == Kind.Remove) { area.Buffs.Removed(record.Entity); area.Stats.Removed(record.Entity); area.Life.Removed(record.Entity); }
        // The client applies a frame's creates in its last phase (WorldOrder.Last, 0x142183ce0): a record about an object
        // created in this frame that would be applied before that goes in the next frame.
        else if (record.Kind != Kind.Message && created.Contains(record.Entity) && record.Order.CompareTo(WorldOrder.Last) < 0)
        {
            held.Add(record);
            return;
        }
        records.Add(record);
    }

    /// <summary>A world record (u16 id, u32 object, u32 order key, u16 checkpoint) as it is: its order from its header.</summary>
    public void Add(uint entity, byte[] bytes, string? log = null, Kind kind = Kind.World, Player? except = null, Player? only = null) =>
        Add(new(entity, bytes, log, kind == Kind.Message || bytes.Length < 12 ? WorldOrder.First
            : new(System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(6)),
                System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(10))), kind, except, only));

    /// <summary>The frame's records, taken for its end; what waited for a create of this frame starts the next.</summary>
    public IReadOnlyList<Record> Take()
    {
        var taken = records;
        records = held;
        held = [];
        created.Clear();
        return taken;
    }
}
