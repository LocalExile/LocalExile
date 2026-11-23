using System.Net.Sockets;
using GameServer.Core.Protocol.Login;

/// <summary>The chat commands GGG's servers have. The development and testing commands are the gm-commands mod
/// (LocalExile-Mods).</summary>
static class CommandsFeature
{
    internal static ChatCommands Register(ChatCommands commands, Player player, GameServer.Modules.M_commands.CharacterPlaytime played)
    {
        return commands
        .Add("played", "/played (time spent online with this character)", async _ =>
            await player.Commands!.Reply(GameServer.Modules.M_commands.CharacterPlaytime.Describe(played.Seconds(player))))
        .Add("passives", "/passives (passive skill points and the allocated passives)", async _ =>
        {
            var (points, refundPoints, nodes, _) = PassiveState(player);
            int unspent = points + PassivesFor(player).PointsGranted(nodes) + AscendancyOrdinaryPoints(player) - nodes.Count;
            await player.Commands!.Reply($"Level {player.Pools.Level}, {player.Character.Experience} experience: {points} passive skill point(s), {unspent} unspent, {refundPoints} refund point(s).\n" +
                string.Join("\n", nodes.Select(n => $"{n} {PassivesFor(player).Find(n)?.Name}").DefaultIfEmpty("No passives allocated.")));
        })
        .Add("oos", "/oos (puts you and the monsters around you back where the server has them when you are out of sync)", async _ =>
        {
            await SendResync(player, "/oos");
            await player.Commands!.Reply("Resynced.");
        });
    }
}
