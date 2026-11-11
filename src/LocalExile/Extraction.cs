using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LocalExile;

/// <summary>extract-data.ps1 for a release: the same manifest (written into the package when it is built, with each entry's
/// program), the same stamps in data\extract-stamps.json and the same rule for what runs. Read-only on the GGPK.</summary>
static class Extraction
{
    static readonly string ManifestFile = Path.Combine("Tools", "extract-manifest.json");
    static readonly string StampFile = Path.Combine("data", "extract-stamps.json");

    sealed record Entry(string Name, string[] Outputs, string Runner, string Exe, string[] Args, int Version, string[] Needs);
    sealed record Stamp([property: JsonPropertyName("version")] int Version, [property: JsonPropertyName("at")] string At);

    public static void Run(Output log)
    {
        log.Step("extract", "checking the game data ...");
        var manifest = JsonSerializer.Deserialize<Entry[]>(File.ReadAllText(ManifestFile)) ?? [];
        var stamps = ReadStamps(log);

        var todo = new List<(Entry Entry, string Why)>();
        foreach (var entry in manifest)
        {
            string? why = Why(entry, stamps);
            why ??= entry.Needs.FirstOrDefault(n => todo.Any(t => t.Entry.Name == n)) is { } needed ? $"{needed} reruns" : null;
            if (why is not null) todo.Add((entry, why));
        }
        if (todo.Count == 0)
        {
            log.Info("game data is up to date");
            return;
        }

        log.Info($"{todo.Count} of {manifest.Length} data files to make from your client. A first run takes several minutes; each step is reported.");
        int done = 0;
        foreach (var (entry, why) in todo)
        {
            done++;
            log.Progress("extract", $"[{done}/{todo.Count}] {entry.Name} ({why}) ...", done - 1, todo.Count);
            var started = DateTime.Now;
            // A release ships no Python: every entry is one of its published programs (build-release.ps1 checks).
            string name = "extract-" + entry.Name;
            int code = Tools.Run(entry.Exe, entry.Args, name, log);
            string output = Local(entry.Outputs[0]);
            bool fresh = File.Exists(output) && File.GetLastWriteTime(output) >= started.AddSeconds(-2);
            if (code != 0 || !fresh)
                throw new LauncherException(ExitCode.Extraction, $"extracting {entry.Name} failed (exit {code}{(fresh ? "" : $", {output} not written")}; logs\\{name}.log):\n{Tools.Tail(Path.Combine("logs", name + ".log"))}");
            stamps[entry.Name] = new(entry.Version, DateTime.Now.ToString("s"));
            File.WriteAllText(StampFile, JsonSerializer.Serialize(stamps));
            log.Progress("extract", $"[{done}/{todo.Count}] {entry.Name} done in {(int)(DateTime.Now - started).TotalSeconds} s", done, todo.Count);
        }
    }

    /// <summary>The manifest's paths are extract-data.ps1's, with backslashes.</summary>
    static string Local(string path) => path.Replace('\\', Path.DirectorySeparatorChar);

    static string? Why(Entry entry, Dictionary<string, Stamp> stamps)
    {
        if (entry.Outputs.FirstOrDefault(o => !File.Exists(Local(o))) is { } missing) return $"{missing} missing";
        if (!stamps.TryGetValue(entry.Name, out var stamp)) return "no stamp";
        return stamp.Version != entry.Version ? $"version {stamp.Version} -> {entry.Version}" : null;
    }

    static Dictionary<string, Stamp> ReadStamps(Output log)
    {
        Directory.CreateDirectory("data");
        if (!File.Exists(StampFile)) return [];
        try { return JsonSerializer.Deserialize<Dictionary<string, Stamp>>(File.ReadAllText(StampFile)) ?? []; }
        catch (JsonException)
        {
            log.Warn($"{StampFile} is unreadable; treating every file as unstamped");
            return [];
        }
    }
}
