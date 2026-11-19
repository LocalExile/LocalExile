using GameServer.Modding;

namespace GameServer.Modules.M_commands;

/// <summary>The chat commands GGG's servers have (/oos, /passives, /played). The development and testing commands are the gm-commands
/// mod in LocalExile-Mods.</summary>
public sealed class CommandsModule : IModule
{
    // Commands.dat ShowTimePlayed is row 1; generic commands carry row + 1.
    const byte PlayedCommand = 2;
    /// <inheritdoc/>
    public void Load(IModuleContext module)
    {
        var played = new CharacterPlaytime();
        module.Lifetime.Own(played);
        GameEvents.CommandsBuilding.Add(module.Manifest.Id, (commands, remote, stream, ciphers, conn) =>
            CommandsFeature.Register(commands, remote, stream, ciphers, conn, played));
        GameEvents.EnterMessages.Add(module.Manifest.Id, conn => { played.Begin(conn); return []; });
        GameEvents.AreaTick.Add(module.Manifest.Id, GameEvents.Always((conn, _) => { played.Step(conn); return Task.CompletedTask; }));
        GameEvents.PlayerLeaving.Add(module.Manifest.Id, (conn, _) => { played.End(conn); return Task.CompletedTask; });
        GameEvents.Packet(GameServer.Core.Protocol.Instance.InstanceItems.CommandId).Add(module.Manifest.Id, async (conn, bytes) =>
        {
            if (GameServer.Core.Protocol.Instance.InstanceItems.ReadCommand(bytes).Id != PlayedCommand) return false;
            await conn.Commands!.Reply(CharacterPlaytime.Describe(played.Seconds(conn)));
            return true;
        });
    }
}
