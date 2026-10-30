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

    /// <summary>Uses the game files' Ruthless ("HardMode") data; fixed for the connection, as its items and skills are.</summary>
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

    /// <summary>The rules of the league that uses the HardMode data, for what is built from that data.</summary>
    public static LeagueRules? HardModeRules =>
        _leagues.Values.FirstOrDefault(l => l.Catalogs == LeagueCatalogs.HardMode)?.Rules;

    /// <summary>A character's module: the Ruthless one for any Ruthless form, else the one named as its league or that
    /// league's base (Hardcore X is X). Standard when there is none, or a feature it needs is not loaded.</summary>
    public static LeagueRuntime Resolve(bool ruthless, string? name, Func<string, bool> loaded, Action<string>? log = null)
    {
        string? wanted = ruthless ? "Ruthless" : name;
        if (wanted is null || wanted.Equals(LeagueCatalog.Standard, StringComparison.OrdinalIgnoreCase)) return LeagueRuntime.Standard;
        string? baseName = LeagueCatalog.FromEnvironment().ResolveSavedLeague(name)?.Base;
        if (!_leagues.TryGetValue(wanted, out var league) && !(baseName is not null && _leagues.TryGetValue(baseName, out league)))
        {
            // Hardcore, SSF and the base leagues without a module are Standard's rules by design: only Ruthless says so.
            if (ruthless) log?.Invoke($"[leagues] no module for {wanted}: {name} plays by Standard's rules");
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
