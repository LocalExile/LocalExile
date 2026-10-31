using GameServer.Modding;

namespace GameServer.Modules.Standard;

/// <summary>Registers the Standard league.</summary>
public sealed class StandardModule : IModule
{
    /// <inheritdoc/>
    public void Load(IModuleContext module) => module.AddLeague(new StandardLeague());
}

/// <summary>The permanent league: the rules every other league changes.</summary>
public sealed class StandardLeague : ILeague
{
    /// <inheritdoc/>
    public string Id => "standard";
    /// <inheritdoc/>
    public string ClientLeagueName => "Standard";
    /// <inheritdoc/>
    public LeagueRules Rules => LeagueRules.Standard;
}
