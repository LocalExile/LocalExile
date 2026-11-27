using GameServer.Core.Projectiles;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public sealed class EviscerateTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Eviscerate_requires_a_shield_and_uses_the_main_hand_axe_to_deal_damage(bool withShield)
    {
        using var fixture = new TriggeredSummonTests.Fixture();
        var player = fixture.Player;
        var inventory = Game.ItemsOf(player);
        var main = Assert.Single(inventory.Items(PlayerInventories.Weapon1));
        var axe = ItemCatalog.Create(Program.Items.Find("Rusted Hatchet")!);
        inventory.Change(PlayerInventories.Weapon1, main.Id, _ => axe);
        ushort skill = Assert.Single(fixture.Equip(("Eviscerate", 1)));
        int effect = Game.SupportedSkill(player, skill).Effect;

        if (withShield)
        {
            inventory.Add(PlayerInventories.Offhand1, 0, 0,
                ItemCatalog.Create(Program.Items.Find("Splintered Tower Shield")!));
            Game.RecomputePoolsAndSpeed(player);
            Assert.Null(Game.WeaponRestricted(player, effect));
            Assert.Null(Game.ShieldWeapon(player, skill));
            Assert.True(Game.HandWeapon(player, AttackHand.Main).MinDamage > 0);
        }
        else
            Assert.NotNull(Game.WeaponRestricted(player, effect));
    }

    [Fact]
    public async Task Both_waves_reach_near_enemies_once_each_and_spread_apart_with_distance()
    {
        var clock = new ViperStrikeMambaTests.AttackClock();
        using var fixture = new TriggeredSummonTests.Fixture(clock);
        var player = fixture.Player;
        player.Use(new(1, "eviscerate-test", 400, 400, 100, 100, (_, _) => true, []) { Sight = (_, _) => 2 });
        player.Walk.Teleport(100, 100, clock.At);
        player.Connection.LockstepClient = false;
        player.Connection.PlayerObjectCreated = true;
        player.ProjectileRuntimeState = fixture.Session;

        var inventory = Game.ItemsOf(player);
        var main = Assert.Single(inventory.Items(PlayerInventories.Weapon1));
        inventory.Change(PlayerInventories.Weapon1, main.Id, _ => ItemCatalog.Create(Program.Items.Find("Rusted Hatchet")!));
        inventory.Add(PlayerInventories.Offhand1, 0, 0, ItemCatalog.Create(Program.Items.Find("Splintered Tower Shield")!));
        ushort skill = Assert.Single(fixture.Equip(("Eviscerate", 1)));

        AddTarget(901, (112, 100));
        AddTarget(902, (162, 65));
        AddTarget(903, (162, 135));
        AddTarget(904, (160, 100));
        AddTarget(905, (82, 100));
        AddTarget(906, (201, 158));
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
            Assert.Empty(hits);

            Step(.08);
            Assert.Equal(2, Assert.Single(hits).Value);
            Assert.Equal(2, hits[901]);
            Step(.42);
            Assert.Equal(3, hits.Count);
            Assert.Equal(2, hits[901]);
            Assert.Equal(1, hits[902]);
            Assert.Equal(1, hits[903]);
        }
        finally { Game.AreaUpdateContext.Value = previous; }

        void AddTarget(uint id, (int X, int Y) at)
        {
            var target = new SpawnedMonster("Eviscerate target", new(at.X, at.Y, 0), 0, 0, 10000)
                { ObjectId = id, Hostile = true, NoRewards = true };
            player.Area.Monsters[id] = target;
            player.Area.Grid.Set(new(id), at, LivingCollision.Size(target));
        }

        void Step(double seconds)
        {
            clock.At += seconds;
            fixture.Session.AdvanceTo(ProjectileRuntime.Time(clock.At));
        }
    }
}
