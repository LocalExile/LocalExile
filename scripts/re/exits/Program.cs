// usage (from the repository root): dotnet run --project scripts/re/exits -c Release -- [area regex] [--all] [--seeds N]
//   [--seed S]… [--json out.json] [--quiet] [--notes] [--objects]
// The offline area transition audit (Agents/docs/protocol/08-area-transitions.md §8). For every campaign area (WorldAreas
// ids "1_…" / "2_…" of acts 1–10 with a topology; --all: every area with one) that the Gateway generates itself
// (ServerLevels, the gate baseline), at seed 1 and N further seeds (default 3, fixed per area), it generates the layout, asks AreaTransitionCatalog for the
// exits exactly as the Gateway does when a player enters (captures off) and checks:
//   missing      a node of the layout names "entranceN" (enabled), its room holds a transition object at which no exit
//                was created (by position), and no exit leads to Connections[N-1]
//   misplaced    an exit leads there, but none sits in (or next to) the room of that node
//   unexpected   an exit leads to an area no enabled "entranceN" of the layout names (and not within the area), and
//                the model matching does not lead there either (info unnamed-exit: Delve's mine)
//   duplicate    more exits lead to an area than the layout's enabled entrances name it
//   teleport     a node names "teleportN" (enabled) and no in-area exit there takes the player to the "telepadN" node
//   unreachable  an exit (or teleport) the player cannot get to by walking and the layout's in-area exits, starting
//                next to the exit back to Connections[0] (else the largest walkable region); "next to" is the
//                Gateway's walk-up range (PlayerWalk.InWalkUpRange), a gate's arrival AreaLayout.Near's 20 cells
//   no-object    (info) an enabled "entranceN" with no exit there or elsewhere and no object left in its room
//   point-only   (info) the same, while another exit leads there (The Ship Graveyard's intact ship)
//   unnamed      (info) a WorldAreas connection no node of the layout names, enabled or disabled
//   lost         the model matching (08 §7) over the same objects reaches a destination no exit reaches now (info
//                lost-one-way when the layout names it only as a disabled entrance)
//   lost-gate    it had a gate (an exit within the area) where no exit is now, at an object the layout names as a
//                teleport (info not-a-gate otherwise: its pairs of unnamed objects, e.g. a Labyrinth room's two doors)
//   unusable     (info) a gate with no walkable cell in reach whose target is reached anyway
//   orphan-telepad (info) a "telepadN" no node's "teleportN" leads to (a portal the game opens after a fight)
//   dropped      (info) a placed transition object the catalog did not create, with its note
// Exit code 1 when any problem (not info) is found. --json writes every (area, seed) result for comparisons.
using System.Text.Json;
using System.Text.RegularExpressions;
using GameServer.Core.Protocol.Instance;
using GameServer.Data.Terrain.Generation;

Console.OutputEncoding = System.Text.Encoding.UTF8;
const int ObjectSize = 5; // the Gateway's AreaTransitionObjectSize
string filter = ".";
int extraSeeds = 3;
var fixedSeeds = new List<uint>();
string? jsonOut = null;
bool quiet = false, all = false, showNotes = false, showObjects = false;
for (int i = 0; i < args.Length; i++)
    switch (args[i])
    {
        case "--seeds": extraSeeds = int.Parse(args[++i]); break;
        case "--seed": fixedSeeds.Add(uint.Parse(args[++i])); break;
        case "--json": jsonOut = args[++i]; break;
        case "--quiet": quiet = true; break;
        case "--all": all = true; break;
        case "--notes": showNotes = true; break;
        case "--objects": showObjects = true; break;
        default: filter = args[i]; break;
    }

if (!File.Exists("AGENTS.md") || !Directory.Exists("data/tables")) { Console.Error.WriteLine("run from the repository root (needs data/)"); return 2; }
AreaTransitionCatalog.UseCaptures = false;

// WorldAreas: id, name, act, hash, topologies; connections (by index) from the atlas (the JSON column is broken).
var areas = new List<(string Id, string Name, int Act, ushort Hash)>();
using (var doc = JsonDocument.Parse(File.ReadAllBytes("data/tables/WorldAreas.json")))
    foreach (var r in doc.RootElement.EnumerateArray())
    {
        string id = r.GetProperty("Id").GetString() ?? "";
        int act = r.GetProperty("Act").GetInt32();
        bool campaign = Regex.IsMatch(id, @"^[12]_\d+_") && act is >= 1 and <= 10;
        if (!(all || campaign) || r.GetProperty("TopologiesKeys").GetArrayLength() == 0) continue;
        if (!Regex.IsMatch(id, filter)) continue;
        areas.Add((id, r.GetProperty("Name").GetString() ?? "", act, (ushort)r.GetProperty("HASH16").GetInt32()));
    }
var connections = new Dictionary<string, List<(int Index, string Id, ushort Hash)>>(StringComparer.Ordinal);
var names = new Dictionary<ushort, string>();
using (var doc = JsonDocument.Parse(File.ReadAllBytes("data/area-transitions.json")))
    foreach (var a in doc.RootElement.GetProperty("areas").EnumerateArray())
    {
        string id = a.GetProperty("id").GetString()!;
        names[(ushort)a.GetProperty("hash16").GetInt32()] = id;
        var list = new List<(int, string, ushort)>();
        if (a.TryGetProperty("connections", out var c) && c.ValueKind == JsonValueKind.Array)
            foreach (var x in c.EnumerateArray())
                list.Add((x.GetProperty("index").GetInt32(), x.GetProperty("id").GetString()!, (ushort)x.GetProperty("hash16").GetInt32()));
        connections[id] = list;
    }
string Name(ushort h) => names.GetValueOrDefault(h, $"0x{h:x4}");

var results = new List<AreaResult>();
var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
int checkedLoads = 0, failedLoads = 0, unsupported = 0;
var clock = System.Diagnostics.Stopwatch.StartNew();
foreach (var area in areas)
{
    if (!ServerLevels.IsSupported(area.Id)) { unsupported++; continue; }
    var seeds = new List<uint> { 1 };
    seeds.AddRange(fixedSeeds);
    var rng = new Random(StableHash(area.Id));
    for (int k = 0; k < extraSeeds; k++) seeds.Add((uint)rng.Next(2, int.MaxValue));
    foreach (uint asked in seeds.Distinct())
    {
        uint seed = ServerLevels.PlayableSeed(area.Id, asked);
        var level = ServerLevels.For(area.Id, seed);
        var result = new AreaResult(area.Id, area.Name, area.Act, asked, seed);
        results.Add(result);
        if (level is null)
        {
            failedLoads++;
            result.Problems.Add($"not generated: {ServerLevels.Error(area.Id, seed)?.Split('\n')[0]}");
            Count("not-generated");
            Print(result);
            continue;
        }
        checkedLoads++;
        var notes = new List<string>();
        var exits = AreaTransitionCatalog.For(area.Hash, area.Id, seed, notes).Select(e => e.Transition).ToList();
        // The model matching over the same objects, for the comparison (lost / lost-gate).
        AreaTransitionCatalog.UseLayout = false;
        var matched = AreaTransitionCatalog.For(area.Hash, area.Id, seed, []).Select(e => e.Transition).ToList();
        AreaTransitionCatalog.UseLayout = true;
        Audit(area, level, exits, matched, notes, result);
        Print(result);
        if (showNotes) foreach (var n in notes) Console.WriteLine($"      - {n}");
        if (showObjects)
        {
            foreach (var o in level.Objects) Console.WriteLine($"      object {o.Type} {o.Model} ({o.X}, {o.Y})");
            foreach (var r in level.Records.SelectMany(r => r.Rooms))
                Console.WriteLine($"      room {r.Room.Path} at tile ({r.X}, {r.Y}) {r.Width}x{r.Height} [{string.Join(" ", r.Descriptions)}]");
        }
    }
}

Console.WriteLine();
Console.WriteLine($"{areas.Count} areas ({unsupported} not generated by the Gateway, skipped), {checkedLoads} loads checked, {failedLoads} not generated, {clock.Elapsed.TotalMinutes:0.0} min");
Console.WriteLine($"loads with a problem: {results.Count(r => r.Problems.Count > 0)}; areas with a problem: {results.Where(r => r.Problems.Count > 0).Select(r => r.Area).Distinct().Count()}");
foreach (var (k, v) in counts) Console.WriteLine($"  {k,-14} {v}");
if (jsonOut is not null)
{
    File.WriteAllText(jsonOut, JsonSerializer.Serialize(new { counts, results }, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"written {jsonOut}");
}
return results.Any(r => r.Problems.Any(p => !p.StartsWith("not generated"))) ? 1 : 0;

void Count(string kind) => counts[kind] = counts.GetValueOrDefault(kind) + 1;

void Print(AreaResult r)
{
    if (quiet && r.Problems.Count == 0) return;
    Console.WriteLine($"{r.Area} ({r.Name}) seed {r.Seed}{(r.Seed != r.AskedSeed ? $" (for {r.AskedSeed})" : "")}: {r.Exits} exit(s){(r.Problems.Count == 0 ? " ok" : "")}");
    foreach (var p in r.Problems) Console.WriteLine($"    {p}");
    if (!quiet) foreach (var p in r.Info) Console.WriteLine($"    ({p})");
}

void Audit((string Id, string Name, int Act, ushort Hash) area, ServerLevels.Level level, List<AreaTransitionObject> exits,
    List<AreaTransitionObject> matched, List<string> notes, AreaResult result)
{
    result.Exits = exits.Count;
    var conns = connections.GetValueOrDefault(area.Id) ?? [];
    var rooms = level.Records.SelectMany(r => r.Rooms).ToList();
    // The layout's descriptions, per room: entranceN[disabled], teleportN[disabled][-label], telepadN.
    var entrances = new List<(int N, bool Enabled, PlacedRoom Room)>();
    var teleports = new List<(int N, bool Enabled, PlacedRoom Room)>();
    var telepads = new List<(int N, PlacedRoom Room)>();
    foreach (var room in rooms)
        foreach (var d in room.Descriptions)
        {
            var m = Regex.Match(d, @"^entrance\s*(\d+)(-?disabled)?$", RegexOptions.IgnoreCase);
            if (m.Success) { entrances.Add((int.Parse(m.Groups[1].Value), !m.Groups[2].Success, room)); continue; }
            m = Regex.Match(d, @"^teleport\s*(\d+)(disabled)?([-=].*)?$", RegexOptions.IgnoreCase);
            if (m.Success) { teleports.Add((int.Parse(m.Groups[1].Value), !m.Groups[2].Success, room)); continue; }
            m = Regex.Match(d, @"^telepad\s*(\d+)(-.*)?$", RegexOptions.IgnoreCase);
            if (m.Success) telepads.Add((int.Parse(m.Groups[1].Value), room));
        }
    bool Near(PlacedRoom room, int x, int y, int margin = 23) =>
        x >= room.X * 23 - margin && y >= room.Y * 23 - margin && x < (room.X + room.Width) * 23 + margin && y < (room.Y + room.Height) * 23 + margin;

    void Problem(string kind, string text) { result.Problems.Add($"{kind}: {text}"); Count(kind); }
    void Info(string kind, string text) { result.Info.Add($"{kind}: {text}"); Count(kind); }

    // Connections the layout names.
    var expected = new Dictionary<ushort, int>();
    foreach (var (n, enabled, room) in entrances)
    {
        var c = conns.FirstOrDefault(c => c.Index == n);
        if (c.Id is null) { Info("no-connection", $"entrance{n} in {Path.GetFileName(room.Room.Path)}, the area has {conns.Count} connection(s)"); continue; }
        if (!enabled) continue;
        expected[c.Hash] = expected.GetValueOrDefault(c.Hash) + 1;
        var to = exits.Where(e => e.DestinationHash16 == c.Hash && e.TeleportTo is null).ToList();
        // A transition object placed in the room where no exit was created could have been this one (by position only).
        bool holdsObject = level.Exits.Any(x => Near(room, x.Object.X, x.Object.Y, 0)
            && !exits.Any(e => e.GridX == x.Object.X && e.GridY == x.Object.Y));
        if (to.Count == 0 && !holdsObject) Info("no-object", $"entrance{n} → {c.Id}: {Path.GetFileName(room.Room.Path)} holds no transition object (a quest portal or a one-way arrival)");
        else if (to.Count == 0) Problem("missing", $"entrance{n} → {c.Id} ({Path.GetFileName(room.Room.Path)}) has no exit");
        else if (!to.Any(e => Near(room, e.GridX, e.GridY)) && !level.Exits.Any(x => Near(room, x.Object.X, x.Object.Y, 0)))
            Info("point-only", $"entrance{n} → {c.Id}: {Path.GetFileName(room.Room.Path)} holds no transition object; exit(s) there elsewhere");
        else if (!to.Any(e => Near(room, e.GridX, e.GridY))) Problem("misplaced", $"entrance{n} → {c.Id}: exit(s) at {string.Join(", ", to.Select(e => $"({e.GridX}, {e.GridY})"))}, not in {Path.GetFileName(room.Room.Path)} at tile ({room.X}, {room.Y})");
    }
    foreach (var c in conns)
        if (!entrances.Any(e => e.N == c.Index)) Info("unnamed", $"connection {c.Index} {c.Id} is named by no node of this layout");
    foreach (var g in exits.Where(e => e.TeleportTo is null).GroupBy(e => e.DestinationHash16))
    {
        if (g.Key == area.Hash) continue;
        if (!expected.TryGetValue(g.Key, out int want) && matched.Any(m => m.DestinationHash16 == g.Key && m.TeleportTo is null))
            Info("unnamed-exit", $"{g.Count()} exit(s) to {Name(g.Key)}, which no node names; the model matching leads there too");
        else if (!expected.TryGetValue(g.Key, out want)) Problem("unexpected", $"{g.Count()} exit(s) to {Name(g.Key)} at {string.Join(", ", g.Select(e => $"({e.GridX}, {e.GridY})"))}, which no enabled entrance names");
        else if (g.Count() > want) Problem("duplicate", $"{g.Count()} exits to {Name(g.Key)} for {want} entrance(s): {string.Join(", ", g.Select(e => $"({e.GridX}, {e.GridY})"))}");
    }
    // In-area teleports.
    foreach (var (n, enabled, room) in teleports)
    {
        var pads = telepads.Where(t => t.N == n).Select(t => t.Room).ToList();
        if (pads.Count == 0) { Info("no-telepad", $"teleport{n} in {Path.GetFileName(room.Room.Path)} has no telepad{n} node"); continue; }
        var gate = exits.FirstOrDefault(e => e.TeleportTo is { } to && Near(room, e.GridX, e.GridY) && pads.Any(p => Near(p, to.X, to.Y)));
        if (gate is null)
        {
            if (enabled) Problem("teleport", $"teleport{n} ({Path.GetFileName(room.Room.Path)}) → telepad{n} ({Path.GetFileName(pads[0].Room.Path)}) has no gate");
            else Info("teleport-off", $"teleport{n}disabled ({Path.GetFileName(room.Room.Path)}) has no gate");
        }
    }
    foreach (var (n, room) in telepads.Where(p => !teleports.Any(t => t.N == p.N)))
        Info("orphan-telepad", $"telepad{n} in {Path.GetFileName(room.Room.Path)}: no node names teleport{n}");
    foreach (var e in exits.Where(e => e.TeleportTo is { } to && !teleports.Any(t => Near(t.Room, e.GridX, e.GridY))))
        Info("gate", $"in-area gate at ({e.GridX}, {e.GridY}) → ({e.TeleportTo!.Value.X}, {e.TeleportTo!.Value.Y}) in no teleport node");
    foreach (var n in notes.Where(n => n.Contains("not created") || n.Contains("ambiguous") || n.Contains("not in topology")))
        Info("dropped", n);

    // Against the model matching over the same objects: a destination it reaches that no exit reaches now, and a gate of
    // its with no exit near it now.
    foreach (var g in matched.Where(e => e.TeleportTo is null && e.DestinationHash16 != area.Hash).GroupBy(e => e.DestinationHash16))
        if (exits.Any(e => e.DestinationHash16 == g.Key && e.TeleportTo is null)) continue;
        // The layout names that connection only as a disabled entrance: an arrival point, no way back (The Sarn Ramparts'
        // "entrance1-disabled" from The Temple of Decay).
        else if (conns.Where(c => c.Hash == g.Key).All(c => entrances.Any(e => e.N == c.Index && !e.Enabled)))
            Info("lost-one-way", $"the model matching led to {Name(g.Key)}; the layout names it only as a disabled entrance");
        else
            Problem("lost", $"the model matching led to {Name(g.Key)} from {string.Join(", ", g.Select(e => $"({e.GridX}, {e.GridY})"))}, no exit does now");
    // Its gates pair two objects no topology names; one the layout names as an entrance or as no exit was no gate.
    foreach (var m in matched.Where(e => e.TeleportTo is not null || e.DestinationHash16 == area.Hash))
        if (!exits.Any(e => Math.Abs(e.GridX - m.GridX) <= 10 && Math.Abs(e.GridY - m.GridY) <= 10))
        {
            var role = level.Exits.FirstOrDefault(x => x.Object.X == m.GridX && x.Object.Y == m.GridY);
            if (role is { Kind: ExitKind.Teleport })
                Problem("lost-gate", $"the model matching had a gate at ({m.GridX}, {m.GridY}), no exit is there now");
            else
                Info("not-a-gate", $"the model matching paired ({m.GridX}, {m.GridY}) as a gate; the layout: {role?.Why ?? "no such object"}");
        }

    // Reachability over the walk grid (cells >= 2, 8 neighbours) and the in-area gates.
    var comp = Components(level.Walk, level.Width, level.Height, out int[] sizes);
    // The regions a player can use an object from: walkable cells in the Gateway's walk-up range
    // (PlayerWalk.InWalkUpRange with the area transition's size); a gate's arrival: AreaLayout.Near's rings (up to 20).
    HashSet<int> Around(int x, int y, bool arrival = false)
    {
        var set = new HashSet<int>();
        int r = arrival ? 20 : 2 * ObjectSize + 3;
        for (int dy = -r; dy <= r; dy++)
            for (int dx = -r; dx <= r; dx++)
            {
                int cx = x + dx, cy = y + dy;
                if (cx < 0 || cy < 0 || cx >= level.Width || cy >= level.Height) continue;
                if (!arrival && !GameServer.Core.World.PlayerWalk.InWalkUpRange((cx, cy), (x, y), ObjectSize)) continue;
                if (comp[cy * level.Width + cx] is int c and >= 0) set.Add(c);
            }
        return set;
    }
    if (sizes.Length == 0) return;
    var start = new HashSet<int>();
    var back = conns.FirstOrDefault(c => c.Index == 1);
    foreach (var e in exits.Where(e => back.Id is not null && e.DestinationHash16 == back.Hash && e.TeleportTo is null))
        start.UnionWith(Around(e.GridX, e.GridY));
    if (start.Count == 0) start.Add(Array.IndexOf(sizes, sizes.Max()));
    var reach = new HashSet<int>(start);
    for (bool grew = true; grew;)
    {
        grew = false;
        foreach (var e in exits.Where(e => e.TeleportTo is not null))
            if (Around(e.GridX, e.GridY).Overlaps(reach))
                foreach (int c in Around(e.TeleportTo!.Value.X, e.TeleportTo!.Value.Y, arrival: true))
                    grew |= reach.Add(c);
    }
    int Nearest(int x, int y)
    {
        for (int r = 0; r < 200; r++)
            for (int dy = -r; dy <= r; dy++)
                foreach (int dx in new[] { -r, r })
                    foreach (var (cx, cy) in new[] { (x + dx, y + dy), (x + dy, y + dx) })
                        if (cx >= 0 && cy >= 0 && cx < level.Width && cy < level.Height && level.Walk[cy * level.Width + cx] >= 2) return r;
        return -1;
    }
    if (Environment.GetEnvironmentVariable("EXITS_WHY") == "1")
        foreach (var e in exits.Where(e => e.TeleportTo is not null))
            Console.WriteLine($"      why: gate ({e.GridX}, {e.GridY}) walkable at {Nearest(e.GridX, e.GridY)}; target {e.TeleportTo} walkable at {Nearest(e.TeleportTo!.Value.X, e.TeleportTo!.Value.Y)}");
    foreach (var e in exits)
        if (Around(e.GridX, e.GridY).Count == 0 && e.TeleportTo is { } to && Around(to.X, to.Y, arrival: true).Overlaps(reach))
            Info("unusable", $"gate at ({e.GridX}, {e.GridY}) has no walkable cell in reach; where it leads is reached anyway");
        else if (!Around(e.GridX, e.GridY).Overlaps(reach))
            Problem("unreachable", $"{(e.TeleportTo is null ? "exit to " + Name(e.DestinationHash16) : "gate")} at ({e.GridX}, {e.GridY})");
}

static int[] Components(byte[] walk, int w, int h, out int[] sizes)
{
    var comp = new int[w * h];
    Array.Fill(comp, -1);
    var list = new List<int>();
    var queue = new Queue<int>();
    for (int i = 0; i < comp.Length; i++)
    {
        if (walk[i] < 2 || comp[i] >= 0) continue;
        int id = list.Count, size = 0;
        comp[i] = id;
        queue.Enqueue(i);
        while (queue.Count > 0)
        {
            int at = queue.Dequeue();
            size++;
            int x = at % w, y = at / w;
            for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    int nx = x + dx, ny = y + dy;
                    if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                    int n = ny * w + nx;
                    if (walk[n] >= 2 && comp[n] < 0) { comp[n] = id; queue.Enqueue(n); }
                }
        }
        list.Add(size);
    }
    sizes = [.. list];
    return comp;
}

static int StableHash(string s)
{
    unchecked
    {
        int h = 23;
        foreach (char c in s) h = h * 31 + c;
        return h & 0x7fffffff;
    }
}

sealed class AreaResult(string area, string name, int act, uint askedSeed, uint seed)
{
    public string Area { get; } = area;
    public string Name { get; } = name;
    public int Act { get; } = act;
    public uint AskedSeed { get; } = askedSeed;
    public uint Seed { get; } = seed;
    public int Exits { get; set; }
    public List<string> Problems { get; } = [];
    public List<string> Info { get; } = [];
}
