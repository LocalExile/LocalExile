using System.Text.Json;
using GameServer.Core;

namespace GameServer.Modules.Delve;

/// <summary>A mine checkpoint connection. Rail links come from room objects, never a path toward the player.</summary>
sealed record DelveRailRoute(IReadOnlyList<(int X, int Y)> Points)
{
    public sealed record Encounter(string Pack, int X, int Y, int Room = -1, bool Destination = false);
    public IReadOnlyList<Encounter> Encounters { get; init; } = [];
    public static DelveRailRoute? Load(string area, uint seed, string folder = "data")
    {
        try { return LoadCore(area, seed, folder); }
        catch (Exception ex) when (ex is IOException or JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            Console.WriteLine($"[gateway] Delve rails unavailable for {area} seed {seed}: {ex.Message}");
            return null;
        }
    }

    static DelveRailRoute? LoadCore(string area, uint seed, string folder)
    {
        string graphFile = Path.Combine(folder, "delve-paths.json");
        string roomsFile = Path.Combine(folder, "walkability", $"{area}_seed{seed}.rooms.json");
        if (!GameServer.Core.ClientTables.Exists(graphFile) || !File.Exists(roomsFile)) return null;
        using var graphs = JsonDocument.Parse(GameServer.Core.ClientTables.ReadAllBytes(graphFile));
        using var rooms = JsonDocument.Parse(File.ReadAllBytes(roomsFile));
        var capture = rooms.RootElement;
        if (capture.GetProperty("area").GetString() != area || capture.GetProperty("seed").GetUInt32() != seed) return null;
        var nodes = new List<((int X, int Y) At, string Type, int Room)>();
        var links = new List<(int A, int B)>();
        var encounters = new List<Encounter>();
        var checkpoints = new List<int>();
        int start = -1, end = -1, roomNumber = 0;
        var byPath = graphs.RootElement.GetProperty("rooms").EnumerateObject().ToDictionary(p => p.Name, p => p.Value, StringComparer.OrdinalIgnoreCase);
        foreach (var room in capture.GetProperty("rooms").EnumerateArray())
        {
            string path = room.GetProperty("path").GetString()!;
            if (!byPath.TryGetValue(path, out var graph)) continue;
            var placement = new TutorialObjects.RoomPlacement(room.GetProperty("x").GetInt32(), room.GetProperty("y").GetInt32(),
                room.GetProperty("width").GetInt32(), room.GetProperty("height").GetInt32(), room.GetProperty("code").GetInt32());
            var indexes = new Dictionary<int, int>();
            foreach (var spawn in graph.GetProperty("spawns").EnumerateArray())
            {
                string name = spawn.GetProperty("Name").GetString()!;
                var p = placement.Place(new(name, (int)Math.Round(spawn.GetProperty("X").GetSingle()), (int)Math.Round(spawn.GetProperty("Y").GetSingle())));
                encounters.Add(new(name, p.X, p.Y, roomNumber));
            }
            foreach (var node in graph.GetProperty("nodes").EnumerateArray())
            {
                string type = node.GetProperty("type").GetString()!;
                var p = placement.Place(new("", (int)Math.Round(node.GetProperty("x").GetSingle()), (int)Math.Round(node.GetProperty("y").GetSingle())));
                int index = nodes.Count;
                indexes.Add(node.GetProperty("index").GetInt32(), index);
                nodes.Add(((p.X, p.Y), type, roomNumber));
                if (type.Contains("/DelveNode", StringComparison.Ordinal))
                {
                    if (path.Contains("/entrance_", StringComparison.Ordinal)) start = index;
                    else if (path.Contains("/encounter_", StringComparison.Ordinal)) checkpoints.Add(index);
                }
            }
            foreach (var link in graph.GetProperty("links").EnumerateArray())
                if (indexes.TryGetValue(link[0].GetInt32(), out int a) && indexes.TryGetValue(link[1].GetInt32(), out int b)) links.Add((a, b));
            roomNumber++;
        }
        if (start >= 0 && checkpoints.Count > 0) end = checkpoints[^1];
        else if (checkpoints.Count >= 2)
        {
            var ordered = checkpoints.OrderBy(i => DelveTerrain.Progress(seed, nodes[i].At)).ToArray();
            start = ordered[0]; end = ordered[^1];
        }
        if (start < 0 || end < 0) return null;
        // The rooms overlap one tile (23 cells). Only authored endpoints may bridge that seam.
        for (int a = 0; a < nodes.Count; a++)
            if (nodes[a].Type.EndsWith("/DelvePathEndpoint", StringComparison.Ordinal))
                for (int b = a + 1; b < nodes.Count; b++)
                    if (nodes[b].Room != nodes[a].Room && nodes[b].Type.EndsWith("/DelvePathEndpoint", StringComparison.Ordinal)
                        && Distance(nodes[a].At, nodes[b].At) <= 24) links.Add((a, b));
        // Cell sleeves can offset the two rail ends along their shared boundary. Their centreline is not in either
        // room's "next" records. Resolve only those boundary seams through the captured terrain, never through walls.
        int authored = nodes.Count;
        for (int a = 0; a < authored; a++)
            for (int b = a + 1; b < authored; b++)
            {
                var na = nodes[a]; var nb = nodes[b];
                double gap = Distance(na.At, nb.At);
                if (na.Room == nb.Room || !na.Type.EndsWith("/DelvePathEndpoint", StringComparison.Ordinal)
                    || !nb.Type.EndsWith("/DelvePathEndpoint", StringComparison.Ordinal) || gap <= 24 || gap > 160
                    || Math.Min(Math.Abs(na.At.X - nb.At.X), Math.Abs(na.At.Y - nb.At.Y)) > 24) continue;
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
        return new(points) { Encounters = encounters.Where(e => seed == 1 || e.Room != nodes[start].Room)
            .Select(e => e with { Destination = e.Room == nodes[end].Room && Distance((e.X, e.Y), nodes[end].At) <= 60 }).ToArray() };
    }

    internal static double Distance((int X, int Y) a, (int X, int Y) b) => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));

    static List<(int X, int Y)>? Sleeve(string area, uint seed, (int X, int Y) a, (int X, int Y) b, string folder)
    {
        string prefix = Path.Combine(folder, "walkability", $"{area}_seed{seed}");
        if (!File.Exists(prefix + ".bin") || !File.Exists(prefix + ".json")) return null;
        using var meta = JsonDocument.Parse(File.ReadAllBytes(prefix + ".json"));
        int w = meta.RootElement.GetProperty("w").GetInt32(), h = meta.RootElement.GetProperty("h").GetInt32();
        var cells = File.ReadAllBytes(prefix + ".bin");
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
