namespace GameServer.Data.Terrain.Generation;

/// <summary>The IncursionPastPlugin's part in the generation (<c>0x141f4c5f0</c>, OnCreateDungeonGraph): the server
/// names, in 0x1d's TerrainPluginState, one 6-byte blob per Incursion room of the area (u16 IncursionRooms key, then
/// the room's slot in the 13-room temple in byte 2's low nibble); each blob queues a past-room world area
/// (Incursion_Room1..10, every one <c>roomsingle_1.dgr</c>). When the n-th such record's graph is read, the n-th blob's
/// PAST room (IncursionRooms +0x4c) replaces the room set's <c>room_blank_past.arm</c> entry at weight 1, the graph's
/// "room" node takes that room's tag, and the six door nodes (the tags of the TEMPLATE_LockedDoor1 / 2 past rooms,
/// "doorclosed" / "2doorclosed") are sorted by y descending, each pair by x ascending, and the n-th gets exist 100 or 0
/// from the temple layout table (<c>byte_142fdd570</c>, row = the slot, the first byte of each pair: a 0 = no
/// neighbour across that door, so the node vanishes without a roll). The server corpus verified the door rule on the
/// Toxic Conduits 3795292572 (slot 9 keeps four of six). The rooms come in blob order; <see cref="Take"/> hands the
/// next one to each past-room record.</summary>
public sealed class IncursionPast(IReadOnlyList<(string Past, int Slot)> rooms)
{
    int _next;

    public static bool IsPastGraph(string graphPath) =>
        graphPath.Replace('\\', '/').Contains("/Leagues/Incursion/Graphs/roomsingle", StringComparison.OrdinalIgnoreCase);

    /// <summary>The blob for the next past-room record, or null when the graph is not one or the blobs are used up.</summary>
    public (string Past, int Slot)? Take(string graphPath)
    {
        if (!IsPastGraph(graphPath) || _next >= rooms.Count) return null;
        return rooms[_next++];
    }

    /// <summary>The temple layout: 13 rows (the room's slot) of 6 neighbours, one per door in the plugin's door order
    /// (bottom-left, bottom-right, left, right, top-left, top-right); 0 = no neighbour.</summary>
    static readonly byte[][] DoorTable =
    [
        [0x03, 0x04, 0x00, 0x01, 0x00, 0x00],
        [0x07, 0x08, 0x02, 0x00, 0x00, 0x00],
        [0x00, 0x0c, 0x00, 0x09, 0x00, 0x03],
        [0x0d, 0x0e, 0x09, 0x0a, 0x04, 0x05],
        [0x0f, 0x10, 0x0a, 0x0b, 0x06, 0x07],
        [0x11, 0x00, 0x0b, 0x00, 0x08, 0x00],
        [0x00, 0x14, 0x00, 0x12, 0x0c, 0x0d],
        [0x15, 0x16, 0x12, 0x13, 0x0e, 0x0f],
        [0x17, 0x00, 0x13, 0x00, 0x10, 0x11],
        [0x00, 0x19, 0x00, 0x18, 0x14, 0x15],
        [0x1a, 0x00, 0x18, 0x00, 0x16, 0x17],
        [0x05, 0x06, 0x01, 0x02, 0x00, 0x00],
        [0x00, 0x00, 0x00, 0x00, 0x19, 0x1a],
    ];

    /// <summary>The past rooms of the two locked-door templates (TEMPLATE_LockedDoor1 / 2), whose tags name the door nodes.</summary>
    static readonly string[] DoorRooms =
    [
        "Metadata/Terrain/Leagues/Incursion/Rooms/door_closed_past_01_01.arm",
        "Metadata/Terrain/Leagues/Incursion/Rooms/door_closed_past_02_01.arm",
    ];

    /// <summary>Types the graph's "room" node and sets the door nodes' exist chances, before the exist rolls.</summary>
    public static void Apply(TileGraph graph, TerrainCache files, string past, int slot, List<string> notes)
    {
        var room = files.Room(past) ?? throw new FileNotFoundException(past);
        foreach (var n in graph.Nodes)
            if (n.Tag == "room") n.Tag = room.Tag;
        var kinds = DoorRooms.Select(p => files.Room(p)?.Tag).Where(t => t is not null).ToHashSet(StringComparer.OrdinalIgnoreCase)!;
        var doors = graph.Nodes.Where(n => kinds.Contains(n.Tag)).ToList();
        if (doors.Count != 6)
        {
            notes.Add($"incursion past room {Path.GetFileName(past)}: {doors.Count} door nodes, not 6 - no door pass");
            return;
        }
        doors.Sort((a, b) => b.Y.CompareTo(a.Y));
        for (int k = 0; k < 6; k += 2)
            if (doors[k].X > doors[k + 1].X) (doors[k], doors[k + 1]) = (doors[k + 1], doors[k]);
        var row = slot < DoorTable.Length ? DoorTable[slot] : new byte[6];
        for (int k = 0; k < 6; k++) doors[k].Exist = row[k] != 0 ? 100 : 0;
        notes.Add($"incursion past room {Path.GetFileName(past)} (tag '{room.Tag}', temple slot {slot}): doors [{string.Join(", ", row.Select(b => b != 0 ? 100 : 0))}]");
    }
}
