using System.Net;
using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;
using GameServer.Modding;
using MonsterRarity = GameServer.Core.Protocol.Instance.MonsterRarity;

namespace GameServer.Gateway.Tests;

public sealed class ForkNativeRegressionTests
{
    [Fact]
    public async Task A_refused_portal_create_does_not_advertise_a_phantom_portal()
    {
        using var runtime = new Runtime();
        using var wire = new Wire();
        var player = runtime.PlayerOf("RefusedTownPortal", new InstanceState(), 100, 100, wire);
        player.Connection.LockstepClient = true;
        player.LeavingArea = true;
        try
        {
            await PortalsFeature.OpenTownPortal(player, 100, 100, "test");
            Assert.Null(player.ReturnPortal);
            Assert.False(PortalsRules.OpenPortals.ContainsKey(player.Character.Name!));
            player.LeavingArea = false;
            await PortalsFeature.OpenTownPortal(player, 100, 100, "test");
            Assert.NotNull(player.ReturnPortal);
            Assert.True(PortalsRules.OpenPortals.ContainsKey(player.Character.Name!));
        }
        finally { PortalsRules.OpenPortals.TryRemove(player.Character.Name!, out _); }
    }

    sealed class Runtime : IDisposable
    {
        readonly string saves = Path.Combine(Path.GetTempPath(), $"localexile-act6-{Guid.NewGuid():N}.db");
        readonly IGameLogic? previousLogic = Program.GameLogicOrNull;
        readonly IModuleLifetime? previousLifetime = Game.CombatLifetime;
        readonly ModuleLifetime lifetime = new("Game");
        readonly List<Player> players = [];
        public Runtime()
        {
            Saves.Open(saves);
            Game.CombatLifetime = lifetime;
            Program.SetGameLogic(new GameServer.Modules.Game.GameLogic());
        }
        public Player PlayerOf(string character, InstanceState instance, int x, int y, Wire wire)
        {
            var player = new Player(new(1, "brine-party-test", 1000, 1000, x, y, null, []), 1, 1)
            {
                Character = { Name = character },
                Instance = instance,
                PlayerLife = 1000000,
                Connection = { PlayerSent = true, Session = new(character, wire.Stream, wire.Ciphers) },
                Pools = new(49, 1000000, 100, 2, 0)
            };
            players.Add(player);
            Program.InGame.TryAdd(player, 0);
            return player;
        }
        public void Dispose()
        {
            foreach (var player in players) { player.Closed = true; Program.InGame.TryRemove(player, out _); }
            lifetime.Stop(ex => throw ex);
            Game.CombatLifetime = previousLifetime;
            Program.SetGameLogic(previousLogic!);
            TestEnvironment.ReleaseSaves(saves);
        }
    }

    sealed class Wire : IDisposable
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
