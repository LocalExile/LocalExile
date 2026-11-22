namespace GameServer.Gateway.Tests;

public sealed class SlamSweepTests
{
    [Fact]
    public void Slams_use_distance_along_their_ground_sweep()
    {
        Assert.Equal(1.4, GroundSlamWave.CloseHitMultiplier(0, 60, 40), 10);
        Assert.Equal(1.2, GroundSlamWave.CloseHitMultiplier(30, 60, 40), 10);
        Assert.Equal(1, GroundSlamWave.CloseHitMultiplier(60, 60, 40), 10);

        var layout = new AreaLayout(1, "test", 100, 100, 10, 10, (x, _) => x < 30, []);
        var end = VolcanicFissureSweep.End(layout, (10, 10), (70, 10), 12);
        Assert.Equal((29, 10), end);
        Assert.Equal(9, VolcanicFissureSweep.ContactDistance((10, 10), end, (19, 16), 8));
        Assert.Equal(double.PositiveInfinity, VolcanicFissureSweep.ContactDistance((10, 10), end, (19, 19), 8));

        var fissure = Game.ResolveProjectileDefinition(Program.Skills.EffectRow("VolcanicFissure"));
        Assert.NotNull(fissure);
        var profile = fissure.Parameters.Resolve(key => key == Stat.BaseNumberOfProjectiles ? 5 : 0, 60, false);
        Assert.Equal(5, profile.Count);
        Assert.True(profile.Lobbed);
        Assert.False(profile.DirectDamage);
        Assert.NotNull(profile.EndArea);
    }
}
