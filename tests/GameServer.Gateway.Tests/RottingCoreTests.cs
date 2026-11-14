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
    [GameFilesFact]
    public void Native_organ_gates_keep_their_domain_names_and_land_outside_each_arena()
    {
        foreach (uint seed in new uint[] { 1, 2, 3 })
        {
            var layout = AreaLayouts.For(AreaTransitionCatalog.HashOf(DepravedTrinity.Area)!.Value, seed);
            Assert.NotNull(layout.Level);
            var c = new ClientState(layout, 1, seed);
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
            var c = new ClientState(new(1, DepravedTrinity.Area, 5000, 5000, 20, 20, (_, _) => true, transitions), 1, 1);
            c.QuestFlags.Add("a9q1-BlackHeartOpen"); // A completed character still has to clear this new instance.
            c.LaterFormPoints[DepravedTrinity.Boss] = (3000 + offset.Item1, 2000 + offset.Item2);
            var names = new[] { "DoedrePortal", "MaligaroPortal", "ShavronnePortal" };
            for (int n = 0; n < 3; n++)
            {
                var at = (X: 1000 + n * 700 + offset.Item1, Y: 1500 + offset.Item2);
                c.Monsters[(uint)n + 1] = new("soul", new PlayerWalk(at.X, at.Y, 1), 80, 5, 1000)
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
                var at = c.Monsters[(uint)n + 1].Walk.Position(0);
                Assert.True(DepravedTrinity.GateShown(c, entry.Item1, entry.Item2));
                Assert.False(DepravedTrinity.GateShown(c, back.Item1, back.Item2));
                Assert.False(DepravedTrinity.GateShown(c, 30, heart));
                Assert.Equal(c.Layout.Near((at.X - 18, at.Y - 12)), DepravedTrinity.Destination(c, entry.Item2));
                Assert.Equal(c.Layout.Near((entry.Item2.GridX + 10, entry.Item2.GridY)), DepravedTrinity.Destination(c, back.Item2));
                c.Monsters[(uint)n + 1].Life = 0;
                Assert.False(DepravedTrinity.GateShown(c, entry.Item1, entry.Item2));
                Assert.True(DepravedTrinity.GateShown(c, back.Item1, back.Item2));
            }
            Assert.True(DepravedTrinity.GateShown(c, 30, heart));
            var centre = c.LaterFormPoints[DepravedTrinity.Boss];
            Assert.Equal(c.Layout.Near((centre.X, centre.Y - 28)), DepravedTrinity.Destination(c, heart));
            Assert.Null(DepravedTrinity.Destination(c, heart with { SourceType = "unrelated" }));
        }
    }

    [Fact]
    public async Task Trinity_waits_for_each_scorpion_wave_survives_reload_and_evacuates_only_after_death()
    {
        using var runtime = new Runtime();
        using var wire = new Wire();
        var markers = Enumerable.Range(0, 6).Select(n => new PlacedTileObject(
            "Metadata/Monsters/SpiderPlated/SpiderPlatedUnholyEmergeSpawner", "", 1980 + n * 8, 2020, 0, 0)).ToList();
        markers.Add(new("Metadata/Terrain/Act9/Area10Level2/Objects/UnholyTrioLootLocator", "", 3000, 1000, 0, 0));
        var layout = new AreaLayout(1, DepravedTrinity.Area, 4000, 4000, 2000, 2000, (_, _) => true, [])
        { Level = new(4000, 4000, [], [], [], [], [], [], []) { RoomObjects = markers } };
        var c = new ClientState(layout, 1, 1)
        {
            Character = "TrinityRegression", PlayerSent = true, PlayerLife = 1000000, Instance = new(),
            Pools = new(64, 1000000, 100, 2, 0), Wire = ("trinity", wire.Stream, wire.Ciphers),
        };
        runtime.Player = c;
        var item = ItemCatalog.Create(Program.Items.Find("Metadata/Items/Currency/CurrencyIdentification")!);
        c.Instance.Ground.Observe(c);
        uint loot = c.Instance.Ground.Add(new WorldItemObject(1995, 1995, item), c);
        uint personal = c.Instance.Ground.Add(new WorldItemObject(1990, 1990, item), c, shared: false);
        uint outside = c.Instance.Ground.Add(new WorldItemObject(500, 500, item), c);
        c.LaterFormPoints[DepravedTrinity.Boss] = (2000, 2000);
        var variety = Game.Monsters.Find(DepravedTrinity.Boss)!;
        Assert.True(DepravedTrinity.Attack(c, variety, 64, 4995).Damage.MaxTotal > 100);
        Assert.True(DepravedTrinity.Attack(c, variety, 64, 4995).Damage.MaxTotal > DepravedTrinity.Attack(c, variety, 1, 4995).Damage.MaxTotal);
        Assert.True(DepravedTrinity.Attack(c, variety, 64, 5607).Damage.MaxTotal > DepravedTrinity.Attack(c, variety, 64, 406).Damage.MaxTotal);
        var boss = new SpawnedMonster("Trinity", new PlayerWalk(2000, 2000, 1), 130, 8, 1000)
            { Variety = variety, Level = 64, Rarity = MonsterRarity.Unique, States = variety.StateMachine, LifePhases = new([66, 33]) };
        const uint id = 0x8000000;
        c.Monsters[id] = boss; c.NextObjectId = id + 1;
        c.MonsterViews[id] = new((2000, 2000), (2000, 2000), 1000);
        var fight = new DepravedTrinity.Fight(id, boss, c);
        await fight.Bind(c);
        Assert.Equal(14, DepravedTrinity.SkillsFor(c, variety, 64)!.Count);
        for (int wave = 0; wave < 2; wave++)
        {
            boss.Life = boss.LifePhases.Damage(boss.Life, boss.MaxLife, int.MaxValue);
            Assert.Equal(wave == 0 ? 660u : 330u, boss.Life);
            await fight.Step(c, 1 + wave * 10);
            Assert.True(boss.Invulnerable);
            var adds = c.Monsters.Values.Where(m => m.Variety?.Id == "Metadata/Monsters/SpiderPlated/SpiderPlatedUnholyEmerge" && !m.Dead && m.Life > 0).ToArray();
            Assert.Equal(6, adds.Length);
            if (wave == 1)
            {
                fight.Stop();
                fight = new(id, boss, c);
                await fight.Bind(c);
            }
            await fight.Step(c, 5 + wave * 10);
            Assert.True(boss.LifePhases.Active);
            Assert.Equal((2000, 2000), c.Walk.Position(0));
            foreach (var add in adds) add.Life = 0;
            await fight.Step(c, 6 + wave * 10);
            Assert.False(boss.LifePhases.Active);
            Assert.Equal(wave + 1, boss.LifePhases.Completed);
        }
        boss.Life = boss.LifePhases.Damage(boss.Life, boss.MaxLife, int.MaxValue);
        Assert.Equal(0u, boss.Life);
        await fight.Step(c, 20);
        await fight.Step(c, 23);
        Assert.Equal((2000, 2000), c.Walk.Position(0));
        await fight.Step(c, 25.5);
        Assert.Equal(layout.Near((3000, 1000)), c.Walk.Position(c.Clock.Elapsed.TotalSeconds));
        Assert.Equal(DepravedTrinity.Landing(c), DepravedTrinity.Destination(c, new AreaTransitionObject(10, 10, 1)
            { SourceType = "Metadata/QuestObjects/Act9/HarvestFinalBossTransition" }));
        Assert.Null(c.Instance.Ground.Get(loot));
        Assert.Equal(3, c.Instance.Ground.All().Count);
        var moved = c.Instance.Ground.All().Single(p => p.Id != personal && p.Id != outside);
        Assert.Same(item, moved.Item.Item);
        Assert.InRange(Math.Abs(moved.Item.GridX - 3000), 0, 50);
        Assert.InRange(Math.Abs(moved.Item.GridY - 1000), 0, 50);
        Assert.False(c.Instance.Ground.IsNewFor(c, moved.Id));
        Assert.Equal((1990, 1990), (c.Instance.Ground.Get(personal)!.GridX, c.Instance.Ground.Get(personal)!.GridY));
        Assert.Equal((c, false), c.Instance.Ground.Origin(personal));
        c.Monsters[7] = new("abandoned summon", new PlayerWalk(3000, 1000, 1), 80, 5, 100) { SummonerId = id };
        c.Monsters[8] = new("friendly", new PlayerWalk(3000, 1000, 1), 80, 5, 100) { OwnerId = Program.PlayerId, Hostile = false };
        Assert.Equal(new uint[] { 7 }, DepravedTrinity.RetireCreatures(c));
        Assert.True(c.Monsters.ContainsKey(8));
        var landing = c.Walk.Position(c.Clock.Elapsed.TotalSeconds);
        await fight.Step(c, 40);
        Assert.Equal(landing, c.Walk.Position(c.Clock.Elapsed.TotalSeconds));
        fight.Stop();
    }

    sealed class Runtime : IDisposable
    {
        readonly string saves = Path.Combine(Path.GetTempPath(), $"localexile-trinity-{Guid.NewGuid():N}.db");
        readonly IGameLogic? previousLogic = Program.GameLogicOrNull;
        readonly IModuleLifetime? previousLifetime = Game.CombatLifetime;
        readonly ModuleLifetime lifetime = new("Game");
        public ClientState? Player;
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
