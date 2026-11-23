using System.Buffers.Binary;
using GameServer.Core.Protocol.Instance;

/// <summary>
/// What the area wrote of its objects (stash, benches, doodads, chests, shrines, portals, effects…): each one's create and
/// its latest record of each kind, until its removal. A client that joins the area is written the same (Join), as monsters
/// and players join from their own state. Records for one player alone (a player's quest view) are that player's, and the
/// Buffs, Stats and Life components join themselves.
/// </summary>
public sealed class AreaObjectRecords(GameArea area)
{
    readonly Dictionary<uint, List<AreaDelta.Record>> objects = [];
    readonly Dictionary<uint, HashSet<Player>> directRecipients = [];

    static bool IsPlayer(uint id) => (id & 0xF0000000) == 0x20000000;
    static bool IsSharedMonster(uint id) => (id & 0xF8000000) == 0x08000000;

    internal void Written(AreaDelta.Record record)
    {
        if (record.Only is not null || record.Except is not null || record.Kind == AreaDelta.Kind.Message || record.Bytes.Length < 12) return;
        lock (objects)
        {
            uint id = record.Entity;
            if (record.Kind == AreaDelta.Kind.Remove) { objects.Remove(id); directRecipients.Remove(id); return; }
            if (IsPlayer(id) || IsSharedMonster(id) || area.Monsters.ContainsKey(id) || area.DormantMonsters.ContainsKey(id)) return;
            if (record.Kind == AreaDelta.Kind.Create) { objects[id] = [record]; directRecipients.Remove(id); return; }
            if (!objects.TryGetValue(id, out var records)) return;
            ushort message = BinaryPrimitives.ReadUInt16BigEndian(record.Bytes);
            if (message is InstanceEntities.AddBuffId or InstanceEntities.RemoveBuffId or InstanceEntities.StatsUpdateId
                or InstanceProtocol.LifeUpdateId) return;
            // The latest of a kind replaces the one before (a component update by its component).
            int same = records.FindIndex(1, r => Kind(r) == Kind(record));
            if (same >= 0) records[same] = record; else records.Add(record);
        }
    }

    /// <summary>
    /// Records an entity message written directly to <paramref name="recipient"/> before their area frames started
    /// (for example during initial area generation), so later joiners receive it without duplicating it for
    /// <paramref name="recipient"/> when their frames join.
    /// </summary>
    public void RecordDirect(uint entity, byte[] bytes, AreaDelta.Kind kind, Player recipient, string? log = null)
    {
        if (kind == AreaDelta.Kind.Message || bytes.Length < 12) return;
        lock (objects)
        {
            if (kind == AreaDelta.Kind.Remove) { objects.Remove(entity); directRecipients.Remove(entity); return; }
            if (IsPlayer(entity) || IsSharedMonster(entity) || area.Monsters.ContainsKey(entity) || area.DormantMonsters.ContainsKey(entity)) return;
            var copy = bytes.ToArray();
            if (kind == AreaDelta.Kind.Create && copy.Length >= 12)
            {
                BinaryPrimitives.WriteUInt32BigEndian(copy.AsSpan(6), uint.MaxValue);
                BinaryPrimitives.WriteUInt16BigEndian(copy.AsSpan(10), 0);
            }
            var order = kind == AreaDelta.Kind.Create ? WorldOrder.Last
                : new WorldOrder(BinaryPrimitives.ReadUInt32BigEndian(copy.AsSpan(6)), BinaryPrimitives.ReadUInt16BigEndian(copy.AsSpan(10)));
            var record = new AreaDelta.Record(entity, copy, log, order, kind);
            if (kind == AreaDelta.Kind.Create)
            {
                objects[entity] = [record];
                directRecipients[entity] = [recipient];
                return;
            }
            if (!objects.TryGetValue(entity, out var records)) return;
            ushort message = BinaryPrimitives.ReadUInt16BigEndian(record.Bytes);
            if (message is InstanceEntities.AddBuffId or InstanceEntities.RemoveBuffId or InstanceEntities.StatsUpdateId
                or InstanceProtocol.LifeUpdateId) return;
            int same = records.FindIndex(1, r => Kind(r) == Kind(record));
            if (same >= 0) records[same] = record; else records.Add(record);
            if (!directRecipients.TryGetValue(entity, out var set)) directRecipients[entity] = set = [];
            set.Add(recipient);
        }
    }

    static (ushort Message, byte Component) Kind(AreaDelta.Record record) => (BinaryPrimitives.ReadUInt16BigEndian(record.Bytes),
        BinaryPrimitives.ReadUInt16BigEndian(record.Bytes) == InstanceEntities.ComponentUpdateId && record.Bytes.Length > 14 ? record.Bytes[14] : (byte)0);

    public bool Has(uint id) { lock (objects) return objects.ContainsKey(id); }

    /// <summary>A joining client is written the area's objects as the others have them.</summary>
    public void Join(Player player)
    {
        area.AssertOwner();
        lock (objects)
        {
            foreach (var (id, records) in objects)
            {
                if (IsPlayer(id) || IsSharedMonster(id) || area.Monsters.ContainsKey(id) || area.DormantMonsters.ContainsKey(id)) continue;
                if (directRecipients.TryGetValue(id, out var sent) && sent.Contains(player)) continue;
                foreach (var record in records) area.Delta.Add(record with { Only = player, Except = null });
            }
        }
    }
}
