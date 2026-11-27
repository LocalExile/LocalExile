using GameServer.Core.Protocol.Instance;
using GameServer.Modding;

namespace GameServer.Modules.M_commands;

/// <summary>The chat commands GGG's servers have (/oos, /oosme, /passives, /atlaspassives, /played, /remaining, /deaths,
/// /kills, /age, /itemlevel, /whois, /ladder, /invite, /afk, /dnd, /autoreply). The development and testing commands are
/// the gm-commands mod in LocalExile-Mods.</summary>
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
        var kills = new CharacterKills();
        module.Lifetime.Own(played);
        module.Lifetime.Own(kills);
        GameEvents.CommandsBuilding.Add(module.Manifest.Id, (commands, player) =>
            CommandsFeature.Register(commands, player, played, deaths, kills));
        GameEvents.EnterMessages.Add(module.Manifest.Id, player => { played.Begin(player); deaths.Observe(player); return []; });
        GameEvents.AreaTick.Add(module.Manifest.Id, GameEvents.Always((player, _) =>
        {
            played.Step(player);
            deaths.Observe(player);
            kills.Step(player);
            return Task.CompletedTask;
        }));
        GameEvents.Resurrected.Add(module.Manifest.Id, player => { deaths.Observe(player); return Task.CompletedTask; });
        GameEvents.Kill.Add(module.Manifest.Id, kill =>
        {
            if (!kill.Monster.NoRewards
                && kill.Monster.Variety is { ExperienceMultiplier: > 0 } variety
                && !BossMods.NoRewards(variety)
                && kill.Player.PlayerLife > 0
                && Dist(kill.At, kill.Player.Walk.Position(AreaTime(kill.Player))) <= ExperienceRange
                && Monsters.BaseExperience(variety, kill.Monster.Level) > 0)
                kills.Record(kill.Player);
        });
        GameEvents.ChatAvailabilityQuery.Add(module.Manifest.Id, ChatPresence.Get);
        GameEvents.ChatActivity.Add(module.Manifest.Id, ChatPresence.ClearAfkOnActivity);
        GameEvents.MessagesHandled.Add(module.Manifest.Id, async (player, ids) =>
        {
            if (!player.HasClient || player.Closed || player.LeavingArea) return;
            for (int i = 0; i < ids.Count; i++)
                if (ChatPresence.IsActionPacket(ids[i]))
                {
                    await ChatPresence.ClearAfkOnActivity(player);
                    return;
                }
        });
        GameEvents.PlayerLeaving.Add(module.Manifest.Id, (player, why) =>
        {
            played.End(player);
            deaths.Observe(player);
            kills.End(player);
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
                    await CommandsFeature.ReplyRemaining(player);
                    return true;
                case InstanceItems.DeathsCommand:
                    await player.Commands!.Reply(CharacterDeaths.Describe(deaths.Count(player)));
                    return true;
                case InstanceItems.KillsCommand:
                    await player.Commands!.Reply(CharacterKills.Describe(kills.Count(player)));
                    return true;
                case InstanceItems.ItemLevelCommand:
                    await player.Commands!.Reply(CommandsFeature.DescribeItemLevel(player));
                    return true;
                case InstanceItems.WhoisCommand:
                    await player.Commands!.Reply(CommandsFeature.DescribeWhois(command.Argument));
                    return true;
                case InstanceItems.LadderCommand:
                    await player.Commands!.Reply(CommandsFeature.DescribeLadder(player));
                    return true;
                case InstanceItems.AutoReplyCommand:
                    await player.Commands!.Reply(ChatPresence.SetAutoReply(player, command.Argument));
                    return true;
                case InstanceItems.AfkCommand:
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
