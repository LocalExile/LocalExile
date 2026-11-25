using GameServer.Core.Projectiles;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public sealed class ElementalHitTests
{
    [Fact]
    public void Elemental_Hit_damage_inputs_include_the_gem_ranges_and_prohibit_final_physical_damage()
    {
        using var fixture = new TriggeredSummonTests.Fixture(new Clock());
        var player = fixture.Player;
        ushort skill = Assert.Single(fixture.Equip(("Elemental Hit", 1)));
        var inputs = Assert.IsType<Game.SupportedHitInputs>(Game.SupportedHitInputsFor(player, skill));
        Assert.Equal(new DamageRange(22, 41), inputs.Raw[DamageType.Fire]);
        Assert.Equal(new DamageRange(18, 33), inputs.Raw[DamageType.Cold]);
        Assert.Equal(new DamageRange(3, 67), inputs.Raw[DamageType.Lightning]);
        Assert.True(inputs.Raw[DamageType.Physical].Max > 0);
        Assert.Equal(0, Game.SupportedHitFrom(inputs, suppress: true)[DamageType.Physical].Max);
    }

    [Fact]
    public async Task Elemental_Hit_uses_only_the_selected_element_and_expands_after_a_successful_hit()
    {
        await CheckContact(chilled: false, protectedTarget: false);
        await CheckContact(chilled: true, protectedTarget: false);
        await CheckContact(chilled: false, protectedTarget: true);
    }

    static async Task CheckContact(bool chilled, bool protectedTarget)
    {
        var clock = new Clock();
        using var fixture = new TriggeredSummonTests.Fixture(clock);
        var player = fixture.Player;
        player.Use(new(1, "elemental-hit-test", 400, 400, 100, 100, (_, _) => true, []) { Sight = (_, _) => 2 });
        player.Walk.Teleport(100, 100, clock.At);
        player.Connection.LockstepClient = false;
        player.Connection.PlayerSent = player.Connection.PlayerObjectCreated = true;
        player.ProjectileRuntimeState = fixture.Session;
        fixture.Session.AdvanceTo(ProjectileRuntime.Time(clock.At));
        using var stats = GameEvents.PlayerStatSources.Add("Game", (owner, values) =>
        {
            if (!ReferenceEquals(owner, player)) return values;
            values[Stat.GlobalAlwaysHit] = 1;
            values[Stat.GlobalCannotCrit] = 1;
            values[Stat.ElementalHitCannotRollFireDamage] = chilled ? 1 : 0;
            values[Stat.ElementalHitCannotRollColdDamage] = chilled ? 0 : 1;
            values[Stat.ElementalHitCannotRollLightningDamage] = 1;
            values[Stat.AttackMinimumAddedPhysicalDamage] = values[Stat.AttackMaximumAddedPhysicalDamage] = 1600;
            values[Stat.AttackMinimumAddedLightningDamage] = values[Stat.AttackMaximumAddedLightningDamage] = 1200;
            if (chilled) values[Stat.AttackMinimumAddedFireDamage] = values[Stat.AttackMaximumAddedFireDamage] = 900;
            else values[Stat.AttackMinimumAddedColdDamage] = values[Stat.AttackMaximumAddedColdDamage] = 900;
            return values;
        });

        var previous = Game.AreaUpdateContext.Value;
        Game.AreaUpdateContext.Value = new(fixture.Lifetime, clock);
        try
        {
            ushort skill = Assert.Single(fixture.Equip(("Elemental Hit", 1)));
            var primary = Target(901, 108, 100);
            if (protectedTarget)
                primary.EncounterStats = new Dictionary<uint, int> { [Stat.BaseCannotBeDamaged] = 1 };
            if (chilled)
                Assert.NotNull(primary.Ailments.WithoutDamage(MonsterElementalAilments.Kind.Chill,
                    4, clock.At, _ => 0, _ => 0, stated: 20));
            var close = Target(902, 108, chilled ? 120 : 109);
            var outside = Target(903, 108, 132);
            uint mana = player.PlayerMana;
            Assert.True(await Game.CastGemSkill(player, skill, (100, 100), (108, 100), 901));
            clock.At += 1;
            fixture.Session.AdvanceTo(ProjectileRuntime.Time(clock.At));
            Assert.Equal(mana - 5, player.PlayerMana);
            if (protectedTarget)
            {
                Assert.Equal(primary.MaxLife, primary.Life);
                Assert.Equal(close.MaxLife, close.Life);
            }
            else
            {
                Assert.InRange(primary.MaxLife - primary.Life, 1u, 100u);
                Assert.InRange(close.MaxLife - close.Life, 1u, 100u);
            }
            Assert.Equal(outside.MaxLife, outside.Life);

            SpawnedMonster Target(uint id, int x, int y)
            {
                var monster = new SpawnedMonster("elemental hit witness", new(x, y, 0), 0, 0, 100_000)
                    { ObjectId = id, Hostile = true, NoRewards = true };
                player.Area.Monsters[id] = monster;
                player.Area.Grid.Set(new(id), (x, y));
                return monster;
            }
        }
        finally { Game.AreaUpdateContext.Value = previous; }
    }

    sealed class Clock : IPlayerUpdateSchedule
    {
        public double At = 1;
        public double Now(Player player) => At;
        public async Task Wait(int milliseconds, CancellationToken stopping)
        {
            await Task.Yield();
            stopping.ThrowIfCancellationRequested();
            At += milliseconds / 1000d;
        }
    }
}
