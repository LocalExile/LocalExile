using GameServer.Modding;

namespace GameServer.Modules.Allflame;

/// <summary>Registers the Allflame challenge league (3.29, "Curse of the Allflame"; LeagueNames.dat row Deepwater).</summary>
public sealed class AllflameModule : IModule
{
    /// <inheritdoc/>
    public void Load(IModuleContext module) => module.AddLeague(new AllflameLeague());
}

/// <summary>Allflame's mechanics are not built: its own economy, inheriting Standard's rules and modules.</summary>
public sealed class AllflameLeague : ILeague
{
    /// <inheritdoc/>
    public string Id => "allflame";
    /// <inheritdoc/>
    public string ClientLeagueName => "Allflame";
    /// <inheritdoc/>
    public LeagueRules? Rules => null;
}
