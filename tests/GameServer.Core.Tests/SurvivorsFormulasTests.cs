using GameServer.Core.World;

namespace GameServer.Core.Tests;

/// <summary>
/// The Path of Building formulas a survivors run uses (CalcOffence calcDamage, CalcTools calcRadius and the cast
/// rate), copied as plain arithmetic so a change to them is visible here before it is felt in a run.
/// </summary>
public class SurvivorsFormulasTests
{
    [Fact]
    public void A_gems_cast_time_and_cast_speed_set_the_interval()
    {
        Assert.Equal(0.75, SurvivorsFormulas.CastInterval(0.75, 1, 0), 6);
        Assert.Equal(0.6975, SurvivorsFormulas.CastInterval(0.75, 2, 0), 6);
        // 20% increased cast speed is a 1.2 divisor, never a 1.2 multiplier on the rate.
        Assert.Equal(0.625, SurvivorsFormulas.CastInterval(0.75, 1, 20), 6);
        Assert.Equal(0.2, SurvivorsFormulas.CastInterval(0.75, 100, 0), 6);
    }

    [Fact]
    public void Critical_chance_is_base_plus_added_times_increased_and_capped()
    {
        // Fireball's 5% and Increased Critical Strikes' +2% base, with 60% increased.
        Assert.Equal(1120, SurvivorsFormulas.CriticalChance(500, 200, 60));
        // Ice Spear's second form: +600% increased on its 7.5% base.
        Assert.Equal(5250, SurvivorsFormulas.CriticalChance(750, 0, 600));
        Assert.Equal(10_000, SurvivorsFormulas.CriticalChance(10_000, 0, 200));
        Assert.Equal(0, SurvivorsFormulas.CriticalChance(0, 0, 500));
    }

    [Fact]
    public void Critical_multiplier_starts_at_150_percent()
    {
        Assert.Equal(150, SurvivorsFormulas.CriticalMultiplier(0));
        Assert.Equal(180, SurvivorsFormulas.CriticalMultiplier(30));
        Assert.Equal(100, SurvivorsFormulas.CriticalMultiplier(-100));
    }

    [Fact]
    public void Area_grows_with_the_square_root_of_increased_area()
    {
        Assert.Equal(10, SurvivorsFormulas.AreaRadius(10, 0), 6);
        Assert.Equal(11.401754, SurvivorsFormulas.AreaRadius(10, 30), 5);
        // 50% less area is sqrt(0.5), not half the radius.
        Assert.Equal(7.071068, SurvivorsFormulas.AreaRadius(10, -50), 5);
    }

    [Fact]
    public void Added_damage_is_scaled_by_the_skill_effectiveness()
    {
        // Base 10-20, added 4-6 at 200% effectiveness, then 25% increased: (15 + 10) × 1.25.
        Assert.Equal(31.25, SurvivorsFormulas.HitDamage(10, 20, 2, 4, 6, 25), 6);
        Assert.Equal(46.875, SurvivorsFormulas.HitDamage(10, 20, 2, 4, 6, 25, 1.5), 6);
        // Effectiveness does not scale the gem's own base, only the added range.
        Assert.Equal(15, SurvivorsFormulas.HitDamage(10, 20, 0, 0, 0, 0), 6);
    }
}
