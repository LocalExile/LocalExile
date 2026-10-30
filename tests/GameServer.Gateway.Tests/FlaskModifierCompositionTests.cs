using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public sealed class FlaskModifierCompositionTests
{
    static readonly Flasks.Flask Hybrid = new(120, 90, 6, 10, 40);
    static Item Flask => new(1, ["Base", "Charges", "Flask"], 2);
    static Func<string, int> Stats(params (string Name, int Value)[] values) => n => values.Where(v => v.Name == n).Sum(v => v.Value);
    static Dictionary<string, int> Local(params (string Name, int Value)[] values) => values.ToDictionary(v => v.Name, v => v.Value);
    static FlaskRecovery.Use Use(Func<string, int> stats, IReadOnlyDictionary<string, int>? local = null,
        Flasks.Flask? type = null) => FlaskRecovery.For(type ?? Hybrid, Flask, local ?? Local(), stats, 100, 1000);
    static List<Flasks.Effect> Parts(FlaskRecovery.Use use)
    {
        ushort id = 1;
        return Flasks.RecoveryEffects(use, 2, 0, () => id++, Program.FlaskEffectLifeBuffRow, Program.FlaskEffectManaBuffRow);
    }

    [Fact]
    public void Native_raw_charge_gain_resolves_once_and_multiplies_local_recovery()
    {
        var raw = Stats(("charges_gained_+%", 25), ("flask_charges_gained_+%", 5));
        var resolved = FlaskCharges.GainStats(raw).ToDictionary(s => s.Stat, s => s.Value);
        Assert.Equal(30, resolved["flask_charges_gained_+%"]);
        double factor = FlaskCharges.GainMultiplier(Local(("local_charges_added_+%", 50)), n => resolved.GetValueOrDefault(n));
        Assert.Equal(1.95, factor, 8);
        var carry = new FlaskCharges.Carry();
        Assert.Equal(1, carry.Take(7, factor));
        Assert.Equal(0.95, carry.Partial(7), 8);
        Assert.Equal(2, carry.Take(7, factor));
        Assert.Equal(0.9, carry.Partial(7), 8);
    }

    [Fact]
    public void Charge_gain_signed_sources_cancel_before_saturation_and_large_gains_do_not_wrap()
    {
        var cancelled = FlaskCharges.GainStats(Stats(("charges_gained_+%", int.MaxValue), ("flask_charges_gained_+%", int.MinValue)));
        Assert.Equal(-1, Assert.Single(cancelled).Value);
        var saturated = FlaskCharges.GainStats(Stats(("charges_gained_+%", int.MaxValue), ("flask_charges_gained_+%", int.MaxValue)));
        Assert.Equal(int.MaxValue, Assert.Single(saturated).Value);
        double factor = FlaskCharges.GainMultiplier(Local(("local_charges_added_+%", int.MaxValue)), _ => int.MaxValue);
        Assert.True(double.IsFinite(factor));
        Assert.True(factor > int.MaxValue);
        var carry = new FlaskCharges.Carry();
        Assert.Equal(int.MaxValue, carry.Take(7, factor));
        Assert.InRange(carry.Partial(7), 0, 0.99);
    }

    [Theory]
    [InlineData(-100)]
    [InlineData(-200)]
    public void Reduced_gain_cannot_become_a_negative_flask_charge_award(int reduction)
    {
        Assert.Equal(0, FlaskCharges.GainMultiplier(Local(), Stats(("flask_charges_gained_+%", reduction))));
        Assert.Equal(0, FlaskCharges.GainMultiplier(Local(("local_charges_added_+%", reduction)), Stats()));
    }

    [Theory]
    [InlineData(100, 0, 3, 6)]
    [InlineData(0, 100, 6, 3)]
    [InlineData(100, -50, 3, 12)]
    [InlineData(-50, 100, 12, 3)]
    public void Typed_hybrid_rates_change_only_their_resource_duration(int lifeRate, int manaRate, double lifeSeconds, double manaSeconds)
    {
        var use = Use(Stats(("flask_life_recovery_rate_+%", lifeRate), ("flask_mana_recovery_rate_+%", manaRate)));
        Assert.Equal(lifeSeconds, use.LifeSeconds!.Value);
        Assert.Equal(manaSeconds, use.ManaSeconds!.Value);
        Assert.Equal(Math.Max(lifeSeconds, manaSeconds), use.Seconds);
        Assert.Equal(120, use.Life);
        Assert.Equal(90, use.Mana);
    }

    [Fact]
    public void Generic_local_and_typed_recovery_rates_multiply_without_changing_total_amount()
    {
        var use = Use(Stats(("flask_recovery_speed_+%", 100), ("flask_mana_recovery_rate_+%", 50)),
            Local(("local_flask_recovery_speed_+%", 100)));
        Assert.Equal(1.5, use.LifeSeconds!.Value);
        Assert.Equal(1.0, use.ManaSeconds!.Value);
        Assert.Equal(120, use.Life);
        Assert.Equal(90, use.Mana);
    }

    [Fact]
    public void Pure_flasks_ignore_the_other_resources_typed_rate()
    {
        var life = Use(Stats(("flask_mana_recovery_rate_+%", 100)), type: Hybrid with { ManaPerUse = 0 });
        var mana = Use(Stats(("flask_life_recovery_rate_+%", 100)), type: Hybrid with { LifePerUse = 0 });
        Assert.Equal(6, life.Seconds);
        Assert.Equal(6, mana.Seconds);
        Assert.Single(Parts(life));
        Assert.Single(Parts(mana));
    }

    [Fact]
    public void Hybrid_recovery_and_buff_expiry_are_independent_and_each_resource_recovers_its_amount_once()
    {
        var parts = Parts(Use(Stats(("flask_mana_recovery_rate_+%", 100))));
        var life = Assert.Single(parts, p => p.LifePerSecond > 0);
        var mana = Assert.Single(parts, p => p.ManaPerSecond > 0);
        Assert.Equal((ushort)5, life.BuffRow);
        Assert.Equal((ushort)9, mana.BuffRow);
        Assert.NotEqual(life.Buff, mana.Buff);
        Assert.Equal(20, life.LifePerSecond);
        Assert.Equal(30, mana.ManaPerSecond);
        var running = new Flasks.Effects();
        foreach (var part in parts) running.Start(part);
        var first = running.Advance(0, 3);
        Assert.Equal(60, first.Life);
        Assert.Equal(90, first.Mana);
        Assert.Same(mana, Assert.Single(first.Ended));
        Assert.True(running.Any);
        var second = running.Advance(3, 6);
        Assert.Equal(60, second.Life);
        Assert.Equal(0, second.Mana);
        Assert.Same(life, Assert.Single(second.Ended));
        Assert.False(running.Any);
        Assert.Equal(0, running.Advance(6, 20).Life);
    }

    [Fact]
    public void Full_Mana_ends_only_Mana_and_does_not_refill_or_end_the_companion_Life_effect()
    {
        var parts = Parts(Use(Stats()));
        var running = new Flasks.Effects();
        foreach (var part in parts) running.Start(part);
        var ended = running.End(p => p.EndsWhenFull(lifeFull: false, manaFull: true));
        Assert.Same(Assert.Single(parts, p => p.ManaPerSecond > 0), Assert.Single(ended));
        var rest = running.Advance(0, 6);
        Assert.Equal(120, rest.Life);
        Assert.Equal(0, rest.Mana);
    }

    [Fact]
    public void Each_hybrid_resource_queues_behind_its_own_higher_rate_without_losing_time()
    {
        var parts = Parts(Use(Stats(("flask_mana_recovery_rate_+%", 100))));
        var running = new Flasks.Effects();
        foreach (var part in parts) running.Start(part);
        running.Start(new(0, 20, Program.FlaskEffectLifeBuffRow, 100, 0, 0, 2));
        var first = running.Advance(0, 3);
        Assert.Equal(220, first.Life); // 200 from the stronger Life flask, then one second of hybrid Life.
        Assert.Equal(90, first.Mana); // Mana never waits for the unrelated Life queue.
        var second = running.Advance(3, 8);
        Assert.Equal(100, second.Life);
        Assert.Equal(0, second.Mana);
        Assert.False(running.Any);
    }

    [Fact]
    public void Enduring_Mana_persists_at_full_Mana_without_making_hybrid_Life_nonqueueing()
    {
        var parts = Parts(Use(Stats(), Local(("local_flask_effect_not_removed_at_full_mana", 1))));
        var life = Assert.Single(parts, p => p.LifePerSecond > 0);
        var mana = Assert.Single(parts, p => p.ManaPerSecond > 0);
        Assert.Equal(1, life.Queue);
        Assert.Equal(0, mana.Queue);
        Assert.True(life.EndsWhenFull(true, true));
        Assert.False(mana.EndsWhenFull(true, true));
    }

    [Fact]
    public void Life_stays_at_full_is_preserved_without_extending_the_Mana_effect()
    {
        var parts = Parts(Use(Stats(("life_flask_effects_are_not_removed_at_full_life", 1))));
        Assert.False(Assert.Single(parts, p => p.LifePerSecond > 0).EndsWhenFull(true, true));
        Assert.True(Assert.Single(parts, p => p.ManaPerSecond > 0).EndsWhenFull(true, true));
    }

    [Fact]
    public void The_resource_tick_full_pool_filter_restores_only_the_component_that_stays_at_full()
    {
        var parts = Parts(Use(Stats(("life_flask_effects_are_not_removed_at_full_life", 1))));
        var running = new Flasks.Effects();
        foreach (var part in parts) running.Start(part);
        running.Advance(0, 1);
        var ended = running.End(_ => true); // Both pools filled; the tick provisionally ends both components.
        var terminal = running.KeepRunning(ended, lifeFull: true, manaFull: true, 1);
        Assert.Equal((ushort)9, Assert.Single(terminal).BuffRow);
        var remaining = running.Advance(1, 6);
        Assert.Equal(100, remaining.Life);
        Assert.Equal(0, remaining.Mana);
        Assert.Equal((ushort)5, Assert.Single(remaining.Ended).BuffRow);
    }

    [Fact]
    public void Mana_at_the_end_arrives_at_its_own_expiry_and_death_discards_all_remaining_parts()
    {
        var parts = Parts(Use(Stats(("flask_mana_recovery_rate_+%", 100)),
            Local(("local_flask_mana_recovery_occurs_instantly_at_end_of_flask_effect", 1))));
        var running = new Flasks.Effects();
        foreach (var part in parts) running.Start(part);
        var before = running.Advance(0, 2.9);
        Assert.Equal(0, before.Mana);
        Assert.Empty(before.Ended);
        var expiry = running.Advance(2.9, 3);
        Assert.Equal(90, expiry.Mana);
        Assert.Single(expiry.Ended);
        Assert.Single(running.End(_ => true)); // The death path removes the remaining Life component.
        Assert.Equal(0, running.Advance(3, 20).Life);
        Assert.Equal(0, running.Advance(3, 20).Mana);
    }

    [Fact]
    public void Instant_flasks_create_no_over_time_effect_or_buff()
    {
        var use = Use(Stats(), Local(("local_flask_recovers_instantly", 1)));
        Assert.Equal(120, use.InstantLife);
        Assert.Equal(90, use.InstantMana);
        Assert.Empty(Parts(use));
    }
}
