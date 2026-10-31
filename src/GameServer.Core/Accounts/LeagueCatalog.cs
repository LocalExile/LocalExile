using System.Text.Json;
using GameServer.Core.Protocol.Login;

namespace GameServer.Core.Accounts;

/// <summary>A league characters can be created in. A variant (Hardcore, SSF, a league form or a combination) names the
/// league it is a form of in the matching parent field, as the client's create screen expects (<see cref="LeagueEntry"/>).</summary>
public sealed record League(string Name, string Base, bool Hardcore, bool Ssf, string? Form = null,
    string SsfParent = "", string HardcoreParent = "", string FormParent = "", string FormDescription = "", bool PermanentDeath = false)
{
    public bool IsMain => !Hardcore && !Ssf && Form is null;

    /// <summary>The card text. It must not be empty: the create screen's card (0x140cc8820) throws "invalid string
    /// position" on an empty one and the client crashes (live 2026-10-18). A leading '#' makes it a lookup key.</summary>
    public string Description =>
        (string.Equals(Base, LeagueCatalog.Standard, StringComparison.OrdinalIgnoreCase) ? "The permanent league." : $"The {Base} challenge league.")
        + (Hardcore ? " Characters that die move to the parent league." : "")
        + (Ssf ? " Solo Self-Found: no trading or parties." : "")
        + (Form is null || FormDescription.Length == 0 ? "" : " " + FormDescription);

    /// <summary>The S→C 0x1b entry. Main leagues get a card on the create screen; variants are reached through its check
    /// boxes. The client has one check box for a league form, and its wire names call it Ruthless.</summary>
    public LeagueEntry Entry() => new(Name, Description, SsfParent, HardcoreParent, FormParent, 0, 0, 0,
        (byte)((IsMain ? LeagueEntry.Main : 0) | (Hardcore ? LeagueEntry.Hardcore : 0)),
        (byte)((IsMain ? 0 : LeagueEntry.Variant) | (Form is not null ? LeagueEntry.Ruthless : 0)),
        0, 0, 0, 0xd1);
}

/// <summary>What a form's four variants are called; {base} is the base league's name.</summary>
public sealed record LeagueFormNames(string Plain, string Hardcore, string Ssf, string HardcoreSsf);

/// <summary>A family of variants of every base league, declared by a module (its <c>module.json</c>), reached with the
/// client's form check box.</summary>
/// <param name="Id">The module's id: the Gateway finds the rules by it.</param>
/// <param name="StandardNames">The variants of Standard.</param>
/// <param name="ChallengeNames">The variants of any other base league.</param>
/// <param name="Description">Added to the variants' card text.</param>
/// <param name="PermanentDeath">A Hardcore death in this form is final, instead of moving to the parent league.</param>
public sealed record LeagueForm(string Id, LeagueFormNames StandardNames, LeagueFormNames ChallengeNames, string Description = "", bool PermanentDeath = false);

/// <summary>What one module's <c>module.json</c> says about leagues ("league": { "bases": [...], "form": {...} }).</summary>
public sealed record LeagueDeclaration(bool Enabled, IReadOnlyList<string> Bases, LeagueForm? Form);

/// <summary>
/// The server's leagues: each base league with its Hardcore and SSF variants, and those of every league form. Named as the
/// official servers name them. Only Standard is the server's own: the rest come from league modules, read here from their
/// <c>module.json</c> because the Auth server, which gives the client this list, does not load them.
/// <c>Game_LEAGUES</c> (comma-separated) replaces the base leagues on offer. Any name works: the client has no league list
/// of its own.
/// </summary>
public sealed class LeagueCatalog
{
    public const string Standard = "Standard";

    public IReadOnlyList<League> All { get; }

    /// <summary>The forms on offer.</summary>
    public IReadOnlyList<LeagueForm> Forms { get; }

    // Also what is off or no longer offered: saved characters keep their league's identity.
    readonly IReadOnlyList<League> _saved;

    /// <param name="baseLeagues">The base leagues to offer.</param>
    /// <param name="forms">The forms to offer.</param>
    /// <param name="savedBases">More base leagues saved characters may be in.</param>
    /// <param name="savedForms">More forms saved characters may be in.</param>
    public LeagueCatalog(IEnumerable<string> baseLeagues, IEnumerable<LeagueForm>? forms = null,
        IEnumerable<string>? savedBases = null, IEnumerable<LeagueForm>? savedForms = null)
    {
        Forms = [.. forms ?? []];
        var bases = baseLeagues.Select(n => n.Trim()).Where(n => n.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (bases.Count == 0) bases.Add(Standard);
        All = [.. bases.SelectMany(b => Variants(b, Forms))];
        var allForms = Forms.Concat(savedForms ?? []).DistinctBy(f => f.Id, StringComparer.OrdinalIgnoreCase).ToList();
        _saved = [.. bases.Append(Standard).Concat(savedBases ?? []).Distinct(StringComparer.OrdinalIgnoreCase).SelectMany(b => Variants(b, allForms))];
    }

    /// <summary>The configured leagues and those of the modules in <c>Game_MODULES_FOLDER</c> (default Modules/Binaries).
    /// A module that is off is not offered, but its saved characters keep their league.</summary>
    public static LeagueCatalog FromEnvironment()
    {
        var declared = Environment.GetEnvironmentVariable("Game_MODULES") == "0" ? []
            : Declarations(Environment.GetEnvironmentVariable("Game_MODULES_FOLDER") ?? Path.Combine("Modules", "Binaries"));
        var on = declared.Where(d => d.Enabled).ToList();
        IEnumerable<string> bases = Environment.GetEnvironmentVariable("Game_LEAGUES") is { Length: > 0 } list ? list.Split(',')
            : on.SelectMany(d => d.Bases).Prepend(Standard);
        return new(bases, on.Select(d => d.Form).OfType<LeagueForm>(), declared.SelectMany(d => d.Bases), declared.Select(d => d.Form).OfType<LeagueForm>());
    }

    /// <summary>Reads each module's declaration in a modules folder, on or off as the Gateway's host has it.</summary>
    public static IReadOnlyList<LeagueDeclaration> Declarations(string folder)
    {
        if (!Directory.Exists(folder)) return [];
        var choices = new Dictionary<string, bool>(StringComparer.Ordinal);
        string state = Path.Combine(folder, "module-state.txt");
        var result = new List<LeagueDeclaration>();
        try
        {
            if (File.Exists(state))
                foreach (string line in File.ReadAllLines(state))
                    if (line.Split(' ', StringSplitOptions.RemoveEmptyEntries) is [var id, var value and ("on" or "off")]) choices[id] = value == "on";
            foreach (string dir in Directory.GetDirectories(folder).Order(StringComparer.Ordinal))
                if (Read(Path.GetFileName(dir), Path.Combine(dir, "module.json"), choices) is { } declared) result.Add(declared);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Console.WriteLine($"[leagues] {folder}: {e.Message}"); }
        return result;
    }

    static LeagueDeclaration? Read(string id, string path, Dictionary<string, bool> choices)
    {
        if (!File.Exists(path)) return null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            if (!doc.RootElement.TryGetProperty("league", out var league)) return null;
            bool enabled = choices.GetValueOrDefault(id, !doc.RootElement.TryGetProperty("enabled", out var on) || on.GetBoolean());
            string[] bases = league.TryGetProperty("bases", out var b) ? [.. b.EnumerateArray().Select(n => n.GetString()!)] : [];
            LeagueForm? form = null;
            if (league.TryGetProperty("form", out var f))
            {
                static LeagueFormNames Names(JsonElement n) => new(n.GetProperty("plain").GetString()!, n.GetProperty("hardcore").GetString()!,
                    n.GetProperty("ssf").GetString()!, n.GetProperty("hardcoreSsf").GetString()!);
                form = new(id, Names(f.GetProperty("standardNames")), Names(f.GetProperty("challengeNames")),
                    f.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "",
                    f.TryGetProperty("permanentDeath", out var p) && p.GetBoolean());
            }
            return new(enabled, bases, form);
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            Console.WriteLine($"[leagues] {path}: {e.Message}");
            return null;
        }
    }

    public League? Find(string? name) =>
        name is null ? null : All.FirstOrDefault(l => string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>A saved character's league, on offer or not. A name no module declares any more keeps its name (the ticket
    /// and the stash go by it) and plays by Standard's rules. Creation uses <see cref="Find"/>, so what is off is not offered.</summary>
    public League? ResolveSavedLeague(string? name) => name is null ? null : Find(name)
        ?? _saved.FirstOrDefault(l => string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase))
        ?? new League(name, Standard, false, false);

    public IReadOnlyList<LeagueEntry> Entries() => [.. All.Select(l => l.Entry())];

    private static IEnumerable<League> Variants(string b, IReadOnlyList<LeagueForm> forms)
    {
        bool std = string.Equals(b, Standard, StringComparison.OrdinalIgnoreCase);
        string hc = std ? "Hardcore" : $"Hardcore {b}";
        string ssf = $"SSF {b}";
        string hcSsf = std ? "SSF Hardcore" : $"HC SSF {b}";
        yield return new(b, b, false, false);
        yield return new(hc, b, true, false, HardcoreParent: b);
        yield return new(ssf, b, false, true, SsfParent: b);
        yield return new(hcSsf, b, true, true, SsfParent: hc);
        foreach (var form in forms)
        {
            var names = std ? form.StandardNames : form.ChallengeNames;
            League Of(string name, bool hardcore, bool solo, string parent) => new(name.Replace("{base}", b), b, hardcore, solo, form.Id,
                FormParent: parent, FormDescription: form.Description, PermanentDeath: form.PermanentDeath);
            yield return Of(names.Plain, false, false, b);
            yield return Of(names.Hardcore, true, false, hc);
            yield return Of(names.Ssf, false, true, ssf);
            yield return Of(names.HardcoreSsf, true, true, hcSsf);
        }
    }
}
