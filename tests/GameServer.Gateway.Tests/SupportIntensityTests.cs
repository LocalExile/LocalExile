using GameServer.Core.Projectiles;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public sealed class SupportIntensityTests
{
    [Fact]
    public async Task Intensity_caps_while_casting_and_falls_with_movement_and_teleport()
    {
        using var fixture = new TriggeredSummonTests.Fixture();
        var player = fixture.Player;
        var clock = new CastClock();
        var previous = Game.AreaUpdateContext.Value;
        player.ProjectileRuntimeState = fixture.Session;
        player.Walk.Teleport(100, 100, clock.At);
        Game.AreaUpdateContext.Value = new(fixture.Lifetime, clock);
        try
        {
            ushort skillId = Assert.Single(fixture.Equip(("Ice Nova", 1), ("Intensify Support", 1)));
            var gem = Game.SupportedSkill(player, skillId);
            const uint area = Stat.SupportSpellBoostAreaOfEffectPlusPctFinal;
            int Modifier() => Game.SupportedSkill(player, skillId).Stats.GetValueOrDefault(area);
            int Radius() => Game.SkillRadius(player, Game.SupportedSkill(player, skillId).Stats, spell: true, gem.Effect);
            int baseline = Radius();
            Assert.Equal(0, Modifier());

            int perStack = gem.Stats[Stat.SupportSpellBoostAreaOfEffectPlusPctFinalPerCharge];
            Assert.Equal(-15, perStack);
            for (int cast = 1; cast <= 4; cast++)
            {
                Assert.True(await Game.CastGemSkill(player, skillId, (100, 100), (100, 100)));
                clock.At += 1;
                fixture.Session.AdvanceTo(ProjectileRuntime.Time(clock.At));
                Assert.Equal(perStack * Math.Min(3, cast), Modifier());
            }
            Assert.True(Radius() < baseline);

            player.Walk.SetSpeed(40, clock.At);
            player.Walk.Order(155, 100, clock.At);
            clock.At += .51;
            var here = player.Walk.Position(clock.At);
            player.Walk.Teleport(here.X, here.Y, clock.At); // stop the walk at its current cell
            Assert.Equal(2 * perStack, Modifier());
            clock.At += 1;
            Assert.Equal(2 * perStack, Modifier());
            player.Walk.Teleport(170, 100, clock.At);
            Assert.Equal(0, Modifier());
            Assert.Equal(baseline, Radius());
        }
        finally { Game.AreaUpdateContext.Value = previous; }
    }

    [Fact]
    public async Task Pinpoint_uses_zero_intensity_for_the_first_volley_and_gains_on_release()
    {
        using var fixture = new TriggeredSummonTests.Fixture();
        var player = fixture.Player;
        ushort skillId = Assert.Single(fixture.Equip(("Fireball", 1)));
        var control = Game.SupportedHitInputsFor(player, skillId, area: true, projectile: true)!;
        double baseline = Game.SupportedHitFrom(control, suppress: false).MaxTotal;
        Assert.True(baseline > 0);

        skillId = Assert.Single(fixture.Equip(("Fireball", 1), ("Pinpoint Support", 1)));
        var linked = Game.SupportedHitInputsFor(player, skillId, area: true, projectile: true)!;
        Assert.True(linked.Projectile);
        Assert.Equal(-19, linked.Get(Stat.SupportGreaterProjectileIntensityProjectileDamagePlusPctFinal));
        Assert.Equal(baseline * .81, Game.SupportedHitFrom(linked, suppress: false).MaxTotal, 8);
        Assert.Equal(Game.SupportedHitFrom(control with { Projectile = false }, suppress: false).MaxTotal,
            Game.SupportedHitFrom(linked with { Projectile = false }, suppress: false).MaxTotal, 8);

        var clock = new CastClock();
        var previous = Game.AreaUpdateContext.Value;
        Game.AreaUpdateContext.Value = new(fixture.Lifetime, clock);
        player.ProjectileRuntimeState = fixture.Session;
        player.Walk.Teleport(100, 100, clock.At);
        try
        {
            int Extra() => Game.SupportedSkill(player, skillId).Stats.GetValueOrDefault(Stat.NumberOfAdditionalProjectiles);
            Assert.Equal(3, Extra());
            Assert.Equal(4, await Volley(1));
            Assert.Equal(2, Extra());
            Assert.Equal(3, await Volley(2));
            Assert.Equal(1, Extra());

            async Task<int> Volley(ushort sequence)
            {
                Assert.True(await Game.CastGemSkill(player, skillId, (100, 100), (190, 100), sequence: sequence));
                for (int step = 0; step < 200; ++step)
                {
                    clock.At += .01;
                    fixture.Session.AdvanceTo(ProjectileRuntime.Time(clock.At));
                    int count = fixture.Session.Runtime.Projectiles.Count(p => p.Use.Sequence == sequence);
                    if (count > 0) return count;
                }
                return 0;
            }
        }
        finally { Game.AreaUpdateContext.Value = previous; }
    }

    sealed class CastClock : IPlayerUpdateSchedule
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
