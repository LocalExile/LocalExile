namespace GameServer.Gateway.Tests;

public class InflictedAilmentTests
{
    static Func<string, int> With(params (string Stat, int Value)[] stats) =>
        name => stats.Where(s => s.Stat == name).Sum(s => s.Value);

    static double Magnitude(MonsterElementalAilments.Kind kind, double cold, double lightning, Func<string, int> attacker,
        Func<string, int>? target = null) =>
        new MonsterElementalAilments().Inflict(cold, lightning, critical: true, threshold: 100, now: 0, attacker,
            target ?? With(), new Random(1)).SingleOrDefault(e => e.Kind == kind)?.Magnitude ?? 0;

    // "Shocks/Chills from your Hits always ... by at least X%" raise the base before the effect modifiers (wiki Shock), so
    // a hit too weak for the 5% minimum still applies one, but only with the ailment's damage.
    [Fact]
    public void Shock_and_chill_minimums_raise_the_base_before_the_effect_modifiers()
    {
        const MonsterElementalAilments.Kind Shock = MonsterElementalAilments.Kind.Shock, Chill = MonsterElementalAilments.Kind.Chill;
        var shockAtLeast15 = With(("shock_minimum_damage_taken_increase_%", 15));
        Assert.Equal(7, Magnitude(Shock, 0, 1, With()));   // floor(50 × 0.01^0.4)
        Assert.Equal(15, Magnitude(Shock, 0, 1, shockAtLeast15));
        Assert.Equal(19, Magnitude(Shock, 0, 10, shockAtLeast15));   // a stronger shock keeps its own base
        Assert.Equal(22, Magnitude(Shock, 0, 1, With(("shock_minimum_damage_taken_increase_%", 15), ("virtual_shock_effect_+%", 50))));
        Assert.Equal(7, Magnitude(Shock, 0, 1, shockAtLeast15, With(("shocked_effect_on_self_+%", -50))));
        Assert.Equal(0, Magnitude(Shock, 0, .01, With()));   // under 5%, discarded
        Assert.Equal(5, Magnitude(Shock, 0, .01, With(("shock_minimum_damage_taken_increase_%", 5))));
        Assert.Equal(0, Magnitude(Shock, 1, 0, shockAtLeast15));   // no lightning, no shock

        var chillAtLeast10 = With(("chill_minimum_slow_%", 10));
        Assert.Equal(7, Magnitude(Chill, 1, 0, With()));
        Assert.Equal(10, Magnitude(Chill, 1, 0, chillAtLeast10));
        Assert.Equal(0, Magnitude(Chill, 0, 1, chillAtLeast10));
    }
}
