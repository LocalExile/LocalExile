using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public class InflictedAilmentTests
{
    const MonsterElementalAilments.Kind Chill = MonsterElementalAilments.Kind.Chill,
        Freeze = MonsterElementalAilments.Kind.Freeze, Shock = MonsterElementalAilments.Kind.Shock;

    static Func<string, int> With(params (string Stat, int Value)[] stats) =>
        name => stats.Where(s => s.Stat == name).Sum(s => s.Value);

    // The damage a monster with a threshold of 100 takes, by type.
    static DamagePacket Taken(double cold = 0, double lightning = 0, double physical = 0, double fire = 0, double chaos = 0) =>
        new[] { (DamageType.Cold, cold), (DamageType.Lightning, lightning), (DamageType.Physical, physical),
            (DamageType.Fire, fire), (DamageType.Chaos, chaos) }
            .Aggregate(DamagePacket.Empty, (hit, part) => hit.Add(part.Item1, new DamageRange(part.Item2, part.Item2)));

    static MonsterElementalAilments.Effect? Hit(MonsterElementalAilments.Kind kind, DamagePacket damage,
        Func<string, int> attacker, Func<string, int>? target = null, bool critical = true) =>
        new MonsterElementalAilments().Inflict(damage, critical, threshold: 100, now: 0, attacker, target ?? With(),
            new Random(1)).SingleOrDefault(e => e.Kind == kind);

    static double Magnitude(MonsterElementalAilments.Kind kind, DamagePacket damage, Func<string, int> attacker,
        Func<string, int>? target = null, bool critical = true) =>
        Hit(kind, damage, attacker, target, critical)?.Magnitude ?? 0;

    // "Shocks/Chills from your Hits always ... by at least X%" raise the base before the effect modifiers (wiki Shock), so
    // a hit too weak for the 5% minimum still applies one, but only with the ailment's damage. "All Damage with Hits can
    // Shock", the one-type forms and the chill and freeze ones make other types count, a cannot keeps one out, "Hits
    // always Shock" needs no chance, and the caps can be raised.
    [Fact]
    public void Minimums_caps_and_the_damage_that_can_inflict_set_what_a_hit_inflicts()
    {
        var shockAtLeast15 = With(("shock_minimum_damage_taken_increase_%", 15));
        Assert.Equal(7, Magnitude(Shock, Taken(lightning: 1), With()));   // floor(50 × 0.01^0.4)
        Assert.Equal(15, Magnitude(Shock, Taken(lightning: 1), shockAtLeast15));
        Assert.Equal(19, Magnitude(Shock, Taken(lightning: 10), shockAtLeast15));   // a stronger shock keeps its own base
        Assert.Equal(22, Magnitude(Shock, Taken(lightning: 1),
            With(("shock_minimum_damage_taken_increase_%", 15), ("virtual_shock_effect_+%", 50))));
        Assert.Equal(7, Magnitude(Shock, Taken(lightning: 1), shockAtLeast15, With(("shocked_effect_on_self_+%", -50))));
        Assert.Equal(0, Magnitude(Shock, Taken(lightning: .01), With()));   // under 5%, discarded
        Assert.Equal(5, Magnitude(Shock, Taken(lightning: .01), With(("shock_minimum_damage_taken_increase_%", 5))));
        Assert.Equal(0, Magnitude(Shock, Taken(cold: 1), shockAtLeast15));   // no lightning, no shock

        var chillAtLeast10 = With(("chill_minimum_slow_%", 10));
        Assert.Equal(7, Magnitude(Chill, Taken(cold: 1), With()));
        Assert.Equal(10, Magnitude(Chill, Taken(cold: 1), chillAtLeast10));
        Assert.Equal(0, Magnitude(Chill, Taken(lightning: 1), chillAtLeast10));

        // Every type counts once with "All Damage can Shock": 3% of the threshold shocks for floor(50 × 0.03^0.4), and
        // Stormy Seas' cold does not count twice.
        var allShock = With(("all_damage_can_shock", 1));
        Assert.Equal(0, Magnitude(Shock, Taken(physical: 1), With()));
        Assert.Equal(7, Magnitude(Shock, Taken(physical: 1), allShock));
        Assert.Equal(12, Magnitude(Shock, Taken(cold: 1, lightning: 1, physical: 1), allShock));
        Assert.Equal(7, Magnitude(Shock, Taken(cold: 1), With(("all_damage_can_shock", 1), ("cold_damage_can_shock", 1))));
        Assert.Equal(7, Magnitude(Chill, Taken(physical: 1), With(("all_damage_can_chill", 1))));
        Assert.Equal(0, Magnitude(Freeze, Taken(physical: 10), With(("all_damage_can_chill", 1))));
        Assert.Equal(.6, Hit(Freeze, Taken(physical: 10), With(("all_damage_can_freeze", 1)))!.Until, 9);   // 0.06 s per 1%

        // One type's: Voltaxic Rift's chaos shocks, the axe's physical chills, lightning freezes; "Your Elemental Damage
        // can Shock" counts fire but not chaos.
        Assert.Equal(7, Magnitude(Shock, Taken(chaos: 1), With(("chaos_damage_can_shock", 1))));
        Assert.Equal(0, Magnitude(Shock, Taken(physical: 1), With(("chaos_damage_can_shock", 1))));
        Assert.Equal(7, Magnitude(Chill, Taken(physical: 1), With(("physical_damage_can_chill", 1))));
        Assert.Equal(.6, Hit(Freeze, Taken(lightning: 10), With(("lightning_damage_can_freeze", 1)))!.Until, 9);
        Assert.Equal(7, Magnitude(Shock, Taken(fire: 1), With(("elemental_damage_can_shock", 1))));
        Assert.Equal(0, Magnitude(Shock, Taken(chaos: 1), With(("elemental_damage_can_shock", 1))));
        // A cannot keeps the type out, even its own element and with all damage can.
        Assert.Null(Hit(Freeze, Taken(cold: 10), With(("cold_damage_cannot_freeze", 1))));
        Assert.Equal(.6, Hit(Freeze, Taken(cold: 10, physical: 10), With(("cold_damage_cannot_freeze", 1), ("all_damage_can_freeze", 1)))!.Until, 9);
        Assert.Equal(7, Magnitude(Chill, Taken(cold: 1), With(("cold_damage_cannot_freeze", 1))));
        Assert.Equal(0, Magnitude(Shock, Taken(lightning: 1), With(("lightning_damage_cannot_shock", 1))));

        // A hit that is not critical shocks only by chance, or always with "Hits always Shock".
        Assert.Equal(0, Magnitude(Shock, Taken(lightning: 1), With(), critical: false));
        Assert.Equal(7, Magnitude(Shock, Taken(lightning: 1), With(("always_shock", 1)), critical: false));

        // The caps: 30% chill, 40% with Shaper of Winter; 50% shock, 90% with Voltaxic Rift's +40%, whose stat is stored
        // as 100.
        Assert.Equal(30, Magnitude(Chill, Taken(cold: 100), With()));
        Assert.Equal(40, Magnitude(Chill, Taken(cold: 100), With(("elementalist_chill_maximum_magnitude_override", 40))));
        Assert.Equal(50, Magnitude(Shock, Taken(lightning: 300), With()));   // floor(50 × 3^0.4) is 77
        Assert.Equal(77, Magnitude(Shock, Taken(lightning: 300),
            With(("unique_voltaxic_rift_shock_maximum_magnitude_override", 100))));

        // "Chill/Shock Attackers for 4 seconds on Block" deals no damage: a 10% chill and a 15% shock through the effect
        // modifiers, for 4 s through the duration modifiers.
        static MonsterElementalAilments.Effect OnBlock(MonsterElementalAilments.Kind kind, Func<string, int> player) =>
            new MonsterElementalAilments().WithoutDamage(kind, 4, now: 0, player, With())!;
        Assert.Equal((10.0, 4.0), (OnBlock(Chill, With()).Magnitude, OnBlock(Chill, With()).Until));
        Assert.Equal(15, OnBlock(Chill, With(("virtual_chill_effect_+%", 50))).Magnitude);
        Assert.Equal(6, OnBlock(Chill, With(("virtual_chill_duration_+%", 50))).Until);
        Assert.Equal(15, OnBlock(Shock, With()).Magnitude);
        Assert.Equal(50, OnBlock(Shock, With(("virtual_shock_effect_+%", 300))).Magnitude);   // 60% over the cap
    }
}
