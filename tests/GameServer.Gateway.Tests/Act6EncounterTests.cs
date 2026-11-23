using System.Net;
using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;
using GameServer.Modding;
using MonsterRarity = GameServer.Core.Protocol.Instance.MonsterRarity;

namespace GameServer.Gateway.Tests;

public sealed class Act6EncounterTests
{
    [Fact]
    public async Task A_pet_on_a_finite_walk_rejoins_immediately_after_an_intra_area_gate()
    {
        using var runtime = new Runtime();
        using var wire = new Wire();
        var player = runtime.PlayerOf("GatePet", new InstanceState(), 300, 200, wire);
        const uint id = 0x04000000;
        var pet = new SpawnedPet(player.NewWalk(10, 10, 10), 1);
        player.Pets[id] = pet;
        pet.Walk.Order(90, 10, 0);
        await GameServer.Modules.CosmeticsAndPets.CosmeticsFeature.FollowThroughGate(player);
        Assert.Equal((306, 203), pet.Walk.Destination);
        Assert.Equal(pet.Walk.Destination, pet.Target);
    }

    [Fact]
    public void Fuel_cart_uses_the_visible_radius_and_checks_the_position_sent_to_the_client()
    {
        var rail = new RailEscort([(0, 0), (65, 0), (65, 26)], 13);
        var cart = new QuestobjectsRules.BeaconCart(new("BeaconPayload", "", 0, 0, 0, null, 0), rail);
        cart.Update(1, [(22, 7)]);
        Assert.False(cart.Moving);
        cart.Update(1, [(22, 6)]);
        Assert.True(cart.Moving);
        cart.Update(0, [(23, 0)]);
        Assert.True(cart.Moving);

        // After two seconds the client has moved 26 cells. Its escort is beside the moving cart.
        cart.Update(2, [(38, 0)]);
        Assert.Equal((26d, 0d), rail.Position);
        Assert.True(cart.Moving);
        // The last order can travel only to the edge of the escort's circle, even if a tick is late.
        Assert.Equal((61, 0), cart.CommandTarget);
        cart.Update(10, [(65, 15)]);
        Assert.Equal((61d, 0d), rail.Position);
        cart.Update(1, [(65, 15)]);
        Assert.Equal((65d, 0d), rail.Position);
        Assert.True(cart.Moving);
        cart.Update(1, []);
        Assert.Equal((65d, 13d), rail.Position);
        Assert.False(cart.Moving);
        cart.Update(3, [(65, 13)]);
        Assert.Equal((65d, 13d), rail.Position);
        cart.Update(1, [(65, 26)]);
        Assert.True(rail.Complete);
        Assert.False(cart.Moving);
    }

    [Fact]
    public async Task EscortAndBossProgressionCannotSkipPendingStages()
    {
        var first = new RailEscort([(0, 0), (13, 0), (13, 26)], 13);
        var second = new RailEscort([(100, 0), (100, 13)], 13);
        first.Advance(0.5, true);
        Assert.Equal((6.5, 0d), first.Position);
        first.Advance(100, false);
        Assert.Equal((6.5, 0d), first.Position);
        first.Advance(1, true);
        Assert.Equal((13d, 6.5), first.Position);
        Assert.Equal(2, first.Target);
        Assert.Equal(1.5, first.RemainingSeconds);
        Assert.False(second.Complete);
        first.Restore((14, 20));
        Assert.Equal((13d, 20d), first.Position);
        first.Advance(100, true);
        first.Advance(100, true);
        Assert.True(first.Complete);
        Assert.Equal((13d, 26d), first.Position);
        Assert.False(second.Complete);
        second.Advance(1, true);
        Assert.True(second.Complete);

        var gate = new LifePhaseGate([75, 50, 25]);
        uint life = 1000;
        Assert.False(gate.Begin(life, 1000));
        foreach (uint floor in new uint[] { 750, 500, 250 })
        {
            life = gate.Damage(life, 1000, int.MaxValue);
            Assert.Equal(floor, life);
            Assert.Equal(floor, gate.Damage(life, 1000, int.MaxValue));
            Assert.True(gate.Begin(life, 1000));
            Assert.False(gate.Begin(life, 1000));
            Assert.Equal(life, gate.Damage(life, 1000, int.MaxValue));
            gate.Finish();
            int completed = gate.Completed;
            gate.Finish();
            Assert.Equal(completed, gate.Completed);
        }
        gate.DamageTakenPercent = 20;
        Assert.Equal(230u, gate.Damage(life, 1000, 100));
        gate.DamageTakenPercent = 100;
        Assert.Equal(150u, gate.Damage(life, 1000, 100));
        Assert.Equal(0u, gate.Damage(life, 1000, int.MaxValue));
        Assert.False(gate.Begin(0, 1000));
        Assert.Equal(1u, new LifePhaseGate([75]).Damage(1, 1, int.MaxValue));
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
              Connection = { LockstepClient = false, PlayerSent = true, Session = new(character, wire.Stream, wire.Ciphers) },
              Character = { Name = character },
              Instance = instance,
              PlayerLife = 1000000,
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
