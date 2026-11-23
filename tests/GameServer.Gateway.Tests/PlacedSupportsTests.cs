using GameServer.Core.Projectiles;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public sealed class PlacedSupportsTests
{
    [Fact]
    public async Task Locus_keeps_close_mines_armed_while_the_rest_of_the_sequence_fires()
    {
        using var fixture = new TriggeredSummonTests.Fixture();
        var player = fixture.Player;
        var clock = new PlacedClock();
        var previous = Game.AreaUpdateContext.Value;
        player.ProjectileRuntimeState = fixture.Session;
        player.Walk.Teleport(100, 100, clock.At);
        Game.AreaUpdateContext.Value = new(fixture.Lifetime, clock);
        try
        {
            const double Until = 20;
            PlacedSkills.Placed Mine(int distance, bool locus)
            {
                uint id = (uint)(700 + distance);
                var own = new Dictionary<uint, int>();
                if (locus) own[Stat.SupportLocusMineCannotDetonateMinesWithinXUnits] = 30;
                var mine = new PlacedSkills.Placed(id, 0, Program.Skills.EffectRow("IceNova"), (100 + distance, 100),
                    true, clock.At, Until, DamagePacket.Of(DamageType.Cold, 100, 100), own, 0, 0, false, 0);
                player.Placed.All.Add(id, mine);
                return mine;
            }

            var near = Mine(29, true);
            Assert.False(Game.CanDetonateMines(player));
            var atBoundary = Mine(30, true);
            var ordinary = Mine(1, false);
            Assert.True(Game.CanDetonateMines(player));
            Game.DetonateMinesLocked(player, clock.At);
            Assert.Equal(double.PositiveInfinity, near.DetonateAt);
            Assert.Equal(2, player.Placed.Chain);
            clock.At += .75;
            await Game.PlayerMinionsTick(player, clock.At);
            Assert.Same(near, Assert.Single(player.Placed.All.Values));
            Assert.DoesNotContain(atBoundary.Id, player.Placed.All.Keys);
            Assert.DoesNotContain(ordinary.Id, player.Placed.All.Keys);

            player.Walk.Teleport(90, 100, clock.At);
            Assert.True(Game.CanDetonateMines(player));
            Game.DetonateMinesLocked(player, clock.At);
            await Game.PlayerMinionsTick(player, clock.At);
            Assert.Empty(player.Placed.All);
        }
        finally { Game.AreaUpdateContext.Value = previous; }
    }

    sealed class PlacedClock : IPlayerUpdateSchedule
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
