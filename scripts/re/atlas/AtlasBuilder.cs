using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using GameServer.Data.Terrain;

namespace Atlas;

/// <summary>Builds the atlas: per WorldAreas row and topology, the rooms its graph nodes use, the area transition
/// objects in those rooms (tile objects of transition tiles, AreaTransition doodads), the named entrance point each
/// belongs to and so the destination (entranceN → Connections[N - 1]); grid placement for fixed layouts.</summary>
static partial class AtlasBuilder
{

    public static void Run(GameFiles files, List<AreaRow> areas, List<TopologyRow> topologies, string output)
    {
        var topoById = topologies.GroupBy(t => t.Id).ToDictionary(g => g.Key, g => g.First());
        var report = new Coverage();
        var outAreas = new List<AreaOut>();
        foreach (var area in areas)
        {
            var conns = area.Connections.Select((c, i) => c >= 0 && c < areas.Count
                ? new ConnectionOut(i + 1, areas[c].Id, areas[c].Name, areas[c].Hash16)
                : new ConnectionOut(i + 1, $"row {c}", "", 0)).ToList();
            var topoOut = new List<TopologyOut>();
            foreach (var tid in area.Topologies)
                topoOut.Add(Topology(files, area, conns, topoById[tid]));
            var a = new AreaOut(area.Id, area.Name, area.Hash16, area.IsTown, conns, topoOut, ConnectionStatus(conns, topoOut));
            outAreas.Add(a);
            report.Add(a);
        }

        var doc = new AtlasOut(
            "Derived from the game files by scripts/re/atlas (Agents/docs/protocol/08-area-transitions.md §6). " +
            "Room and grid cells: 23 per tile, world = (cell + 0.5) * 250/23. Fixed layouts carry grid positions; generated ones only room-local ones.",
            report.Summary(), outAreas, files.Missing);
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        File.WriteAllText(output, JsonSerializer.Serialize(doc, options));
        Console.WriteLine($"wrote {output}");
        report.Print();
    }

    // ---------------------------------------------------------------------------------------------------------------

    static TopologyOut Topology(GameFiles files, AreaRow area, List<ConnectionOut> conns, TopologyRow topo)
    {
        var graph = files.Graph(topo.Graph);
        if (graph is null)
            return new TopologyOut(topo.Id, topo.Graph, "missing", null, null, [], [], [$"graph file {files.Missing.GetValueOrDefault(topo.Graph, "missing")}"]);
        bool dgr = topo.Graph.EndsWith(".dgr", StringComparison.OrdinalIgnoreCase);
        string kind = dgr ? graph.Nodes.Count == 1 ? "fixed" : "fixed-graph" : "generated";
        var problems = new List<string>(graph.Problems);
        var nodes = new List<NodeOut>();
        var transitions = new List<TransitionOut>();
        string? rsPath = Walk(files, area, conns, topo.Graph, graph, kind, null, 0, nodes, transitions, problems);
        return new TopologyOut(topo.Id, topo.Graph, kind, graph.MasterFile, rsPath, nodes, transitions, problems);
    }

    /// <summary>One graph's nodes: their rooms' transitions. A node tagged "graph" embeds sub-graphs: its first name is a
    /// graph file or a group of the master's FileGroups (.fgp: "group" then one graph path per line, relative to the
    /// master's folder), of which the generator picks one; every candidate is walked (no grid positions there).</summary>
    static string? Walk(GameFiles files, AreaRow area, List<ConnectionOut> conns, string graphPath, GraphFile graph, string kind,
        string? subGraph, int depth, List<NodeOut> nodes, List<TransitionOut> transitions, List<string> problems)
    {
        var tsi = files.Tsi(graph.MasterFile);
        string? rsPath = tsi?.GetValueOrDefault("RoomSet") is { } rs ? TerrainText.Resolve(graph.MasterFile, rs) : null;
        string? tsPath = tsi?.GetValueOrDefault("TileSet") is { } ts ? TerrainText.Resolve(graph.MasterFile, ts) : null;
        string where = subGraph is null ? "" : $" (sub-graph {subGraph})";
        if (tsi is null) problems.Add($"master file {graph.MasterFile} missing{where}");
        else
        {
            if (rsPath is null) problems.Add($"master file has no RoomSet{where}");
            if (tsPath is null) problems.Add($"master file has no TileSet{where}");
            if (tsi.TryGetValue("OuterBufferSize", out var ob) && ob != "0" && kind == "fixed")
                problems.Add($"OuterBufferSize {ob}: grid positions assume no outer buffer");
        }
        var rooms = rsPath is null ? [] : files.List(rsPath, ".arm");
        var tiles = TransitionTiles(files, tsPath);
        string roomKind = subGraph is null ? kind : "sub-graph";

        foreach (var node in graph.Nodes)
        {
            if (node.Tag.Length == 0) continue;
            if (node.Tag.Equals("graph", StringComparison.OrdinalIgnoreCase) && node.Names.Count > 0)
            {
                nodes.Add(new NodeOut(node.Index, node.Tag, node.Rotation, node.Names, []) { SubGraph = subGraph });
                if (depth >= 4) { problems.Add($"sub-graphs nested deeper than 4 at {graphPath}"); continue; }
                foreach (var sub in SubGraphs(files, graph.MasterFile, node.Names[0], problems))
                {
                    var g = files.Graph(sub);
                    if (g is null) { problems.Add($"sub-graph {sub} missing"); continue; }
                    Walk(files, area, conns, sub, g, kind, sub, depth + 1, nodes, transitions, problems);
                }
                continue;
            }
            var nodeRooms = rooms.Where(p => files.Room(p) is { } r && r.Tag.Equals(node.Tag, StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            int? roomCode = TilePlacement.FromGraphRotation(node.Rotation);
            nodes.Add(new NodeOut(node.Index, node.Tag, node.Rotation, node.Names, nodeRooms) { SubGraph = subGraph });
            foreach (var roomPath in nodeRooms)
            {
                var room = files.Room(roomPath)!;
                if (room.Problem is not null) problems.Add($"room {roomPath}: {room.Problem}");
                var found = RoomObjects(files, roomPath, room, tiles)
                    .Select(obj => Resolve(area, conns, roomKind, node, roomCode, roomPath, room, obj, nodeRooms.Count) with { SubGraph = subGraph }).ToList();
                transitions.AddRange(found);
                transitions.AddRange(PointOnly(conns, roomKind, node, roomCode, roomPath, room, found, nodeRooms.Count).Select(t => t with { SubGraph = subGraph }));
            }
            // A named entrance with no room at all.
            if (nodeRooms.Count == 0 && node.Names.Any(n => n.StartsWith("entrance", StringComparison.OrdinalIgnoreCase)))
                problems.Add($"node {node.Index} \"{node.Tag}\"{where} names {string.Join(", ", node.Names)} but no room in the room set has that tag");
        }
        return rsPath;
    }

    internal static IEnumerable<string> SubGraphs(GameFiles files, string master, string name, List<string> problems)
    {
        if (name.EndsWith(".dgr", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".tgr", StringComparison.OrdinalIgnoreCase))
            return [TerrainText.Resolve(master, name)];
        var tsi = files.Tsi(master);
        if (tsi?.GetValueOrDefault("FileGroups") is not { } fgp)
        {
            problems.Add($"sub-graph group \"{name}\" but {master} has no FileGroups");
            return [];
        }
        string fgpPath = TerrainText.Resolve(master, fgp);
        var text = files.Bytes(fgpPath) is { } d ? TerrainText.Decode(d) : null;
        if (text is null) { problems.Add($"FileGroups {fgpPath} missing"); return []; }
        var list = new List<string>();
        bool inGroup = false;
        foreach (var line in TerrainText.Lines(text).Select(l => l.Trim()).Where(l => l.Length > 0))
        {
            if (line.StartsWith('"')) { inGroup = line.Trim('"').Equals(name, StringComparison.OrdinalIgnoreCase); continue; }
            if (inGroup && !line.StartsWith("version")) list.Add(TerrainText.Resolve(fgpPath, line));
        }
        if (list.Count == 0) problems.Add($"sub-graph group \"{name}\" not in {fgpPath}");
        return list;
    }

    sealed record TransitionTile(string Path, TileDefinition Def, List<TileObject> Objects);

    static readonly Dictionary<string, List<TransitionTile>> TileSetCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The tiles of a tile set whose .tmd has area transition records.</summary>
    static List<TransitionTile> TransitionTiles(GameFiles files, string? tileSet)
    {
        if (tileSet is null) return [];
        if (TileSetCache.TryGetValue(tileSet, out var hit)) return hit;
        var list = new List<TransitionTile>();
        foreach (var path in files.List(tileSet, ".tdt").Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var def = files.Tdt(path);
            if (def is null) continue;
            var objs = files.TileObjects(path, def);
            if (objs is null) continue;
            var t = objs.Where(o => files.IsTransitionType(o.Type)).ToList();
            if (t.Count > 0) list.Add(new TransitionTile(path, def, t));
        }
        TileSetCache[tileSet] = list;
        return list;
    }

    /// <summary>An area transition object of a room: a tile object (with the tile codes its key matched) or a doodad.</summary>
    sealed record RoomObject(string Source, string Model, string Type, RoomSlot? Slot, TransitionTile? Tile, TileObject? Record,
        int[] Codes, int Variants, float DoodadX, float DoodadY, bool PointsDecided = true);

    static IEnumerable<RoomObject> RoomObjects(GameFiles files, string roomPath, RoomFile room, List<TransitionTile> tiles)
    {
        foreach (var slot in room.Slots)
        {
            var hits = new List<(TransitionTile Tile, int[] Codes, bool Decided)>();
            foreach (var t in tiles)
            {
                var codes = KeyMatch.Codes(room, slot.Key, t.Def, out bool decided);
                if (codes.Length > 0) hits.Add((t, codes, decided));
            }
            foreach (var (t, codes, decided) in hits)
                foreach (var rec in t.Objects)
                    yield return new RoomObject("tile", rec.VirtualModel(t.Path), rec.Type, slot, t, rec, codes, hits.Count, 0, 0, decided);
        }
        foreach (var d in room.Doodads.Where(d => files.IsTransitionType(d.Type)))
            yield return new RoomObject("doodad", d.Model, d.Type, null, null, null, [], 1, d.X, d.Y);
    }

    // A point name: letters, number, "disabled", then an optional "-suffix" (teleport1-WeaverArenaName).
    [GeneratedRegex(@"^(\D*?)\s*(\d*)(disabled)?(-.*)?$", RegexOptions.IgnoreCase)]
    private static partial Regex NameParts();

    /// <summary>What a named point is for: an exit to Connections[N - 1], an in-area teleport, a side-area or sub-area link.</summary>
    static string RoleOf(string name)
    {
        string b = NameParts().Match(name).Groups[1].Value.ToLowerInvariant();
        return b switch
        {
            "entrance" => "entrance",
            "teleport" or "telepad" => "teleport",
            "sidearea" => "sidearea",
            _ when name.StartsWith("subarea=", StringComparison.OrdinalIgnoreCase) || name == "link" => "sublink",
            _ => "other",
        };
    }

    /// <summary>The graph node's name for a room point: the same name (or with "disabled"), or for a generic point
    /// ("entrance") the one numbered node name not used by another point, or (as many generic points as names) the name
    /// in the same position. Returns the name, a note for "matched", or a problem.</summary>
    static (string? Name, string Note, string? Problem) NodeNameFor(GraphNode node, RoomFile room, NamedPoint point)
    {
        var exact = node.Names.FirstOrDefault(n => n.Equals(point.Name, StringComparison.OrdinalIgnoreCase)
            || n.Equals(point.Name + "disabled", StringComparison.OrdinalIgnoreCase));
        if (exact is not null) return (exact, "", null);
        var pp = NameParts().Match(point.Name);
        if (pp.Groups[2].Value.Length > 0) return (null, "", null);
        var generic = node.Names.Where(n => NameParts().Match(n) is var m && m.Groups[1].Value.Equals(pp.Groups[1].Value, StringComparison.OrdinalIgnoreCase)
            && m.Groups[2].Value.Length > 0 && !room.Points.Any(q => q.Name.Equals(n, StringComparison.OrdinalIgnoreCase))).ToList();
        if (generic.Count == 1) return (generic[0], ", generic point numbered by the graph node", null);
        if (generic.Count == 0) return (null, "", null);
        // As many generic points of that name as node names: assume they pair up in order.
        var same = room.Points.Where(q => q.Name.Equals(point.Name, StringComparison.OrdinalIgnoreCase)).ToList();
        if (same.Count == generic.Count)
            return (generic[same.IndexOf(point)], $", generic point paired by order with the node's names ({string.Join(", ", generic)}): assumed", null);
        return (null, "", $"generic point \"{point.Name}\" ({same.Count} in the room) and several node names ({string.Join(", ", generic)})");
    }

    static TransitionOut Resolve(AreaRow area, List<ConnectionOut> conns, string kind, GraphNode node, int? roomCode,
        string roomPath, RoomFile room, RoomObject obj, int roomVariants)
    {
        var notes = new List<string>();
        // Room-local position(s): one per matching tile code.
        var positions = new List<(int Code, float X, float Y)>();
        (int X0, int Y0, int X1, int Y1)? rect = null;
        if (obj.Record is { } rec && obj.Slot is { } slot && obj.Tile is { } tile)
        {
            int ox = slot.Column * TilePlacement.CellsPerTile, oy = slot.Row * TilePlacement.CellsPerTile;
            var (kw, kh) = (slot.Key.Width * TilePlacement.CellsPerTile, slot.Key.Height * TilePlacement.CellsPerTile);
            rect = (ox, oy, ox + kw, oy + kh);
            foreach (int c in obj.Codes)
            {
                var (x, y) = TilePlacement.Cell(c, rec.CellX, rec.CellY, tile.Def.Width * TilePlacement.CellsPerTile, tile.Def.Height * TilePlacement.CellsPerTile);
                positions.Add((c, x + ox, y + oy));
            }
            if (obj.Variants > 1) notes.Add($"one of {obj.Variants} transition tiles matching this slot (the generator picks one)");
            if (!obj.PointsDecided) notes.Add("no orientation also fits the key's edge points; orientations from edges and corners only");
            if (obj.Codes.Length > 1) notes.Add($"tile key matches {obj.Codes.Length} orientations ({string.Join(", ", obj.Codes)})");
            if (slot.Key.Heights.Any(h => h != 0)) notes.Add($"tile key heights {string.Join(" ", slot.Key.Heights)}: z may be offset");
        }
        else positions.Add((-1, obj.DoodadX, obj.DoodadY));

        // The room's named point this object belongs to (any name but "default"): one inside the tile, else the nearest.
        // Monster and object spawn points ("Metadata/…") are never an exit's: The Climb's prison.arm has
        // Metadata/Monsters/CorpseWheel/CorpseWheelAggroCharge beside its entrance, which left the exit unresolved.
        var named = room.Points.Where(p => !p.Name.Equals("default", StringComparison.OrdinalIgnoreCase)
            && !p.Name.StartsWith("Metadata/", StringComparison.OrdinalIgnoreCase)).ToList();
        NamedPoint? point = null;
        string? how = null, unresolved = null, role = null;
        bool internalOnly = false, unused = false;
        if (named.Count == 0)
        {
            role = RoleOf(node.Names.FirstOrDefault(n => RoleOf(n) != "other") ?? "");
            unresolved = role switch
            {
                "teleport" => "in-area teleport (the room has no named point; the node names " + string.Join(", ", node.Names) + ")",
                "sidearea" => "side area link (the room has no named point; the node names " + string.Join(", ", node.Names) + ")",
                "sublink" => "sub-area link (" + string.Join(", ", node.Names) + ")",
                _ => "the room has no named point" + (node.Names.Count > 0 ? $" (the node names {string.Join(", ", node.Names)})" : ""),
            };
            internalOnly = role == "teleport";
        }
        else
        {
            var inside = rect is { } r ? named.Where(p => p.X >= r.X0 && p.X < r.X1 && p.Y >= r.Y0 && p.Y < r.Y1).ToList() : [];
            var pool = inside.Count > 0 ? inside : named;
            how = inside.Count > 0 ? "inside the transition tile" : "nearest point";
            var picks = positions.Select(pos => pool.MinBy(p => Dist(p, pos.X, pos.Y))!).Distinct().ToList();
            if (picks.Count == 1) point = picks[0];
            else unresolved = $"the tile's orientations put it nearest to different points ({string.Join(", ", picks.Select(p => p.Name))})";
            if (point is not null && inside.Count == 0)
            {
                float far = positions.Min(pos => Dist(point, pos.X, pos.Y));
                how += $" ({far:0} cells)";
                if (far > 80) notes.Add($"{far:0} cells from the nearest named point: the pairing is a guess");
            }
        }

        // The graph node's name for that point: the same name (or with "disabled"), or a numbered name for a generic one.
        string? nodeName = null;
        if (point is not null)
        {
            var (mapped, suffix, problem) = NodeNameFor(node, room, point);
            nodeName = mapped;
            how += suffix;
            unresolved ??= problem;
            role = RoleOf(nodeName ?? point.Name);
            if (nodeName is null && role == "teleport") { unresolved = $"in-area teleport (generic point \"{point.Name}\")"; internalOnly = true; }
            if (nodeName is null && unresolved is null && (unused = true))
                unresolved = $"point \"{point.Name}\" is not named by graph node \"{node.Tag}\" ({(node.Names.Count == 0 ? "no names" : string.Join(", ", node.Names))}): not used here";
        }

        int? connection = null;
        bool enabled = true;
        ConnectionOut? dest = null;
        if (nodeName is not null)
        {
            var m = NameParts().Match(nodeName);
            enabled = m.Groups[3].Value.Length == 0;
            if (role == "entrance" && int.TryParse(m.Groups[2].Value, out int n))
            {
                connection = n;
                if (n >= 1 && n <= conns.Count) dest = conns[n - 1];
                else unresolved = conns.Count == 0 ? "the area has no connections" : $"{nodeName} but the area has {conns.Count} connection(s)";
            }
            else if (role == "teleport") { unresolved = $"in-area teleport ({nodeName})"; internalOnly = true; }
            else if (role == "sidearea") unresolved = $"side area link ({nodeName}): its destination is not in WorldAreas.Connections";
            else unresolved = $"node name \"{nodeName}\" is not an entrance";
        }

        // Grid placement for a fixed layout (one room, fixed rotation, one orientation).
        GridOut? grid = null;
        if (kind == "fixed" && unresolved is null)
        {
            if (roomCode is null) notes.Add($"room rotation \"{node.Rotation}\" is chosen by the generator: no grid position");
            else if (positions.Count != 1) notes.Add("tile orientation ambiguous: no grid position");
            else if (roomVariants > 1) notes.Add($"one of {roomVariants} rooms with tag \"{node.Tag}\": no grid position");
            else
            {
                var pos = positions[0];
                int rw = room.Width * TilePlacement.CellsPerTile, rh = room.Height * TilePlacement.CellsPerTile;
                var (gx, gy) = TilePlacement.Cell(roomCode.Value, (int)MathF.Floor(pos.X), (int)MathF.Floor(pos.Y), rw, rh);
                if (pos.Code >= 0)
                {
                    int code = TilePlacement.Compose(roomCode.Value, pos.Code);
                    grid = new GridOut(gx, gy, code, TilePlacement.Angle(code, obj.Record!.Angle), float.IsNaN(obj.Record.Z) ? null : obj.Record.Z);
                    if (float.IsNaN(obj.Record.Z)) notes.Add("text .tmd (version 8): no height; angle assumed");
                }
                else
                {
                    grid = new GridOut(gx, gy, roomCode.Value, null, null);
                    notes.Add("doodad: rotation and height not derived");
                }
                if (node.Rotation.StartsWith('F')) notes.Add("mirrored room: the F rotation tokens are assumed to mirror x first, as codes 4..7 do");
            }
        }

        return new TransitionOut(
            obj.Source, obj.Model, obj.Type, roomPath, node.Index, node.Tag,
            obj.Tile?.Path, obj.Record?.Name, obj.Slot is { } s ? [s.Column, s.Row] : null,
            positions.Count == 1 ? [MathF.Round(positions[0].X, 1), MathF.Round(positions[0].Y, 1)] : null,
            obj.Codes.Length == 1 ? obj.Codes[0] : null,
            point?.Name, point is null ? null : [point.X, point.Y], nodeName, how, enabled, connection,
            dest?.Id, dest?.Hash16, dest?.Name, grid, role, unresolved is null ? "resolved" : internalOnly ? "internal" : unused ? "unused" : "unresolved", unresolved,
            notes.Count == 0 ? null : notes);
    }

    /// <summary>Entrance points the graph node names but no transition object belongs to: the exit is there, but its
    /// object is not a tile object or doodad of the room (Oriath's entrance2, for example). Destination and the point's
    /// cell only; the server has to place a plain AreaTransition (no model).</summary>
    static IEnumerable<TransitionOut> PointOnly(List<ConnectionOut> conns, string kind, GraphNode node, int? roomCode,
        string roomPath, RoomFile room, List<TransitionOut> found, int roomVariants)
    {
        foreach (var point in room.Points)
        {
            if (found.Any(t => t.Point == point.Name && t.PointCell is { } c && c[0] == point.X && c[1] == point.Y)) continue;
            var (nodeName, note, _) = NodeNameFor(node, room, point);
            if (nodeName is null || RoleOf(nodeName) != "entrance") continue;
            var m = NameParts().Match(nodeName);
            if (!int.TryParse(m.Groups[2].Value, out int n)) continue;
            bool enabled = m.Groups[3].Value.Length == 0;
            var dest = n >= 1 && n <= conns.Count ? conns[n - 1] : null;
            var notes = new List<string> { "no transition object in the room's tiles or doodads: position is the named (arrival) point, model unknown" };
            if (note.Length > 0) notes.Add(note.TrimStart(',', ' '));
            GridOut? grid = null;
            if (kind == "fixed" && roomCode is { } rc && roomVariants == 1)
            {
                var (gx, gy) = TilePlacement.Cell(rc, (int)MathF.Floor(point.X), (int)MathF.Floor(point.Y),
                    room.Width * TilePlacement.CellsPerTile, room.Height * TilePlacement.CellsPerTile);
                grid = new GridOut(gx, gy, rc, null, null);
            }
            yield return new TransitionOut("point", null, null, roomPath, node.Index, node.Tag, null, null, null,
                [point.X, point.Y], null, point.Name, [point.X, point.Y], nodeName, "named point only", enabled, n,
                dest?.Id, dest?.Hash16, dest?.Name, grid, "entrance", dest is null ? "unresolved" : "point-only",
                dest is null ? (conns.Count == 0 ? "the area has no connections" : $"{nodeName} but the area has {conns.Count} connection(s)") : null, notes);
        }
    }

    static float Dist(NamedPoint p, float x, float y) => MathF.Sqrt((p.X - x) * (p.X - x) + (p.Y - y) * (p.Y - y));

    /// <summary>Per connection: in how many of the area's topologies an enabled transition leads there.</summary>
    static List<ConnectionStatusOut> ConnectionStatus(List<ConnectionOut> conns, List<TopologyOut> topos)
    {
        var result = new List<ConnectionStatusOut>();
        var usable = topos.Where(t => t.Kind != "missing").ToList();
        foreach (var c in conns)
        {
            int resolved = 0, disabledOnly = 0, pointOnly = 0;
            var reasons = new SortedSet<string>();
            foreach (var t in usable)
            {
                var to = t.Transitions.Where(x => x.Status == "resolved" && x.Connection == c.Index).ToList();
                if (to.Any(x => x.Enabled)) { resolved++; continue; }
                var points = t.Transitions.Where(x => x.Status == "point-only" && x.Connection == c.Index).ToList();
                if (points.Any(x => x.Enabled)) { pointOnly++; continue; }
                to.AddRange(points);
                if (to.Count > 0) { disabledOnly++; continue; }
                string name = $"entrance{c.Index}";
                bool named = t.Nodes.Any(n => n.Names.Any(x => x.StartsWith(name, StringComparison.OrdinalIgnoreCase)
                    && (x.Length == name.Length || !char.IsDigit(x[name.Length]))));
                reasons.Add(!named ? "no graph node names it"
                    : t.Transitions.Any(x => x.NodeName is { } nn && nn.StartsWith(name, StringComparison.OrdinalIgnoreCase)) ? "named, but its transition is unresolved"
                    : "named by a graph node, but no transition object found at that point");
            }
            string status = usable.Count == 0 ? "no-topology" : resolved == usable.Count ? "resolved"
                : resolved + pointOnly == usable.Count ? "point-only" : resolved + pointOnly > 0 ? "partial"
                : disabledOnly > 0 && resolved + disabledOnly == usable.Count ? "disabled" : "unresolved";
            result.Add(new ConnectionStatusOut(c.Index, c.Id, status, resolved, pointOnly, disabledOnly, usable.Count, reasons.Count == 0 ? null : reasons.ToList()));
        }
        return result;
    }
}

// JSON shapes ---------------------------------------------------------------------------------------------------------

sealed record AtlasOut(string About, CoverageOut Coverage, List<AreaOut> Areas, SortedDictionary<string, string> MissingFiles);

sealed record AreaOut(string Id, string Name, ushort Hash16, bool IsTown, List<ConnectionOut> Connections,
    List<TopologyOut> Topologies, List<ConnectionStatusOut> ConnectionStatus);

sealed record ConnectionOut(int Index, string Id, string Name, ushort Hash16);

sealed record ConnectionStatusOut(int Index, string Id, string Status, int Topologies, int PointOnly, int DisabledOnly, int Of, List<string>? Reasons);

sealed record TopologyOut(string Id, string Graph, string Kind, string? Master, string? RoomSet, List<NodeOut> Nodes,
    List<TransitionOut> Transitions, List<string> Problems);

sealed record NodeOut(int Index, string Tag, string Rotation, IReadOnlyList<string> Names, List<string> Rooms)
{
    /// <summary>The sub-graph (of a "graph" node) this node belongs to; null for the topology's own graph.</summary>
    public string? SubGraph { get; init; }
}

sealed record TransitionOut(string Source, string? Model, string? ObjectType, string Room, int Node, string NodeTag,
    string? Tile, string? Record, int[]? TileSlot, float[]? RoomCell, int? TileCode,
    string? Point, float[]? PointCell, string? NodeName, string? Matched, bool Enabled, int? Connection,
    string? Destination, ushort? DestinationHash16, string? DestinationName, GridOut? Grid, string? Role,
    string Status, string? Reason, List<string>? Notes)
{
    public string? SubGraph { get; init; }
}

/// <summary>Grid cell, the combined placement code (room ∘ tile), Positioned rotation and the fixed height (world z).</summary>
sealed record GridOut(int X, int Y, int Code, float? Rotation, float? Z);
