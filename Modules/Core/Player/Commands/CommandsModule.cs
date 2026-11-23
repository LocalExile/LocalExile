using GameServer.Core.Protocol.Instance;
using GameServer.Modding;

namespace GameServer.Modules.M_commands;

/// <summary>The chat commands GGG's servers have (/oos, /passives, /played, /remaining, /deaths, /age, /whois, /invite,
/// /afk, /dnd). The development and testing commands are the gm-commands mod in LocalExile-Mods.</summary>
public sealed class CommandsModule : IModule
{
    /// <summary>This module's game data, built in a fixed order when it loads: before the Gateway listens, as the
    /// Gateway's own (WorldLoader). Tests that use the module call it once.</summary>
    public static void LoadData() => CommandsFeature.LoadData();

    /// <inheritdoc/>
    public void Load(IModuleContext module)
    {
        LoadData();
        var played = new CharacterPlaytime();
        var deaths = new CharacterDeaths();
        module.Lifetime.Own(played);
        GameEvents.CommandsBuilding.Add(module.Manifest.Id, (commands, player) =>
            CommandsFeature.Register(commands, player, played, deaths));
        GameEvents.EnterMessages.Add(module.Manifest.Id, player => { played.Begin(player); deaths.Observe(player); return []; });
        GameEvents.AreaTick.Add(module.Manifest.Id, GameEvents.Always((player, _) => { played.Step(player); deaths.Observe(player); return Task.CompletedTask; }));
        GameEvents.Resurrected.Add(module.Manifest.Id, player => { deaths.Observe(player); return Task.CompletedTask; });
        GameEvents.ChatAvailabilityQuery.Add(module.Manifest.Id, ChatPresence.Get);
        GameEvents.ChatActivity.Add(module.Manifest.Id, ChatPresence.ClearAfkOnActivity);
        GameEvents.PlayerLeaving.Add(module.Manifest.Id, (player, why) =>
        {
            played.End(player);
            deaths.Observe(player);
            ChatPresence.Clear(player, why);
            return Task.CompletedTask;
        });
        GameEvents.Packet(InstanceItems.CommandId).Add(module.Manifest.Id, async (player, bytes) =>
        {
            var command = InstanceItems.ReadCommand(bytes);
            switch (command.Id)
            {
                case InstanceItems.PlayedCommand:
                    await player.Commands!.Reply(CharacterPlaytime.Describe(played.Seconds(player)));
                    return true;
                case InstanceItems.AgeCommand:
                    await player.Commands!.Reply(CommandsFeature.DescribeAge(player));
                    return true;
                case InstanceItems.RemainingCommand:
                    await player.Commands!.Reply(CommandsFeature.DescribeRemaining(player));
                    return true;
                case InstanceItems.DeathsCommand:
                    await player.Commands!.Reply(CharacterDeaths.Describe(deaths.Count(player)));
                    return true;
                case InstanceItems.WhoisCommand:
                    await player.Commands!.Reply(CommandsFeature.DescribeWhois(command.Argument));
                    return true;
                case InstanceItems.AutoReplyCommand:
                    await player.Commands!.Reply(ChatPresence.SetAutoReply(player, command.Argument));
                    return true;
                case InstanceItems.AfkCommand:
                    // Preserve lockstep latency probing: S→C 0x194 elicits an automatic empty-arg C→S 0x16 AfkCommand
                    // response that Messages.cs handles via AnswerLockstepProbe when a probe is in flight.
                    if (player.Connection.LockstepClient && command.Argument.Length == 0
                        && Volatile.Read(ref player.Connection.LockstepProbeSentAt) != 0) return false;
                    await player.Commands!.Reply(ChatPresence.ToggleAfk(player, command.Argument));
                    return true;
                case InstanceItems.DndCommand or InstanceItems.DoNotDisturbCommand:
                    await player.Commands!.Reply(ChatPresence.ToggleDnd(player, command.Argument));
                    return true;
                default:
                    return false;
            }
        });
    }
}
