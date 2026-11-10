using GameServer.Modding;

namespace GameServer.Modules.M_commands;

/// <summary>The chat commands GGG's servers have (/oos, /passives). The development and testing commands are the gm-commands
/// mod in LocalExile-Mods.</summary>
public sealed class CommandsModule : IModule
{
    /// <inheritdoc/>
    public void Load(IModuleContext module) => GameEvents.CommandsBuilding.Add(module.Manifest.Id, CommandsFeature.Register);
}
