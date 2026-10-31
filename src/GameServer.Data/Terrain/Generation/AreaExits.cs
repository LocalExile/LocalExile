using System.Text.RegularExpressions;

namespace GameServer.Data.Terrain.Generation;

/// <summary>What an area transition object of a generated layout is, from the graph node of the room it stands in.</summary>
public enum ExitKind
{
    /// <summary>The node names no exit for it (a sewer grate of a side area that did not roll, an unused transition room's
    /// arch), or the node's exits went to objects nearer their points: not created.</summary>
    Unused,
    /// <summary>"entranceN": the exit to the area's WorldAreas Connections[N − 1]; "entranceNdisabled" is only the
    /// arrival point from there (<see cref="PlacedExit.Enabled"/> false: not created).</summary>
    Entrance,
    /// <summary>"teleportN[-label]": a gate within the area to the slot of "telepadN" (<see cref="PlacedExit.Target"/>);
    /// "teleportNdisabled-…" starts locked in the game (an arena's way in, a shortcut back).</summary>
    Teleport,
}

/// <summary>An area transition object of a generated layout with its role: the object, what it is, the description's
/// number N, whether the description is enabled, the teleport's label key ("BrutusAreaName", "%areaname%" for the area's
/// own name) and target cell, the room it stands in and why (for the log).</summary>
public sealed record PlacedExit(PlacedTileObject Object, ExitKind Kind, int Number, bool Enabled, string? Label,
    (int X, int Y)? Target, string? Room, string Why);

/// <summary>A layout's named entrance point: where "entranceN" (enabled or not) sits, i.e. where a player arriving from
/// Connections[N − 1] belongs.</summary>
public sealed record EntrancePoint(int Number, bool Enabled, int X, int Y);

/// <summary>
/// The area transitions of a generated layout and where each leads, decided by the layout itself
/// (Agents/docs/protocol/08-area-transitions.md §8). Every transition object (a tile's .tmd record or a room's object line
/// whose type has the AreaTransition component) stands in a placed room, and that room's graph node descriptions name its
/// exits: "entranceN" leads to WorldAreas Connections[N − 1] ("…disabled": an arrival point only), "teleportN[-label]"
/// within the area to "telepadN", which may be in another room or the same one.
/// <para>A room places them at its named points ("slots"), in order: the k-th generic "telepad"/"teleport" point holds the
/// node's k-th "teleportN" (the gate) and its k-th "telepadN" (the arrival), the k-th "entrance" point its k-th
/// "entranceN"; a numbered point ("entrance2", "telepad1") holds its own number. So every arrival is next to the gate
/// back (The Harvest's boss1: "telepad7" "teleport8" "teleport9" "telepad10", point 0 = the arrival from the core and the
/// gate back, point 1 = the gate to boss2 and the arrival from it). Each slot takes the nearest object of the room;
/// an exit without a point of its kind takes a point left over (The Upper Prison's stairs room has an "entrance" point
/// and the node's only exit "teleport1"), else an object left over.</para>
/// <para>The model plays no part, so reskinned tiles (The Cavern of Wrath's corrupted_* records), objects no static file
/// lists (The Submerged Passage's watery_depth_entrance) and one model used for different exits all resolve.</para>
/// </summary>
public static class AreaExits
{
    static readonly Regex EntranceDescription = new(@"^entrance\s*(\d+)(-?disabled)?$", RegexOptions.IgnoreCase);
    static readonly Regex TeleportDescription = new(@"^teleport\s*(\d+)(disabled)?(?:[-=](.*))?$", RegexOptions.IgnoreCase);
    static readonly Regex TelepadDescription = new(@"^telepad\s*(\d+)(?:-.*)?$", RegexOptions.IgnoreCase);
    static readonly Regex EntrancePointName = new(@"^entrance(\d*)$", RegexOptions.IgnoreCase);
    static readonly Regex TeleportPointName = new(@"^(?:telepad|teleport)(\d*)$", RegexOptions.IgnoreCase);

    const string WaypointType = "Metadata/MiscellaneousObjects/Waypoint";

    /// <summary>The transition objects of a generated layout (no waypoints, no effects): the tiles' records
    /// (<see cref="AreaObjects.Transitions"/>) and the rooms' object lines of a transition type.</summary>
    public static List<PlacedTileObject> Objects(TerrainCache files, GenerationResult result)
    {
        var objects = AreaObjects.Transitions(files, result).Where(o => o.Type != WaypointType && !AreaObjects.IsEffect(o.Type)).ToList();
        objects.AddRange(AreaObjects.RoomObjects(result, t => t != WaypointType && files.IsTransitionType(t)));
        return objects;
    }

    /// <summary>Each transition object of the layout with its role, and where each "entranceN" is (<see cref="Entrances"/>).
    /// <paramref name="usable"/>: whether a player can walk up to an object at a cell (a walkable cell in reach).</summary>
    public static (List<PlacedExit> Exits, List<EntrancePoint> Entrances) Resolve(TerrainCache files, GenerationResult result,
        Func<int, int, bool>? usable = null)
    {
        var rooms = Rooms(files, result.Records.SelectMany(r => r.Rooms));
        var objects = Objects(files, result);
        return (Resolve(rooms, objects, usable), Entrances(rooms, objects));
    }

    /// <summary>Where each "entranceN" of the layout is (its slot, else its room's first transition object, else the
    /// room's centre).</summary>
    public static List<EntrancePoint> Entrances(IReadOnlyList<LayoutRoom> rooms, IReadOnlyList<PlacedTileObject> objects)
    {
        var points = new List<EntrancePoint>();
        foreach (var room in rooms)
            foreach (var d in room.Descriptions.Where(d => d.Kind == ExitKind.Entrance))
            {
                var inRoom = objects.FirstOrDefault(o => room.Contains(o.X, o.Y));
                (int X, int Y) at = room.SlotOf(d) is { } p ? (p.X, p.Y) : inRoom is { } o ? (o.X, o.Y) : room.Centre;
                points.Add(new EntrancePoint(d.Number, d.Enabled, at.X, at.Y));
            }
        return points;
    }

    /// <summary>The roles of <paramref name="objects"/> in a layout of rooms (<see cref="Rooms"/>).</summary>
    public static List<PlacedExit> Resolve(IReadOnlyList<LayoutRoom> rooms, IReadOnlyList<PlacedTileObject> objects,
        Func<int, int, bool>? usable = null)
    {
        var exits = new List<PlacedExit>();
        var byRoom = new Dictionary<LayoutRoom, List<PlacedTileObject>>();
        foreach (var o in objects)
        {
            // The room it stands in: of those whose footprint holds it, one whose node names an exit (a background fill
            // room around The Rogue Harbour's rooms names none), the last placed (rooms placed later lie on top).
            // An object a few cells outside such a room, in one that names none, is the exit room's (The Cathedral
            // Rooftop's Kitava ladder, 3 cells off bossentry.arm in a fill room).
            var room = rooms.LastOrDefault(r => r.Contains(o.X, o.Y) && r.Descriptions.Any(d => !d.IsTelepad))
                ?? rooms.LastOrDefault(r => r.Contains(o.X, o.Y, 23) && r.Descriptions.Any(d => !d.IsTelepad))
                ?? rooms.LastOrDefault(r => r.Contains(o.X, o.Y));
            if (room is null) { exits.Add(new PlacedExit(o, ExitKind.Unused, 0, false, null, null, null, "in no placed room")); continue; }
            if (!byRoom.TryGetValue(room, out var list)) byRoom[room] = list = [];
            list.Add(o);
        }
        var roomOf = new Dictionary<PlacedTileObject, LayoutRoom>(ReferenceEqualityComparer.Instance);
        foreach (var (room, list) in byRoom)
        {
            exits.AddRange(Assign(room, list, rooms, usable));
            foreach (var o in list) roomOf[o] = room;
        }
        ArriveByTheGateBack(exits, roomOf, rooms);
        // The objects' order (the client's tile order).
        var order = new Dictionary<PlacedTileObject, int>(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < objects.Count; i++) order.TryAdd(objects[i], i);
        return [.. exits.OrderBy(e => order.GetValueOrDefault(e.Object, int.MaxValue))];
    }

    /// <summary>A named point of a placed room, in the layout's cells.</summary>
    public sealed record RoomPoint(string Name, int X, int Y);

    /// <summary>An exit description of a room's graph node: kind, N, enabled, label; telepads are kind
    /// <see cref="ExitKind.Unused"/> with <see cref="IsTelepad"/>.</summary>
    public sealed record Description(ExitKind Kind, int Number, bool Enabled, string? Label, bool IsTelepad = false);

    /// <summary>A placed room with its named points and its node's exit descriptions, each description with its slot.</summary>
    public sealed class LayoutRoom
    {
        public required PlacedRoom Room { get; init; }
        public required IReadOnlyList<RoomPoint> Points { get; init; }
        public required IReadOnlyList<Description> Descriptions { get; init; }
        readonly Dictionary<Description, RoomPoint> _slots = new(ReferenceEqualityComparer.Instance);

        public string Name => Path.GetFileName(Room.Room.Path);
        public bool Contains(int x, int y, int margin = 0) =>
            x >= Room.X * 23 - margin && y >= Room.Y * 23 - margin && x < (Room.X + Room.Width) * 23 + margin
            && y < (Room.Y + Room.Height) * 23 + margin;
        public (int X, int Y) Centre => (Room.X * 23 + Room.Width * 23 / 2, Room.Y * 23 + Room.Height * 23 / 2);
        public RoomPoint? SlotOf(Description d) => _slots.GetValueOrDefault(d);

        // Slots: numbered points by number; then the k-th generic point of a kind for the k-th description of that kind
        // (teleports and telepads each count from the first "telepad"/"teleport" point); then points left over, in order.
        int Number(RoomPoint p, Regex r) => r.Match(p.Name).Groups[1].Value is { Length: > 0 } n ? int.Parse(n) : 0;
        List<RoomPoint> EntrancePoints => [.. Points.Where(p => EntrancePointName.IsMatch(p.Name))];
        List<RoomPoint> TeleportPoints => [.. Points.Where(p => TeleportPointName.IsMatch(p.Name))];

        // Phase 1, the room alone: entrances (numbered points by number, then the k-th generic "entrance" point for the
        // k-th enabled one: a disabled one is only an arrival point, The Control Blocks' "entrance2disabled"
        // "entrance1") and telepads (by number, then the k-th generic "telepad"/"teleport" point).
        internal void PlaceArrivals()
        {
            void Place(IEnumerable<Description> descriptions, List<RoomPoint> points, Regex name)
            {
                var list = descriptions.ToList();
                foreach (var d in list)
                    if (points.FirstOrDefault(p => Number(p, name) == d.Number) is { } numbered) _slots[d] = numbered;
                var generic = points.Where(p => Number(p, name) == 0).ToList();
                int k = 0;
                foreach (var d in list.Where(d => !_slots.ContainsKey(d)))
                    if (k < generic.Count) _slots[d] = generic[k++];
            }
            Place(Descriptions.Where(d => d.Kind == ExitKind.Entrance).OrderBy(d => d.Enabled ? 0 : 1), EntrancePoints, EntrancePointName);
            Place(Descriptions.Where(d => d.IsTelepad), TeleportPoints, TeleportPointName);
        }

        // Phase 2, with every room's telepads: a gate stands beside the arrival from the place it leads to: "teleportM"
        // takes the slot of this room's "telepadK" whose "teleportK" stands in the room of "telepadM" (K ≠ M). The
        // Cathedral Rooftop's bossentry ("telepad1" "telepad2" "telepad3" "teleport4" "teleport3-ArenaNameKitava"
        // "teleport1-%areaname%" "teleport5-…Ladder"): the arena gate teleport3 is at telepad1's point, its way out
        // teleport1 at telepad3's, the way to the dock teleport4 at telepad2's (the dock's teleport2 lands there). Else a
        // numbered point, else the k-th generic one no gate took; then points left over; a telepad without a point
        // arrives at its room's gate.
        internal void PlaceGates(IReadOnlyList<LayoutRoom> rooms)
        {
            LayoutRoom? RoomOf(Func<Description, bool> which) =>
                rooms.Where(r => r.Descriptions.Any(which)).OrderBy(r => ReferenceEquals(r, this) ? 1 : 0).FirstOrDefault();
            var teleportPoints = TeleportPoints;
            var gates = Descriptions.Where(d => d.Kind == ExitKind.Teleport).ToList();
            var gateTaken = new HashSet<RoomPoint>(ReferenceEqualityComparer.Instance);
            foreach (var g in gates)
            {
                if (teleportPoints.FirstOrDefault(p => Number(p, TeleportPointName) == g.Number) is { } numbered) { _slots[g] = numbered; continue; }
                var target = RoomOf(d => d.IsTelepad && d.Number == g.Number);
                var arrival = Descriptions.Where(d => d.IsTelepad && d.Number != g.Number && _slots.ContainsKey(d))
                    .FirstOrDefault(d => target is not null && ReferenceEquals(RoomOf(t => t.Kind == ExitKind.Teleport && t.Number == d.Number), target));
                if (arrival is not null) { _slots[g] = _slots[arrival]; gateTaken.Add(_slots[g]); }
            }
            var generic = teleportPoints.Where(p => Number(p, TeleportPointName) == 0).ToList();
            foreach (var g in gates.Where(g => !_slots.ContainsKey(g)))
                if (generic.FirstOrDefault(p => !gateTaken.Contains(p)) is { } free) { _slots[g] = free; gateTaken.Add(free); }
            var taken = new HashSet<RoomPoint>(_slots.Where(s => !s.Key.IsTelepad).Select(s => s.Value), ReferenceEqualityComparer.Instance);
            var left = EntrancePoints.Concat(teleportPoints).Where(p => !taken.Contains(p)).ToList();
            foreach (var d in Descriptions.Where(d => !_slots.ContainsKey(d) && !d.IsTelepad))
            {
                if (left.Count == 0) break;
                _slots[d] = left[0];
                left.RemoveAt(0);
            }
            foreach (var d in Descriptions.Where(d => d.IsTelepad && !_slots.ContainsKey(d)))
                if (_slots.FirstOrDefault(s => s.Key.Kind == ExitKind.Teleport && s.Key.Number != d.Number).Value is { } gate) _slots[d] = gate;
                else if (Points.FirstOrDefault(p => EntrancePointName.IsMatch(p.Name) || TeleportPointName.IsMatch(p.Name)) is { } any) _slots[d] = any;
        }
    }

    /// <summary>The placed rooms with their points, descriptions and slots.</summary>
    public static List<LayoutRoom> Rooms(TerrainCache files, IEnumerable<PlacedRoom> rooms)
    {
        var list = rooms.Select(r => new LayoutRoom { Room = r, Points = Points(files, r), Descriptions = Descriptions(r) }).ToList();
        foreach (var room in list) room.PlaceArrivals();
        foreach (var room in list) room.PlaceGates(list);
        return list;
    }

    // The room's named points (room cells, turned by its orientation like its doodads, 0x142040770) other than spawns
    // (named by a Metadata path), in file order.
    static List<RoomPoint> Points(TerrainCache files, PlacedRoom room)
    {
        if (files.ArmFile(room.Room.Path) is not { } file) return [];
        int w = room.Room.Width * 23, h = room.Room.Height * 23;
        var points = new List<RoomPoint>();
        foreach (var p in file.Points)
        {
            if (p.Name.Contains('/')) continue;
            var (cx, cy) = Orientation.Map(room.Orientation, (int)p.X, (int)p.Y, w, h);
            points.Add(new RoomPoint(p.Name, room.X * 23 + cx, room.Y * 23 + cy));
        }
        return points;
    }

    static List<Description> Descriptions(PlacedRoom room)
    {
        var list = new List<Description>();
        foreach (var d in room.Descriptions)
        {
            if (EntranceDescription.Match(d) is { Success: true } e)
                list.Add(new Description(ExitKind.Entrance, int.Parse(e.Groups[1].Value), !e.Groups[2].Success, null));
            else if (TeleportDescription.Match(d) is { Success: true } t)
                list.Add(new Description(ExitKind.Teleport, int.Parse(t.Groups[1].Value), !t.Groups[2].Success,
                    t.Groups[3].Success && t.Groups[3].Value.Length > 0 ? t.Groups[3].Value : null));
            else if (TelepadDescription.Match(d) is { Success: true } p)
                list.Add(new Description(ExitKind.Unused, int.Parse(p.Groups[1].Value), true, null, IsTelepad: true));
        }
        return list;
    }

    static long Distance(int x, int y, PlacedTileObject o) => (long)(x - o.X) * (x - o.X) + (long)(y - o.Y) * (y - o.Y);

    static IEnumerable<PlacedExit> Assign(LayoutRoom room, List<PlacedTileObject> objects, IReadOnlyList<LayoutRoom> rooms,
        Func<int, int, bool>? usable)
    {
        var exitsOfNode = room.Descriptions.Where(d => !d.IsTelepad).OrderBy(d => d.Enabled || d.Kind == ExitKind.Teleport ? 0 : 1).ToList();
        if (exitsOfNode.Count == 0)
        {
            foreach (var o in objects)
                yield return new PlacedExit(o, ExitKind.Unused, 0, false, null, null, room.Name, $"{room.Name}'s node names no exit");
            yield break;
        }
        // Each exit with a slot takes the nearest object (closest pairs first); exits without one take what is left. An
        // object no player can walk up to comes after every usable one (The Harbour Bridge arena's transition1 door, 44
        // cells from walkable ground; The Lower Prison's ceiling marker). Of gates tied for an object, one leading to
        // another room picks first.
        var pick = new Dictionary<PlacedTileObject, (Description D, string Why)>(ReferenceEqualityComparer.Instance);
        bool Usable(PlacedTileObject o) => usable?.Invoke(o.X, o.Y) != false;
        bool Inward(Description d) => d.Kind == ExitKind.Teleport
            && rooms.Where(r => r.Descriptions.Any(t => t.IsTelepad && t.Number == d.Number)).All(r => ReferenceEquals(r, room));
        var pairs = from d in exitsOfNode
                    let slot = room.SlotOf(d)
                    where slot is not null
                    from o in objects
                    orderby Usable(o) ? 0 : 1, Distance(slot.X, slot.Y, o), Inward(d) ? 1 : 0
                    select (d, o, slot);
        var done = new HashSet<Description>(ReferenceEqualityComparer.Instance);
        foreach (var (d, o, slot) in pairs)
        {
            if (done.Contains(d) || pick.ContainsKey(o)) continue;
            pick[o] = (d, exitsOfNode.Count == 1 ? $"{room.Name}'s only exit" : $"{room.Name}, at point \"{slot.Name}\"");
            done.Add(d);
        }
        foreach (var d in exitsOfNode.Where(d => !done.Contains(d)))
            if (objects.Where(o => !pick.ContainsKey(o)).OrderBy(o => Usable(o) ? 0 : 1).FirstOrDefault() is { } o)
                pick[o] = (d, $"{room.Name}, no point for it");
        foreach (var o in objects)
        {
            if (!pick.TryGetValue(o, out var p))
            {
                yield return new PlacedExit(o, ExitKind.Unused, 0, false, null, null, room.Name, $"{room.Name}'s exits went to objects nearer their points");
                continue;
            }
            var (d, why) = p;
            (int X, int Y)? target = null;
            if (d.Kind == ExitKind.Teleport)
            {
                target = Telepad(rooms, d.Number, room);
                if (target is null) why += $", no telepad{d.Number}";
            }
            yield return new PlacedExit(o, d.Kind, d.Number, d.Enabled, d.Label, target, room.Name, why);
        }
    }

    // An arrival is next to the gate back: "teleportN" in room S lands in the room R that names "telepadN", beside R's
    // gate (another number's) whose own telepad is in S, at that gate's object. The Hall of Grandmasters' main.arm has
    // eight gates to the arena entrances and eight arrivals from them at "telepad" points that repeat; each arrival is at
    // the door back to its arena. Without such a gate the telepad's slot stays.
    static void ArriveByTheGateBack(List<PlacedExit> exits, Dictionary<PlacedTileObject, LayoutRoom> roomOf, IReadOnlyList<LayoutRoom> rooms)
    {
        LayoutRoom? PadRoom(int n, LayoutRoom from)
        {
            var pads = rooms.Where(r => r.Descriptions.Any(d => d.IsTelepad && d.Number == n)).ToList();
            return pads.FirstOrDefault(r => !ReferenceEquals(r, from)) ?? pads.FirstOrDefault();
        }
        var gates = exits.Where(e => e.Kind == ExitKind.Teleport).ToList();
        for (int i = 0; i < exits.Count; i++)
        {
            var e = exits[i];
            if (e.Kind != ExitKind.Teleport || !roomOf.TryGetValue(e.Object, out var from) || PadRoom(e.Number, from) is not { } to) continue;
            var back = gates.FirstOrDefault(g => g.Number != e.Number && roomOf.TryGetValue(g.Object, out var r) && ReferenceEquals(r, to)
                && PadRoom(g.Number, to) is { } there && ReferenceEquals(there, from));
            if (back is not null) exits[i] = e with { Target = (back.Object.X, back.Object.Y) };
        }
    }

    // Where "teleportN" takes the player: the slot of "telepadN" (in another room than the gate's when several rooms name
    // it), else that room's centre.
    static (int X, int Y)? Telepad(IReadOnlyList<LayoutRoom> rooms, int n, LayoutRoom from)
    {
        var pads = rooms.SelectMany(r => r.Descriptions.Where(d => d.IsTelepad && d.Number == n).Select(d => (Room: r, D: d))).ToList();
        if (pads.Count == 0) return null;
        var (room, d) = pads.FirstOrDefault(p => !ReferenceEquals(p.Room, from)) is { Room: not null } other ? other : pads[0];
        return room.SlotOf(d) is { } slot ? (slot.X, slot.Y) : room.Centre;
    }
}
