global using LeagueRules = GameServer.Modding.LeagueRules;
using System.Collections.Immutable;
using GameServer.Core.Accounts;
using GameServer.Modding;

/// <summary>A connection's league (<c>Program.LeagueFor</c>): Standard, or what a module said. It holds no object of the
/// module (the data is the shared contracts'), so a connection does not keep an unloaded mod in memory.</summary>
sealed class LeagueRuntime(string id, string name, bool hardMode, LeagueRules rules)
{
    public static readonly LeagueRuntime Standard = new("standard", LeagueCatalog.Standard, false, LeagueRules.Standard);

    LeagueRules _rules = rules;

    public string Id { get; } = id;
    public string Name { get; } = name;

    /// <summary>Uses the game files' HardMode data; fixed for the connection, as its items and skills are.</summary>
    public bool HardMode { get; } = hardMode;

    /// <summary>The module's rules now (its tunables hot-reload), the last ones it gave once it has unloaded.</summary>
    public LeagueRules Rules => ServerLeagues.Live(Id) is { } rules ? _rules = rules : _rules;
}

/// <summary>The league modules that are loaded (<see cref="IModContext.AddLeague"/>): who a character's league belongs to.
/// Standard is built in; a league with no module, or one whose required features are not loaded, plays as Standard.</summary>
static class ServerLeagues
{
    static ImmutableDictionary<string, ILeague> _leagues = ImmutableDictionary.Create<string, ILeague>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Registers a league under its client name; false when one has that name or id already.</summary>
    public static bool Add(ILeague league)
    {
        string id = league.Id;
        return ImmutableInterlocked.Update(ref _leagues, all =>
            all.Values.Any(l => l.Id.Equals(id, StringComparison.OrdinalIgnoreCase)) ? all : all.Add(league.ClientLeagueName, league))
            && ReferenceEquals(_leagues.GetValueOrDefault(league.ClientLeagueName), league);
    }

    public static void Remove(ILeague league) =>
        ImmutableInterlocked.Update(ref _leagues, all => all.RemoveRange(all.Where(l => ReferenceEquals(l.Value, league)).Select(l => l.Key)));

    public static LeagueRules? Live(string id)
    {
        foreach (var league in _leagues.Values)
            if (league.Id.Equals(id, StringComparison.OrdinalIgnoreCase)) return league.Rules;
        return null;
    }

    // Without the standard module's folder LeagueRuntime.Standard is what is left: the rules' defaults.
    /// <summary>A character's module: the one of its league form (<see cref="ILeague.Id"/>), else the one named as its league
    /// or that league's base (Hardcore X is X). Standard when there is none, or a feature it needs is not loaded.</summary>
    public static LeagueRuntime Resolve(string? form, string? name, Func<string, bool> loaded, Action<string>? log = null)
    {
        var all = _leagues;
        ILeague? league = form is null ? null : all.Values.FirstOrDefault(l => l.Id.Equals(form, StringComparison.OrdinalIgnoreCase));
        if (league is null && form is null && !all.TryGetValue(name ?? LeagueCatalog.Standard, out league)
            && LeagueCatalog.Startup.ResolveSavedLeague(name)?.Base is { } baseName)
            all.TryGetValue(baseName, out league);
        if (league is null)
        {
            // A form always means a module; a base league without one (Hardcore, SSF) is just Standard's rules.
            if (form is not null) log?.Invoke($"[leagues] no module for {form}: {name} plays by Standard's rules");
            return LeagueRuntime.Standard;
        }
        var missing = league.RequiredFeatures.Where(f => !loaded(f)).ToList();
        if (missing.Count > 0)
        {
            log?.Invoke($"[leagues] {league.Id} needs {string.Join(", ", missing)}: {name} plays by Standard's rules");
            return LeagueRuntime.Standard;
        }
        return new(league.Id, league.ClientLeagueName, league.Catalogs == LeagueCatalogs.HardMode, league.Rules);
    }
}
