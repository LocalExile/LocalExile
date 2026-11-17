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
    public async Task A_cancelled_Brine_King_swing_cannot_register_a_contact()
    {
        using var runtime = new Runtime();
        using var wire = new Wire();
        var instance = new InstanceState();
        var conn = runtime.Connection("CancelledBrineSwing", instance, 100, 100, wire);
        instance.World.Runner = conn;
        const uint id = 0x08000000;
        var variety = Game.Monsters.Find(BrineKing.Boss)!;
        var boss = new SpawnedMonster("Brine King", new PlayerWalk(100, 100, 30), 60, 8, 1000)
        {
            Variety = variety, Level = 49, Rarity = MonsterRarity.Unique, BossBarShown = true,
            States = BrineKing.StartStates(variety), LifePhases = new([75, 50, 25]),
            Attack = new(0x4005, 1, 8, DamagePacket.Empty) { Scripted = true },
        };
        conn.Monsters[id] = boss;
        conn.MonsterViews[id] = new((100, 100), (100, 100), boss.Life);
        var fight = new BrineKing.Fight(id, boss, conn);
        await fight.Bind(conn);
        var gate = Program.SendGates.GetValue(wire.Ciphers, _ => new(1, 1));
        Assert.True(gate.Wait(0));
        Task swing;
        try
        {
            swing = fight.Step(conn, 1);
            boss.ActionGeneration++;
        }
        finally { gate.Release(); }
        await swing;
        var contacts = (System.Collections.ICollection)typeof(BrineKing.Fight)
            .GetField("hits", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(fight)!;
        Assert.Empty(contacts);
        await fight.Step(conn, 3);
        Assert.Single(contacts.Cast<object>());
    }

    [Fact]
    public async Task A_refused_portal_create_does_not_advertise_a_phantom_portal()
    {
        using var runtime = new Runtime();
        using var wire = new Wire();
        var conn = runtime.Connection("RefusedTownPortal", new InstanceState(), 100, 100, wire);
        conn.LockstepClient = true;
        conn.LeavingArea = true;
        try
        {
            await PortalsFeature.OpenTownPortal("portal-test", wire.Stream, wire.Ciphers, conn, 100, 100, "test");
            Assert.Null(conn.ReturnPortal);
            Assert.False(PortalsRules.OpenPortals.ContainsKey(conn.Character!));
            conn.LeavingArea = false;
            await PortalsFeature.OpenTownPortal("portal-test", wire.Stream, wire.Ciphers, conn, 100, 100, "test");
            Assert.NotNull(conn.ReturnPortal);
            Assert.True(PortalsRules.OpenPortals.ContainsKey(conn.Character!));
        }
        finally { PortalsRules.OpenPortals.TryRemove(conn.Character!, out _); }
    }

    sealed class Runtime : IDisposable
    {
        readonly string saves = Path.Combine(Path.GetTempPath(), $"localexile-act6-{Guid.NewGuid():N}.db");
        readonly IGameLogic? previousLogic = Program.GameLogicOrNull;
        readonly IModuleLifetime? previousLifetime = Game.CombatLifetime;
        readonly ModuleLifetime lifetime = new("Game");
        readonly List<ClientState> players = [];
        public Runtime()
        {
            Saves.Open(saves);
            Game.CombatLifetime = lifetime;
            Program.SetGameLogic(new GameServer.Modules.Game.GameLogic());
        }
        public ClientState Connection(string character, InstanceState instance, int x, int y, Wire wire)
        {
            var conn = new ClientState(new(1, "brine-party-test", 1000, 1000, x, y, null, []), 1, 1)
            {
                Character = character, Instance = instance, PlayerLife = 1000000, PlayerSent = true,
                Pools = new(49, 1000000, 100, 2, 0), Wire = (character, wire.Stream, wire.Ciphers),
            };
            players.Add(conn);
            Program.InGame.TryAdd(conn, 0);
            return conn;
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
