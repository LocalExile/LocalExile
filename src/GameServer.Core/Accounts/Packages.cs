using System.Text.Json;

namespace GameServer.Core.Accounts;

/// <summary>What a <c>module.json</c> says about the company it keeps: the modules it cannot share a league with, and, for a mode
/// (a module that changes the whole game), the only modules that may (the game itself always may).</summary>
public sealed record ModuleLimits(IReadOnlyList<string> Conflicts, IReadOnlyList<string>? Only);

/// <summary>
/// The packages among the mods (<c>Mods/Binaries/&lt;id&gt;/</c>, Agents/docs/dev/modding.md): a mod's folder that carries leagues
/// in <c>Leagues/</c> and modules in <c>Modules/</c>, laid out as the repository's own. Read once when a server starts, as the
/// leagues are, by Auth and the Gateway alike.
/// </summary>
public static class Packages
{
    public static string Folder => Environment.GetEnvironmentVariable("Game_MODS_FOLDER") ?? "Mods/Binaries";

    static readonly JsonDocumentOptions Json = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    /// <summary>The <paramref name="name"/> folder ("Leagues" or "Modules") of every package that is on: one with a readable
    /// <c>mod.json</c>, not turned off by it or by the state file (/mod_disable). None with <c>Game_MODS=0</c>.</summary>
    public static IReadOnlyList<string> Roots(string name, string? folder = null)
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
        var roots = new List<string>();
        foreach (string dir in Directory.GetDirectories(folder).Order(StringComparer.Ordinal))
        {
            string root = Path.Combine(dir, name);
            if (Directory.Exists(root) && Enabled(dir) is { } enabled && choices.GetValueOrDefault(Path.GetFileName(dir), enabled)) roots.Add(root);
        }
        return roots;
    }

    // Its mod.json's "enabled" (true when absent); null when it has no readable mod.json, so is no mod.
    static bool? Enabled(string dir)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "mod.json")), Json);
            return doc.RootElement.ValueKind != JsonValueKind.Object ? null
                : !(doc.RootElement.TryGetProperty("enabled", out var on) && on.ValueKind == JsonValueKind.False);
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>The limits the modules under these folders declare, by module id (the folder's name); a module that declares
    /// none is not in it. A <c>module.json</c> whose limits are not lists of ids is an error line.</summary>
    public static Dictionary<string, ModuleLimits> Limits(IEnumerable<string> roots, List<string> errors)
    {
        var limits = new Dictionary<string, ModuleLimits>(StringComparer.OrdinalIgnoreCase);
        var folders = new Stack<string>(roots.Where(Directory.Exists));
        while (folders.TryPop(out string? dir))
        {
            string manifest = Path.Combine(dir, "module.json");
            if (!File.Exists(manifest))
            {
                // A module's folder is not looked into, nor is a build's output (as the Gateway's host of modules reads them).
                foreach (string inside in Directory.GetDirectories(dir))
                    if (Path.GetFileName(inside) is not ("bin" or "obj")) folders.Push(inside);
                continue;
            }
            string id = Path.GetFileName(dir);
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(manifest), Json);
                string[]? Ids(string name) => !doc.RootElement.TryGetProperty(name, out var list) ? null
                    : list.ValueKind == JsonValueKind.Array && list.EnumerateArray().All(e => e.ValueKind == JsonValueKind.String) ? [.. list.EnumerateArray().Select(e => e.GetString()!)]
                    : throw new JsonException($"\"{name}\" is not a list of module ids");
                if (doc.RootElement.ValueKind != JsonValueKind.Object) continue;
                string[]? conflicts = Ids("conflicts"), only = Ids("only");
                if (conflicts is not null || only is not null) limits.TryAdd(id, new(conflicts ?? [], only));
            }
            catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { errors.Add($"{id}: module.json: {e.Message}"); }
        }
        return limits;
    }

    /// <summary>Why these modules cannot make up one league, by the limits they declare; null when they can.</summary>
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

    /// <summary>A module's id: the last part of its path under Modules/.</summary>
    public static string Id(string path) => path.Replace('\\', '/').TrimEnd('/').Split('/')[^1];
}
