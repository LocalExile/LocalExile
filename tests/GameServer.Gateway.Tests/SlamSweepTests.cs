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

    [Fact]
    public void Slam_wave_and_melee_area_scale_with_aoe_and_fist_of_war()
    {
        // Level 1 (gemBasePlus = 0, radius = 12): 60; +350% AoE -> radius 25 -> >120 cells; Fist of War -> proportional.
        Assert.Equal(60, GroundSlamWave.Length(12), 6);
        Assert.Equal(70, GroundSlamWave.Length(22, gemBasePlus: 10), 6);
        Assert.True(GroundSlamWave.Length(46, gemBasePlus: 10) > 140);
        Assert.True(GroundSlamWave.Length(27, gemBasePlus: 10) > 85);

        // Pulverise more area multiplies SkillArea.Radius.
        int baseRadius = SkillArea.Radius(18, _ => 0, spell: false);
        int withPulverise = SkillArea.Radius(18, k => k == Stat.SupportPulveriseAreaOfEffectPlusPctFinal ? 54 : 0, spell: false);
        Assert.True(withPulverise > baseRadius);

        // Area check helper covers all Slam / Sweep melee gems.
        Assert.True(Program.Skills.IsArea(Program.Skills.EffectRow("GroundSlam")));
        Assert.True(Program.Skills.IsArea(Program.Skills.EffectRow("Sunder")));
        Assert.True(Program.Skills.IsArea(Program.Skills.EffectRow("Earthquake")));
    }
}
