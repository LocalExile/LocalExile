using GameServer.Core.Protocol.Login;

namespace GameServer.Core.Accounts;

/// <summary>A league characters can be created in. A variant (Hardcore, SSF, Ruthless or a combination) names the
/// league it is a form of in the matching parent field, as the client's create screen expects (<see cref="LeagueEntry"/>).</summary>
public sealed record League(string Name, string Base, bool Hardcore, bool Ssf, bool Ruthless,
    string SsfParent = "", string HardcoreParent = "", string RuthlessParent = "")
{
    public bool IsMain => !Hardcore && !Ssf && !Ruthless;

    /// <summary>The card text. It must not be empty: the create screen's card (0x140cc8820) throws "invalid string
    /// position" on an empty one and the client crashes (live 2026-10-18). A leading '#' makes it a lookup key.</summary>
    public string Description =>
        (string.Equals(Base, LeagueCatalog.Standard, StringComparison.OrdinalIgnoreCase) ? "The permanent league." : $"The {Base} challenge league.")
        + (Hardcore ? " Characters that die move to the parent league." : "")
        + (Ssf ? " Solo Self-Found: no trading or parties." : "")
        + (Ruthless ? " Ruthless: extreme item scarcity." : "");

    /// <summary>The S→C 0x1b entry. Main leagues get a card on the create screen; variants are reached through its
    /// Hardcore / Solo Self-Found / Ruthless check boxes.</summary>
    public LeagueEntry Entry() => new(Name, Description, SsfParent, HardcoreParent, RuthlessParent, 0, 0, 0,
        (byte)((IsMain ? LeagueEntry.Main : 0) | (Hardcore ? LeagueEntry.Hardcore : 0)),
        (byte)((IsMain ? 0 : LeagueEntry.Variant) | (Ruthless ? LeagueEntry.Ruthless : 0)),
        0, 0, 0, 0xd1);
}

/// <summary>
/// The server's leagues: each base league with its seven variants, named as the official servers name them
/// (Standard: Hardcore, SSF Standard, SSF Hardcore, Ruthless, Hardcore Ruthless, SSF Ruthless, SSF Hardcore Ruthless;
/// a challenge league L: Hardcore L, SSF L, HC SSF L, Ruthless L, HC Ruthless L, SSF Ruthless L, HC SSF R L).
/// Base leagues come from <c>Game_LEAGUES</c> (comma-separated), default Standard and Allflame (3.29, "Curse of the
/// Allflame"; LeagueNames.dat row Deepwater). Any name works: the client has no league list of its own.
/// </summary>
public sealed class LeagueCatalog
{
    public const string Standard = "Standard";

    public IReadOnlyList<League> All { get; }

    public LeagueCatalog(IEnumerable<string> baseLeagues)
    {
        var all = new List<League>();
        foreach (string b in baseLeagues.Select(n => n.Trim()).Where(n => n.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
            all.AddRange(Variants(b));
        if (all.Count == 0) all.AddRange(Variants(Standard));
        All = all;
    }

    public static LeagueCatalog Default { get; } = new([Standard, "Allflame"]);

    public static LeagueCatalog FromEnvironment() =>
        Environment.GetEnvironmentVariable("Game_LEAGUES") is { Length: > 0 } list ? new(list.Split(',')) : Default;

    public League? Find(string? name) =>
        name is null ? null : All.FirstOrDefault(l => string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Resolves saved characters in the configured catalog or the default Standard/Allflame families.
    /// Creation uses <see cref="Find"/> so a narrower configuration does not offer disabled families again.</summary>
    public League? ResolveSavedLeague(string? name) => Find(name) ?? Default.Find(name);

    public IReadOnlyList<LeagueEntry> Entries() => [.. All.Select(l => l.Entry())];

    private static IEnumerable<League> Variants(string b)
    {
        bool std = string.Equals(b, Standard, StringComparison.OrdinalIgnoreCase);
        string hc = std ? "Hardcore" : $"Hardcore {b}";
        string ssf = $"SSF {b}";
        string hcSsf = std ? "SSF Hardcore" : $"HC SSF {b}";
        yield return new(b, b, false, false, false);
        yield return new(hc, b, true, false, false, HardcoreParent: b);
        yield return new(ssf, b, false, true, false, SsfParent: b);
        yield return new(hcSsf, b, true, true, false, SsfParent: hc);
        yield return new(std ? "Ruthless" : $"Ruthless {b}", b, false, false, true, RuthlessParent: b);
        yield return new(std ? "Hardcore Ruthless" : $"HC Ruthless {b}", b, true, false, true, RuthlessParent: hc);
        yield return new(std ? "SSF Ruthless" : $"SSF Ruthless {b}", b, false, true, true, RuthlessParent: ssf);
        yield return new(std ? "SSF Hardcore Ruthless" : $"HC SSF R {b}", b, true, true, true, RuthlessParent: hcSsf);
    }
}
