using System.Net.Sockets;
using GameServer.Core.Protocol.Login;

/// <summary>The chat commands GGG's servers have. The development and testing commands are the gm-commands mod
/// (LocalExile-Mods).</summary>
static class CommandsFeature
{
    internal static ChatCommands Register(ChatCommands commands, string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, GameServer.Modules.M_commands.CharacterPlaytime played, Func<string?> depositKeep)
    {
        return commands
        .Add("played", "/played (time spent online with this character)", async _ =>
            await conn.Commands!.Reply(GameServer.Modules.M_commands.CharacterPlaytime.Describe(played.Seconds(conn))))
        .Add("passives", "/passives (passive skill points and the allocated passives)", async _ =>
        {
            var (points, refundPoints, nodes, _) = PassiveState(conn);
            int unspent = points + PassivesFor(conn).PointsGranted(nodes) + AscendancyOrdinaryPoints(conn) - nodes.Count;
            await conn.Commands!.Reply($"Level {conn.Pools.Level}, {conn.Experience} experience: {points} passive skill point(s), {unspent} unspent, {refundPoints} refund point(s).\n" +
                string.Join("\n", nodes.Select(n => $"{n} {PassivesFor(conn).Find(n)?.Name}").DefaultIfEmpty("No passives allocated.")));
        })
        .Add("deposit", "/deposit [keep] (at your stash: currency, essences, fragments, cards, gems and flasks go to their tabs, except the items the Commands module's depositKeep setting names; /deposit keep lists them)", async args =>
        {
            var keep = (depositKeep() ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (args.Length > 0 && args[0].Equals("keep", StringComparison.OrdinalIgnoreCase))
                await commands.Reply(keep.Count == 0 ? "/deposit keeps nothing." : "/deposit keeps: " + string.Join(", ", keep) + ".");
            else await DepositAll(remote, stream, ciphers, conn, keep);
        })
        .Add("oos", "/oos (puts you and the monsters around you back where the server has them when you are out of sync)", async _ =>
        {
            await SendResync(remote, stream, ciphers, conn, "/oos");
            await conn.Commands!.Reply("Resynced.");
        });
    }
}
