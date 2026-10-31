using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using GameServer.Data.Terrain.Generation;


/// <summary>Dev: one record per capture tour stop (CaptureTour), appended to <see cref="File"/> as a JSON line, so a long
/// tour can be studied afterwards without running it again. What the client did (its log lines for the load, its tile and
/// doodad hashes, the recorder's files), what the Gateway generated for the same (area, seed) (ServerLevels: tile hash,
/// time, the exception in full), each layer of the two compared (walk and projectile grids, cell heights, doodads,
/// transitions, rooms, quest objects), and the rooms' monster points fitted from the client's doodads against those fitted
/// from the Gateway's. The draw-by-draw comparison stays with `lg.ps1 loads` (it needs the port's draw log).</summary>
static class TourRecords
{
    public static string File { get; set; } = "data/tour-records/stops.jsonl";

    static readonly Regex Recorded = new("AreaTransition|Waypoint$|TownEntrance$");
    static readonly Lazy<string> Commit = new(() =>
    {
        string Git(string args)
        {
            try
            {
                using var p = Process.Start(new ProcessStartInfo("git", args) { RedirectStandardOutput = true, UseShellExecute = false })!;
                string output = p.StandardOutput.ReadToEnd().Trim();
                p.WaitForExit(5000);
                return output;
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { return ""; }
        }
        string head = Git("rev-parse --short HEAD");
        // Changes not committed yet (the Gateway runs what is in the working tree).
        return Git("status --porcelain --untracked-files=no -- src scripts/re/levelgen").Length > 0 ? head + "+dirty" : head;
    });

    /// <summary>Appends the record of one stop: <paramref name="outcome"/> is captured, not captured or client failed.</summary>
    public static void Write(string area, uint seed, string outcome, string? clientFailure = null, AreaLayout? layout = null)
    {
        var clock = Stopwatch.StartNew();
        var record = new Dictionary<string, object?>
        {
            ["time"] = DateTime.Now.ToString("s"), ["commit"] = Commit.Value, ["area"] = area, ["seed"] = seed,
            ["outcome"] = outcome, ["clientFailure"] = clientFailure,
        };
        string stem = $"data/walkability/{area}_seed{seed}";
        try
        {
            var client = Client(area, seed, stem);
            record["client"] = client;
            var level = ServerLevels.For(area, seed);
            record["server"] = Server(area, seed, level);
            if (level is not null) record["compare"] = Compare(level, stem, client.GetValueOrDefault("tileHash") as uint?);
            if (level is not null && outcome == "captured") record["points"] = Points(area, seed);
            if (layout is not null)
                record["layout"] = new Dictionary<string, object?>
                {
                    ["grid"] = $"{layout.GridW}x{layout.GridH}", ["spawn"] = new[] { layout.SpawnX, layout.SpawnY },
                    ["walkable"] = layout.Walkable is not null, ["transitions"] = layout.AreaTransitions.Length,
                    ["waypoint"] = layout.Waypoint is { } w ? new[] { w.GridX, w.GridY } : null,
                };
        }
        catch (Exception ex)
        {
            record["recordError"] = ex.ToString();
        }
        record["recordMs"] = clock.ElapsedMilliseconds;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(File)!);
            System.IO.File.AppendAllText(File, JsonSerializer.Serialize(record) + "\n");
        }
        catch (IOException ex) { Console.WriteLine($"[gateway] tour record {area} seed {seed}: {ex.Message}"); }
    }

    // The client's log lines of this load (from its "Generating level" line to the next one), its hashes and level, and
    // the recorder's files with their sizes.
    static Dictionary<string, object?> Client(string area, uint seed, string stem)
    {
        var client = new Dictionary<string, object?>();
        var lines = ClientLines(area, seed);
        client["log"] = lines;
        foreach (var l in lines)
        {
            if (Regex.Match(l, @"Tile hash: (\d+)") is { Success: true } t) client["tileHash"] = uint.Parse(t.Groups[1].Value);
            if (Regex.Match(l, @"Doodad hash: (\d+)") is { Success: true } d) client["doodadHash"] = uint.Parse(d.Groups[1].Value);
            if (Regex.Match(l, @"Generating level (\d+) area") is { Success: true } g) client["level"] = int.Parse(g.Groups[1].Value);
        }
        string folder = Path.GetDirectoryName(stem)!, name = Path.GetFileName(stem) + ".";
        client["files"] = Directory.Exists(folder)
            ? Directory.EnumerateFiles(folder, name + "*").Where(f => !f.EndsWith(".tmp"))
                .ToDictionary(f => Path.GetFileName(f)[name.Length..], f => new FileInfo(f).Length)
            : new Dictionary<string, long>();
        return client;
    }

    static List<string> ClientLines(string area, uint seed)
    {
        if (!System.IO.File.Exists(CaptureTour.ClientLog)) return [];
        string[] lines;
        try
        {
            using var log = new StreamReader(new FileStream(CaptureTour.ClientLog, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
            lines = log.ReadToEnd().Split('\n');
        }
        catch (IOException) { return []; }
        int at = Array.FindLastIndex(lines, l => l.Contains($"area \"{area}\" with seed {seed}"));
        if (at < 0) return [];
        var slice = new List<string>();
        for (int i = at; i < lines.Length && slice.Count < 400 && (i == at || !lines[i].Contains("Generating level ")); i++)
            if (lines[i].Trim().Length > 0) slice.Add(lines[i].TrimEnd('\r'));
        return slice;
    }

    static Dictionary<string, object?> Server(string area, uint seed, ServerLevels.Level? level) => new()
    {
        ["supported"] = ServerLevels.IsSupported(area),
        ["generated"] = level is not null,
        ["error"] = level is null ? ServerLevels.Error(area, seed) : null,
        ["tileHash"] = level?.TileHash,
        ["ms"] = level?.Milliseconds,
        ["grid"] = level is null ? null : $"{level.Width}x{level.Height}",
        ["doodads"] = level?.Doodads.Count,
        ["rooms"] = level?.Rooms.Count,
        ["objects"] = level?.Objects.Count,
        ["questObjects"] = level?.QuestObjects.Count,
    };

    // Each layer against the recorder's file: the number of cells, doodads or objects that differ (-1: no capture).
    static Dictionary<string, object?> Compare(ServerLevels.Level level, string stem, uint? clientHash)
    {
        var c = new Dictionary<string, object?> { ["tileHash"] = clientHash is null ? null : clientHash == level.TileHash };
        static int Differ(byte[] mine, string file)
        {
            if (!System.IO.File.Exists(file)) return -1;
            var theirs = System.IO.File.ReadAllBytes(file);
            return theirs.Length != mine.Length ? int.MaxValue : mine.Where((v, i) => v != theirs[i]).Count();
        }
        c["walk"] = Differ(level.Walk, stem + ".bin");
        c["projectile"] = Differ(level.Projectile, stem + ".projectile.bin");
        if (System.IO.File.Exists(stem + ".heights.bin"))
        {
            var recorded = System.IO.File.ReadAllBytes(stem + ".heights.bin");
            c["heights"] = recorded.Length != level.Heights.Length * 2 ? int.MaxValue
                : level.Heights.Where((v, i) => v != BitConverter.ToInt16(recorded, i * 2)).Count();
        }
        else c["heights"] = -1;
        if (System.IO.File.Exists(stem + ".doodads.json"))
        {
            using var dd = JsonDocument.Parse(System.IO.File.ReadAllBytes(stem + ".doodads.json"));
            var models = dd.RootElement.GetProperty("models").EnumerateArray().Select(m => m.GetString()!.ToLowerInvariant()).ToList();
            var theirs = dd.RootElement.GetProperty("doodads").EnumerateArray().Select(d => (models[d[0].GetInt32()], d[1].GetInt32(), d[2].GetInt32())).ToHashSet();
            var mine = level.Doodads.Select(d => (d.Model.ToLowerInvariant(), d.X, d.Y)).ToHashSet();
            c["doodads"] = new { captured = theirs.Count, placed = mine.Count, missing = theirs.Except(mine).Count(), extra = mine.Except(theirs).Count(),
                firstMissing = theirs.Except(mine).Take(3).Select(d => $"{Path.GetFileName(d.Item1)} ({d.Item2}, {d.Item3})").ToArray() };
        }
        if (System.IO.File.Exists(stem + ".transitions.json"))
        {
            using var tj = JsonDocument.Parse(System.IO.File.ReadAllBytes(stem + ".transitions.json"));
            var theirs = tj.RootElement.GetProperty("objects").EnumerateArray()
                .Select(o => (o.GetProperty("objectType").GetString()!, o.GetProperty("model").GetString()!.ToLowerInvariant(), o.GetProperty("x").GetInt32(), o.GetProperty("y").GetInt32())).ToHashSet();
            var mine = level.Objects.Where(o => Recorded.IsMatch(o.Type)).Select(o => (o.Type, o.Model.ToLowerInvariant(), o.X, o.Y)).ToHashSet();
            c["transitions"] = theirs.Except(mine).Count() + mine.Except(theirs).Count();
        }
        if (System.IO.File.Exists(stem + ".rooms.json"))
        {
            using var rj = JsonDocument.Parse(System.IO.File.ReadAllBytes(stem + ".rooms.json"));
            var theirs = rj.RootElement.GetProperty("rooms").EnumerateArray()
                .Select(r => new PlacedArmRoom(r.GetProperty("path").GetString()!, r.GetProperty("x").GetInt32(), r.GetProperty("y").GetInt32(),
                    r.GetProperty("width").GetInt32(), r.GetProperty("height").GetInt32(), r.GetProperty("code").GetInt32())).ToHashSet();
            var mine = level.Rooms.ToHashSet();
            c["rooms"] = new { captured = theirs.Count, missing = theirs.Except(mine).Count(), extra = mine.Except(theirs).Count() };
        }
        if (System.IO.File.Exists(stem + ".objects.json"))
        {
            using var oj = JsonDocument.Parse(System.IO.File.ReadAllBytes(stem + ".objects.json"));
            var theirs = oj.RootElement.GetProperty("objects").EnumerateArray()
                .Select(o => (o.GetProperty("objectType").GetString()!, o.GetProperty("x").GetInt32(), o.GetProperty("y").GetInt32())).ToHashSet();
            var mine = level.QuestObjects.Select(o => (o.Type, o.X, o.Y)).ToHashSet();
            c["questObjects"] = theirs.Except(mine).Count() + mine.Except(theirs).Count();
        }
        return c;
    }

    // The rooms' named points (monster packs, bosses, start points) fitted to the client's doodads and to the Gateway's.
    static Dictionary<string, object?> Points(string area, uint seed)
    {
        var fromClient = AreaPopulation.FitRooms(area, seed, [], "data");
        var serverNotes = new List<string>();
        var fromServer = AreaPopulation.FitRooms(area, seed, serverNotes, "data", generated: true);
        var theirs = fromClient.Points.Select(p => (p.Name, p.X, p.Y)).ToHashSet();
        var mine = fromServer.Points.Select(p => (p.Name, p.X, p.Y)).ToHashSet();
        return new()
        {
            ["fromClient"] = theirs.Count, ["fromServer"] = mine.Count,
            ["missing"] = theirs.Except(mine).Take(5).Select(p => $"{p.Name} ({p.X}, {p.Y})").ToArray(),
            ["extra"] = mine.Except(theirs).Take(5).Select(p => $"{p.Name} ({p.X}, {p.Y})").ToArray(),
            ["objectsFromClient"] = fromClient.Objects.Count, ["objectsFromServer"] = fromServer.Objects.Count,
            ["serverNotes"] = serverNotes.Take(20).ToArray(),
        };
    }
}
