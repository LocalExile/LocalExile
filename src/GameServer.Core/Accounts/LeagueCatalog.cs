using System.Text.Json;
using GameServer.Core.Protocol.Login;

namespace GameServer.Core.Accounts;

/// <summary>A league (<c>Leagues/&lt;Name&gt;/league.json</c>): where characters live, a set of modules and mods, and where they go next.
/// A variant the client shows through a check box on another league's card names it in <see cref="EntryOf"/>; with none it has a
/// card of its own.</summary>
public sealed record League(string Name, string Base, bool Hardcore, bool Ssf, string? Form = null,
    string SsfParent = "", string HardcoreParent = "", string FormParent = "", string FormDescription = "", bool PermanentDeath = false)
{
    /// <summary>Where its characters go when it ends, or when it is removed by migration. Not C# inheritance: nothing else passes on.</summary>
    public string? Parent { get; init; }
    /// <summary>The league whose card shows this one's check box.</summary>
    public string? EntryOf { get; init; }
    /// <summary>The text on its card; the client crashes on an empty one.</summary>
    public string? CardText { get; init; }
    /// <summary>Where a Hardcore death sends a character, if not the parent.</summary>
    public string? DeathLeague { get; init; }
    /// <summary>Offered on the create screen.</summary>
    public bool Creatable { get; init; } = true;
    /// <summary>It has ended: no new characters, and its characters cannot be entered until they migrate to the parent.</summary>
    public bool Ended { get; init; }
    /// <summary>"HardMode": which of the game files' data its characters use.</summary>
    public string? DataSet { get; init; }
    /// <summary>What it is made of: the modules its <c>modules.json</c> loads (paths under Modules/), then its <see cref="Mods"/>.</summary>
    public IReadOnlyList<string> Modules { get; init; } = [];
    /// <summary>False for a character's league with no folder: it can be listed but not entered.</summary>
    public bool Loaded { get; init; } = true;
    /// <summary>The mods it is made of (<c>league.json</c> "mods", ids): without one that is installed and on it does not
    /// load. A mod that runs in every league needs no listing.</summary>
    public IReadOnlyList<string> Mods { get; init; } = [];

    public bool IsMain => EntryOf is null;

    /// <summary>The card text. It must not be empty: the create screen's card (0x140cc9370) throws "invalid string
    /// position" on an empty one and the client crashes (live 2026-10-18). A leading '#' makes it a lookup key.</summary>
    public string Description => CardText is { Length: > 0 } text ? text : $"The {Name} league.";

    /// <summary>Optional LeagueCategory id sent in S→C 0x1b LeagueEntry.C1 (+0xc1), configured per league in league.json.</summary>
    public byte LaunchCategory { get; init; }

    /// <summary>The S→C 0x1b entry. Main leagues get a card on the create screen; variants are reached through its check
    /// boxes. The client has one check box for a league form, and its wire names call it Ruthless.</summary>
    public LeagueEntry Entry() => new(Name, Description, SsfParent, HardcoreParent, FormParent, 0, 0, 0,
        (byte)((IsMain ? LeagueEntry.Main : 0) | (Hardcore ? LeagueEntry.Hardcore : 0)),
        (byte)((IsMain ? 0 : LeagueEntry.Variant) | (Form is not null ? LeagueEntry.Ruthless : 0)),
        0, 0, LaunchCategory, 0xd1);
}

/// <summary>
/// The leagues in <c>Game_LEAGUES_FOLDER</c> (default Leagues) and in the mods (<see cref="ModFolders"/>), read once when a
/// server starts. Standard is required; every other league's parents must lead to Standard or Void, and one that does not is not
/// loaded, nor is one whose modules and mods cannot share a league (<see cref="ModFolders.Clash"/>) or that names a mod
/// that is not on. Auth and the Gateway read the same folders. Any name works: the client has no league list of its own.
/// </summary>
public sealed class LeagueCatalog
{
    public const string Standard = "Standard";

    /// <summary>The leagues that are loaded, ended and Void included.</summary>
    public IReadOnlyList<League> Loaded { get; }

    /// <summary>What can be created in.</summary>
    public IReadOnlyList<League> All { get; }

    /// <summary>Why a folder's league did not load.</summary>
    public IReadOnlyList<string> Errors { get; }

    /// <param name="mods">The ids of the mods that are on; null: not checked.</param>
    public LeagueCatalog(IEnumerable<League> leagues, IReadOnlyList<string>? errors = null, IReadOnlyDictionary<string, ModuleLimits>? limits = null,
        IReadOnlySet<string>? mods = null)
    {
        var errs = new List<string>(errors ?? []);
        var list = new List<League>();
        // The first of a name stays: the repository's own leagues come before a mod's.
        foreach (var l in leagues)
            if (list.Any(o => o.Name.Equals(l.Name, StringComparison.OrdinalIgnoreCase))) errs.Add($"{l.Name}: a league of that name is already loaded");
            else list.Add(l);
        var byName = list.ToDictionary(l => l.Name, StringComparer.OrdinalIgnoreCase);
        bool Rooted(League l)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (League? at = l; at is not null; at = at.Parent is { } p && byName.TryGetValue(p, out var next) ? next : null)
            {
                if (!seen.Add(at.Name)) return false;
                if (at.Parent is null) return at.Name.Equals(Standard, StringComparison.OrdinalIgnoreCase) || at.Name.Equals(HardcoreDeathRules.VoidLeague, StringComparison.OrdinalIgnoreCase);
                if (!byName.ContainsKey(at.Parent)) return false;
            }
            return false;
        }
        var good = new List<League>();
        foreach (var l in list)
        {
            if (!Rooted(l)) { errs.Add($"{l.Name}: its parents do not lead to Standard or Void"); continue; }
            if (l.Modules.Distinct(StringComparer.OrdinalIgnoreCase).Count() != l.Modules.Count) { errs.Add($"{l.Name}: a module is listed twice"); continue; }
            // Not one its user removed from it (ChangeModules).
            if (mods is not null && l.Mods.FirstOrDefault(m => !mods.Contains(m)
                && l.Modules.Any(listed => ModFolders.Id(listed).Equals(m, StringComparison.OrdinalIgnoreCase))) is { } missing)
            {
                errs.Add($"{l.Name}: it needs the mod {missing}, which is not installed or is turned off");
                continue;
            }
            if (limits is not null && ModFolders.Clash(l.Modules, limits) is { } clash) { errs.Add($"{l.Name}: {clash}"); continue; }
            good.Add(l);
        }
        Loaded = good;
        Errors = errs;
        All = [.. good.Where(l => l.Creatable && !l.Ended)];
    }

    /// <summary>The leagues in a folder: each a subfolder with a league.json and a modules.json, and Source/ or Binaries/; with the
    /// user's changes to their module lists from <paramref name="moduleChanges"/> (<see cref="ChangeModules"/>) when it exists.
    /// <paramref name="modules"/> are the folders of modules, and <paramref name="mods"/> the folders of the mods that are on
    /// (<see cref="ModFolders.On"/>): a mod's <c>Leagues/</c> is read after <paramref name="folder"/>, and the leagues must
    /// keep the limits both declare.</summary>
    public static LeagueCatalog FromFolder(string folder, string? moduleChanges = null, IEnumerable<string>? modules = null, IReadOnlyList<string>? mods = null)
    {
        var leagues = new List<League>();
        var errors = new List<string>();
        var limits = modules is null && mods is null ? null : ModFolders.Limits(modules ?? [], errors, mods);
        foreach (string dir in new[] { folder }.Concat((mods ?? []).Select(m => Path.Combine(m, "Leagues"))).Where(Directory.Exists).SelectMany(Directory.GetDirectories))
        {
            string path = Path.Combine(dir, "league.json");
            // A league exists as Source or as Binaries.
            if (!File.Exists(path) || (!Directory.Exists(Path.Combine(dir, "Source")) && !Directory.Exists(Path.Combine(dir, "Binaries")))) continue;
            try { leagues.Add(Read(Path.GetFileName(dir), path)); }
            catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or IOException) { errors.Add($"{Path.GetFileName(dir)}: {e.Message}"); }
        }
        if (moduleChanges is not null && File.Exists(moduleChanges)) leagues = ChangeModules(leagues, File.ReadAllText(moduleChanges), errors, limits);
        return new(leagues.OrderBy(l => !l.Name.Equals(Standard, StringComparison.OrdinalIgnoreCase)), errors, limits,
            mods?.Select(m => Path.GetFileName(m)!).ToHashSet(StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>The user's own lists, over the leagues' <c>modules.json</c> and "mods" (which stay as they ship):
    /// <c>{"&lt;league&gt;": {"add": [paths under Modules/, or mod ids], "remove": [paths or ids]}}</c>. An added one goes last, so a
    /// module listed before it that gives rules keeps them. The game itself cannot be removed. A wrong entry is an error
    /// line and changes nothing, as is a change that leaves modules that cannot share a league; an unreadable file changes
    /// no league.</summary>
    public static List<League> ChangeModules(List<League> leagues, string json, List<string> errors, IReadOnlyDictionary<string, ModuleLimits>? limits = null)
    {
        const string Name = "league-modules.json";
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }); }
        catch (JsonException e) { errors.Add($"{Name}: {e.Message}"); return leagues; }
        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object) { errors.Add($"{Name}: not an object of league to changes"); return leagues; }
            var changed = leagues.ToList();
            foreach (var entry in doc.RootElement.EnumerateObject())
            {
                int at = changed.FindIndex(l => l.Name.Equals(entry.Name, StringComparison.OrdinalIgnoreCase));
                if (at < 0) { errors.Add($"{Name}: no league \"{entry.Name}\""); continue; }
                string[]? Paths(string name) => !entry.Value.TryGetProperty(name, out var list) ? []
                    : list.ValueKind == JsonValueKind.Array && list.EnumerateArray().All(e => e.ValueKind == JsonValueKind.String) ? [.. list.EnumerateArray().Select(e => e.GetString()!)] : null;
                if (entry.Value.ValueKind != JsonValueKind.Object || Paths("add") is not { } add || Paths("remove") is not { } remove)
                {
                    errors.Add($"{Name}: {entry.Name}'s changes are not {{\"add\": [...], \"remove\": [...]}} lists of modules");
                    continue;
                }
                static string Id(string path) => ModFolders.Id(path);
                var modules = changed[at].Modules.ToList();
                foreach (string gone in remove)
                {
                    if (Id(gone).Equals("Game", StringComparison.OrdinalIgnoreCase)) { errors.Add($"{Name}: {entry.Name} cannot remove the game itself (Core/Game)"); continue; }
                    modules.RemoveAll(m => m.Equals(gone, StringComparison.OrdinalIgnoreCase) || Id(m).Equals(Id(gone), StringComparison.OrdinalIgnoreCase));
                }
                foreach (string added in add)
                    if (!modules.Any(m => Id(m).Equals(Id(added), StringComparison.OrdinalIgnoreCase))) modules.Add(added.Replace('\\', '/'));
                if (limits is not null && ModFolders.Clash(modules, limits) is { } clash) { errors.Add($"{Name}: {entry.Name}: {clash}"); continue; }
                changed[at] = changed[at] with { Modules = modules };
            }
            return changed;
        }
    }

    static League Read(string folderName, string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        var r = doc.RootElement;
        string? Text(string n) => r.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        bool Flag(string n, bool or = false) => r.TryGetProperty(n, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : or;
        byte Byte(string n, byte or = 0) => r.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetByte(out byte b) ? b : or;
        string name = Text("name") ?? folderName;
        string? entryOf = Text("entryOf"), kind = Text("entryKind");
        // Beside league.json: the modules the league loads.
        using var modules = JsonDocument.Parse(File.ReadAllText(Path.Combine(Path.GetDirectoryName(path)!, "modules.json")), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        string[] listed = modules.RootElement.TryGetProperty("modules", out var moduleList) ? [.. moduleList.EnumerateArray().Select(e => e.GetString()!)] : [];
        string[] mods = !r.TryGetProperty("mods", out var own) ? []
            : own.ValueKind == JsonValueKind.Array && own.EnumerateArray().All(e => e.ValueKind == JsonValueKind.String && e.GetString()!.IndexOfAny(['/', '\\']) < 0)
                ? [.. own.EnumerateArray().Select(e => e.GetString()!)]
            : throw new InvalidOperationException("\"mods\" is not a list of mod ids");
        return new League(name, Text("base") ?? name, Flag("hardcore"), Flag("ssf"), Text("form"),
            SsfParent: kind == "ssf" ? entryOf ?? "" : "", HardcoreParent: kind == "hardcore" ? entryOf ?? "" : "", FormParent: kind == "form" ? entryOf ?? "" : "",
            PermanentDeath: string.Equals(Text("deathLeague"), HardcoreDeathRules.VoidLeague, StringComparison.OrdinalIgnoreCase))
        {
            Parent = Text("parent"), EntryOf = entryOf, CardText = Text("description"), DeathLeague = Text("deathLeague"),
            Creatable = Flag("creatable", true), Ended = Flag("ended"), DataSet = Text("dataSet"), Modules = [.. listed, .. mods],
            LaunchCategory = Byte("launchCategory"), Mods = mods,
        };
    }

    /// <summary>The leagues in the configured folder and in the mods, with the user's own lists (<c>data/league-modules.json</c>).</summary>
    public static LeagueCatalog FromEnvironment() => FromFolder(Environment.GetEnvironmentVariable("Game_LEAGUES_FOLDER") ?? "Leagues",
        Environment.GetEnvironmentVariable("Game_LEAGUE_MODULES") ?? Path.Combine("data", "league-modules.json"),
        [Environment.GetEnvironmentVariable("Game_MODULES_FOLDER") ?? "Modules"], ModFolders.On());

    /// <summary>The leagues as they were when this server started: leagues come and go only with a restart.</summary>
    public static LeagueCatalog Startup => _startup.Value;
    static readonly Lazy<LeagueCatalog> _startup = new(FromEnvironment);

    /// <summary>The stash key of an account in a league: every league has its own stash and gold. Standard (and a
    /// character without a league) keeps the account's own key, so the stash it had before leagues counted stays there.</summary>
    public static string StashKey(string account, string? league) =>
        string.IsNullOrEmpty(league) || league.Equals(Standard, StringComparison.OrdinalIgnoreCase) ? account : $"{account} ({league})";

    /// <summary>Whether a saved character of this league can be played: not one with no folder, an ended one or Void.</summary>
    public bool CanEnter(string? name) => ResolveSavedLeague(name) is { Loaded: true, Ended: false };

    /// <summary>A league that can be created in.</summary>
    public League? Find(string? name) =>
        name is null ? null : All.FirstOrDefault(l => string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>A loaded league by name.</summary>
    public League? FindLoaded(string? name) =>
        name is null ? null : Loaded.FirstOrDefault(l => string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>A saved character's league. One with no folder keeps its name (the ticket and the stash go by it) but is not
    /// <see cref="League.Loaded"/>: the character can be listed and not entered, until the folder is back.</summary>
    public League? ResolveSavedLeague(string? name) => name is null ? null : FindLoaded(name)
        ?? new League(name, name, false, false) { Loaded = false };

    public IReadOnlyList<LeagueEntry> Entries() => [.. All.Select(l => l.Entry())];
}
