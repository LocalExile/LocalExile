using System.Net;
using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;
using GameServer.Modding;
using MonsterRarity = GameServer.Core.Protocol.Instance.MonsterRarity;

namespace GameServer.Gateway.Tests;

/// <summary>The shared helpers of the party tests: a game runtime with players, and their wire. The followers' own tests
/// went with the playerbots mod to LocalExile-Mods.</summary>
public sealed class PartyFollowerTests
{
    internal sealed class Runtime : IDisposable
    {
        readonly IGameLogic? previousLogic = Program.GameLogicOrNull;
        readonly IModuleLifetime? previousLifetime = Game.CombatLifetime;
        readonly ModuleLifetime lifetime = new("Game");
        readonly List<Player> players = [];
        public Runtime()
        {
            Game.CombatLifetime = lifetime;
            Program.SetGameLogic(new GameServer.Modules.Game.GameLogic());
        }
        // peer: a fixed id for a rule that is seeded by it; otherwise the next id of the process.
        public Player PlayerOf(InstanceState instance, Wire wire, string? character = null, uint? peer = null)
        {
            AreaLayout layout = new(1, "follower", 300, 300, 100, 100, null, []);
            var player = peer is { } id
                ? new Player(layout, 1, 1) { Instance = instance, Character = { Name = character }, Id = id }
                : new Player(layout, 1, 1) { Instance = instance, Character = { Name = character } };
            player.PlayerLife = 1000; player.Connection.PlayerSent = true; player.Connection.Driven = new(); player.Connection.LockstepClient = false;
            player.Connection.Session = new("follower-test", wire.Stream, wire.Ciphers);
            players.Add(player); Program.InGame.TryAdd(player, 0);
            return player;
        }
        public void Dispose()
        {
            foreach (var player in players) { player.Closed = true; Program.InGame.TryRemove(player, out _); }
            lifetime.Stop(ex => throw ex);
            Game.CombatLifetime = previousLifetime;
            Program.SetGameLogic(previousLogic!);
        }
    }

    internal sealed class Wire : IDisposable
    {
        readonly TcpListener listener = new(IPAddress.Loopback, 0);
        readonly TcpClient writer = new();
        readonly TcpClient reader;
        public readonly ConnectionCiphers Ciphers = new(new byte[64], false);
        public NetworkStream Stream => writer.GetStream();
        public Wire()
        {
            listener.Start();
            var accepting = listener.AcceptTcpClientAsync();
            writer.Connect(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
            reader = accepting.GetAwaiter().GetResult();
        }
        public void Dispose() { reader.Dispose(); writer.Dispose(); listener.Stop(); }
    }
}
