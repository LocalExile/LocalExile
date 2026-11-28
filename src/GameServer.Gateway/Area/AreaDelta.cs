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

    /// <summary>Whether this frame's undrained records already contain a create for <paramref name="entity"/>
    /// that will be written to <paramref name="player"/> when this frame ends.</summary>
    public bool HasCreateFor(uint entity, Player player) =>
        created.Contains(entity) && records.Any(r => r.Kind == Kind.Create && r.Entity == entity && r.For(player));

    /// <summary>Remove a shared object even when its replay was sent only to individual joining players.</summary>
    public void RemoveSharedObject(uint entity, byte[] bytes, string? log = null)
    {
        area.AssertOwner();
        lock (viewers) viewers.Remove(entity);
        Add(entity, bytes, log, Kind.Remove);
    }

    /// <summary>Forgets a departed player from every private-entity viewer list so long-lived areas (such as hideouts)
    /// do not retain closed sessions.</summary>
    public void RemoveViewer(Player player)
    {
        lock (viewers)
        {
            List<uint>? empty = null;
            foreach (var (entity, list) in viewers)
            {
                if (list.Remove(player) && list.Count == 0)
                    (empty ??= []).Add(entity);
            }
            if (empty is not null)
                foreach (uint entity in empty) viewers.Remove(entity);
        }
    }

    /// <summary>Discards undelivered frame delta records once no client remains in the area (for example when a player
    /// leaves their hideout). Durable state is already committed to Area.Objects / Buffs / Stats / Life and will be
    /// replayed cleanly via Join on re-entry instead of delivering stale departure records to the next entrant.</summary>
    public void DiscardPending()
    {
        records.Clear();
        held.Clear();
        created.Clear();
    }

    public void Add(Record record)
    {
        area.AssertOwner();
        // Shared entities replayed to a joining player (peer players 0x20000000, shared monsters 0x08000000,
        // dormant monsters, area objects) must never register that joiner as the entity's sole private viewer.
        if (record.Kind == Kind.Create && record.Only is { } owner && record.Except is null
            && (record.Entity & 0xF0000000) != 0x20000000 && (record.Entity & 0xF8000000) != 0x08000000
            && !area.Monsters.ContainsKey(record.Entity) && !area.DormantMonsters.ContainsKey(record.Entity)
            && !area.Objects.Has(record.Entity))
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
        else if (record.Kind == Kind.Remove)
        {
            area.Buffs.Removed(record.Entity); area.Stats.Removed(record.Entity); area.Life.Removed(record.Entity);
            // A removal in the same frame as the object's create waits for the next frame so it never precedes
            // or drops below any AfterBirth/World records held for that frame (matching WorldOutput).
            if (created.Contains(record.Entity))
            {
                held.Add(record);
                return;
            }
        }
        // The client applies a frame's creates in its last phase (WorldOrder.Last, 0x142183ce0): a record about an object
        // created in this frame that would be applied before that goes in the next frame.
        else if (record.Kind != Kind.Message && created.Contains(record.Entity) && record.Order.CompareTo(WorldOrder.Last) < 0)
        {
            held.Add(record);
            return;
        }
        records.Add(record);
    }

    /// <summary>A world record (u16 id, u32 object, u32 order key, u16 checkpoint) as it is: its order from its header.
    /// Active lockstep creates and removals always carry key 0xffffffff (WorldOrder.Last) so they sort in the client's
    /// create/remove phase regardless of caller defaults.</summary>
    public void Add(uint entity, byte[] bytes, string? log = null, Kind kind = Kind.World, Player? except = null, Player? only = null)
    {
        if (kind == Kind.Create && bytes.Length >= 12
            && (System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(6)) != uint.MaxValue
                || System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(10)) != 0))
        {
            bytes = bytes.ToArray();
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(6), uint.MaxValue);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(10), 0);
        }
        else if (kind == Kind.Remove && bytes.Length >= 10
            && System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(6)) != uint.MaxValue)
        {
            bytes = bytes.ToArray();
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(6), uint.MaxValue);
        }
        Add(new(entity, bytes, log, kind == Kind.Message || bytes.Length < 12 ? WorldOrder.First
            : new(System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(6)),
                System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(10))), kind, except, only));
    }

    /// <summary>Drops any queued <c>ActionStarted</c> (<c>0x01ab</c>) records for <paramref name="entity"/> that have not
    /// been flushed yet in this frame (for example a monster that queued a charge earlier in the frame and died before
    /// the frame ended).</summary>
    public void DiscardActions(uint entity)
    {
        area.AssertOwner();
        static bool IsActionFor(Record r, uint id) =>
            r.Entity == id && r.Kind == Kind.World && r.Bytes.Length >= 2
            && (r.Bytes[0] << 8 | r.Bytes[1]) == GameServer.Core.Protocol.Instance.InstanceProtocol.ActionStartedId;
        records.RemoveAll(r => IsActionFor(r, entity));
        held.RemoveAll(r => IsActionFor(r, entity));
    }

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
