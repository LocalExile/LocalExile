using GameServer.Core.Projectiles;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public sealed class ShieldOfLightTests
{
    [Fact]
    public void A_peer_keeps_its_tile_order_while_moving_and_joins_the_tail_on_reentry()
    {
        var grid = new AreaGrid();
        EntityId peer = new(0x20000002), minion = new(0x04000001);
        grid.Set(peer, (116, 114));
        grid.Set(minion, (118, 114));
        grid.Set(peer, (130, 114));
        Assert.True(grid.ClientTileScanOrder(peer, (130, 114)).CompareTo(grid.ClientTileScanOrder(minion, (118, 114))) < 0);

        // The x=115 client tile boundary is inside one 16-cell server bucket.
        grid.Set(peer, (114, 114));
        grid.Set(peer, (116, 114));
        Assert.True(grid.ClientTileScanOrder(minion, (118, 114)).CompareTo(grid.ClientTileScanOrder(peer, (116, 114))) < 0);
    }

    [Fact]
    public async Task The_first_enemy_contact_emits_four_independent_waves_from_eligible_allies()
    {
        var clock = new ViperStrikeMambaTests.AttackClock();
        using var fixture = new TriggeredSummonTests.Fixture(clock);
        var player = fixture.Player;
        player.Use(new(1, "shield-of-light-test", 400, 400, 100, 100, (_, _) => true, []) { Sight = (_, _) => 2 });
        player.Walk.Teleport(100, 100, clock.At);
        player.Connection.LockstepClient = false;
        player.Connection.PlayerObjectCreated = true;
        player.ProjectileRuntimeState = fixture.Session;
        Game.ItemsOf(player).Add(PlayerInventories.Offhand1, 0, 0,
            ItemCatalog.Create(Program.Items.Find("Splintered Tower Shield")!));
        ushort skill = Assert.Single(fixture.Equip(("Shield of Light", 1)));

        AddMonster(901, (108, 100));
        AddMonster(902, (108, 130));
        AddMonster(903, (108, 75));
        AddAlly(910, (116, 84));
        AddAlly(911, (115, 84));
        AddAlly(912, (103, 80));
        AddAlly(913, (107, 82));
        AddAlly(914, (116, 119));
        AddAlly(915, (109, 121));
        // Scanned before the four emitters, but lacks the semantic-minion stat and is out of reach of 902.
        AddAlly(909, (74, 81), semantic: false);
        var hits = new Dictionary<uint, int>();
        using var damage = GameEvents.OutgoingDamage.Add("Game", (owner, _, target, packet) =>
        {
            if (ReferenceEquals(owner, player))
                hits[target.ObjectId] = hits.GetValueOrDefault(target.ObjectId) + 1;
            return packet;
        });
        var previous = Game.AreaUpdateContext.Value;
        Game.AreaUpdateContext.Value = new(fixture.Lifetime, clock);
        try
        {
            Game.CharacterPlayerStats(player);
            player.IncomingHitRandom = new RetaliationTests.LowRolls();
            player.Pools = player.Pools with { SpellBlockChance = 100 };
            Assert.True(Game.TrySyntheticEnemyHit(player, DamageType.Physical, 1,
                block: true, spell: true, suppress: false, out var block));
            Assert.True(block.Blocked);
            fixture.Session.AdvanceTo(ProjectileRuntime.Time(clock.At));
            Assert.True(await Game.CastGemSkill(player, skill, (100, 100), (110, 100), sequence: 1));

            Step(.005);
            Assert.Equal(1, Assert.Single(hits).Value);
            Assert.Equal(1, hits[901]);
            Step(.5);
            Assert.Equal(5, hits[901]);
            Assert.Equal(4, hits[902]);
            Assert.False(hits.ContainsKey(903));
            Assert.Equal(2, hits.Count);
        }
        finally { Game.AreaUpdateContext.Value = previous; }

        void AddAlly(uint id, (int X, int Y) at, bool semantic = true)
        {
            AddMonster(id, at, hostile: false);
            var visual = new MonsterObject(at.X, at.Y, 10000, [], Stats:
                [(Stat.IsPlayerMinion, 1), (Stat.IsSemanticMinion, semantic ? 1 : 0)]);
            player.MinionOrigins[id] = new(0, visual, 0, 1, MonsterRarity.Normal);
        }

        void AddMonster(uint id, (int X, int Y) at, bool hostile = true)
        {
            var monster = new SpawnedMonster("Shield of Light fixture", new(at.X, at.Y, 0), 0, 0, 10000)
                { ObjectId = id, Hostile = hostile, NoRewards = true };
            player.Area.Monsters[id] = monster;
            player.Area.Grid.Set(new(id), at, LivingCollision.Size(monster));
        }

        void Step(double seconds)
        {
            clock.At += seconds;
            fixture.Session.AdvanceTo(ProjectileRuntime.Time(clock.At));
        }
    }
}
