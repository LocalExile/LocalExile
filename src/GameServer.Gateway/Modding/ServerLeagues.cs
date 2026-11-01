global using LeagueRules = GameServer.Modding.LeagueRules;
using System.Collections.Immutable;
using GameServer.Core.Accounts;
using LeagueCode = GameServer.Modding.League;

/// <summary>A connection's league (<c>Program.LeagueFor</c>), fixed until its next area. It holds no object of a module, so a
/// connection does not keep a reloaded assembly in memory.</summary>
sealed class LeagueRuntime(League definition, IReadOnlySet<string> modules)
{
    /// <summary>The rules' defaults, with no modules: a league that is not loaded, for what still reaches the Gateway.</summary>
    public static readonly LeagueRuntime Standard = new(new League(LeagueCatalog.Standard, LeagueCatalog.Standard, false, false), ImmutableHashSet<string>.Empty);

    public string Id => definition.Name;
    public string Name => definition.Name;
    public IReadOnlySet<string> Modules => modules;

    /// <summary>Uses the game files' HardMode data; fixed for the connection, as its items and skills are.</summary>
    public bool HardMode { get; } = definition.DataSet == "HardMode";

    /// <summary>The league's rules now: its tunables hot-reload.</summary>
    public LeagueRules Rules => ServerLeagues.RulesOf(definition, modules);
}

/// <summary>Which modules each league has, and the rules they give. Set when the Gateway starts: leagues are not loaded, unloaded,
/// added or removed while it runs. One copy of each module serves every league that lists it; its hooks run per player's league.</summary>
static class ServerLeagues
{
    static ImmutableDictionary<string, Func<LeagueRules>> _rules = ImmutableDictionary.Create<string, Func<LeagueRules>>(StringComparer.OrdinalIgnoreCase);
    static ImmutableDictionary<string, LeagueCode> _code = ImmutableDictionary.Create<string, LeagueCode>(StringComparer.OrdinalIgnoreCase);

    public static void AddRules(string module, Func<LeagueRules> rules) => ImmutableInterlocked.AddOrUpdate(ref _rules, module, rules, (_, _) => rules);
    public static void RemoveRules(string module) => ImmutableInterlocked.TryRemove(ref _rules, module, out _);
    public static void SetCode(string league, LeagueCode code) => ImmutableInterlocked.AddOrUpdate(ref _code, league, code, (_, _) => code);
    public static void RemoveCode(string league) => ImmutableInterlocked.TryRemove(ref _code, league, out _);

    /// <summary>The rules a module gives now.</summary>
    public static LeagueRules? Live(string module) => _rules.GetValueOrDefault(module)?.Invoke();

    public static LeagueRules RulesOf(League league, IReadOnlySet<string> modules)
    {
        if (_code.GetValueOrDefault(league.Name)?.Rules is { } own) return own;
        foreach (string module in ModulesOf(league))
            if (modules.Contains(module) && _rules.GetValueOrDefault(module) is { } rules) return rules();
        foreach (var (module, rules) in _rules)
            if (modules.Contains(module)) return rules();
        return LeagueRules.Standard;
    }

    /// <summary>A league's modules, by id: its modules.json lists paths under Modules/, and a module's id is its folder's name.</summary>
    public static IReadOnlySet<string> ModulesOf(League league) => league.Modules.Select(path => Path.GetFileName(path)).ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Every module any loaded league lists, and the game itself: the others do not load.</summary>
    public static IReadOnlySet<string> Wanted(LeagueCatalog leagues) =>
        leagues.Loaded.SelectMany(ModulesOf).Append(GameModule).ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>The module that gives the framework its <see cref="IGameLogic"/>.</summary>
    public const string GameModule = "Game";

    /// <summary>A character's league, by name; a form's first league for a connection made without one. A league that is not
    /// loaded cannot be entered, so this is only for what still reaches the Gateway.</summary>
    public static LeagueRuntime Resolve(string? form, string? name, LeagueCatalog leagues)
    {
        var league = leagues.FindLoaded(name ?? (form is null ? LeagueCatalog.Standard : null))
            ?? (form is null ? null : leagues.Loaded.FirstOrDefault(l => l.Form == form && !l.Hardcore && !l.Ssf));
        return league is null ? LeagueRuntime.Standard : new(league, ModulesOf(league));
    }
}
