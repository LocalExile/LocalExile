using GameServer.Modding;

namespace GameServer.Modules.M_commands;

/// <summary>The chat commands GGG's servers have (/oos, /passives). The development and testing commands are the gm-commands
/// mod in LocalExile-Mods.</summary>
public sealed class CommandsModule : IModule, IDisposable
{
    readonly List<IDisposable> _events = [];

    /// <inheritdoc/>
    public void Dispose()
    {
        foreach (var e in _events) e.Dispose();
    }

    /// <inheritdoc/>
    public void Load(IModuleContext module) => _events.Add(GameEvents.CommandsBuilding.Add("Commands", CommandsFeature.Register));
}
