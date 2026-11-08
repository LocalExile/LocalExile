namespace GameServer.Data.Terrain.Generation;

/// <summary>The IncursionTemplePlugin's part in the generation (<c>0x141f4cfa0</c>, OnCreateDungeonGraph on the Temple of
/// Atzoatl's graph, <c>Leagues/Incursion/Graphs/temple_3.dgr</c>): the server sends, in 0x1d's TerrainPluginState, the
/// temple's layout as eleven u16 IncursionRooms keys (the row's HASH16 at +0x38, the same keys the Chronicle of Atzoatl
/// item carries) and 26 door bits. The graph's eleven "room" nodes (the PRESENT tag of TEMPLATE_BlankRoom), sorted by y
/// ascending then by x inside rows of 2 / 4 / 3 / 2, take the n-th key's present room (IncursionRooms +0x30): its tag
/// becomes the node's, and the room joins the room set at weight 1 when the set lacks it (the chooser then draws
/// draw(1); generate.rs's own door_open 10 / door_closed 30 / entrance and boss 100 keep their weights). The 26 door
/// nodes (TEMPLATE_OpenDoor1 / OpenDoor2's tags), sorted the same way in rows of 2 / 6 / 3 / 6 / 2 / 4 / 1 / 2: a CLEAR
/// bit n turns door n into TEMPLATE_LockedDoor1 / LockedDoor2's room; when one of the eleven keys is TrapRoomI / II /
/// III (the first in key order, tier 1..3) every SET-bit door becomes TEMPLATE_OpenDoor1Trap&lt;tier&gt; /
/// OpenDoor2Trap&lt;tier&gt;'s room (weight 1, not in the set). The four other typed nodes (entrance, joiner, boss_entry,
/// boss) only get placement rects for the UI. The server corpus verified both rules on the Temple10 3485592801 and the
/// Temple7_ 319016223 (a tier-2 Trap Workshop).</summary>
public sealed class IncursionTemple(IReadOnlyList<int> keys, uint doorBits)
{
    public static bool IsTempleGraph(string graphPath) =>
        graphPath.Replace('\\', '/').Contains("/Leagues/Incursion/Graphs/temple", StringComparison.OrdinalIgnoreCase);

    static readonly (int From, int To)[] RoomRows = [(0, 2), (2, 6), (6, 9), (9, 11)];
    static readonly (int From, int To)[] DoorRows = [(0, 2), (2, 8), (8, 11), (11, 17), (17, 19), (19, 23), (24, 26)];

    /// <summary>Types the room and door nodes before the exist rolls; returns the present rooms the room set must gain
    /// at weight 1 (those it lacks), in blob order.</summary>
    public List<string> Apply(TileGraph graph, TerrainCache files, List<string> notes)
    {
        var extra = new List<string>();
        if (files.Raw("Data/IncursionRooms.datc64") is not { } data) throw new FileNotFoundException("Data/IncursionRooms.datc64");
        var table = new RawDat(data);
        var byId = new Dictionary<string, int>(StringComparer.Ordinal);
        var byKey = new Dictionary<int, int>();
        for (int r = 0; r < table.Count; r++)
        {
            string id = table.String(r, 0);
            if (id.Length == 0) continue;
            byId.TryAdd(id, r);
            byKey.TryAdd(table.U16(r, 56), r);
        }
        string Present(string id) => byId.TryGetValue(id, out int r) ? table.String(r, 48) : throw new InvalidDataException($"IncursionRooms has no {id}");
        string TagOf(string arm) => (files.Room(arm) ?? throw new FileNotFoundException(arm)).Tag;
        string blank = TagOf(Present("TEMPLATE_BlankRoom")), od1 = TagOf(Present("TEMPLATE_OpenDoor1")), od2 = TagOf(Present("TEMPLATE_OpenDoor2"));
        string ld1 = TagOf(Present("TEMPLATE_LockedDoor1")), ld2 = TagOf(Present("TEMPLATE_LockedDoor2"));

        var rooms = graph.Nodes.Where(n => n.Tag == blank).ToList();
        var doors = graph.Nodes.Where(n => n.Tag == od1 || n.Tag == od2).ToList();
        int others = graph.Nodes.Count(n => n.Tag.Length != 0 && n.Tag != blank && n.Tag != od1 && n.Tag != od2 && n.Tag != "graph");
        if (rooms.Count != 11 || doors.Count != 26 || others != 4)
            throw new InvalidDataException($"Incorrect number of room nodes in Incursion Temple graph ({rooms.Count} / {doors.Count} / {others}, want 11 / 26 / 4)");
        if (keys.Count != 11) throw new InvalidDataException($"the IncursionTemplePlugin blob names {keys.Count} rooms, not 11");
        Sort(rooms, RoomRows);
        Sort(doors, DoorRows);

        var picks = new List<string>();
        for (int i = 0; i < 11; i++)
        {
            if (!byKey.TryGetValue(keys[i], out int row) || table.String(row, 48).Length == 0)
                throw new InvalidDataException($"Unknown Incursion Temple room hash {keys[i]:x4}");
            string arm = table.String(row, 48);
            rooms[i].Tag = TagOf(arm);
            if (!extra.Contains(arm, StringComparer.OrdinalIgnoreCase)) extra.Add(arm);
            picks.Add(table.String(row, 0));
        }
        // The trap tier: the first key (in blob order) that is one of the three trap rooms.
        int tier = 0;
        foreach (int key in keys)
        {
            if (!byKey.TryGetValue(key, out int row)) continue;
            tier = table.String(row, 0) switch { "TrapRoomI" => 1, "TrapRoomII" => 2, "TrapRoomIII" => 3, _ => 0 };
            if (tier != 0) break;
        }
        for (int i = 0; i < 26; i++)
            if ((doorBits >> i & 1) == 0) doors[i].Tag = doors[i].Tag == od1 ? ld1 : ld2;
        if (tier != 0)
        {
            string tr1 = Present($"TEMPLATE_OpenDoor1Trap{tier}"), tr2 = Present($"TEMPLATE_OpenDoor2Trap{tier}");
            string tt1 = TagOf(tr1), tt2 = TagOf(tr2);
            for (int i = 0; i < 26; i++)
                if ((doorBits >> i & 1) != 0) doors[i].Tag = doors[i].Tag == od1 ? tt1 : tt2;
            extra.Add(tr1);
            extra.Add(tr2);
        }
        notes.Add($"incursion temple: rooms [{string.Join(", ", picks)}], door bits 0x{doorBits:x} ({System.Numerics.BitOperations.PopCount(doorBits & 0x3ffffff)} open), trap tier {tier}");
        return extra;
    }

    /// <summary><c>0x141f4c390</c> then <c>0x141f4c3a0</c>: by y ascending, then by x inside each row of the layout.</summary>
    static void Sort(List<GraphNode> nodes, (int From, int To)[] rows)
    {
        var sorted = nodes.OrderBy(n => n.Y).ToList();
        foreach (var (from, to) in rows)
        {
            var slice = sorted.GetRange(from, to - from).OrderBy(n => n.X).ToList();
            for (int i = 0; i < slice.Count; i++) sorted[from + i] = slice[i];
        }
        for (int i = 0; i < nodes.Count; i++) nodes[i] = sorted[i];
    }
}
