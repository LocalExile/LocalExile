using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.Diagnostics;
using DamageModel = GameServer.Core.World.DamageModel;
using DamageType = GameServer.Core.World.DamageType;
using DamagePacket = GameServer.Core.World.DamagePacket;

static partial class CommandsRules
{
    // A spawned monster's id from the argument if it is 0x hex, else the last spawned one.
    internal static uint? MonsterArg(ClientState conn, string[] args, int i)
    {
        if (args.Length > i && (args[i].StartsWith("0x", StringComparison.OrdinalIgnoreCase)))
        {
            uint id = Convert.ToUInt32(args[i], 16);
            return conn.Monsters.ContainsKey(id) ? id : null;
        }
        return conn.Monsters.Count == 0 ? null : conn.Monsters.Keys.Last();
    }

    // A cell 3 cells from the server's estimate of the player, walkable if the walk map says so.
    internal static (int X, int Y) NextToPlayer(ClientState conn)
    {
        var (px, py) = conn.Walk.Position(conn.Clock.Elapsed.TotalSeconds);
        foreach (var (dx, dy) in new[] { (3, 0), (-3, 0), (0, 3), (0, -3), (2, 2), (-2, -2) })
            if (conn.Layout.Walkable is not { } w || w(px + dx, py + dy)) return (px + dx, py + dy);
        return (px, py);
    }
}
