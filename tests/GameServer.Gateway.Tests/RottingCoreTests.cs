using System.Net;
using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;
using GameServer.Data.Terrain.Generation;
using GameServer.Modding;
using MonsterRarity = GameServer.Core.Protocol.Instance.MonsterRarity;

namespace GameServer.Gateway.Tests;

public sealed class RottingCoreTests
{
    [Fact]
    public async Task Doedres_soul_is_visible_on_creation_and_saved_instance_restore()
    {
        using var runtime = new Runtime(); using var wire = new Wire();
        var c = new Player(new(1, DepravedTrinity.Area, 4000, 4000, 2000, 2000, (_, _) => true, []), 1, 1)
            { Character = { Name = "VisibleSoulRegression" }, Instance = new(), Connection = { Session = new("soul-visibility", wire.Stream, wire.Ciphers) } };
        runtime.Player = c;
        var v = Game.Monsters.Find(DepravedTrinity.Souls[0])!;
        int visible = v.StateMachine!.IndexOf("visible");
        Assert.Equal(0, v.StateMachine.SharedStates[visible]);
        uint first = await Game.SpawnMonster(c, v, 64,
            MonsterRarity.Unique, 2100, 2100);
        AssertVisible(first);
        uint restored = await Game.RestoreMonster(c, v,
            new(v.Row, 64, MonsterRarity.Unique, true, 2200, 2200, 50));
        AssertVisible(restored);
        Assert.Equal(50u, c.Area.Monsters[restored].Life);
        Assert.Null(DepravedTrinity.StartStates(Game.Monsters.Find(DepravedTrinity.Boss)!));
        Assert.Null(DepravedTrinity.StartStates(Game.Monsters.Find(DepravedTrinity.Souls[1])!));
        void AssertVisible(uint id)
        {
            var origin = c.Instance.World.MonsterOrigins[id].Object;
            Assert.Equal(1, c.Area.Monsters[id].States!.SharedStates[visible]);
            Assert.Equal(1, origin.StateMachine!.SharedStates[visible]);
            Assert.Contains(visible, origin.StateMachine.Overridden);
        }
    }

    [GameFilesFact]
    public void Native_organ_gates_keep_their_domain_names_and_land_outside_each_arena()
    {
        foreach (uint seed in new uint[] { 1, 2, 3 })
        {
            var layout = AreaLayouts.For(AreaTransitionCatalog.HashOf(DepravedTrinity.Area)!.Value, seed);
            Assert.NotNull(layout.Level);
            var c = new Player(layout, 1, seed);
            DepravedTrinity.ConfigureRoutes(c);
            string[] names = ["Doedre", "Maligaro", "Shavronne"];
            uint[] domains = [1017109034, 3550752648, 1836249657];
            for (int n = 0; n < 3; n++)
            {
                var hub = Assert.Single(c.Layout.AreaTransitions, p => DepravedTrinity.Portal(p.Transition.Model) == n
                    && p.Transition.SourceType?.EndsWith("UnholyTrioSubBossPortal", StringComparison.OrdinalIgnoreCase) == true).Transition;
                var room = Assert.Single(layout.Level!.Records.SelectMany(r => r.Rooms), r => r.Room.Path.Contains("/" + names[n] + "/Unique/organ_"));
                var gates = c.Layout.AreaTransitions.Where(p => p.Transition.GridX >= room.X * 23 && p.Transition.GridX < (room.X + room.Width) * 23
                    && p.Transition.GridY >= room.Y * 23 && p.Transition.GridY < (room.Y + room.Height) * 23).Select(p => p.Transition).ToArray();
                var arena = Assert.Single(gates, g => g.SourceType == "Metadata/Terrain/Mountain/Belly/Objects/BellyArenaTransition");
                var back = Assert.Single(gates, g => g.SourceType == "Metadata/MiscellaneousObjects/PortalToggleable");
                Assert.Equal(domains[n], hub.Label);
                Assert.Equal(1058717380u, back.Label);
                Assert.NotEqual(hub.TeleportTo, arena.TeleportTo);
                Assert.Equal((hub.GridX, hub.GridY), back.TeleportTo);
                var landing = DepravedTrinity.Destination(c, hub)!.Value;
                Assert.InRange(landing.X, room.X * 23, (room.X + room.Width) * 23 - 1);
                Assert.InRange(landing.Y, room.Y * 23, (room.Y + room.Height) * 23 - 1);
                Assert.True(c.Layout.WalkableFor(landing.X, landing.Y, 1));
            }
            var exit = Assert.Single(c.Layout.AreaTransitions, p => p.Transition.SourceType == "Metadata/QuestObjects/Act9/MalachaiDepthsReturn").Transition;
            Assert.Equal(AreaTransitionCatalog.HashOf("2_9_10_1"), exit.DestinationHash16);
            Assert.Null(exit.TeleportTo);
            Assert.Null(DepravedTrinity.Destination(c, exit));
            var before = c.Layout;
            DepravedTrinity.ConfigureRoutes(c);
            Assert.Same(before, c.Layout);
        }
    }

    [Fact]
    public void Organ_portals_follow_their_placed_boss_rooms_and_cannot_skip_living_souls()
    {
        foreach (var offset in new[] { (0, 0), (700, 1300) })
        {
            var transitions = new (uint, AreaTransitionObject)[7];
            var c = new Player(new(1, DepravedTrinity.Area, 5000, 5000, 20, 20, (_, _) => true, transitions), 1, 1);
            c.Character.QuestFlags.Add("a9q1-BlackHeartOpen"); // A completed character still has to clear this new instance.
            c.LaterFormPoints[DepravedTrinity.Boss] = (3000 + offset.Item1, 2000 + offset.Item2);
            var names = new[] { "DoedrePortal", "MaligaroPortal", "ShavronnePortal" };
            for (int n = 0; n < 3; n++)
            {
                var at = (X: 1000 + n * 700 + offset.Item1, Y: 1500 + offset.Item2);
                c.Area.Monsters[(uint)n + 1] = new("soul", new PlayerWalk(at.X, at.Y, 1), 80, 5, 1000)
                    { Variety = Game.Monsters.Find(DepravedTrinity.Souls[n]) };
                var portal = new AreaTransitionObject(100 + n * 30 + offset.Item1, 100 + offset.Item2, 1, names[n] + ".ao")
                    { SourceType = "Metadata/Terrain/Act9/Area10Level2/Objects/UnholyTrioSubbossPortal" };
                transitions[n * 2] = ((uint)n + 10, portal);
                // The same art is used at both ends, but the endpoint in the boss room is a return.
                transitions[n * 2 + 1] = ((uint)n + 20, portal with { GridX = at.X + 20, GridY = at.Y });
            }
            var heart = new AreaTransitionObject(200 + offset.Item1, 150 + offset.Item2, 1)
                { SourceType = "Metadata/QuestObjects/Act9/HarvestFinalBossTransition" };
            transitions[6] = (30u, heart);
            for (int n = 0; n < 3; n++)
            {
                var entry = transitions[n * 2]; var back = transitions[n * 2 + 1];
                var at = c.Area.Monsters[(uint)n + 1].Walk.Position(0);
                Assert.True(DepravedTrinity.GateShown(c, entry.Item1, entry.Item2));
                Assert.False(DepravedTrinity.GateShown(c, back.Item1, back.Item2));
                Assert.False(DepravedTrinity.GateShown(c, 30, heart));
                Assert.Equal(c.Layout.Near((at.X - 18, at.Y - 12)), DepravedTrinity.Destination(c, entry.Item2));
                Assert.Equal(c.Layout.Near((entry.Item2.GridX + 10, entry.Item2.GridY)), DepravedTrinity.Destination(c, back.Item2));
                c.Area.Monsters[(uint)n + 1].Life = 0;
                Assert.False(DepravedTrinity.GateShown(c, entry.Item1, entry.Item2));
                Assert.True(DepravedTrinity.GateShown(c, back.Item1, back.Item2));
            }
            Assert.True(DepravedTrinity.GateShown(c, 30, heart));
            var centre = c.LaterFormPoints[DepravedTrinity.Boss];
            Assert.Equal(c.Layout.Near((centre.X, centre.Y - 28)), DepravedTrinity.Destination(c, heart));
            Assert.Null(DepravedTrinity.Destination(c, heart with { SourceType = "unrelated" }));
        }
    }

    sealed class Runtime : IDisposable
    {
        readonly string saves = Path.Combine(Path.GetTempPath(), $"localexile-trinity-{Guid.NewGuid():N}.db");
        readonly IGameLogic? previousLogic = Program.GameLogicOrNull;
        readonly IModuleLifetime? previousLifetime = Game.CombatLifetime;
        readonly ModuleLifetime lifetime = new("Game");
        public Player? Player;
        public Runtime() { Saves.Open(saves); Game.CombatLifetime = lifetime; Program.SetGameLogic(new GameServer.Modules.Game.GameLogic()); }
        public void Dispose()
        {
            if (Player is { } c) c.Closed = true;
            lifetime.Stop(ex => throw ex); Game.CombatLifetime = previousLifetime; Program.SetGameLogic(previousLogic!);
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
        public Wire() { listener.Start(); var accept = listener.AcceptTcpClientAsync(); writer.Connect(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port); reader = accept.GetAwaiter().GetResult(); }
        public void Dispose() { reader.Dispose(); writer.Dispose(); listener.Stop(); }
    }
}
