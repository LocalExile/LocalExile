

/// <summary>Dev: the capture tour (`/tour &lt;file&gt;`, live.ps1 -Walkmaps). The file lists "&lt;area id&gt; &lt;seed&gt;" lines
/// (# comments; `levelgen tourlist` writes one). The character is sent to a new instance of each with that seed; once
/// scripts/walkmaps has written the load's generation trace, walk map and doodads, it goes on to the next. Monsters are
/// not spawned on the way. The tour belongs to the character, not the connection: every area change reconnects.
/// A seed the client cannot generate makes it show the exception and hang: the stop goes to tour-failures.txt with the
/// client's message (Client/logs/Client.txt) and the tour waits. What is left is kept in data/tour-left.txt, so after
/// the client (and the servers) are restarted with live.ps1 the tour resumes when the character arrives anywhere.</summary>
static class CaptureTour
{
    sealed class Tour
    {
        public required Queue<(string Area, ushort Hash, uint Seed)> Left { get; init; }
        public (string Area, uint Seed)? Current { get; set; }
        public DateTime StepStarted { get; set; }
        public int Total { get; init; }
        public int Captured { get; set; }
        public int Missed { get; set; }
        public bool Arrived { get; set; }
    }

    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Tour> Tours = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Where the tour keeps what is left, the failures, and the client log it reads (tests point these
    /// elsewhere).</summary>
    public static string StateFile { get; set; } = "data/tour-left.txt";
    public static string FailuresFile { get; set; } = "data/walkability/tour-failures.txt";
    public static string ClientLog { get; set; } = "Client/logs/Client.txt";
    /// <summary>Area id → HASH16 (the transition catalog; tests replace it).</summary>
    public static Func<string, ushort?> HashOf { get; set; } = AreaTransitionCatalog.HashOf;

    public static bool Active(string character) => Tours.ContainsKey(character);

    /// <summary>Drops the tour from memory, as a Gateway restart does (the saved one stays; tests).</summary>
    internal static void Unload(string character) => Tours.TryRemove(character, out _);

    static (Queue<(string, ushort, uint)> Left, List<string> Unknown) Read(IEnumerable<string> lines)
    {
        var left = new Queue<(string, ushort, uint)>();
        var unknown = new List<string>();
        foreach (var raw in lines)
        {
            // A comment starts the line or follows whitespace: some ids end in '#' (1_Labyrinth_DG_end_bottleneck_#).
            var line = System.Text.RegularExpressions.Regex.Replace(raw, @"(^|\s)#.*$", "").Trim();
            if (line.Length == 0) continue;
            var f = line.Split((char[])[' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (f.Length < 2 || !uint.TryParse(f[1], out uint seed) || seed == 0) { unknown.Add(line); continue; }
            if (HashOf(f[0]) is not { } hash) { unknown.Add(f[0]); continue; }
            left.Enqueue((f[0], hash, seed));
        }
        return (left, unknown);
    }

    /// <summary>Reads the list and starts the tour; returns what to tell the player.</summary>
    public static string Start(string character, string path)
    {
        var (left, unknown) = Read(File.ReadLines(path));
        var t = Tours[character] = new Tour { Left = left, Total = left.Count };
        Save(character, t);
        return $"Tour of {left.Count} (area, seed) pairs started{(unknown.Count > 0 ? $"; skipped {unknown.Count} unknown: {string.Join(", ", unknown.Take(5))}" : "")}. /tour stop ends it.";
    }

    /// <summary>Picks up this character's saved tour after a restart: the stop that was under way first, unless the
    /// client logged that it failed (then it is listed as a failure). Null when there is none.</summary>
    public static string? Resume(string character)
    {
        if (Active(character) || !File.Exists(StateFile)) return null;
        var lines = File.ReadAllLines(StateFile);
        if (lines.Length == 0 || lines[0] != $"# tour of {character}") return null;
        int Count(string key) => lines.Select(l => System.Text.RegularExpressions.Regex.Match(l, $@"^# {key}: (\d+)$"))
            .Where(m => m.Success).Select(m => int.Parse(m.Groups[1].Value)).FirstOrDefault();
        var (left, _) = Read(lines);
        var t = new Tour { Left = left, Total = Count("total"), Captured = Count("captured"), Missed = Count("missed") };
        string note = "";
        if (lines.FirstOrDefault(l => l.StartsWith("# current: ")) is { } cur
            && cur["# current: ".Length..].Split(' ') is [var area, var s] && uint.TryParse(s, out uint seed)
            && ClientFailure(area, seed) is { } failure)
        {
            // The saved list still has that stop first: the Gateway went down before it noticed the failure.
            if (t.Left.TryPeek(out var first) && first.Item1 == area && first.Item3 == seed) t.Left.Dequeue();
            RecordFailure(t, area, seed, failure);
            note = $"; {area} seed {seed} failed in the client ({failure})";
        }
        Tours[character] = t;
        Save(character, t);
        return $"Tour resumed: {t.Captured} captured, {t.Missed} missed, {t.Left.Count} left{note}.";
    }

    public static string Stop(string character)
    {
        if (!Tours.TryRemove(character, out var t)) return "No tour running.";
        Forget(character);
        return $"Tour stopped: {t.Captured} captured, {t.Missed} missed, {t.Left.Count} left.";
    }

    public static string Status(string character) =>
        Tours.TryGetValue(character, out var t)
            ? $"Tour: {t.Captured} of {t.Total} captured, {t.Missed} missed, {t.Left.Count} left; now {t.Current?.Area} seed {t.Current?.Seed}."
            : "No tour running.";

    /// <summary>Whether this load is the tour's current stop.</summary>
    public static bool IsCurrent(string character, string area, uint seed) =>
        Tours.TryGetValue(character, out var t) && t.Current is var (a, s) && s == seed && a.Equals(area, StringComparison.OrdinalIgnoreCase);

    /// <summary>No stop under way (just started or resumed, or the last one failed): the next arrival anywhere goes on
    /// to the next stop.</summary>
    public static bool Waiting(string character) => Tours.TryGetValue(character, out var t) && t.Current is null;

    /// <summary>The client reported the current stop generated (0x7d).</summary>
    public static void MarkArrived(string character)
    {
        if (Tours.TryGetValue(character, out var t)) t.Arrived = true;
    }

    /// <summary>The client's own message when its generation of (area, seed) failed: the first "generation failed" /
    /// "Generation Failure" line after the last "Generating level … area … with seed …" of that pair in its log.</summary>
    public static string? ClientFailure(string area, uint seed)
    {
        if (!File.Exists(ClientLog)) return null;
        string[] lines;
        try
        {
            // The running client keeps its log open for writing.
            using var log = new StreamReader(new FileStream(ClientLog, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
            lines = log.ReadToEnd().Split('\n');
        }
        catch (IOException) { return null; }
        int at = Array.FindLastIndex(lines, l => l.Contains($"area \"{area}\" with seed {seed}"));
        if (at < 0) return null;
        for (int i = at + 1; i < lines.Length && !lines[i].Contains("Generating level "); i++)
            if (lines[i].Contains("generation failed", StringComparison.OrdinalIgnoreCase)
                || lines[i].Contains("Generation Failure", StringComparison.OrdinalIgnoreCase))
                return System.Text.RegularExpressions.Regex.Replace(lines[i].Trim(), @"^.*?\] ", "");
        return null;
    }

    /// <summary>While the client has not arrived at the stop: when its log says the generation failed, the stop is a
    /// failure (tour-failures.txt, which levelgen tourlist leaves out) and the tour waits for the client to come back.
    /// Returns the client's message then.</summary>
    public static string? Failed(string character, string area, uint seed)
    {
        if (!Tours.TryGetValue(character, out var t) || t.Arrived || t.Current is not var (a, s) || s != seed || a != area) return null;
        if (ClientFailure(area, seed) is not { } failure) return null;
        RecordFailure(t, area, seed, failure);
        t.Current = null;
        Save(character, t);
        return failure;
    }

    static void RecordFailure(Tour t, string area, uint seed, string failure)
    {
        t.Missed++;
        try { File.AppendAllText(FailuresFile, $"{DateTime.Now:s} {area} {seed}: the client did not arrive ({failure})\n"); }
        catch (IOException) { }
        TourRecords.Write(area, seed, "client failed", failure);
    }

    /// <summary>Waits until scripts/walkmaps has written this load's files: the generation trace and the walk map
    /// (written after the step started), then the doodads unchanged for 3 s. False after 120 s.</summary>
    public static async Task<bool> WaitForCapture(string character, string area, uint seed)
    {
        if (!Tours.TryGetValue(character, out var t)) return false;
        string stem = $"data/walkability/{area}_seed{seed}";
        bool Fresh(string file) => File.Exists(file) && File.GetLastWriteTimeUtc(file) >= t.StepStarted.AddSeconds(-1);
        DateTime lastChange = DateTime.UtcNow, last = DateTime.MinValue;
        bool ok = false;
        for (var until = DateTime.UtcNow.AddSeconds(120); DateTime.UtcNow < until && !ok; await Task.Delay(250))
        {
            var doodads = File.Exists(stem + ".doodads.json") ? File.GetLastWriteTimeUtc(stem + ".doodads.json") : DateTime.MinValue;
            if (doodads != last) { last = doodads; lastChange = DateTime.UtcNow; }
            // The walk map is kept when one exists already (walkmaps.py), so an older one counts.
            ok = Fresh(stem + ".gen.json") && File.Exists(stem + ".bin") && DateTime.UtcNow - lastChange > TimeSpan.FromSeconds(3);
        }
        if (ok) t.Captured++;
        else t.Missed++;
        t.Current = null;
        Save(character, t);
        return ok;
    }

    /// <summary>The next stop, or null when the tour is over (it is then removed).</summary>
    public static (string Area, ushort Hash, uint Seed)? Next(string character)
    {
        if (!Tours.TryGetValue(character, out var t)) return null;
        if (t.Left.Count == 0) { Tours.TryRemove(character, out _); Forget(character); return null; }
        var next = t.Left.Peek();
        t.Current = (next.Area, next.Seed);
        // Saved with the stop still listed: a restart before it resolves tries it again, unless the client logged a
        // failure for it (Resume).
        Save(character, t);
        t.Left.Dequeue();
        t.StepStarted = DateTime.UtcNow;
        t.Arrived = false;
        return next;
    }

    public static string Summary(string character) =>
        Tours.TryGetValue(character, out var t) ? $"{t.Captured} captured, {t.Missed} missed" : "";

    static void Save(string character, Tour t)
    {
        var lines = new List<string> { $"# tour of {character}", $"# total: {t.Total}", $"# captured: {t.Captured}", $"# missed: {t.Missed}" };
        if (t.Current is var (area, seed)) lines.Add($"# current: {area} {seed}");
        lines.AddRange(t.Left.Select(s => $"{s.Area} {s.Seed}"));
        try { File.WriteAllLines(StateFile, lines); }
        catch (IOException) { }
    }

    static void Forget(string character)
    {
        try { if (File.Exists(StateFile) && File.ReadLines(StateFile).FirstOrDefault() == $"# tour of {character}") File.Delete(StateFile); }
        catch (IOException) { }
    }
}
