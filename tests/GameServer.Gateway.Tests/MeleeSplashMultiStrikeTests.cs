using GameServer.Core.Projectiles;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public sealed class MeleeSplashMultiStrikeTests
{
    [Theory]
    [InlineData("Heavy Strike")]
    [InlineData("Molten Strike")]
    public async Task An_additional_strike_splashes_a_monster_outside_the_first_targets_radius(string attack)
    {
        var clock = new AttackClock();
        using var f = new TriggeredSummonTests.Fixture(clock);
        var player = f.Player;
        var previous = Game.AreaUpdateContext.Value;
        player.ProjectileRuntimeState = f.Session;
        player.Walk.Teleport(100, 100, clock.At);
        Game.AreaUpdateContext.Value = new(f.Lifetime, clock);
        using var stats = GameEvents.PlayerStatSources.Add("Game", (owner, values) =>
        {
            if (ReferenceEquals(owner, player))
            {
                values[Stat.GlobalAlwaysHit] = 1;
                values[Stat.GlobalCannotCrit] = 1;
                values[Stat.MeleeAttackNumberOfSpiritStrikes] = 1;
            }
            return values;
        });

        try
        {
            ushort skill = Assert.Single(f.Equip((attack, 1), ("Melee Splash Support", 1)));
            Assert.NotEqual(0, Game.SupportedSkill(player, skill).Stats
                .GetValueOrDefault(Stat.SupportMeleeSplashDamagePlusPctFinalForSplash));
            SpawnedMonster Monster(uint id, int x)
            {
                var monster = new SpawnedMonster("splash target", new(x, 100, 0), 0, 0, 1_000_000);
                player.Area.Monsters.Add(id, monster);
                player.Area.Grid.Set(new(id), (x, 100));
                return monster;
            }
            var first = Monster(901, 109);
            var second = Monster(902, 125);
            var splashOnly = Monster(903, 131);

            Assert.True(await Game.CastGemSkill(player, skill, (100, 100), (109, 100), 901));
            while (first.Life == first.MaxLife && clock.At < 2)
            {
                clock.At += .025;
                f.Session.AdvanceTo(ProjectileRuntime.Time(clock.At));
            }
            Assert.True(first.Life < first.MaxLife, "Primary strike did not hit.");
            Assert.True(second.Life < second.MaxLife, "Spirit strike did not hit.");
            Assert.True(splashOnly.Life < splashOnly.MaxLife, "Second strike did not splash its nearby monster.");
        }
        finally
        {
            Game.AreaUpdateContext.Value = previous;
        }
    }

    sealed class AttackClock : IPlayerUpdateSchedule
    {
        public double At = 1;
        public double Now(Player player) => At;
        public Task Wait(int milliseconds, CancellationToken stopping)
        {
            stopping.ThrowIfCancellationRequested();
            At += milliseconds / 1000d;
            return Task.CompletedTask;
        }
    }
}
