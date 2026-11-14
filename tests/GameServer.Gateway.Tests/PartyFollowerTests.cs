using System.Net;
using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;
using GameServer.Modding;
using MonsterRarity = GameServer.Core.Protocol.Instance.MonsterRarity;

namespace GameServer.Gateway.Tests;

public sealed class PartyFollowerTests
{
    [Fact]
    public async Task Shared_deaths_reward_nearby_players_and_hide_each_observers_bar_once()
    {
        await PartyExperienceChecks.Verify();
        await MixedPartyChecks.Verify();
        using var runtime = new Runtime();
        using var firstWire = new Wire();
        using var sleepingWire = new Wire();
        var instance = new InstanceState();
        var first = runtime.Connection(instance, firstWire);
        var sleeping = runtime.Connection(instance, sleepingWire);
        instance.World.Runner = first;
        const uint id = 0x08000000;
        var variety = Game.Monsters.All.First();
        var monster = new SpawnedMonster("shared victim", new PlayerWalk(110, 100, 30), 60, 8, 1000)
            { Life = 0, Dead = true };
        instance.World.Monsters[id] = monster;
        instance.World.MonsterOrigins[id] = new(InstanceEntities.ObjectTypeHash(variety.Id),
            new MonsterObject(110, 100, 1000, variety.ComponentsOrBase, variety.AnimatedObject),
            (ushort)variety.Row, 1, MonsterRarity.Normal);
        // Also repair views whose life was already mirrored to zero before this module loaded.
        first.MonsterViews[id] = new((110, 100), (110, 100), 0);
        sleeping.MonsterViews[id] = new((110, 100), (110, 100), 1000);
        sleeping.AsleepMonsters.Add(id);
        string hide = Convert.ToHexString(InstanceEntities.StatsUpdate(id, [(StatKeys.HideMiniLifeBar, 1)]));
        string zero = Convert.ToHexString(InstanceProtocol.LifeUpdate(id, 0));
        int Count(Wire wire, string hex) => Program.HistoryFor(wire.Ciphers).Lines().Count(line => line.Contains(hex, StringComparison.Ordinal));
        await Game.SyncInstanceMonsters("observer", firstWire.Stream, firstWire.Ciphers, first);
        Assert.Equal(1, Count(firstWire, hide));
        Assert.Equal(1, Count(firstWire, zero));
        await Game.SyncInstanceMonsters("observer", firstWire.Stream, firstWire.Ciphers, first);
        Assert.Equal(1, Count(firstWire, hide));
        await Game.SyncInstanceMonsters("sleeping", sleepingWire.Stream, sleepingWire.Ciphers, sleeping);
        Assert.Equal(0, Count(sleepingWire, hide));
        sleeping.AsleepMonsters.Remove(id);
        await Game.SyncInstanceMonsters("awake", sleepingWire.Stream, sleepingWire.Ciphers, sleeping);
        Assert.Equal(1, Count(sleepingWire, hide));
        Assert.Equal(0u, sleeping.MonsterViews[id].Life);
        monster.Dead = false; monster.Life = 500;
        await Game.SyncInstanceMonsters("revived", firstWire.Stream, firstWire.Ciphers, first);
        Assert.Equal(1, Count(firstWire, Convert.ToHexString(InstanceEntities.StatsUpdate(id, [(StatKeys.HideMiniLifeBar, 0)]))));
    }

    [Fact]
    public void Followers_clear_the_entrance_remain_calm_and_balance_attacking_with_catching_up()
    {
        ControlledPlayerBuildChecks.VerifyCampaignBuilds();
        using var runtime = new Runtime();
        using var wire = new Wire();
        var instance = new InstanceState();
        var leader = runtime.Connection(instance, wire);
        var bot = runtime.Connection(instance, wire);
        leader.Walk.Teleport(100, 100, 0);
        leader.Walk.Order(180, 100, 0);
        var ahead = Game.ControlledFollowerPoint(bot, leader, arrival: true);
        Assert.True(ahead.X > 110);
        Assert.InRange(Math.Sqrt(Math.Pow(ahead.X - 100, 2) + Math.Pow(ahead.Y - 100, 2)), 12, 32);
        bot.Walk.Teleport(201, 100, 0);
        bot.ControlledNextAction = 1000;
        Assert.True(Game.KeepControlledFollowerNear(bot, leader, 0));
        var returned = bot.Walk.Position(0);
        Assert.True(Game.Dist(returned, (100, 100)) < 32);
        Assert.Equal(.35, bot.ControlledNextAction);
        bot.Walk.Teleport(180, 100, 0);
        int action = bot.ActionCount;
        Assert.True(Game.KeepControlledFollowerNear(bot, leader, 0));
        Assert.Equal((180, 100), bot.Walk.Position(0)); // Normal catch-up walks, rather than teleports.
        Assert.True(bot.ActionCount > action);
        Assert.True(bot.Walk.Destination.X > 110);
        Assert.True(Game.Dist(bot.Walk.Destination, (100, 100)) < 32);
        // A nearby swing/cast gets its contact before following resumes, rather than being cancelled every AI tick.
        bot.Walk.Teleport(70, 100, 0);
        bot.Walk.Order(70, 100, 0);
        Game.HoldControlledFollowerAttack(bot, 0, .75, 123);
        action = bot.ActionCount;
        Assert.True(Game.KeepControlledFollowerNear(bot, leader, .2));
        Assert.Equal(action, bot.ActionCount);
        Assert.Equal((70, 100), bot.Walk.Destination);
        Assert.True(Game.KeepControlledFollowerNear(bot, leader, 1));
        Assert.True(bot.ActionCount > action);
        Assert.True(Game.ControlledFollowAttackFits(20, 37, .75, ahead: -20));
        Assert.True(Game.ControlledFollowAttackFits(30, 40, .75, ahead: 30));
        Assert.False(Game.ControlledFollowAttackFits(49, 37, .75));
        Assert.False(Game.ControlledFollowAttackFits(50, 70, 1));
        Assert.False(Game.ControlledFollowAttackFits(0, 37, 3));
        var inFront = runtime.Connection(instance, wire);
        inFront.Walk.Teleport(137, 100, 0);
        Assert.False(Game.KeepControlledFollowerNear(inFront, leader, 0));
        Assert.Equal(0, inFront.ActionCount);
        Assert.Equal((137, 100), inFront.Walk.Destination);

        leader.Walk.Teleport(100, 100, 0);
        // The idle turns are seeded by the peer id: about one id in a hundred finds no free cell and never adjusts.
        var idle = runtime.Connection(instance, wire, peer: 0x20000001);
        idle.Walk.Teleport(160, 100, 0);
        action = idle.ActionCount;
        int adjustments = 0;
        for (int tick = 0; tick <= 1200; tick++)
        {
            double now = tick / 10.0; // Two minutes of simulated AI, without a real-time wait.
            Assert.False(Game.KeepControlledFollowerNear(idle, leader, now),
                $"idle at {now}: follower {idle.Walk.Position(now)}, leader {leader.Walk.Position(now)}, goal {leader.Walk.Destination}");
            var before = idle.Walk.Position(now);
            Game.IdleControlledFollower(idle, leader, now);
            if (idle.ActionCount != action)
            {
                adjustments++;
                Assert.InRange(Game.Dist(before, idle.Walk.Destination), 2, 8);
                Assert.True(Game.Dist(idle.Walk.Destination, (100, 100)) <= 60);
                action = idle.ActionCount;
            }
            if (now < 14) Assert.Equal(0, adjustments);
        }
        Assert.InRange(adjustments, 1, 3);
        foreach (int gap in new[] { 0, 3, 55, 60 })
        {
            var standing = runtime.Connection(instance, wire);
            standing.Walk.Teleport(100 + gap, 100, 0);
            for (int tick = 0; tick < 100; tick++)
            {
                double now = tick / 10.0;
                Assert.False(Game.KeepControlledFollowerNear(standing, leader, now));
                Game.IdleControlledFollower(standing, leader, now);
                Assert.Equal(0, standing.ActionCount);
            }
        }
        var delayed = runtime.Connection(instance, wire);
        delayed.Walk.Teleport(180, 100, 0);
        delayed.Walk.SetSpeed(1, 0);
        Assert.True(Game.KeepControlledFollowerNear(delayed, leader, 0));
        Assert.Equal((180, 100), delayed.Walk.Position(0));
        Assert.True(Game.KeepControlledFollowerNear(delayed, leader, 1.3));
        Assert.True(Game.Dist(delayed.Walk.Position(1.3), (100, 100)) < 32);
        // Formation search cannot cross a disconnected wall into an apparently clear pocket.
        var isolated = new ClientState(new(1, "pocket", 300, 300, 100, 100,
            (x, y) => x <= 105 || x >= 115, []), 1, 1);
        Assert.True(Game.ControlledFollowerPoint(isolated, leader, arrival: true).X <= 105);
    }

    internal sealed class Runtime : IDisposable
    {
        readonly IGameLogic? previousLogic = Program.GameLogicOrNull;
        readonly IModuleLifetime? previousLifetime = Game.CombatLifetime;
        readonly ModuleLifetime lifetime = new("Game");
        readonly List<ClientState> players = [];
        public Runtime()
        {
            Game.CombatLifetime = lifetime;
            Program.SetGameLogic(new GameServer.Modules.Game.GameLogic());
        }
        // peer: a fixed id for a rule that is seeded by it; otherwise the next id of the process.
        public ClientState Connection(InstanceState instance, Wire wire, string? character = null, uint? peer = null)
        {
            AreaLayout layout = new(1, "follower", 300, 300, 100, 100, null, []);
            var conn = peer is { } id
                ? new ClientState(layout, 1, 1) { Instance = instance, Character = character, PeerId = id }
                : new ClientState(layout, 1, 1) { Instance = instance, Character = character };
            conn.PlayerLife = 1000; conn.PlayerSent = true; conn.Controlled = true;
            conn.Wire = ("follower-test", wire.Stream, wire.Ciphers);
            players.Add(conn); Program.InGame.TryAdd(conn, 0);
            return conn;
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
