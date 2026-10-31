using GameServer.Modding;

namespace GameServer.Modules.Allflame;

/// <summary>Registers the Allflame challenge league (3.29, "Curse of the Allflame"; LeagueNames.dat row Deepwater).</summary>
public sealed class AllflameModule : IModule
{
    /// <inheritdoc/>
    public void Load(IModuleContext module) => module.AddLeague(new AllflameLeague());
}

/// <summary>Allflame's mechanics are not built: it is its own economy with Standard's rules, and the place they will go.</summary>
public sealed class AllflameLeague : ILeague
{
    /// <inheritdoc/>
    public string Id => "allflame";
    /// <inheritdoc/>
    public string ClientLeagueName => "Allflame";
    /// <inheritdoc/>
    public LeagueRules Rules => LeagueRules.Standard;
}
