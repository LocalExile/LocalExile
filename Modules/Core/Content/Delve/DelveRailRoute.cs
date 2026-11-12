using System.Text.Json;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using GameServer.Data.Terrain.Generation;
using GameServer.Core;

namespace GameServer.Modules.Delve;

/// <summary>A mine checkpoint connection. Rail links come from room objects, never a path toward the player.</summary>
sealed record DelveRailRoute(IReadOnlyList<(int X, int Y)> Points)
{
    public sealed record Encounter(string Pack, int X, int Y, int Room = -1, bool Destination = false);
    public IReadOnlyList<Encounter> Encounters { get; init; } = [];
    public string Shape()
    {
        var trip = new DelveJourney(Points, 0);
        var first = Points.Zip(Points.Skip(1), (a, b) => (X: b.X - a.X, Y: b.Y - a.Y)).First(d => d != (0, 0));
        double angle = Math.Atan2(first.Y, first.X), cos = Math.Cos(angle), sin = Math.Sin(angle);
        var shape = new List<int> { (int)Math.Round(trip.Length / 23) };
        for (int i = 1; i <= 16; i++)
        {
            var p = trip.At(trip.Length * i / 16).At;
            double x = p.X - Points[0].X, y = p.Y - Points[0].Y;
            shape.Add((int)Math.Round((x * cos + y * sin) / 23));
            shape.Add((int)Math.Round((y * cos - x * sin) / 23));
        }
        return "v1:" + string.Join(",", shape);
    }
    public static bool Similar(string a, string b)
    {
        if (a == b) return true;
        if (!a.StartsWith("v1:", StringComparison.Ordinal) || !b.StartsWith("v1:", StringComparison.Ordinal)) return false;
        var left = a[3..].Split(','); var right = b[3..].Split(',');
        if (left.Length != 33 || right.Length != 33) return false;
        var x = new int[33]; var y = new int[33];
        for (int i = 0; i < 33; i++)
            if (!int.TryParse(left[i], out x[i]) || !int.TryParse(right[i], out y[i])) return false;
        if (Math.Abs((long)x[0] - y[0]) * 6 > Math.Max(x[0], y[0])) return false;
        double normal = 0, reflected = 0;
        for (int i = 1; i < 33; i += 2)
        {
            double dx = (double)x[i] - y[i], dy = (double)x[i + 1] - y[i + 1], mirror = (double)x[i + 1] + y[i + 1];
            normal += dx * dx + dy * dy; reflected += dx * dx + mirror * mirror;
        }
        // Translation, rotation, mirroring and small bends must not disguise a substantially repeated journey.
        return Math.Min(normal, reflected) / 16 <= 16;
    }
    sealed class Sources(ClientTable rooms, ClientTable spawns, ClientTable nodes, ClientTable links)
    {
        public readonly Dictionary<string, int> ByPath = rooms.ToDictionary(r => r.Text("room")!, r => r.Index, StringComparer.OrdinalIgnoreCase);
        public readonly ILookup<long, ClientRow> Spawns = spawns.By("room"), Nodes = nodes.By("room"), Links = links.By("room");
    }
    static readonly object SourcesGate = new();
    static object? SourceKey;
    static Sources? CurrentSources;

    public static void Warmup()
    {
        try { ReadSources(); }
        catch (Exception ex) when (ex is IOException or JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        { Console.WriteLine($"[gateway] Delve rail catalog warmup unavailable: {ex.Message}"); }
    }

    static Sources? ReadSources()
    {
        lock (SourcesGate)
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                var key = ClientTables.CacheKey;
                if (Equals(key, SourceKey)) return CurrentSources;
                var rooms = ReadTable("delve_path_rooms");
                var sources = rooms is null ? null : new Sources(rooms,
                    ReadTable("delve_path_spawns") ?? ClientTable.FromRows("delve_path_spawns", []),
                    ReadTable("delve_path_nodes") ?? ClientTable.FromRows("delve_path_nodes", []),
                    ReadTable("delve_path_links") ?? ClientTable.FromRows("delve_path_links", []));
                if (!Equals(key, ClientTables.CacheKey)) continue;
                SourceKey = key; CurrentSources = sources;
                return sources;
            }
            return null; // A catalog being replaced must not produce a route from mixed revisions.
        }
    }

    sealed class CachedRoute { public Sources? Sources; public DelveRailRoute? Route; }
    static readonly ConditionalWeakTable<ServerLevels.Level, CachedRoute> Cache = new();

    public static DelveRailRoute? Load(string area, uint seed, string folder = "data")
    {
        var clock = Stopwatch.StartNew();
        try
        {
            var sources = ReadSources();
            if (sources is null) return null;
            var generated = folder == "data" ? ServerLevels.For(area, seed) : null;
            if (generated is null) return LoadCore(area, seed, folder, null, sources);
            var cached = Cache.GetValue(generated, _ => new());
            lock (cached)
            {
                if (cached.Route is not null && cached.Sources == sources) return cached.Route;
                var route = LoadCore(area, seed, folder, generated, sources);
                if (route is not null)
                {
                    cached.Sources = sources; cached.Route = route;
                    Console.WriteLine($"[gateway] Delve rails prepared: seed {seed}, {route.Points.Count} points in {clock.Elapsed.TotalMilliseconds:0.00} ms");
                }
                return route;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            Console.WriteLine($"[gateway] Delve rails unavailable for {area} seed {seed}: {ex.Message}");
            return null;
        }
    }

    static ClientTable? ReadTable(string name)
    {
        try { return ClientTables.Table(name); }
        catch (FileNotFoundException ex) when (ex.FileName == name) { return null; }
    }

    static DelveRailRoute? LoadCore(string area, uint seed, string folder, ServerLevels.Level? generated, Sources sources)
    {
        // client.db's delve_path_* tables (data/delve-paths.json): each mine room's rail graph, by its .arm path.
        string roomsFile = Path.Combine(folder, "walkability", $"{area}_seed{seed}.rooms.json");
        if (generated is null && !File.Exists(roomsFile)) return null;
        using var capture = generated is null ? JsonDocument.Parse(File.ReadAllBytes(roomsFile)) : null;
        IReadOnlyList<PlacedArmRoom> rooms;
        if (generated is not null) rooms = generated.Rooms;
        else
        {
            var root = capture!.RootElement;
            if (root.GetProperty("area").GetString() != area || root.GetProperty("seed").GetUInt32() != seed) return null;
            rooms = root.GetProperty("rooms").EnumerateArray().Select(r => new PlacedArmRoom(r.GetProperty("path").GetString()!,
                r.GetProperty("x").GetInt32(), r.GetProperty("y").GetInt32(), r.GetProperty("width").GetInt32(),
                r.GetProperty("height").GetInt32(), r.GetProperty("code").GetInt32())).ToArray();
        }
        var nodes = new List<((int X, int Y) At, string Type, int Room)>();
        var links = new List<(int A, int B)>();
        var encounters = new List<Encounter>();
        var checkpoints = new List<int>();
        int start = -1, end = -1, roomNumber = 0;
        var byPath = sources.ByPath;
        var spawnsOf = sources.Spawns;
        var nodesOf = sources.Nodes;
        var linksOf = sources.Links;
        foreach (var room in rooms)
        {
            string path = room.Path;
            if (!byPath.TryGetValue(path, out var graph)) continue;
            var placement = new TutorialObjects.RoomPlacement(room.X, room.Y, room.Width, room.Height, room.Code);
            var indexes = new Dictionary<int, int>();
            foreach (var spawn in spawnsOf[graph])
            {
                string name = spawn.Text("Name")!;
                var p = placement.Place(new(name, (int)Math.Round((float)spawn.Real("X")), (int)Math.Round((float)spawn.Real("Y"))));
                encounters.Add(new(name, p.X, p.Y, roomNumber));
            }
            foreach (var node in nodesOf[graph])
            {
                string type = node.Text("type")!;
                var p = placement.Place(new("", (int)Math.Round((float)node.Real("x")), (int)Math.Round((float)node.Real("y"))));
                int index = nodes.Count;
                indexes.Add(node.Int("index"), index);
                nodes.Add(((p.X, p.Y), type, roomNumber));
                if (type.Contains("/DelveNode", StringComparison.Ordinal))
                {
                    if (path.Contains("/entrance_", StringComparison.Ordinal)) start = index;
                    else if (path.Contains("/encounter_", StringComparison.Ordinal)) checkpoints.Add(index);
                }
            }
            foreach (var link in linksOf[graph])
                if (indexes.TryGetValue(link.Int("from"), out int a) && indexes.TryGetValue(link.Int("to"), out int b)) links.Add((a, b));
            roomNumber++;
        }
        if (ServerLevels.DelveTerrain(seed) is { } terrain)
        {
            bool InCell(int i, (int X, int Y)? at) => at is { } p && nodes[i].At.X >= p.X && nodes[i].At.X < p.X + 25 * 23
                && nodes[i].At.Y >= p.Y && nodes[i].At.Y < p.Y + 25 * 23;
            start = checkpoints.Single(i => InCell(i, DelveTerrain.Anchor(terrain, false)));
            end = checkpoints.Single(i => InCell(i, DelveTerrain.Anchor(terrain, true)));
        }
        else if (start >= 0 && checkpoints.Count > 0) end = checkpoints[^1];
        else if (checkpoints.Count >= 2)
        {
            var ordered = checkpoints.OrderBy(i => DelveTerrain.Progress(seed, nodes[i].At)).ToArray();
            start = ordered[0]; end = ordered[^1];
        }
        if (start < 0 || end < 0) return null;
        // Only nearby authored endpoints can join rooms. Bucket them instead of comparing every graph-node pair.
        var endpoints = Enumerable.Range(0, nodes.Count)
            .Where(i => nodes[i].Type.EndsWith("/DelvePathEndpoint", StringComparison.Ordinal)).ToArray();
        var buckets = endpoints.ToLookup(i => ((int)Math.Floor(nodes[i].At.X / 160.0), (int)Math.Floor(nodes[i].At.Y / 160.0)));
        var pairs = new List<(int A, int B, double Squared)>();
        foreach (int a in endpoints)
        {
            var na = nodes[a];
            int bx = (int)Math.Floor(na.At.X / 160.0), by = (int)Math.Floor(na.At.Y / 160.0);
            var nearby = new List<int>();
            for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++) nearby.AddRange(buckets[(bx + dx, by + dy)]);
            // Preserve authored order for Dijkstra's equal-length ties.
            foreach (int b in nearby.Where(b => b > a).Order())
            {
                var nb = nodes[b];
                if (na.Room == nb.Room) continue;
                double dx = (double)na.At.X - nb.At.X, dy = (double)na.At.Y - nb.At.Y;
                double squared = dx * dx + dy * dy;
                if (squared <= 160 * 160) pairs.Add((a, b, squared));
            }
        }
        foreach (var (a, b, squared) in pairs)
            if (squared <= 24 * 24) links.Add((a, b));
        foreach (var (a, b, squared) in pairs)
        {
                var na = nodes[a]; var nb = nodes[b];
                if (squared <= 24 * 24 || Math.Min(Math.Abs(na.At.X - nb.At.X), Math.Abs(na.At.Y - nb.At.Y)) > 24) continue;
                var seam = Sleeve(area, seed, na.At, nb.At, folder);
                if (seam is null) continue;
                int previousNode = a;
                foreach (var point in seam.Skip(1).SkipLast(1))
                {
                    int n = nodes.Count; nodes.Add((point, "sleeve", -1)); links.Add((previousNode, n)); previousNode = n;
                }
                links.Add((previousNode, b));
        }
        var adjacent = Enumerable.Range(0, nodes.Count).Select(_ => new HashSet<int>()).ToArray();
        foreach (var (a, b) in links) { adjacent[a].Add(b); adjacent[b].Add(a); }
        var distance = Enumerable.Repeat(double.PositiveInfinity, nodes.Count).ToArray();
        var previous = Enumerable.Repeat(-1, nodes.Count).ToArray();
        var queue = new PriorityQueue<int, double>();
        distance[start] = 0; queue.Enqueue(start, 0);
        while (queue.TryDequeue(out int a, out double cost))
        {
            if (cost != distance[a]) continue;
            if (a == end) break;
            foreach (int b in adjacent[a])
            {
                double next = cost + Distance(nodes[a].At, nodes[b].At);
                if (next >= distance[b]) continue;
                previous[b] = a; distance[b] = next; queue.Enqueue(b, next);
            }
        }
        if (!double.IsFinite(distance[end]))
        {
            Console.WriteLine($"[gateway] Delve rail graph disconnected: seed {seed}, {nodes.Count} nodes, from {nodes[start].At} to {nodes[end].At}");
            return null;
        }
        var points = new List<(int X, int Y)>();
        for (int i = end; i >= 0; i = previous[i]) points.Add(nodes[i].At);
        points.Reverse();
        if (generated is not null)
        {
            var walkable = new List<(int X, int Y)> { points[0] };
            for (int i = 1; i < points.Count; i++)
            {
                if (ClearSegment(generated.Walk, generated.Width, generated.Height, points[i - 1], points[i]))
                    walkable.Add(points[i]);
                else if (Sleeve(area, seed, points[i - 1], points[i], folder) is { } detour)
                    walkable.AddRange(detour.Skip(1));
                else return null; // Never open a trip whose rail crosses an impassable wall.
            }
            points = walkable;
        }
        return new(points) { Encounters = encounters.Where(e => seed == 1 || e.Room != nodes[start].Room)
            .Select(e => e with { Destination = e.Room == nodes[end].Room && Distance((e.X, e.Y), nodes[end].At) <= 60 }).ToArray() };
    }

    static bool ClearSegment(byte[] cells, int w, int h, (int X, int Y) from, (int X, int Y) to)
    {
        bool Open(int x, int y) => x >= 0 && y >= 0 && x < w && y < h && cells[y * w + x] >= 5;
        if (!Open(from.X, from.Y)) return false;
        int steps = Math.Max(Math.Abs(to.X - from.X), Math.Abs(to.Y - from.Y)) * 2;
        var last = from;
        for (int k = 1; k <= steps; k++)
        {
            int x = (int)Math.Round(from.X + (to.X - from.X) * (double)k / steps);
            int y = (int)Math.Round(from.Y + (to.Y - from.Y) * (double)k / steps);
            if (!Open(x, y) || x != last.X && y != last.Y && (!Open(x, last.Y) || !Open(last.X, y))) return false;
            last = (x, y);
        }
        return true;
    }

    internal static double Distance((int X, int Y) a, (int X, int Y) b)
    {
        double dx = (double)a.X - b.X, dy = (double)a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    static List<(int X, int Y)>? Sleeve(string area, uint seed, (int X, int Y) a, (int X, int Y) b, string folder)
    {
        string prefix = Path.Combine(folder, "walkability", $"{area}_seed{seed}");
        var generated = folder == "data" ? ServerLevels.For(area, seed) : null;
        if (generated is null && (!File.Exists(prefix + ".bin") || !File.Exists(prefix + ".json"))) return null;
        using var meta = generated is null ? JsonDocument.Parse(File.ReadAllBytes(prefix + ".json")) : null;
        int w = generated?.Width ?? meta!.RootElement.GetProperty("w").GetInt32(), h = generated?.Height ?? meta!.RootElement.GetProperty("h").GetInt32();
        var cells = generated?.Walk ?? File.ReadAllBytes(prefix + ".bin");
        if (w <= 0 || h <= 0 || cells.LongLength != (long)w * h) return null;
        var previous = new Dictionary<(int X, int Y), (int X, int Y)> { [a] = a };
        var queue = new Queue<(int X, int Y)>(); queue.Enqueue(a);
        while (queue.TryDequeue(out var p) && previous.Count <= 100000)
        {
            if (p == b)
            {
                var path = new List<(int X, int Y)> { b };
                while (p != a) { p = previous[p]; path.Add(p); }
                if (path.Count > 500) return null;
                path.Reverse();
                // A four-neighbour search makes diagonal sleeves into one-cell zigzags. Keep only clear straight
                // sections so the Crawler does not turn 90 degrees at every grid cell or slow at artificial knots.
                var smooth = new List<(int X, int Y)> { path[0] };
                for (int i = 0; i < path.Count - 1;)
                {
                    int j = path.Count - 1;
                    while (j > i + 1 && !Clear(path[i], path[j])) j--;
                    smooth.Add(path[j]); i = j;
                }
                return smooth;
            }
            foreach (var (dx, dy) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
            {
                var n = (X: p.X + dx, Y: p.Y + dy);
                if (n.X < 0 || n.Y < 0 || n.X >= w || n.Y >= h
                    || n.X < Math.Min(a.X, b.X) - 80 || n.X > Math.Max(a.X, b.X) + 80
                    || n.Y < Math.Min(a.Y, b.Y) - 80 || n.Y > Math.Max(a.Y, b.Y) + 80
                    || previous.ContainsKey(n) || cells[n.Y * w + n.X] < 5) continue;
                previous[n] = p; queue.Enqueue(n);
            }
        }
        return null;

        bool Clear((int X, int Y) from, (int X, int Y) to)
        {
            int steps = Math.Max(Math.Abs(to.X - from.X), Math.Abs(to.Y - from.Y)) * 2;
            var last = from;
            bool Open(int x, int y) => x >= 0 && y >= 0 && x < w && y < h && cells[y * w + x] >= 5;
            for (int k = 1; k <= steps; k++)
            {
                int x = (int)Math.Round(from.X + (to.X - from.X) * (double)k / steps);
                int y = (int)Math.Round(from.Y + (to.Y - from.Y) * (double)k / steps);
                if (!Open(x, y) || x != last.X && y != last.Y && (!Open(x, last.Y) || !Open(last.X, y))) return false;
                last = (x, y);
            }
            return true;
        }
    }
}
