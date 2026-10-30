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
    static readonly Regex EntranceDescription = new(@"^entrance(\d+)(disabled)?$", RegexOptions.IgnoreCase);
    static readonly Regex TeleportDescription = new(@"^teleport(\d+)(disabled)?(?:-(.*))?$", RegexOptions.IgnoreCase);
    static readonly Regex TelepadDescription = new(@"^telepad(\d+)$", RegexOptions.IgnoreCase);
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

    /// <summary>Each transition object of the layout with its role.</summary>
    public static List<PlacedExit> Resolve(TerrainCache files, GenerationResult result) =>
        Resolve(Rooms(files, result.Records.SelectMany(r => r.Rooms)), Objects(files, result));

    /// <summary>Where each "entranceN" of the layout is (its slot, else its room's first transition object, else the
    /// room's centre).</summary>
    public static List<EntrancePoint> Entrances(TerrainCache files, GenerationResult result)
    {
        var objects = Objects(files, result);
        var points = new List<EntrancePoint>();
        foreach (var room in Rooms(files, result.Records.SelectMany(r => r.Rooms)))
            foreach (var d in room.Descriptions.Where(d => d.Kind == ExitKind.Entrance))
            {
                (int X, int Y) at = room.SlotOf(d) is { } p ? (p.X, p.Y)
                    : objects.FirstOrDefault(o => room.Contains(o.X, o.Y)) is { } o ? (o.X, o.Y) : room.Centre;
                points.Add(new EntrancePoint(d.Number, d.Enabled, at.X, at.Y));
            }
        return points;
    }

    /// <summary>The roles of <paramref name="objects"/> in a layout of rooms (<see cref="Rooms"/>).</summary>
    public static List<PlacedExit> Resolve(IReadOnlyList<LayoutRoom> rooms, IReadOnlyList<PlacedTileObject> objects)
    {
        var exits = new List<PlacedExit>();
        var byRoom = new Dictionary<LayoutRoom, List<PlacedTileObject>>();
        foreach (var o in objects)
        {
            // The room it stands in: the last placed whose footprint holds it (rooms placed later lie on top).
            var room = rooms.LastOrDefault(r => r.Contains(o.X, o.Y));
            if (room is null) { exits.Add(new PlacedExit(o, ExitKind.Unused, 0, false, null, null, null, "in no placed room")); continue; }
            if (!byRoom.TryGetValue(room, out var list)) byRoom[room] = list = [];
            list.Add(o);
        }
        foreach (var (room, list) in byRoom)
            exits.AddRange(Assign(room, list, rooms));
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
        public bool Contains(int x, int y) =>
            x >= Room.X * 23 && y >= Room.Y * 23 && x < (Room.X + Room.Width) * 23 && y < (Room.Y + Room.Height) * 23;
        public (int X, int Y) Centre => (Room.X * 23 + Room.Width * 23 / 2, Room.Y * 23 + Room.Height * 23 / 2);
        public RoomPoint? SlotOf(Description d) => _slots.GetValueOrDefault(d);

        // Slots: numbered points by number; then the k-th generic point of a kind for the k-th description of that kind
        // (teleports and telepads each count from the first "telepad"/"teleport" point); then points left over, in order.
        internal void PlaceSlots()
        {
            var entrancePoints = Points.Where(p => EntrancePointName.IsMatch(p.Name)).ToList();
            var teleportPoints = Points.Where(p => TeleportPointName.IsMatch(p.Name)).ToList();
            int Number(RoomPoint p, Regex r) => r.Match(p.Name).Groups[1].Value is { Length: > 0 } n ? int.Parse(n) : 0;
            var taken = new HashSet<RoomPoint>(ReferenceEqualityComparer.Instance);
            void Place(IEnumerable<Description> descriptions, List<RoomPoint> points, Regex name, bool telepads)
            {
                var list = descriptions.ToList();
                foreach (var d in list)
                    if (points.FirstOrDefault(p => Number(p, name) == d.Number) is { } numbered) { _slots[d] = numbered; taken.Add(numbered); }
                var generic = points.Where(p => Number(p, name) == 0).ToList();
                var free = new List<RoomPoint>(generic);
                foreach (var d in list.Where(d => !_slots.ContainsKey(d)))
                {
                    // A telepad shares a gate's point but never its own gate's: the arrival comes from where that point's
                    // gate leads (The Weaver's treasure room: "teleport1" and "telepad2" at one point).
                    var point = telepads
                        ? free.FirstOrDefault(p => !_slots.Any(s => s.Key.Kind == ExitKind.Teleport && s.Key.Number == d.Number && ReferenceEquals(s.Value, p))) ?? free.FirstOrDefault()
                        : free.FirstOrDefault();
                    if (point is null) break;
                    _slots[d] = point;
                    free.Remove(point);
                    if (!telepads) taken.Add(point);
                }
            }
            // Enabled entrances first: a disabled one is only an arrival point and is never created (The Control Blocks'
            // entrance room: "entrance2disabled" "entrance1", one "entrance" point).
            Place(Descriptions.Where(d => d.Kind == ExitKind.Entrance).OrderBy(d => d.Enabled ? 0 : 1), entrancePoints, EntrancePointName, false);
            Place(Descriptions.Where(d => d.Kind == ExitKind.Teleport), teleportPoints, TeleportPointName, false);
            // Telepads share the gates' points (an arrival is next to the gate back).
            Place(Descriptions.Where(d => d.IsTelepad), teleportPoints, TeleportPointName, true);
            var left = entrancePoints.Concat(teleportPoints).Where(p => !taken.Contains(p)).ToList();
            foreach (var d in Descriptions.Where(d => !_slots.ContainsKey(d) && !d.IsTelepad))
            {
                if (left.Count == 0) break;
                _slots[d] = left[0];
                left.RemoveAt(0);
            }
            // A telepad in a room without teleport points arrives at the room's gate (another number's), else its first point.
            foreach (var d in Descriptions.Where(d => d.IsTelepad && !_slots.ContainsKey(d)))
                if (_slots.FirstOrDefault(s => s.Key.Kind == ExitKind.Teleport && s.Key.Number != d.Number).Value is { } gate) _slots[d] = gate;
                else if (Points.FirstOrDefault(p => EntrancePointName.IsMatch(p.Name) || TeleportPointName.IsMatch(p.Name)) is { } any) _slots[d] = any;
        }
    }

    /// <summary>The placed rooms with their points, descriptions and slots.</summary>
    public static List<LayoutRoom> Rooms(TerrainCache files, IEnumerable<PlacedRoom> rooms)
    {
        var list = new List<LayoutRoom>();
        foreach (var r in rooms)
        {
            var room = new LayoutRoom { Room = r, Points = Points(files, r), Descriptions = Descriptions(r) };
            room.PlaceSlots();
            list.Add(room);
        }
        return list;
    }

    // The room's named points (room cells, turned by its orientation like its doodads, 0x142040770) other than spawns
    // (named by a Metadata path), in file order.
    static List<RoomPoint> Points(TerrainCache files, PlacedRoom room)
    {
        if (files.Text(room.Room.Path) is not { } text) return [];
        var file = RoomFile.Parse(text);
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

    static IEnumerable<PlacedExit> Assign(LayoutRoom room, List<PlacedTileObject> objects, IReadOnlyList<LayoutRoom> rooms)
    {
        var exitsOfNode = room.Descriptions.Where(d => !d.IsTelepad).OrderBy(d => d.Enabled || d.Kind == ExitKind.Teleport ? 0 : 1).ToList();
        if (exitsOfNode.Count == 0)
        {
            foreach (var o in objects)
                yield return new PlacedExit(o, ExitKind.Unused, 0, false, null, null, room.Name, $"{room.Name}'s node names no exit");
            yield break;
        }
        // Each exit with a slot takes the nearest object (closest pairs first); exits without one take what is left.
        var pick = new Dictionary<PlacedTileObject, (Description D, string Why)>(ReferenceEqualityComparer.Instance);
        var pairs = from d in exitsOfNode
                    let slot = room.SlotOf(d)
                    where slot is not null
                    from o in objects
                    orderby Distance(slot.X, slot.Y, o)
                    select (d, o, slot);
        var done = new HashSet<Description>(ReferenceEqualityComparer.Instance);
        foreach (var (d, o, slot) in pairs)
        {
            if (done.Contains(d) || pick.ContainsKey(o)) continue;
            pick[o] = (d, exitsOfNode.Count == 1 ? $"{room.Name}'s only exit" : $"{room.Name}, at point \"{slot.Name}\"");
            done.Add(d);
        }
        foreach (var d in exitsOfNode.Where(d => !done.Contains(d)))
            if (objects.FirstOrDefault(o => !pick.ContainsKey(o)) is { } o)
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
