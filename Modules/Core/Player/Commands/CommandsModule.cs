using GameServer.Core.Protocol.Instance;
using GameServer.Modding;

namespace GameServer.Modules.M_commands;

/// <summary>The chat commands GGG's servers have (/oos, /passives, /played, /remaining, /deaths, /age, /whois, /invite,
/// /afk, /dnd). The development and testing commands are the gm-commands mod in LocalExile-Mods.</summary>
public sealed class CommandsModule : IModule
{
    /// <inheritdoc/>
    public void Load(IModuleContext module)
    {
        var played = new CharacterPlaytime();
        var deaths = new CharacterDeaths();
        module.Lifetime.Own(played);
        GameEvents.CommandsBuilding.Add(module.Manifest.Id, (commands, remote, stream, ciphers, conn) =>
            CommandsFeature.Register(commands, remote, stream, ciphers, conn, played, deaths));
        GameEvents.EnterMessages.Add(module.Manifest.Id, conn => { played.Begin(conn); deaths.Observe(conn); return []; });
        GameEvents.Tick.Add(module.Manifest.Id, (conn, _) => { played.Step(conn); deaths.Observe(conn); return Task.CompletedTask; });
        GameEvents.Resurrected.Add(module.Manifest.Id, conn => { deaths.Observe(conn); return Task.CompletedTask; });
        GameEvents.PlayerLeaving.Add(module.Manifest.Id, (conn, why) =>
        {
            played.End(conn);
            deaths.Observe(conn);
            ChatPresence.Clear(conn, why);
            return Task.CompletedTask;
        });
        GameEvents.Packet(InstanceItems.CommandId).Add(module.Manifest.Id, async (conn, bytes) =>
        {
            var command = InstanceItems.ReadCommand(bytes);
            switch (command.Id)
            {
                case InstanceItems.PlayedCommand:
                    await conn.Commands!.Reply(CharacterPlaytime.Describe(played.Seconds(conn)));
                    return true;
                case InstanceItems.AgeCommand:
                    await conn.Commands!.Reply(CommandsFeature.DescribeAge(conn));
                    return true;
                case InstanceItems.RemainingCommand:
                    await conn.Commands!.Reply(CommandsFeature.DescribeRemaining(conn));
                    return true;
                case InstanceItems.DeathsCommand:
                    await conn.Commands!.Reply(CharacterDeaths.Describe(deaths.Count(conn)));
                    return true;
                case InstanceItems.WhoisCommand:
                    await conn.Commands!.Reply(CommandsFeature.DescribeWhois(command.Argument));
                    return true;
                case InstanceItems.AutoReplyCommand:
                    await conn.Commands!.Reply(ChatPresence.SetAutoReply(conn, command.Argument));
                    return true;
                case InstanceItems.AfkCommand:
                    // Preserve lockstep latency probing: S→C 0x194 elicits an automatic C→S 0x16 AfkCommand response
                    // that Messages.cs handles via AnswerLockstepProbe when a probe is in flight.
                    if (conn.LockstepClient && Volatile.Read(ref conn.LockstepProbeSentAt) != 0) return false;
                    await conn.Commands!.Reply(ChatPresence.ToggleAfk(conn, command.Argument));
                    return true;
                case InstanceItems.DndCommand or InstanceItems.DoNotDisturbCommand:
                    await conn.Commands!.Reply(ChatPresence.ToggleDnd(conn, command.Argument));
                    return true;
                default:
                    return false;
            }
        });
    }
}
