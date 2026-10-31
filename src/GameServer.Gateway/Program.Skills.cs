using System.Net.Sockets;
using System.Collections.Frozen;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using Progression = GameServer.Core.World.Progression;
using DamageModel = GameServer.Core.World.DamageModel;
using DamagePacket = GameServer.Core.World.DamagePacket;

partial class Program
{

    internal static string CharacterName(ClientState conn) => string.IsNullOrEmpty(conn.Character) ? "Exile" : conn.Character;
}
