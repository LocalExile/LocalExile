using GameServer.Core.Protocol.Instance;
using GameServer.Modding;
using PlayerWalk = GameServer.Core.World.PlayerWalk;

// What modules can do with a player's world (IPlayerWorld), in the Gateway's terms.
partial class Program
{
    /// <summary>The modules a connection's league lists.</summary>
    internal static IReadOnlySet<string> ModulesFor(ClientState conn) => LeagueFor(conn).Modules;
}
