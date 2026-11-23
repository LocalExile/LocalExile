using GameServer.Modding;

namespace GameServer.Modules.M_commands;

/// <summary>The chat commands GGG's servers have (/oos, /passives, /played), and /deposit (affinity items into their stash tabs). The development and testing commands are the gm-commands
/// mod in LocalExile-Mods.</summary>
public sealed class CommandsModule : IModule
{
    /// <summary>settings.json: the item names /deposit leaves in the inventory, comma-separated.</summary>
    public sealed class DepositSettings
    {
        public string? DepositKeep { get; set; }
    }

    // Commands.dat ShowTimePlayed is row 1; generic commands carry row + 1.
    const byte PlayedCommand = 2;
    /// <inheritdoc/>
    public void Load(IModuleContext module)
    {
        var played = new CharacterPlaytime();
        module.Lifetime.Own(played);
        GameEvents.CommandsBuilding.Add(module.Manifest.Id, (commands, player) =>
            CommandsFeature.Register(commands, player, played, () => module.Settings<DepositSettings>().DepositKeep));
        GameEvents.EnterMessages.Add(module.Manifest.Id, player => { played.Begin(player); return []; });
        GameEvents.AreaTick.Add(module.Manifest.Id, GameEvents.Always((player, _) => { played.Step(player); return Task.CompletedTask; }));
        GameEvents.PlayerLeaving.Add(module.Manifest.Id, (player, _) => { played.End(player); return Task.CompletedTask; });
        GameEvents.Packet(GameServer.Core.Protocol.Instance.InstanceItems.CommandId).Add(module.Manifest.Id, async (player, bytes) =>
        {
            if (GameServer.Core.Protocol.Instance.InstanceItems.ReadCommand(bytes).Id != PlayedCommand) return false;
            await player.Commands!.Reply(CharacterPlaytime.Describe(played.Seconds(player)));
            return true;
        });
    }
}
