using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using GameServer.Data.Terrain;
using GameServer.Data.Terrain.Generation;

namespace LevelGenTool;

/// <summary>`levelgen gate`: the regression check to run before every commit. Every seed-1 trace
/// (data/walkability/&lt;area&gt;_seed1.gen.json: tile hash, matching draws through the doodad stage, the walk and
/// projectile grids, the doodads against the capture), the traced loads of areas without a seed-1 trace checked the
/// same way, and every (area, seed) the client logged (the oracle), run by worker processes that each open the game
/// once. The results go to gate-latest.json beside this file and are compared with gate-baseline.json (committed):
/// what was gained, what was lost, and the first-difference clusters to work on next. `--save` makes the results the
/// new baseline; the exit code is 1 when anything was lost.
/// Options: --jobs N, --only &lt;area regex&gt;, --no-oracle, --save.</summary>
static class Gate
{
    /// <summary>One check's outcome. Capture checks fill all fields, oracle checks only <see cref="Hash"/>.
    /// <see cref="Captured"/> is -1 for a trace with no doodad capture (no .doodads.json); <see cref="Heights"/> (cells
    /// whose ground height differs) is -1 with no height capture (no .heights.bin).</summary>
    public sealed record Result(string Hash, int Draws = 0, int TraceDraws = 0, string? FirstDiff = null, int Walk = -1, int Projectile = -1,
        int Placed = 0, int Matched = 0, int Captured = 0, int Heights = -1, int Objects = -1)
    {
        /// <summary>The port's area transitions and waypoints (<see cref="AreaObjects"/>) are the captured ones
        /// (.transitions.json: type, model and cell), no more and no fewer.</summary>
        [System.Text.Json.Serialization.JsonIgnore] public bool ObjectsOk => HashOk && Objects == 0;
        [System.Text.Json.Serialization.JsonIgnore] public bool HeightsOk => HashOk && Heights == 0;
        [System.Text.Json.Serialization.JsonIgnore] public bool HashOk => Hash == "match";
        [System.Text.Json.Serialization.JsonIgnore] public bool GridsOk => Walk == 0 && Projectile == 0;
        /// <summary>The port placed the captured doodads, and only those; an empty capture (an area that places none)
        /// counts when the tile hash matches.</summary>
        [System.Text.Json.Serialization.JsonIgnore] public bool DoodadsOk => HashOk && Captured >= 0 && Placed == Captured && Matched == Captured;
        /// <summary>Every draw the port made equals the trace's (the client's later draws belong to stages not ported).</summary>
        [System.Text.Json.Serialization.JsonIgnore] public bool DrawsOk => HashOk && (FirstDiff is null || FirstDiff.EndsWith(" (port stopped)"));
    }

    internal static readonly System.Text.RegularExpressions.Regex Recorded =new("AreaTransition|Waypoint$|TownEntrance$");

    static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        // Stop messages as written (no ' for an apostrophe), so the baseline reads and diffs as text.
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static int Run(string[] args)
    {
        string root = Shared.Root();
        string here = Path.Combine(root, "scripts", "re", "levelgen");
        int jobs = Math.Clamp(Environment.ProcessorCount / 2, 1, 8);
        string only = ".";
        bool oracle = true, save = false;
        for (int i = 0; i < args.Length; i++)
            switch (args[i])
            {
                case "--jobs": jobs = int.Parse(args[++i]); break;
                case "--only": only = args[++i]; break;
                case "--no-oracle": oracle = false; break;
                case "--save": save = true; break;
            }
        var tasks = new List<string>();
        foreach (var trace in Directory.GetFiles(Path.Combine(root, "data", "walkability"), "*_seed1.gen.json").Order())
        {
            string area = Path.GetFileName(trace)[..^"_seed1.gen.json".Length];
            if (System.Text.RegularExpressions.Regex.IsMatch(area, only)) tasks.Add($"capture\t{area}");
        }
        if (oracle)
            foreach (var ((area, seed), (level, hash)) in Shared.LoggedHashes())
                if (System.Text.RegularExpressions.Regex.IsMatch(area, only)) tasks.Add($"oracle\t{area}\t{seed}\t{level}\t{hash}");
        // An area with no seed-1 trace (its seed 1 the client cannot generate either): each of its traced loads at another
        // seed, checked as fully as a seed-1 capture ("load <area> <seed>"; ServerLevelAreas supports the area on it).
        if (oracle)
            foreach (var ((area, seed), (_, hash)) in Shared.LoggedHashes())
                if (seed != 1 && System.Text.RegularExpressions.Regex.IsMatch(area, only)
                    && !File.Exists(Path.Combine(root, "data", "walkability", $"{area}_seed1.gen.json"))
                    && File.Exists(Path.Combine(root, "data", "walkability", $"{area}_seed{seed}.gen.json")))
                    tasks.Add($"load\t{area}\t{seed}\t{hash}");
        if (oracle)
            foreach (var ((area, seed), (level, message)) in Shared.LoggedFailures())
                if (System.Text.RegularExpressions.Regex.IsMatch(area, only))
                    tasks.Add($"failure\t{area}\t{seed}\t{level}\t{Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(message))}");

        var clock = Stopwatch.StartNew();
        var results = RunWorkers(tasks, jobs);
        Console.WriteLine($"gate: {tasks.Count} checks with {jobs} workers in {clock.Elapsed.TotalSeconds:0}s");
        Write(Path.Combine(here, "gate-latest.json"), results);

        string baselinePath = Path.Combine(here, "gate-baseline.json");
        var baseline = File.Exists(baselinePath)
            ? JsonSerializer.Deserialize<SortedDictionary<string, Result>>(File.ReadAllText(baselinePath)) ?? new()
            : new SortedDictionary<string, Result>();
        // A partial run compares only what it ran.
        if (only != "." || !oracle)
            baseline = new(baseline.Where(b => results.ContainsKey(b.Key)).ToDictionary(b => b.Key, b => b.Value));
        Summary(results, baseline);
        int lost = Compare(results, baseline);
        Clusters(results);
        if (save)
        {
            Write(baselinePath, only == "." && oracle ? results : Merge(baselinePath, results));
            Console.WriteLine($"saved as the baseline ({Path.GetRelativePath(root, baselinePath)})");
        }
        return lost > 0 ? 1 : 0;
    }

    /// <summary>`levelgen survey [--jobs N]`: every WorldAreas row with a topology that has no generation trace
    /// (data/tables/WorldAreas.json), run through the port at seed 1 and its area level: generation, walkability,
    /// the doodad stage and the cell heights. Nothing to compare with: "ok" says only that the port got through, a
    /// stop names what it does not support. Written to survey-latest.json beside the baseline (local).</summary>
    public static int Survey(string[] args)
    {
        string root = Shared.Root();
        int jobs = Math.Clamp(Environment.ProcessorCount / 2, 1, 8);
        for (int i = 0; i < args.Length; i++)
            if (args[i] == "--jobs") jobs = int.Parse(args[++i]);
        using var areas = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "data", "tables", "WorldAreas.json")));
        var tasks = new List<string>();
        foreach (var row in areas.RootElement.EnumerateArray())
        {
            string id = row.GetProperty("Id").GetString()!;
            if (row.GetProperty("TopologiesKeys").GetArrayLength() == 0) continue;
            if (Directory.GetFiles(Path.Combine(root, "data", "walkability"), $"{id}_seed*.gen.json").Length > 0) continue;
            tasks.Add($"survey\t{id}\t{Math.Max(1, row.GetProperty("AreaLevel").GetInt32())}");
        }
        var clock = Stopwatch.StartNew();
        var results = RunWorkers(tasks, jobs);
        Console.WriteLine($"survey: {tasks.Count} areas with {jobs} workers in {clock.Elapsed.TotalSeconds:0}s");
        Write(Path.Combine(root, "scripts", "re", "levelgen", "survey-latest.json"), results);
        foreach (var g in results.Values.GroupBy(r => r.Hash.StartsWith("stop") ? r.Hash : "ok").OrderByDescending(g => g.Count()))
            Console.WriteLine($"  {g.Count(),4} × {g.Key}");
        return 0;
    }

    /// <summary>`levelgen tourcheck &lt;tour file&gt; [--jobs N]`: runs every "&lt;area&gt; &lt;seed&gt;" stop of a /tour list through
    /// the port (at the area's WorldAreas level) and comments out the stops it fails on, with the reason. Such a seed
    /// the client most likely cannot generate either: it shows "Terrain Generation Failure" and hangs, which stops the
    /// tour until the client is restarted (1_1_3 seed 2042820024 fails in both at the same edge).</summary>
    public static int TourCheck(string[] args)
    {
        string root = Shared.Root();
        int jobs = Math.Clamp(Environment.ProcessorCount / 2, 1, 8);
        string file = args[0];
        for (int i = 1; i < args.Length; i++)
            if (args[i] == "--jobs") jobs = int.Parse(args[++i]);
        using var areas = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "data", "tables", "WorldAreas.json")));
        var levels = areas.RootElement.EnumerateArray().ToDictionary(r => r.GetProperty("Id").GetString()!, r => Math.Max(1, r.GetProperty("AreaLevel").GetInt32()));
        var lines = File.ReadAllLines(file);
        var tasks = new List<string>();
        foreach (var line in lines)
        {
            var f = line.Split('#')[0].Split((char[])[' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (f.Length >= 2 && uint.TryParse(f[1], out _)) tasks.Add($"try\t{f[0]}\t{f[1]}\t{levels.GetValueOrDefault(f[0], 1)}");
        }
        var results = RunWorkers(tasks, jobs);
        int dropped = 0;
        var output = lines.Select(line =>
        {
            var f = line.Split('#')[0].Split((char[])[' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (f.Length < 2 || !results.TryGetValue($"try {f[0]} {f[1]}", out var r) || !r.Hash.StartsWith("stop")) return line;
            dropped++;
            return $"# port fails: {f[0]} {f[1]}: {r.Hash[6..]}";
        }).ToList();
        File.WriteAllLines(file, output);
        Console.WriteLine($"{file}: {tasks.Count} stops, {dropped} commented out (the port fails on them)");
        return 0;
    }

    /// <summary>`levelgen loads [area regex] [--jobs N]`: every logged load at a seed other than 1 that has a trace (the
    /// random-seed tours), checked as fully as a seed-1 capture: draws, both grids, doodads, cell heights and
    /// transitions. The gate's oracle checks only their tile hash. Written to loads-latest.json beside the baseline
    /// (local); nothing is compared with the baseline.</summary>
    public static int Loads(string[] args)
    {
        string root = Shared.Root();
        int jobs = Math.Clamp(Environment.ProcessorCount / 2, 1, 8);
        string only = ".";
        for (int i = 0; i < args.Length; i++)
            if (args[i] == "--jobs") jobs = int.Parse(args[++i]);
            else only = args[i];
        var tasks = new List<string>();
        foreach (var ((area, seed), (_, hash)) in Shared.LoggedHashes())
            if (seed != 1 && System.Text.RegularExpressions.Regex.IsMatch(area, only)
                && File.Exists(Path.Combine(root, "data", "walkability", $"{area}_seed{seed}.gen.json")))
                tasks.Add($"load\t{area}\t{seed}\t{hash}");
        var clock = Stopwatch.StartNew();
        var results = RunWorkers(tasks, jobs);
        Console.WriteLine($"loads: {tasks.Count} traced loads at other seeds with {jobs} workers in {clock.Elapsed.TotalSeconds:0}s");
        Write(Path.Combine(root, "scripts", "re", "levelgen", "loads-latest.json"), results);
        var rs = results.Values;
        int With(Func<Result, bool> has) => rs.Count(has);
        Console.WriteLine($"  tile hash:   {With(r => r.HashOk)} of {rs.Count}");
        Console.WriteLine($"  draws:       {With(r => r.DrawsOk)} of {rs.Count}");
        Console.WriteLine($"  grids:       {With(r => r.HashOk && r.GridsOk)} of {With(r => r.Walk >= 0)}");
        Console.WriteLine($"  doodads:     {With(r => r.DoodadsOk)} of {With(r => r.Captured >= 0)}");
        Console.WriteLine($"  heights:     {With(r => r.HeightsOk)} of {With(r => r.Heights >= 0)}");
        Console.WriteLine($"  transitions: {With(r => r.ObjectsOk)} of {With(r => r.Objects >= 0)}");
        foreach (var (key, r) in results)
        {
            var bad = new List<string>();
            if (!r.HashOk) bad.Add(r.Hash);
            else
            {
                if (!r.DrawsOk) bad.Add($"draws {r.Draws}/{r.TraceDraws} first at {r.FirstDiff}");
                if (!r.GridsOk) bad.Add($"walk {r.Walk} projectile {r.Projectile} cells");
                if (r.Captured >= 0 && !r.DoodadsOk) bad.Add($"doodads {r.Matched} of {r.Captured} captured, {r.Placed} placed");
                if (r.Heights > 0) bad.Add($"heights {r.Heights} cells");
                if (r.Objects > 0) bad.Add($"transitions {r.Objects} off");
            }
            if (bad.Count > 0) Console.WriteLine($"  {key[5..]}: {string.Join("; ", bad)}");
        }
        return 0;
    }

    static Result TryArea(string areaId, uint seed, int level)
    {
        LevelGenerator.Generate(Shared.Files(), AreaInfo.Load(Shared.Read, areaId, level), seed);
        return new Result("ok");
    }

    /// <summary>A survey task: the port at seed 1, as far as it goes (<see cref="Survey"/>). The size goes in
    /// <see cref="Result.FirstDiff"/> ("w x h tiles, n doodads, heights lo … hi").</summary>
    static Result SurveyArea(string areaId, int level)
    {
        var files = Shared.Files();
        var area = AreaInfo.Load(Shared.Read, areaId, level);
        var rng = new LevelRandom(1, area.RandomnessDisabled);
        var result = LevelGenerator.Generate(files, area, 1, rng: rng);
        var walk = Walkability.Build(result.Grid, tile => files.Walk(tile.Path));
        var stage = new DoodadStage(files, rng, result.Grid, walk, []);
        stage.Run(result.Records);
        var heights = CellHeights.Build(result.Grid, tile => files.Walk(tile.Path));
        return new Result("ok", FirstDiff: $"{result.Grid.Width}x{result.Grid.Height} tiles, {stage.Placed.Count} doodads, " +
            $"heights {(heights.Length == 0 ? 0 : heights.Min())} … {(heights.Length == 0 ? 0 : heights.Max())}");
    }

    /// <summary>One check per line, so a diff of the baseline shows the checks that changed.</summary>
    static void Write(string path, SortedDictionary<string, Result> results) =>
        File.WriteAllText(path, "{\n" + string.Join(",\n", results.Select(r => $"  {JsonSerializer.Serialize(r.Key)}: {JsonSerializer.Serialize(r.Value, Json)}")) + "\n}\n");

    static SortedDictionary<string, Result> Merge(string baselinePath, SortedDictionary<string, Result> results)
    {
        var all = JsonSerializer.Deserialize<SortedDictionary<string, Result>>(File.ReadAllText(baselinePath)) ?? new();
        foreach (var (k, v) in results) all[k] = v;
        return all;
    }

    static SortedDictionary<string, Result> RunWorkers(List<string> tasks, int jobs)
    {
        var queue = new ConcurrentQueue<string>(tasks);
        var results = new ConcurrentDictionary<string, Result>();
        string dll = Path.Combine(AppContext.BaseDirectory, "levelgen.dll");
        int done = 0;
        var threads = Enumerable.Range(0, Math.Min(jobs, Math.Max(1, tasks.Count))).Select(_ => new Thread(() =>
        {
            var psi = new ProcessStartInfo("dotnet", $"\"{dll}\" worker")
            {
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
            };
            psi.Environment["LEVELGEN_LOCAL"] = "1";
            foreach (var key in psi.Environment.Keys.Where(k => k.StartsWith("LEVELGEN_", StringComparison.Ordinal) && k != "LEVELGEN_LOCAL").ToList())
                psi.Environment.Remove(key);
            using var p = Process.Start(psi)!;
            p.ErrorDataReceived += (_, _) => { };
            p.BeginErrorReadLine();
            while (queue.TryDequeue(out var task))
            {
                p.StandardInput.WriteLine(task);
                p.StandardInput.Flush();
                string? line = p.StandardOutput.ReadLine();
                string key = Key(task);
                results[key] = line is null ? new Result("stop: worker died") : JsonSerializer.Deserialize<Result>(line)!;
                int n = Interlocked.Increment(ref done);
                if (n % 50 == 0) Console.Error.WriteLine($"  {n} of {tasks.Count}");
                if (line is null) break;
            }
            p.StandardInput.Close();
            p.WaitForExit();
        })).ToList();
        foreach (var t in threads) t.Start();
        foreach (var t in threads) t.Join();
        // A worker that died leaves its queue to the others; anything still queued was never run.
        while (queue.TryDequeue(out var task)) results[Key(task)] = new Result("stop: not run");
        return new SortedDictionary<string, Result>(results, StringComparer.Ordinal);
    }

    static string Key(string task)
    {
        var f = task.Split('\t');
        return f[0] switch { "capture" => $"capture {f[1]}", "survey" => $"survey {f[1]}", "try" => $"try {f[1]} {f[2]}", "failure" => $"failure {f[1]} {f[2]}", "load" => $"load {f[1]} {f[2]}", _ => $"oracle {f[1]} {f[2]}" };
    }

    /// <summary>`levelgen worker`: one task per stdin line, one JSON result per stdout line.</summary>
    public static int Worker()
    {
        var output = Console.Out;
        // Generation code may print; only results go to stdout.
        Console.SetOut(TextWriter.Null);
        for (string? line; (line = Console.In.ReadLine()) is not null;)
        {
            var f = line.Split('\t');
            Result r;
            try
            {
                r = f[0] switch
                {
                    "capture" => Capture(f[1]),
                    "survey" => SurveyArea(f[1], int.Parse(f[2])),
                    "try" => TryArea(f[1], uint.Parse(f[2]), int.Parse(f[3])),
                    "load" => Capture(f[1], uint.Parse(f[2]), uint.Parse(f[3])),
                    "failure" => Failure(f[1], uint.Parse(f[2]), int.Parse(f[3]), System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(f[4]))),
                    _ => Oracle(f[1], uint.Parse(f[2]), int.Parse(f[3]), uint.Parse(f[4])),
                };
            }
            catch (Exception e) { r = new Result("stop: " + Reason(e)); }
            finally { Shared.ResetHooks(); }
            output.WriteLine(JsonSerializer.Serialize(r));
            output.Flush();
        }
        return 0;
    }

    static string Reason(Exception e) => $"{e.GetType().Name}: {e.Message.Split('\n')[0]}";

    /// <summary>A logged load: its tile hash against the log's; with a trace of it (every logged load has one since
    /// the capture tours) and another hash or a stop, also the draws against the trace and the first difference, as for
    /// a capture.</summary>
    /// <summary>A load the client failed to generate: "match" when the port fails the same way
    /// (<see cref="Shared.SameFailure"/>), else what the port did instead (generated it, failed otherwise, or stopped).</summary>
    static Result Failure(string areaId, uint seed, int level, string message)
    {
        try
        {
            LevelGenerator.Generate(Shared.Files(), AreaInfo.Load(Shared.Read, areaId, level), seed);
            return new Result($"generated (the client: {message})");
        }
        catch (NotSupportedException e) { return new Result("stop: " + Reason(e)); }
        catch (Exception e) when (e is InvalidOperationException or IndexOutOfRangeException or ArgumentException)
        {
            string mine = e.Message.Split('\n')[0];
            return new Result(Shared.SameFailure(message, mine) ? "match" : $"another failure: {mine} (the client: {message})");
        }
        finally { Shared.ResetHooks(); }
    }

    static Result Oracle(string areaId, uint seed, int level, uint hash)
    {
        string stem = Path.Combine(Shared.Root(), "data", "walkability", $"{areaId}_seed{seed}");
        bool traced = File.Exists(stem + ".gen.json");
        try
        {
            var result = LevelGenerator.Generate(Shared.Files(), AreaInfo.Load(Shared.Read, areaId, level), seed);
            if (result.Grid.Hash() == hash) return new Result("match");
            if (!traced) return new Result("mismatch");
        }
        catch (Exception) when (traced) { }
        finally { Shared.ResetHooks(); }
        return Capture(areaId, seed, hash) with { Walk = -1, Projectile = -1, Placed = 0, Matched = 0, Captured = 0, Heights = -1, Objects = -1 };
    }

    static Result Capture(string areaId, uint seed = 1, uint? loggedHash = null)
    {
        var files = Shared.Files();
        string stem = Path.Combine(Shared.Root(), "data", "walkability", $"{areaId}_seed{seed}");
        using var trace = JsonDocument.Parse(File.ReadAllText(stem + ".gen.json"));
        var t = trace.RootElement;
        int level = t.TryGetProperty("level", out var l) ? l.GetInt32() : 1;
        uint clientHash = loggedHash ?? t.GetProperty("tileHash").GetUInt32();
        var area = AreaInfo.Load(Shared.Read, areaId, level);
        var rng = new LevelRandom(seed, area.RandomnessDisabled) { Log = [] };
        GenerationResult? result = null;
        string hash;
        try
        {
            result = LevelGenerator.Generate(files, area, seed, rng: rng);
            hash = result.Grid.Hash() == clientHash ? "match" : "mismatch";
        }
        catch (Exception e) { hash = "stop: " + Reason(e); }
        int walkDiff = -1, projDiff = -1, placed = 0, matched = 0, captured = File.Exists(stem + ".doodads.json") ? 0 : -1, heightDiff = -1, objectDiff = -1;
        if (result is not null)
        {
            var walk = Walkability.Build(result.Grid, tile => files.Walk(tile.Path));
            var stage = new DoodadStage(files, rng, result.Grid, walk, []);
            try { stage.Run(result.Records); }
            catch (Exception e) when (e is NotSupportedException or InvalidDataException or InvalidOperationException) { }
            walkDiff = Differ(walk.Walk, stem + ".bin");
            projDiff = Differ(walk.Projectile, stem + ".projectile.bin");
            if (File.Exists(stem + ".transitions.json"))
            {
                using var tj = JsonDocument.Parse(File.ReadAllText(stem + ".transitions.json"));
                var recorded = tj.RootElement.GetProperty("objects").EnumerateArray()
                    .Select(o => (o.GetProperty("objectType").GetString()!, o.GetProperty("model").GetString()!.ToLowerInvariant(), o.GetProperty("x").GetInt32(), o.GetProperty("y").GetInt32())).ToHashSet();
                // The recorder keeps only type names it knows (walkmaps.js: AreaTransition, …Waypoint, …TownEntrance);
                // the port finds every type with the AreaTransition component (BossArenaEntranceTransition, …).
                var generated = AreaObjects.Transitions(files, result).Where(o => Recorded.IsMatch(o.Type)).Select(o => (o.Type, o.Model.ToLowerInvariant(), o.X, o.Y)).ToHashSet();
                objectDiff = recorded.Except(generated).Count() + generated.Except(recorded).Count();
            }
            if (File.Exists(stem + ".heights.bin"))
            {
                var heights = CellHeights.Build(result.Grid, tile => files.Walk(tile.Path));
                var recorded = File.ReadAllBytes(stem + ".heights.bin");
                heightDiff = recorded.Length != heights.Length * 2 ? int.MaxValue
                    : heights.Where((v, i) => v != BitConverter.ToInt16(recorded, i * 2)).Count();
            }
            if (File.Exists(stem + ".doodads.json"))
            {
                using var dd = JsonDocument.Parse(File.ReadAllText(stem + ".doodads.json"));
                var models = dd.RootElement.GetProperty("models").EnumerateArray().Select(m => m.GetString()!.ToLowerInvariant()).ToList();
                var set = dd.RootElement.GetProperty("doodads").EnumerateArray()
                    .Select(d => (models[d[0].GetInt32()], d[1].GetInt32(), d[2].GetInt32())).ToList();
                captured = set.Count;
                var lookup = set.ToHashSet();
                placed = stage.Placed.Count;
                matched = stage.Placed.Count(p => lookup.Contains((p.Model.ToLowerInvariant(), p.X, p.Y)));
            }
        }
        // Draws through the doodad stage, against the trace; the first difference names the client's caller.
        var theirs = Shared.TraceDraws(t);
        var mine = rng.Log!;
        int same = 0;
        while (same < theirs.Count && same < mine.Count && theirs[same].Kind == mine[same].Kind && theirs[same].Arg == mine[same].Arg && theirs[same].Result == mine[same].Result) same++;
        string? first = same < theirs.Count ? theirs[same].Caller + (same < mine.Count ? "" : " (port stopped)") : null;
        return new Result(hash, same, theirs.Count, first, walkDiff, projDiff, placed, matched, captured, heightDiff, objectDiff);
    }

    static int Differ(byte[] mine, string file)
    {
        if (!File.Exists(file)) return -1;
        var theirs = File.ReadAllBytes(file);
        return theirs.Length != mine.Length ? int.MaxValue : mine.Where((v, i) => v != theirs[i]).Count();
    }

    static void Summary(SortedDictionary<string, Result> now, SortedDictionary<string, Result> before)
    {
        static (int, int, int, int, int, int) Counts(IEnumerable<KeyValuePair<string, Result>> r)
        {
            var list = r.ToList();
            var caps = list.Where(p => p.Key.StartsWith("capture ")).Select(p => p.Value).ToList();
            var orc = list.Where(p => p.Key.StartsWith("oracle ")).Select(p => p.Value).ToList();
            return (orc.Count(x => x.HashOk), caps.Count(x => x.HashOk), caps.Count(x => x.HashOk && x.GridsOk), caps.Count(x => x.DrawsOk),
                caps.Count(x => x.DoodadsOk), caps.Count);
        }
        var (o1, h1, g1, r1, d1, c1) = Counts(now);
        var (o0, h0, g0, r0, d0, _) = Counts(before);
        int uncaptured = now.Count(p => p.Key.StartsWith("capture ") && p.Value.Captured < 0);
        int orcTotal = now.Keys.Count(k => k.StartsWith("oracle "));
        string D(int a, int b) => before.Count == 0 ? "" : a == b ? "" : $" ({(a > b ? "+" : "")}{a - b})";
        Console.WriteLine($"  oracle loads matching:          {o1} of {orcTotal}{D(o1, o0)}");
        static int Failed(SortedDictionary<string, Result> r) => r.Count(p => p.Key.StartsWith("failure ") && p.Value.HashOk);
        Console.WriteLine($"  client failures reproduced:     {Failed(now)} of {now.Keys.Count(k => k.StartsWith("failure "))}{D(Failed(now), Failed(before))}");
        Console.WriteLine($"  seed-1 traces, tile hash:       {h1} of {c1}{D(h1, h0)}");
        Console.WriteLine($"  seed-1 traces, both grids exact: {g1} of {c1}{D(g1, g0)}");
        Console.WriteLine($"  seed-1 traces, every port draw: {r1} of {c1}{D(r1, r0)}");
        Console.WriteLine($"  seed-1 traces, every doodad:    {d1} of {c1 - uncaptured}{D(d1, d0)} ({uncaptured} with no doodad capture)");
        static int Heights(SortedDictionary<string, Result> r) => r.Count(p => p.Key.StartsWith("capture ") && p.Value.HeightsOk);
        int withHeights = now.Count(p => p.Key.StartsWith("capture ") && p.Value.Heights >= 0);
        static int Objects(SortedDictionary<string, Result> r) => r.Count(p => p.Key.StartsWith("capture ") && p.Value.ObjectsOk);
        int withObjects = now.Count(p => p.Key.StartsWith("capture ") && p.Value.Objects >= 0);
        Console.WriteLine($"  seed-1 traces, transitions:     {Objects(now)} of {withObjects}{D(Objects(now), Objects(before))} ({c1 - withObjects} with no transitions capture)");
        Console.WriteLine($"  seed-1 traces, cell heights:    {Heights(now)} of {withHeights}{D(Heights(now), Heights(before))} ({c1 - withHeights} with no height capture)");
        var stops = now.Where(p => p.Key.StartsWith("capture ")).Select(p => p.Value.Hash).GroupBy(h => h.StartsWith("stop") ? "stop" : h)
            .ToDictionary(g => g.Key, g => g.Count());
        Console.WriteLine($"  seed-1 traces: {stops.GetValueOrDefault("match")} match, {stops.GetValueOrDefault("mismatch")} another hash, {stops.GetValueOrDefault("stop")} stop");
    }

    /// <summary>Per check, what got better or worse; returns how many got worse.</summary>
    static int Compare(SortedDictionary<string, Result> now, SortedDictionary<string, Result> before)
    {
        if (before.Count == 0) { Console.WriteLine("no baseline yet (run with --save)"); return 0; }
        var gained = new List<string>();
        var lost = new List<string>();
        foreach (var (key, r) in now)
        {
            if (!before.TryGetValue(key, out var b)) { gained.Add($"{key}: new ({r.Hash})"); continue; }
            void Check(bool was, bool isNow, string what)
            {
                if (was && !isNow) lost.Add($"{key}: {what} lost ({b.Hash} → {r.Hash})");
                else if (!was && isNow) gained.Add($"{key}: {what}");
            }
            Check(b.HashOk, r.HashOk, "tile hash");
            if (key.StartsWith("capture "))
            {
                Check(b.HashOk && b.GridsOk, r.HashOk && r.GridsOk, "grids");
                Check(b.DrawsOk, r.DrawsOk, "every port draw");
                Check(b.DoodadsOk, r.DoodadsOk, "doodads");
                Check(b.HeightsOk, r.HeightsOk, "cell heights");
                Check(b.ObjectsOk, r.ObjectsOk, "transitions");
                if (r.Draws < b.Draws) lost.Add($"{key}: draws {b.Draws} → {r.Draws} (first difference {r.FirstDiff})");
                else if (r.Draws > b.Draws) gained.Add($"{key}: draws {b.Draws} → {r.Draws}");
                if (b.HashOk && r.HashOk && r.Matched < b.Matched) lost.Add($"{key}: doodads matched {b.Matched} → {r.Matched}");
            }
            if (!b.HashOk && !r.HashOk && b.Hash != r.Hash) gained.Add($"{key}: {b.Hash} → {r.Hash}");
        }
        foreach (var key in before.Keys.Where(k => !now.ContainsKey(k))) lost.Add($"{key}: not checked any more");
        Console.WriteLine($"gained {gained.Count}, lost {lost.Count}");
        foreach (var g in gained.Take(40)) Console.WriteLine("  + " + g);
        if (gained.Count > 40) Console.WriteLine($"  + … {gained.Count - 40} more (gate-latest.json)");
        foreach (var x in lost) Console.WriteLine("  - " + x);
        return lost.Count;
    }

    /// <summary>The work queue: traces that do not match, grouped by the client's caller at the first difference, and
    /// the stops of every check grouped by reason.</summary>
    static void Clusters(SortedDictionary<string, Result> now)
    {
        var diffs = now.Where(p => p.Key.StartsWith("capture ") && p.Value.FirstDiff is not null &&
                !(p.Value.HashOk && p.Value.GridsOk && p.Value.DrawsOk && (p.Value.DoodadsOk || p.Value.Captured < 0) && (p.Value.HeightsOk || p.Value.Heights < 0) && (p.Value.ObjectsOk || p.Value.Objects < 0)))
            .GroupBy(p => p.Value.FirstDiff!).OrderByDescending(g => g.Count()).Take(12);
        Console.WriteLine("first differences (traces not fully matching), by the client's caller:");
        foreach (var g in diffs)
            Console.WriteLine($"  {g.Count(),3} × {g.Key}: {string.Join(", ", g.Select(p => p.Key["capture ".Length..]).Take(8))}{(g.Count() > 8 ? ", …" : "")}");
        // Every logged load whose tile hash is wrong, by the first difference against its own trace: the work queue
        // by how many loads one cause holds back.
        var loads = now.Where(p => p.Key.StartsWith("oracle ") && !p.Value.HashOk && p.Value.TraceDraws > 0)
            .GroupBy(p => (p.Value.FirstDiff ?? "every draw matches").Replace(" (port stopped)", "")).OrderByDescending(g => g.Count()).Take(20);
        Console.WriteLine("logged loads with another tile hash, by the client's caller at the first difference (areas):");
        foreach (var g in loads)
        {
            var areas = g.Select(p => p.Key.Split(' ')[1]).Distinct().ToList();
            Console.WriteLine($"  {g.Count(),3} × {g.Key} ({areas.Count}): {string.Join(", ", areas.Take(8))}{(areas.Count > 8 ? ", …" : "")}");
        }
        var stops = now.Where(p => p.Value.Hash.StartsWith("stop")).GroupBy(p => Normalise(p.Value.Hash)).OrderByDescending(g => g.Count()).Take(12);
        Console.WriteLine("stops (traces and oracle), by reason:");
        foreach (var g in stops) Console.WriteLine($"  {g.Count(),3} × {g.Key}");
    }

    static string Normalise(string stop)
    {
        string s = System.Text.RegularExpressions.Regex.Replace(stop, @"\(.*", "");
        s = System.Text.RegularExpressions.Regex.Replace(s, @"-?\d+", "#");
        return s.Length > 110 ? s[..110] : s;
    }
}
