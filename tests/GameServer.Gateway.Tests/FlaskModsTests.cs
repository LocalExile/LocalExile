using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

// Flask mods on life and mana flasks and on every flask's charges (FlaskCharges, FlaskRecovery), with the numbers of
// Path of Building's Item.lua / CalcPerform.lua and poewiki's Flask page.
public class FlaskModsTests
{
    // Small Life Flask (Flasks.dat, ComponentCharges): 70 life over 3 s, 7 of 21 charges per use; Small Mana Flask 50 mana.
    static readonly Flasks.Flask SmallLife = new(70, 0, 3.0, 7, 21), SmallMana = new(0, 50, 3.0, 6, 24);
    static Item Flask(byte quality = 0) => new(0x1111, ["Base", "Charges", "Flask"], 0x2222) { Quality = quality };
    static Func<string, int> Character(params (string Stat, int Value)[] stats) =>
        name => stats.Where(s => s.Stat == name).Sum(s => s.Value);
    static readonly Func<string, int> None = Character();
    static Dictionary<string, int> Local(params (string Stat, int Value)[] stats) => stats.ToDictionary(s => s.Stat, s => s.Value);

    [Fact]
    public void Charges_used_and_maximum_charges_follow_the_flask_and_the_character()
    {
        Assert.Equal(SmallLife, FlaskCharges.Of(SmallLife, Local()));
        // Chemist's 28% reduced charges used: floor(7 × 0.72) = 5; Ample +35 maximum charges.
        var f = FlaskCharges.Of(SmallLife, Local(("local_charges_used_+%", -28), ("local_extra_max_charges", 35)));
        Assert.Equal((5, 56), (f.ChargesPerUse, f.MaxCharges));
        // The tree's 5% reduced flask charges used multiplies after the flask's own: floor(floor(30 × 0.72) × 0.95) = 19.
        var quicksilver = new Flasks.Flask(0, 0, 6, 30, 60);
        Assert.Equal(19, FlaskCharges.Of(quicksilver, Local(("local_charges_used_+%", -28)), Character(("flask_charges_used_+%", -5))).ChargesPerUse);
        // Nitrate's 25% increased charges used.
        Assert.Equal(8, FlaskCharges.Of(SmallLife, Local(("local_charges_used_+%", 25))).ChargesPerUse);
    }

    [Fact]
    public void A_life_flask_recovers_its_amount_with_quality_over_its_time()
    {
        var plain = FlaskRecovery.For(SmallLife, Flask(), Local(), None, life: 100, maxLife: 200);
        Assert.Equal((70.0, 0.0, 3.0, 0.0), (plain.Life, plain.Mana, plain.Seconds, plain.InstantLife));
        // 20% quality: 20% more recovered, same time (PoB lifeBase × (1 + quality)).
        Assert.Equal(84, FlaskRecovery.For(SmallLife, Flask(20), Local(), None, 100, 200).Life, 6);
        // Saturated: 70% more recovered at 33% reduced rate: 119 over 3 / 0.67 s.
        var saturated = FlaskRecovery.For(SmallLife, Flask(), Local(("local_flask_amount_to_recover_+%", 70), ("local_flask_recovery_speed_+%", -33)), None, 100, 200);
        Assert.Equal(119, saturated.Life, 6);
        Assert.Equal(3 / 0.67, saturated.Seconds, 6);
        // Catalysed: 70% increased recovery rate.
        Assert.Equal(3 / 1.7, FlaskRecovery.For(SmallLife, Flask(), Local(("local_flask_recovery_speed_+%", 70)), None, 100, 200).Seconds, 6);
    }

    [Fact]
    public void Instant_recovery_comes_at_once()
    {
        // Seething: Instant Recovery, 66% reduced amount recovered.
        var seething = FlaskRecovery.For(SmallLife, Flask(), Local(("local_flask_recovers_instantly", 1), ("local_flask_amount_to_recover_+%", -66)), None, 100, 200);
        Assert.Equal((0.0, 0.0), (seething.Life, seething.Seconds));
        Assert.Equal(70 * 0.34, seething.InstantLife, 6);
        // Bubbling: 50% applied instantly, 36% reduced amount, 135% increased rate.
        var bubbling = FlaskRecovery.For(SmallLife, Flask(), Local(("local_flask_recovery_amount_%_to_recover_instantly", 50),
            ("local_flask_amount_to_recover_+%", -36), ("local_flask_recovery_speed_+%", 135)), None, 100, 200);
        Assert.Equal(70 * 0.64 * 0.5, bubbling.InstantLife, 6);
        Assert.Equal(70 * 0.64 * 0.5, bubbling.Life, 6);
        Assert.Equal(3 / 2.35, bubbling.Seconds, 6);
    }

    [Fact]
    public void Low_life_mods_apply_at_half_life_or_less()
    {
        var panicked = Local(("local_flask_recover_instantly_when_on_low_life", 1), ("local_flask_amount_to_recover_+%", -11));
        Assert.Equal(0, FlaskRecovery.For(SmallLife, Flask(), panicked, None, life: 101, maxLife: 200).InstantLife);
        Assert.Equal(70 * 0.89, FlaskRecovery.For(SmallLife, Flask(), panicked, None, life: 100, maxLife: 200).InstantLife, 6);
        // Cautious: 130% more recovery on low life.
        var cautious = Local(("local_flask_amount_to_recover_+%_when_on_low_life", 130));
        Assert.Equal(70, FlaskRecovery.For(SmallLife, Flask(), cautious, None, 150, 200).Life, 6);
        Assert.Equal(161, FlaskRecovery.For(SmallLife, Flask(), cautious, None, 50, 200).Life, 6);
        // Not scaled by flask effect (PoB CalcPerform): 10% increased effect adds 10% of the base, not 10% of the 230%.
        var withEffect = Local(("local_flask_amount_to_recover_+%_when_on_low_life", 130), ("local_flask_effect_+%", 10));
        Assert.Equal(70 * (1.1 + 2.3 - 1), FlaskRecovery.For(SmallLife, Flask(), withEffect, None, 50, 200).Life, 6);
        Assert.Equal(70 * 1.1, FlaskRecovery.For(SmallLife, Flask(), withEffect, None, 150, 200).Life, 6);
    }

    [Fact]
    public void The_characters_flask_stats_count()
    {
        // 30% increased life recovery from flasks, 20% increased flask effect duration (recovers 20% longer at the same
        // rate), 10% increased flask effect and 30% increased flask recovery rate.
        var r = FlaskRecovery.For(SmallLife, Flask(), Local(), Character(("flask_life_to_recover_+%", 30), ("flask_duration_+%", 20),
            ("flask_effect_+%", 10), ("flask_recovery_speed_+%", 30)), 100, 200);
        Assert.Equal(70 * 1.3 * 1.2 * 1.1, r.Life, 6);
        Assert.Equal(3 * 1.2 / 1.3, r.Seconds, 6);
        // Mana flask stats leave a life flask alone, and the other way round.
        Assert.Equal(70, FlaskRecovery.For(SmallLife, Flask(), Local(), Character(("flask_mana_to_recover_+%", 50)), 100, 200).Life, 6);
        Assert.Equal(75, FlaskRecovery.For(SmallMana, Flask(), Local(), Character(("flask_mana_to_recover_+%", 50)), 100, 200).Mana, 6);
    }

    [Fact]
    public void Some_mods_take_from_the_other_pool_or_give_mana_at_the_end()
    {
        // Sapping: 60% increased life recovered, removes 10% of the life recovered from mana.
        var sapping = FlaskRecovery.For(SmallLife, Flask(), Local(("local_flask_life_to_recover_+%", 60), ("local_flask_removes_%_of_life_recovery_from_mana_on_use", 10)), None, 100, 200);
        Assert.Equal(112, sapping.Life, 6);
        Assert.Equal(11.2, sapping.RemovesMana, 6);
        // Foreboding: mana recovery occurs instantly at the end of the effect, 66% increased amount.
        var foreboding = FlaskRecovery.For(SmallMana, Flask(), Local(("local_flask_mana_recovery_occurs_instantly_at_end_of_flask_effect", 1), ("local_flask_amount_to_recover_+%", 66)), None, 100, 200);
        Assert.Equal((0.0, 3.0), (foreboding.Mana, foreboding.Seconds));
        Assert.Equal(83, foreboding.ManaAtEnd, 6);
        // Enduring: the effect is not removed at full mana.
        Assert.True(FlaskRecovery.For(SmallMana, Flask(), Local(("local_flask_effect_not_removed_at_full_mana", 1)), None, 100, 200).ManaStaysAtFull);
        Assert.True(FlaskRecovery.For(SmallLife, Flask(), Local(), Character(("life_flask_effects_are_not_removed_at_full_life", 1)), 100, 200).LifeStaysAtFull);
        // "… Flask Effects are not removed when … is Filled / … do not Queue": the character's stats also stop the queue,
        // each for its own kind of flask; Enduring for its mana flask.
        Assert.True(FlaskRecovery.For(SmallLife, Flask(), Local(), Character(("life_flask_effects_are_not_removed_at_full_life", 1)), 100, 200).NoQueue);
        Assert.False(FlaskRecovery.For(SmallLife, Flask(), Local(), Character(("mana_flask_effects_are_not_removed_at_full_mana", 1)), 100, 200).NoQueue);
        Assert.True(FlaskRecovery.For(SmallMana, Flask(), Local(), Character(("mana_flask_effects_are_not_removed_at_full_mana", 1)), 100, 200).NoQueue);
        Assert.True(FlaskRecovery.For(SmallMana, Flask(), Local(("local_flask_effect_not_removed_at_full_mana", 1)), None, 100, 200).NoQueue);
        Assert.False(FlaskRecovery.For(SmallMana, Flask(), Local(), None, 100, 200).NoQueue);
    }

    [Fact]
    public void A_flask_brings_no_dead_player_back()
    {
        // A monster can kill the player while the flask's charges go out (an await): the instant part must not set life 1.
        var seething = FlaskRecovery.For(SmallLife, Flask(), Local(("local_flask_recovers_instantly", 1)), None, 100, 200);
        Assert.Null(FlaskRecovery.Instant(seething, life: 0, mana: 50, maxLife: 200, manaCap: 100));
        Assert.Equal(((uint)170, (uint)50), FlaskRecovery.Instant(seething, life: 100, mana: 50, maxLife: 200, manaCap: 100));
        // Costs from the other pool never kill: life stays at least 1, mana at least 0.
        var cost = new FlaskRecovery.Use(0, 0, 3, 0, 40, RemovesLife: 500, RemovesMana: 0, 0, false, false, 0);
        Assert.Equal(((uint)1, (uint)90), FlaskRecovery.Instant(cost, life: 100, mana: 50, maxLife: 200, manaCap: 90));
    }

    [Fact]
    public void A_life_flask_suffix_recovers_more_life_over_ten_seconds_below_full_life()
    {
        var local = Local(("local_flask_additional_x%_life_recovery_per_second_over_10_seconds_if_not_on_full_life", 40));
        // "Recover an additional 40% of Flask's Life Recovery Amount over 10 seconds": 40% of the Small Life Flask's 70,
        // whatever the character's maximum life (5000 here).
        Assert.Equal(28, FlaskRecovery.For(SmallLife, Flask(), local, None, 150, 5000).ExtraLife, 6);
        Assert.Equal(0, FlaskRecovery.For(SmallLife, Flask(), local, None, 5000, 5000).ExtraLife);
        // Of the whole amount, the instant part too: Bubbling's 50% instantly and 36% reduced amount, 20% quality.
        var bubbling = Local(("local_flask_additional_x%_life_recovery_per_second_over_10_seconds_if_not_on_full_life", 40),
            ("local_flask_recovery_amount_%_to_recover_instantly", 50), ("local_flask_amount_to_recover_+%", -36));
        Assert.Equal(70 * 1.2 * 0.64 * 0.4, FlaskRecovery.For(SmallLife, Flask(20), bubbling, None, 150, 5000).ExtraLife, 6);
    }

    [Fact]
    public void Mana_at_the_end_comes_when_the_effect_runs_out_and_stays_survive_a_full_pool()
    {
        var effects = new Flasks.Effects();
        effects.Start(new Flasks.Effect(4, 1, 9, 0, 0, 0, 3, ManaAtEnd: 83));
        Assert.Equal(0, effects.Advance(0, 2.9).Mana);
        var (_, mana, ended) = effects.Advance(2.9, 3.1);
        Assert.Equal(83, mana, 6);
        Assert.Single(ended);

        // A full pool: a plain effect ends, one that stays at full or still owes its mana goes on.
        var plain = new Flasks.Effect(0, 2, 9, 0, 10, 0, 3);
        var enduring = new Flasks.Effect(1, 3, 9, 0, 10, 0, 3, ManaStays: true);
        var foreboding = new Flasks.Effect(2, 4, 9, 0, 0, 0, 3, ManaAtEnd: 50);
        effects.Start(plain); effects.Start(enduring); effects.Start(foreboding);
        Assert.True(plain.EndsWhenFull(lifeFull: true, manaFull: true));
        Assert.False(enduring.EndsWhenFull(true, true));
        Assert.True(foreboding.EndsWhenFull(true, manaFull: true));
        Assert.False(foreboding.EndsWhenFull(true, manaFull: false));
        // RunResourceRecovery: the full-pool check ends them all, KeepRunning puts back the ones that stay.
        var full = effects.End(e => (e.LifePerSecond == 0 || true) && (e.ManaPerSecond == 0 || true));
        var gone = effects.KeepRunning(full, lifeFull: true, manaFull: true, now: 1);
        Assert.Equal([plain, foreboding], gone);
        Assert.True(effects.Any);
        // One that stays at full still ends when its time is up.
        var (_, _, done) = effects.Advance(1, 10);
        Assert.Contains(enduring, done);
        Assert.False(effects.Any);
    }
}
