namespace GameServer.Gateway.Tests;

public class InflictedAilmentTests
{
    const MonsterElementalAilments.Kind Chill = MonsterElementalAilments.Kind.Chill,
        Freeze = MonsterElementalAilments.Kind.Freeze, Shock = MonsterElementalAilments.Kind.Shock;

    static Func<string, int> With(params (string Stat, int Value)[] stats) =>
        name => stats.Where(s => s.Stat == name).Sum(s => s.Value);

    // One hit against a threshold of 100; other is its physical, fire and chaos damage.
    static MonsterElementalAilments.Effect? Hit(MonsterElementalAilments.Kind kind, double cold, double lightning,
        Func<string, int> attacker, Func<string, int>? target = null, bool critical = true, double other = 0) =>
        new MonsterElementalAilments().Inflict(cold, lightning, critical, threshold: 100, now: 0, attacker,
            target ?? With(), new Random(1), other: other).SingleOrDefault(e => e.Kind == kind);

    static double Magnitude(MonsterElementalAilments.Kind kind, double cold, double lightning, Func<string, int> attacker,
        Func<string, int>? target = null, bool critical = true, double other = 0) =>
        Hit(kind, cold, lightning, attacker, target, critical, other)?.Magnitude ?? 0;

    // "Shocks/Chills from your Hits always ... by at least X%" raise the base before the effect modifiers (wiki Shock), so
    // a hit too weak for the 5% minimum still applies one, but only with the ailment's damage. "All Damage with Hits can
    // Shock" and the chill and freeze forms make every type count, "Hits always Shock" needs no chance, and the caps can be
    // raised.
    [Fact]
    public void Minimums_caps_all_damage_and_always_shock_set_what_a_hit_inflicts()
    {
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

        // Every type counts once with "All Damage can Shock": 3% of the threshold shocks for floor(50 × 0.03^0.4), and
        // Stormy Seas' cold does not count twice.
        var allShock = With(("all_damage_can_shock", 1));
        Assert.Equal(0, Magnitude(Shock, 0, 0, With(), other: 1));
        Assert.Equal(7, Magnitude(Shock, 0, 0, allShock, other: 1));
        Assert.Equal(12, Magnitude(Shock, 1, 1, allShock, other: 1));
        Assert.Equal(7, Magnitude(Shock, 1, 0, With(("all_damage_can_shock", 1), ("cold_damage_can_shock", 1))));
        Assert.Equal(7, Magnitude(Chill, 0, 0, With(("all_damage_can_chill", 1)), other: 1));
        Assert.Equal(0, Magnitude(Freeze, 0, 0, With(("all_damage_can_chill", 1)), other: 10));
        Assert.Equal(.6, Hit(Freeze, 0, 0, With(("all_damage_can_freeze", 1)), other: 10)!.Until, 9);   // 0.06 s per 1%

        // A hit that is not critical shocks only by chance, or always with "Hits always Shock".
        Assert.Equal(0, Magnitude(Shock, 0, 1, With(), critical: false));
        Assert.Equal(7, Magnitude(Shock, 0, 1, With(("always_shock", 1)), critical: false));

        // The caps: 30% chill, 40% with Shaper of Winter; 50% shock, 90% with Voltaxic Rift's +40%, whose stat is stored
        // as 100.
        Assert.Equal(30, Magnitude(Chill, 100, 0, With()));
        Assert.Equal(40, Magnitude(Chill, 100, 0, With(("elementalist_chill_maximum_magnitude_override", 40))));
        Assert.Equal(50, Magnitude(Shock, 0, 300, With()));   // floor(50 × 3^0.4) is 77
        Assert.Equal(77, Magnitude(Shock, 0, 300,
            With(("unique_voltaxic_rift_shock_maximum_magnitude_override", 100))));
    }
}
