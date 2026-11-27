using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public sealed class CrushingFistTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task The_slam_hits_around_the_projected_fist_position_in_the_second_animation(bool blockedAim)
    {
        var clock = new ViperStrikeMambaTests.AttackClock();
        using var fixture = new TriggeredSummonTests.Fixture(clock);
        var player = fixture.Player;
        player.Use(new(1, "crushing-fist-test", 400, 400, 100, 100,
            (x, _) => !blockedAim || x != 104, [])
        {
            Sight = (x, y) => x == 119 || blockedAim && (x, y) == (109, 100) ? 0 : 2,
        });
        player.Walk.Teleport(100, 100, clock.At);
        player.Connection.PlayerObjectCreated = true;
        player.ProjectileRuntimeState = fixture.Session;

        var inventory = Game.ItemsOf(player);
        var main = Assert.Single(inventory.Items(PlayerInventories.Weapon1));
        inventory.Change(PlayerInventories.Weapon1, main.Id,
            _ => ItemCatalog.Create(Program.Items.Find("Rusted Hatchet")!));
        inventory.Add(PlayerInventories.Offhand1, 0, 0,
            ItemCatalog.Create(Program.Items.Find("Splintered Tower Shield")!));
        ushort skill = Assert.Single(fixture.Equip(("Crushing Fist", 1)));

        AddTarget(901, (112, 100));
        AddTarget(902, (110, 121));
        AddTarget(903, (83, 100));
        AddTarget(904, (121, 100));
        AddTarget(905, (159, 100));
        AddTarget(906, (108, 122));
        var hits = new List<(uint Target, double At)>();
        using var damage = GameEvents.OutgoingDamage.Add("Game", (owner, _, target, packet) =>
        {
            if (ReferenceEquals(owner, player)) hits.Add((target.ObjectId, clock.At));
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

            double start = clock.At;
            double attackTime = Game.SupportedAttackSeconds(player, skill, AttackHand.Main);
            Assert.True(await Game.CastGemSkill(player, skill, (100, 100), (165, 100), sequence: 2));

            uint[] expected = blockedAim ? [902, 906] : [901, 902];
            Assert.Equal(expected, hits.Select(hit => hit.Target).Order());
            Assert.All(hits, hit => Assert.InRange((hit.At - start) / attackTime, .75, .95));
        }
        finally { Game.AreaUpdateContext.Value = previous; }

        void AddTarget(uint id, (int X, int Y) at)
        {
            var target = new SpawnedMonster("Crushing Fist target", new(at.X, at.Y, 0), 0, 0, 10000)
                { ObjectId = id, Hostile = true, NoRewards = true };
            player.Area.Monsters[id] = target;
            player.Area.Grid.Set(new(id), at, LivingCollision.Size(target));
        }
    }
}
