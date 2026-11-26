using System.Text.Json;

namespace GameServer.Core.Accounts;

/// <summary>What a <c>module.json</c> or a <c>mod.json</c> says about the company it keeps in a league's list: the modules and
/// mods it cannot share a league with, and, for a mode (one that changes the whole game), the only ones that may (the game
/// itself always may).</summary>
public sealed record ModuleLimits(IReadOnlyList<string> Conflicts, IReadOnlyList<string>? Only);

/// <summary>
/// The mods' folders (<c>Mods/Binaries/&lt;id&gt;/</c>, Agents/docs/dev/modding.md) as a server reads them once when it starts,
/// Auth and the Gateway alike: beside its code a mod may bring leagues in <c>Leagues/</c> and world data in <c>sql/</c>, laid
/// out as the repository's own.
/// </summary>
public static class ModFolders
{
    public static string Folder => Environment.GetEnvironmentVariable("Game_MODS_FOLDER") ?? "Mods/Binaries";

    static readonly JsonDocumentOptions Json = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    /// <summary>The folders of the mods that are on: one with a readable <c>mod.json</c>, not turned off by it or by the state
    /// file (/mod_disable). None with <c>Game_MODS=0</c>.</summary>
    public static IReadOnlyList<string> On(string? folder = null)
    {
        folder ??= Folder;
        if (Environment.GetEnvironmentVariable("Game_MODS") == "0" || !Directory.Exists(folder)) return [];
        var choices = new Dictionary<string, bool>(StringComparer.Ordinal);
        string state = Path.Combine(folder, "mod-state.txt");
        try
        {
            if (File.Exists(state))
                foreach (string line in File.ReadAllLines(state))
                    if (line.Split(' ', StringSplitOptions.RemoveEmptyEntries) is [var id, var choice and ("on" or "off")]) choices[id] = choice == "on";
        }
        catch (IOException) { }
        return [.. Directory.GetDirectories(folder).Order(StringComparer.Ordinal)
            .Where(dir => Enabled(dir) is { } enabled && choices.GetValueOrDefault(Path.GetFileName(dir), enabled))];
    }

    /// <summary>The <paramref name="name"/> folder ("Leagues" or "sql") of every mod that is on and has one.</summary>
    public static IReadOnlyList<string> Roots(string name, string? folder = null) =>
        [.. On(folder).Select(dir => Path.Combine(dir, name)).Where(Directory.Exists)];

    // Its mod.json's "enabled" (true when absent); null when it has no readable mod.json, so is no mod.
    static bool? Enabled(string dir)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "mod.json")), Json);
            // The key in any case, as the Gateway's host of mods reads it.
            return doc.RootElement.ValueKind != JsonValueKind.Object ? null
                : !doc.RootElement.EnumerateObject().Any(p => p.Name.Equals("enabled", StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.False);
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>The limits the modules under these folders and these mods (<see cref="On"/>) declare, by id (the folder's
    /// name); one that declares none is not in it. A manifest whose limits are not lists of ids is an error line. A mod with a
    /// module's id is not read: the module runs, not the mod.</summary>
    public static Dictionary<string, ModuleLimits> Limits(IEnumerable<string> roots, List<string> errors, IEnumerable<string>? mods = null)
    {
        var limits = new Dictionary<string, ModuleLimits>(StringComparer.OrdinalIgnoreCase);
        void Read(string dir, string file)
        {
            string id = Path.GetFileName(dir);
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, file)), Json);
                string[]? Ids(string name) => !doc.RootElement.TryGetProperty(name, out var list) ? null
                    : list.ValueKind == JsonValueKind.Array && list.EnumerateArray().All(e => e.ValueKind == JsonValueKind.String) ? [.. list.EnumerateArray().Select(e => e.GetString()!)]
                    : throw new JsonException($"\"{name}\" is not a list of ids");
                if (doc.RootElement.ValueKind != JsonValueKind.Object) return;
                string[]? conflicts = Ids("conflicts"), only = Ids("only");
                if (conflicts is not null || only is not null) limits.TryAdd(id, new(conflicts ?? [], only));
            }
            catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { errors.Add($"{id}: {file}: {e.Message}"); }
        }
        var moduleIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var folders = new Stack<string>(roots.Where(Directory.Exists));
        while (folders.TryPop(out string? dir))
        {
            if (File.Exists(Path.Combine(dir, "module.json"))) { moduleIds.Add(Path.GetFileName(dir)); Read(dir, "module.json"); continue; }
            // A module's folder is not looked into, nor is a build's output (as the Gateway's host of modules reads them).
            foreach (string inside in Directory.GetDirectories(dir))
                if (Path.GetFileName(inside) is not ("bin" or "obj")) folders.Push(inside);
        }
        foreach (string dir in mods ?? [])
            if (!moduleIds.Contains(Path.GetFileName(dir))) Read(dir, "mod.json");
        return limits;
    }

    /// <summary>Why these modules and mods cannot make up one league, by the limits they declare; null when they can.</summary>
    public static string? Clash(IEnumerable<string> modules, IReadOnlyDictionary<string, ModuleLimits> limits)
    {
        string[] ids = [.. modules.Select(Id)];
        foreach (string id in ids)
        {
            if (!limits.TryGetValue(id, out var own)) continue;
            if (own.Conflicts.FirstOrDefault(c => ids.Contains(c, StringComparer.OrdinalIgnoreCase)) is { } other) return $"{id} cannot run with {other}";
            if (own.Only is { } only && ids.FirstOrDefault(o => !o.Equals(id, StringComparison.OrdinalIgnoreCase)
                && !o.Equals("Game", StringComparison.OrdinalIgnoreCase) && !only.Contains(o, StringComparer.OrdinalIgnoreCase)) is { } extra)
                return $"{id} is a mode and does not allow {extra}";
        }
        return null;
    }

    /// <summary>An id in a league's list: a mod's id, or the last part of a module's path under Modules/.</summary>
    public static string Id(string path) => path.Replace('\\', '/').TrimEnd('/').Split('/')[^1];
}
