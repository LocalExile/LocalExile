using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using GameServer.Data.Ggpk;
using GameServer.Core.Protocol.Instance;
using GameServer.Data.Terrain;

namespace GameServer.Modules.Incursion;

/// <summary>
/// One character's Temple of Atzoatl in the making: the 11 rooms, their tiers, which of their doors are closed, and
/// what the incursions made of them. The graph is the client's own <c>temple_3.dgr</c> (11 <c>room</c> nodes, the
/// <c>entrance</c>, the Apex <c>boss</c>; the client's temple generator refuses any other count of rooms and of the 26
/// doors that join the adjacent pairs). Each room starts as a tier-0 filler or the tier-1 room of a family not yet in
/// the temple, never twice in one plan, and each door closed or open. A run's outcome [external]: killing the resident
/// makes the room the other architect's tier-1 room, killing the other raises the resident's room one tier (its
/// <c>RoomUpgrade_IncursionRoomsKey</c>, at most tier 3), a tier-0 room becomes the survivor's tier-1 room, and a
/// killed architect does not come back to this temple. 12 incursions open it [external].
/// <para>
/// The temple belongs to the character: it is drawn from the character's name and saved under the character
/// (<see cref="ToJson"/> / <see cref="FromJson"/>, through <c>IPlayerWorld.CharacterData</c>), so the incursions add up
/// across areas, logins and restarts.
/// </para>
/// </summary>
public sealed class IncursionTemple
{
    public const string GraphPath = "Metadata/Terrain/Leagues/Incursion/Graphs/temple_3.dgr";

    /// <summary>How many incursions open the temple [external].</summary>
    public const int RunsToOpen = 12;
    /// <summary>Share of the starting slots that hold a tier-1 room of a new family [ESTIMATE].</summary>
    public const double Tier1Share = 0.5;
    /// <summary>Share of the starting doors that are closed [ESTIMATE].</summary>
    public const double DoorClosedShare = 0.75;
    /// <summary>The room and door nodes the client's temple generator requires ("Incorrect number of room nodes in
    /// Incursion Temple graph", "... door nodes ..."): what <c>temple_3.dgr</c> has.</summary>
    public const int RoomNodes = 11, DoorNodes = 26;

    /// <summary>One node of the graph: its position and its kind (<c>room</c>, <c>entrance</c>, <c>boss</c>,
    /// <c>door</c>, <c>2door</c>).</summary>
    public sealed record Node(int X, int Y, string Kind);

    /// <summary>The graph: the nodes, the room slots in reading order (by y, then x), and the adjacent pairs.</summary>
    public sealed record Graph(IReadOnlyList<Node> Nodes, IReadOnlyList<int> Slots, IReadOnlyList<(int A, int B)> Edges);

    /// <summary>One incursion's pick: the slot it opens, its room, the resident architect's IncursionArchitect row (null
    /// for a tier-0 slot), and the other architect(s): <c>First</c> alone with a resident, <c>First</c> and
    /// <c>Second</c> without.</summary>
    public sealed record PickedRoom(int Slot, int Room, int? Resident, int First, int? Second);

    readonly Graph _graph;
    readonly Dictionary<int, int> _rooms = [];
    readonly Dictionary<(int, int), bool> _closed = [];
    readonly List<int> _levels = [];
    readonly HashSet<int> _killed = [];

    /// <summary>Slot node → IncursionRooms row.</summary>
    public IReadOnlyDictionary<int, int> Rooms => _rooms;
    /// <summary>The temple's slots in the graph's reading order; the room a slot holds is <see cref="Rooms"/>[slot].</summary>
    public IReadOnlyList<int> Slots => _graph.Slots;
    /// <summary>Incursions finished.</summary>
    public int Done { get; private set; }
    /// <summary>The incursions' area levels, in order.</summary>
    public IReadOnlyList<int> Levels => _levels;
    /// <summary>Architects killed in this temple.</summary>
    public IReadOnlySet<int> KilledArchitects => _killed;
    /// <summary>True once 12 incursions are done.</summary>
    public bool IsOpen => Done >= RunsToOpen;

    IncursionTemple(Graph graph) => _graph = graph;

    IncursionTemple(Graph graph, Random rng, int level) : this(graph)
    {
        var families = IncursionTables.TempleFamilies;
        var usedFamilies = new HashSet<int>();
        var usedRooms = new HashSet<int>();
        void Family(List<int> candidates, int slot)
        {
            int family = candidates[rng.Next(candidates.Count)];
            usedFamilies.Add(family);
            usedRooms.Add(_rooms[slot] = families[family][1]);
        }
        foreach (int slot in graph.Slots)
        {
            var free = families.Where(f => !usedFamilies.Contains(f.Key) && f.Value.ContainsKey(1)
                    && IncursionTables.ArchitectByRow(f.Key) is { MinLevel: var min } && min <= level)
                .Select(f => f.Key).ToList();
            if (free.Count > 0 && rng.NextDouble() < Tier1Share) { Family(free, slot); continue; }
            // A filler the plan does not hold yet; with 10 fillers for 11 slots the last can run out, and then takes a
            // tier-1 room of a family not yet in the temple [ESTIMATE].
            var fillers = IncursionTables.FillerRooms.Where(r => !usedRooms.Contains(r.Row)).ToList();
            if (fillers.Count > 0) usedRooms.Add(_rooms[slot] = fillers[rng.Next(fillers.Count)].Row);
            else if (free.Count > 0) Family(free, slot);
            else _rooms[slot] = IncursionTables.FillerRooms.Count == 0 ? 0 : IncursionTables.FillerRooms[rng.Next(IncursionTables.FillerRooms.Count)].Row;
        }
        foreach (var edge in graph.Edges) _closed[edge] = rng.NextDouble() < DoorClosedShare;
    }

    /// <summary>The slot's architect family, null for a tier-0 slot.</summary>
    public int? Family(int slot) => IncursionTables.AllRooms[_rooms[slot]].ArchitectKey;

    /// <summary>The slot's room tier.</summary>
    public int Tier(int slot) => IncursionTables.AllRooms[_rooms[slot]].Tier;

    HashSet<int> FamiliesPresent() => [.. _rooms.Keys.Select(Family).OfType<int>()];

    /// <summary>
    /// An incursion: any slot below tier 3, its room, its resident architect (unless killed) and one other architect
    /// not in the temple and not killed [external]; two others for a tier-0 slot. Null when the temple is open, no slot or no free
    /// architect is left. A slot already opened can be drawn again: a temple is 12 incursions over 11 slots, and a
    /// room's tiers 2 and 3 come from opening the same slot again.
    /// </summary>
    public PickedRoom? Pick(int level, Random rng)
    {
        if (IsOpen) return null;
        // A slot whose resident the server cannot write at this level is not drawn: the run's architects come from the same
        // test (IncursionTables.Usable), so what is picked is what is created.
        var slots = _graph.Slots.Where(s => Tier(s) < 3 && (Family(s) is not { } f || _killed.Contains(f) || IncursionTables.Usable(f, level))).ToList();
        if (slots.Count == 0) return null;
        int slot = slots[rng.Next(slots.Count)];
        var present = FamiliesPresent();
        var free = IncursionTables.TempleFamilies
            .Where(f => !present.Contains(f.Key) && !_killed.Contains(f.Key) && f.Value.ContainsKey(1) && IncursionTables.Usable(f.Key, level))
            .Select(f => f.Key).ToList();
        int? resident = Family(slot);
        if (resident is { } r && _killed.Contains(r)) resident = null;
        if (resident is not null)
            return free.Count == 0 ? null : new PickedRoom(slot, _rooms[slot], resident, free[rng.Next(free.Count)], null);
        if (free.Count < 2) return null;
        int a = free[rng.Next(free.Count)], b;
        do b = free[rng.Next(free.Count)]; while (b == a);
        return new PickedRoom(slot, _rooms[slot], null, a, b);
    }

    /// <summary>One incursion's outcome: <paramref name="killed"/> is the architect rows killed in it, first first; the
    /// first decides (killing either lets the other flee [external]). Returns the room the slot holds now.</summary>
    public int Apply(PickedRoom pick, IReadOnlyList<int> killed, int areaLevel)
    {
        int slot = pick.Slot;
        // An open temple is finished: later runs change nothing.
        if (IsOpen) return _rooms[slot];
        Done++;
        _levels.Add(areaLevel);
        int? first = killed.Count > 0 ? killed[0] : null;
        if (first is { } dead) _killed.Add(dead);
        if (pick.Resident is not null)
        {
            if (first == pick.Resident) _rooms[slot] = IncursionTables.TempleFamilies[pick.First][1];
            else if (first == pick.First && IncursionTables.AllRooms[_rooms[slot]].RoomUpgrade is { } up) _rooms[slot] = up;
        }
        else if (first is not null)
            _rooms[slot] = IncursionTables.TempleFamilies[first == pick.First ? pick.Second!.Value : pick.First][1];
        return _rooms[slot];
    }

    /// <summary>The closed doors of a slot, by the past room's direction names (graph +y is map +y [ESTIMATE]).</summary>
    public IReadOnlyList<string> DoorsFor(int slot)
    {
        var doors = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var ((a, b), closed) in _closed)
            if (closed && (slot == a || slot == b) && Direction(slot, a == slot ? b : a) is var direction
                && IncursionTables.DoorSpots.ContainsKey(direction))
                doors.Add(direction);
        return [.. doors];
    }

    string Direction(int from, int to)
    {
        var a = _graph.Nodes[from];
        var b = _graph.Nodes[to];
        int dx = b.X - a.X, dy = b.Y - a.Y;
        if (Math.Abs(dy) < 15) return dx > 0 ? "r" : "l";
        return (dy > 0 ? "b" : "t") + (dx > 0 ? "r" : "l");
    }

    /// <summary>A character's temple, drawn from the character's name; null without the graph.</summary>
    public static IncursionTemple? Create(string character, int level)
    {
        if (_temple is not { } graph) return null;
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes($"Incursion temple|{character}"));
        return new IncursionTemple(graph, new Random(BitConverter.ToInt32(hash, 0)), level);
    }

    public IncursionTempleData ItemData(int areaLevel, Random rng, bool randomizeTiers)
    {
        var rooms = new List<ushort>();
        foreach (int slot in Slots)
        {
            int row = _rooms[slot];
            if (randomizeTiers && Family(slot) is { } family && IncursionTables.TempleFamilies.TryGetValue(family, out var tiers))
            {
                var available = tiers.Where(t => t.Key is >= 1 and <= 3).OrderBy(t => t.Key).Select(t => t.Value).ToArray();
                if (available.Length > 0) row = available[rng.Next(available.Length)];
            }
            rooms.Add(IncursionTables.RoomHash16(row));
        }
        uint connections = 0;
        for (int i = 0; i < _graph.Edges.Count && i < 32; i++)
            if (!_closed.GetValueOrDefault(_graph.Edges[i], true)) connections |= 1u << i;
        return new((uint)Math.Clamp(areaLevel, 1, 100), rooms, connections);
    }

    public const int SaveVersion = 1;

    /// <summary>The temple as saved text, <c>{"v":1,"rooms":[row…],"doors":[0|1…],"done":n,"levels":[level…],
    /// "killed":[row…]}</c>: the rooms in slot order and the doors (1 closed) in edge order.</summary>
    public string ToJson() => JsonSerializer.Serialize(new Save(SaveVersion, [.. _graph.Slots.Select(s => _rooms[s])],
        [.. _graph.Edges.Select(e => _closed[e] ? 1 : 0)], Done, [.. _levels], [.. _killed.Order()]));

    sealed record Save([property: JsonPropertyName("v")] int Version, [property: JsonPropertyName("rooms")] int[]? Rooms,
        [property: JsonPropertyName("doors")] int[]? Doors, [property: JsonPropertyName("done")] int Done,
        [property: JsonPropertyName("levels")] int[]? Levels, [property: JsonPropertyName("killed")] int[]? Killed);

    /// <summary>The temple a saved text holds; null when it is not one for this graph and these tables (the caller then
    /// draws a fresh one).</summary>
    public static IncursionTemple? FromJson(string? text)
    {
        if (text is null || _temple is not { } graph) return null;
        Save? save;
        try { save = JsonSerializer.Deserialize<Save>(text); }
        catch (JsonException) { return null; }
        if (save is not { Version: SaveVersion, Rooms: { } rooms, Doors: { } doors, Levels: { } levels, Killed: { } killed }
            || rooms.Length != graph.Slots.Count || doors.Length != graph.Edges.Count || save.Done < 0 || levels.Length != save.Done
            || rooms.Any(r => r < 0 || r >= IncursionTables.AllRooms.Count) || doors.Any(d => d is not (0 or 1))
            || levels.Any(l => l <= 0) || killed.Any(row => IncursionTables.ArchitectByRow(row) is null))
            return null;
        var temple = new IncursionTemple(graph) { Done = save.Done };
        for (int i = 0; i < rooms.Length; i++) temple._rooms[graph.Slots[i]] = rooms[i];
        for (int i = 0; i < doors.Length; i++) temple._closed[graph.Edges[i]] = doors[i] == 1;
        temple._levels.AddRange(levels);
        temple._killed.UnionWith(killed);
        return temple;
    }

    static Graph? _temple;

    /// <summary>The graph read from the game pack (the module's LoadData); null without it.</summary>
    public static Graph? TempleGraph => _temple;

    /// <summary>Reads <c>temple_3.dgr</c>: two room nodes are adjacent when beside each other (|dy| &lt; 15, 75 &lt; |dx|
    /// &lt; 105) or diagonal to the row above or below (55 &lt; |dy| &lt; 95, 30 &lt; |dx| &lt; 60).</summary>
    public static void LoadGameData()
    {
        _temple = null;
        byte[]? bytes;
        try { bytes = GamePack.Default.TryRead(GraphPath.ToLowerInvariant()); }
        catch (Exception ex) when (ex is NotSupportedException or IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return;
        }
        if (bytes is null) return;
        var nodes = GraphFile.Parse(TerrainText.Decode(bytes)).Nodes.Select(n => new Node(n.X, n.Y, n.Tag)).ToList();
        var rooms = Enumerable.Range(0, nodes.Count).Where(i => nodes[i].Kind is "room" or "entrance" or "boss").ToList();
        var slots = rooms.Where(i => nodes[i].Kind == "room").OrderBy(i => (nodes[i].Y, nodes[i].X)).ToList();
        if (slots.Count == 0 || !rooms.Any(i => nodes[i].Kind == "entrance") || !rooms.Any(i => nodes[i].Kind == "boss")) return;
        var edges = new List<(int, int)>();
        for (int a = 0; a < rooms.Count; a++)
            for (int b = a + 1; b < rooms.Count; b++)
            {
                int dx = Math.Abs(nodes[rooms[b]].X - nodes[rooms[a]].X), dy = Math.Abs(nodes[rooms[b]].Y - nodes[rooms[a]].Y);
                if ((dy < 15 && dx is > 75 and < 105) || (dy is > 55 and < 95 && dx is > 30 and < 60)) edges.Add((rooms[a], rooms[b]));
            }
        _temple = new Graph(nodes, slots, edges);
    }
}
