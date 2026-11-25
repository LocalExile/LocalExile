using GameServer.Core.Projectiles;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public sealed class ProjectileStrikeExtrasTests
{
    [Theory]
    [InlineData("Frost Blades")]
    [InlineData("Lightning Strike")]
    public async Task Ancestral_call_hits_the_nearby_target_at_the_melee_contact(string name)
    {
        var clock = new ContactClock();
        using var fixture = new TriggeredSummonTests.Fixture(clock);
        var player = fixture.Player;
        player.ProjectileRuntimeState = fixture.Session;
        player.Walk.Teleport(100, 100, clock.At);
        var previous = Game.AreaUpdateContext.Value;
        Game.AreaUpdateContext.Value = new(fixture.Lifetime, clock);
        using var accuracy = GameEvents.PlayerStatSources.Add("Game", (owner, stats) =>
        {
            if (!ReferenceEquals(owner, player)) return stats;
            stats[Stat.GlobalAlwaysHit] = 1;
            stats[Stat.MeleeStrikeSkillStrikePreviousLocation] = 1;
            return stats;
        });

        try
        {
            ushort skill = Assert.Single(fixture.Equip((name, 1), ("Ancestral Call Support", 1)));
            Assert.Equal(2, Game.SupportedSkill(player, skill).Stats.GetValueOrDefault(Stat.MeleeAttackNumberOfSpiritStrikes));

            SpawnedMonster Target(uint id, int x, int y)
            {
                var target = new SpawnedMonster("Strike contact target", new(x, y, 0), 0, 0, 1_000_000);
                player.Area.Monsters.Add(id, target);
                player.Area.Grid.Set(new(id), (x, y));
                return target;
            }

            var primary = Target(901, 109, 100);
            var extra = Target(902, 100, 123);
            var beyondStrikeRange = Target(903, 100, 144);
            Assert.True(await Game.CastGemSkill(player, skill, (100, 100), (109, 100), 901));
            while (primary.Life == primary.MaxLife && clock.At < 3)
            {
                clock.At += .01;
                fixture.Session.AdvanceTo(ProjectileRuntime.Time(clock.At));
            }
            Assert.True(primary.Life < primary.MaxLife, "The primary melee contact did not land.");
            Assert.True(extra.Life < extra.MaxLife, "The simultaneous additional melee contact did not land.");
            Assert.Equal(beyondStrikeRange.MaxLife, beyondStrikeRange.Life);

            var volleys = fixture.Session.Runtime.Projectiles
                .Where(p => p.ParentId == 0).GroupBy(p => p.UseId).ToArray();
            Assert.Equal(2, volleys.Length);
            var firstVolley = volleys.Single(group => ((ProjectileOffense)group.First().Use.Payload!).ExcludedTarget?.Id == 901);
            var extraVolley = volleys.Single(group => ((ProjectileOffense)group.First().Use.Payload!).ExcludedTarget?.Id == 902);
            Assert.Equal(firstVolley.First().CreatedAt, extraVolley.First().CreatedAt);
            bool ice = name == "Frost Blades";
            Assert.Equal(ice ? 5 : 3, extraVolley.Count());
            Assert.Equal(new ProjectileVector(100, ice ? 123 : 118), extraVolley.First().Use.Origin);
            Assert.Equal(ice ? 109 : 103, firstVolley.First().Use.Origin.X);

            uint extraLife = extra.Life;
            while (beyondStrikeRange.Life == beyondStrikeRange.MaxLife && clock.At < 3)
            {
                clock.At += .01;
                fixture.Session.AdvanceTo(ProjectileRuntime.Time(clock.At));
            }
            Assert.True(beyondStrikeRange.Life < beyondStrikeRange.MaxLife, "The extra volley did not reach its own direction.");
            Assert.Equal(extraLife, extra.Life);

            clock.At += 3;
            fixture.Session.AdvanceTo(ProjectileRuntime.Time(clock.At));
            player.Walk.Teleport(150, 100, clock.At);
            var movedPrimary = Target(904, 159, 100);
            uint previousLife = primary.Life;
            Assert.True(await Game.CastGemSkill(player, skill, (150, 100), (159, 100), 904));
            double deadline = clock.At + 2;
            while (movedPrimary.Life == movedPrimary.MaxLife && clock.At < deadline)
            {
                clock.At += .01;
                fixture.Session.AdvanceTo(ProjectileRuntime.Time(clock.At));
            }
            Assert.True(movedPrimary.Life < movedPrimary.MaxLife);
            Assert.True(primary.Life < previousLife, "The extra strike at the previous location did not land.");
        }
        finally
        {
            Game.AreaUpdateContext.Value = previous;
        }
    }

    sealed class ContactClock : IPlayerUpdateSchedule
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
