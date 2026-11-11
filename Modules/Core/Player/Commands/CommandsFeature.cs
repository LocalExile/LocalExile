using System.Net.Sockets;
using GameServer.Core.Protocol.Login;

/// <summary>The chat commands GGG's servers have. The development and testing commands are the gm-commands mod
/// (LocalExile-Mods).</summary>
static class CommandsFeature
{
    internal static ChatCommands Register(ChatCommands commands, string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
    {
        return commands
        .Add("passives", "/passives (passive skill points and the allocated passives)", async _ =>
        {
            var (points, refundPoints, nodes, _) = PassiveState(conn);
            int unspent = points + PassivesFor(conn).PointsGranted(nodes) + AscendancyOrdinaryPoints(conn) - nodes.Count;
            await conn.Commands!.Reply($"Level {conn.Pools.Level}, {conn.Experience} experience: {points} passive skill point(s), {unspent} unspent, {refundPoints} refund point(s).\n" +
                string.Join("\n", nodes.Select(n => $"{n} {PassivesFor(conn).Find(n)?.Name}").DefaultIfEmpty("No passives allocated.")));
        })
        .Add("oos", "/oos (puts you and the monsters around you back where the server has them when you are out of sync)", async _ =>
        {
            await SendResync(remote, stream, ciphers, conn, "/oos");
            await conn.Commands!.Reply("Resynced.");
        });
    }
}
