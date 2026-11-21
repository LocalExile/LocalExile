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

    internal void Written(AreaDelta.Record record)
    {
        if (record.Only is not null || record.Except is not null || record.Kind == AreaDelta.Kind.Message || record.Bytes.Length < 12) return;
        uint id = record.Entity;
        if (record.Kind == AreaDelta.Kind.Remove) { objects.Remove(id); return; }
        if (area.Monsters.ContainsKey(id) || area.DormantMonsters.ContainsKey(id)) return;
        if (record.Kind == AreaDelta.Kind.Create) { objects[id] = [record]; return; }
        if (!objects.TryGetValue(id, out var records)) return;
        ushort message = BinaryPrimitives.ReadUInt16BigEndian(record.Bytes);
        if (message is InstanceEntities.AddBuffId or InstanceEntities.RemoveBuffId or InstanceEntities.StatsUpdateId
            or InstanceProtocol.LifeUpdateId) return;
        // The latest of a kind replaces the one before (a component update by its component).
        int same = records.FindIndex(1, r => Kind(r) == Kind(record));
        if (same >= 0) records[same] = record; else records.Add(record);
    }

    static (ushort Message, byte Component) Kind(AreaDelta.Record record) => (BinaryPrimitives.ReadUInt16BigEndian(record.Bytes),
        BinaryPrimitives.ReadUInt16BigEndian(record.Bytes) == InstanceEntities.ComponentUpdateId && record.Bytes.Length > 12 ? record.Bytes[12] : (byte)0);

    public bool Has(uint id) => objects.ContainsKey(id);

    /// <summary>A joining client is written the area's objects as the others have them.</summary>
    public void Join(Player player)
    {
        area.AssertOwner();
        foreach (var (id, records) in objects)
        {
            if (area.Monsters.ContainsKey(id) || area.DormantMonsters.ContainsKey(id)) continue;
            foreach (var record in records) area.Delta.Add(record with { Only = player, Except = null });
        }
    }
}
