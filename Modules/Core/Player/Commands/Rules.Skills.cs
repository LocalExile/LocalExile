using System.Net.Sockets;
using System.Collections.Frozen;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using Progression = GameServer.Core.World.Progression;
using DamageModel = GameServer.Core.World.DamageModel;
using DamagePacket = GameServer.Core.World.DamagePacket;

static partial class CommandsRules
{
    // An intentional /set_level reset invalidates older XP events before publishing its current level and points.
    internal static async Task SetLevel(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, int level)
    {
        lock (conn.Monsters)
            if (HardcoreProgressBlocked(conn)) return;
        var change = RecordExperienceReset(conn, level);
        await PublishExperienceChange(remote, stream, ciphers, conn, change, $"/set_level {level}");
    }
}
