using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public class AilmentPanelStatsTests
{
    static readonly Lazy<StatCatalog> Stats = new(() => new StatCatalog(BenchCraftingTests.Data()));

    static Dictionary<string, int> Rows(AilmentPanelStats.Context context, DamagePacket raw, DamageConversion? conversion = null,
        params (string Stat, int Value)[] stats)
    {
        var given = stats.ToDictionary(s => s.Stat, s => s.Value);
        return AilmentPanelStats.Compute(context, raw, conversion ?? new DamageConversion(), n => given.GetValueOrDefault(n))
            .ToDictionary(r => r.Stat, r => r.Value);
    }

    static readonly AilmentPanelStats.Context MainHand = new("main_hand", Spell: false, Melee: true);
    static readonly AilmentPanelStats.Context Spell = new("spell", Spell: true);

    [Fact]
    public void Bleed_ignite_and_poison_use_their_base_percentages_of_the_typed_hit_per_minute()
    {
        var rows = Rows(MainHand, DamagePacket.Of(DamageType.Physical, 100, 200));
        Assert.Equal(70 * 60, rows["minimum_bleeding_damage_to_deal_per_minute_from_non_critical_main_hand_damage"]);
        Assert.Equal(140 * 60, rows["maximum_bleeding_damage_to_deal_per_minute_from_non_critical_main_hand_damage"]);
        Assert.Equal(70 * 60, rows["minimum_bleeding_damage_to_deal_per_minute_from_critical_main_hand_damage"]);
        Assert.Equal(0, rows["maximum_ignite_damage_to_deal_per_minute_from_non_critical_main_hand_damage"]);
        Assert.Equal(30 * 60, rows["minimum_poison_damage_to_deal_per_minute_from_non_critical_main_hand_damage"]);
        Assert.Equal(60 * 60, rows["maximum_poison_damage_to_deal_per_minute_from_critical_main_hand_damage"]);
        Assert.DoesNotContain("minimum_poison_damage_to_always_deal_per_minute_from_non_critical_main_hand_damage", rows.Keys);
        var fire = Rows(Spell, DamagePacket.Of(DamageType.Fire, 10, 10));
        Assert.Equal(9 * 60, fire["minimum_ignite_damage_to_deal_per_minute_from_non_critical_spell_damage"]);
        Assert.DoesNotContain("minimum_bleeding_damage_to_deal_per_minute_from_non_critical_spell_damage", fire.Keys);
        Assert.Equal(0, fire["maximum_poison_damage_to_deal_per_minute_from_non_critical_spell_damage"]);
    }

    [Fact]
    public void Converted_damage_keeps_the_visited_types_increases_and_the_ailment_buckets()
    {
        var toFire = new DamageConversion().AddGlobal(DamageType.Physical, DamageType.Fire, 100);
        var rows = Rows(MainHand, DamagePacket.Of(DamageType.Physical, 100, 200), toFire,
            ("physical_damage_+%", 50), ("fire_damage_+%", 20), ("burn_damage_+%", 30), ("elemental_damage_+%", 10),
            ("damage_over_time_+%", 5), ("damage_+%", 5), ("attack_damage_+%", 5), ("melee_damage_+%", 5),
            ("spell_damage_+%", 100), ("area_damage_+%", 100));
        // 100..200 fire × (1 + 130%) × 90% per second.
        Assert.Equal((int)Math.Round(100 * 2.3 * .9 * 60), rows["minimum_ignite_damage_to_deal_per_minute_from_non_critical_main_hand_damage"]);
        Assert.Equal((int)Math.Round(200 * 2.3 * .9 * 60), rows["maximum_ignite_damage_to_deal_per_minute_from_non_critical_main_hand_damage"]);
        Assert.Equal(0, rows["maximum_bleeding_damage_to_deal_per_minute_from_non_critical_main_hand_damage"]);
        Assert.Equal(0, rows["maximum_poison_damage_to_deal_per_minute_from_non_critical_main_hand_damage"]);
    }

    [Fact]
    public void Damage_over_time_multipliers_apply_and_the_critical_rows_add_the_critical_ailment_multiplier()
    {
        var rows = Rows(MainHand, DamagePacket.Of(DamageType.Physical, 100, 100), null,
            ("dot_multiplier_+", 20), ("physical_dot_multiplier_+", 10), ("chaos_dot_multiplier_+", 40),
            ("critical_ailment_dot_multiplier_+", 50), ("bleeding_damage_+%", 100));
        Assert.Equal((int)Math.Round(100 * 2 * 1.3 * .7 * 60), rows["minimum_bleeding_damage_to_deal_per_minute_from_non_critical_main_hand_damage"]);
        Assert.Equal((int)Math.Round(100 * 2 * 1.8 * .7 * 60), rows["minimum_bleeding_damage_to_deal_per_minute_from_critical_main_hand_damage"]);
        Assert.Equal((int)Math.Round(100 * 1.6 * .3 * 60), rows["minimum_poison_damage_to_deal_per_minute_from_non_critical_main_hand_damage"]);
        Assert.Equal((int)Math.Round(100 * 2.1 * .3 * 60), rows["minimum_poison_damage_to_deal_per_minute_from_critical_main_hand_damage"]);
    }

    [Fact]
    public void Chaos_only_poison_rows_show_with_a_chaos_poison_source_and_use_the_chaos_portion_alone()
    {
        var raw = DamagePacket.Of(DamageType.Physical, 100, 100).Add(DamageType.Chaos, new DamageRange(10, 10));
        var rows = Rows(Spell, raw, null, ("chaos_damage_chance_to_poison_%", 20), ("spell_damage_+%", 100), ("attack_damage_+%", 100));
        Assert.Equal((int)Math.Round(110 * 2 * .3 * 60), rows["minimum_poison_damage_to_deal_per_minute_from_non_critical_spell_damage"]);
        Assert.Equal((int)Math.Round(10 * 2 * .3 * 60), rows["minimum_poison_damage_to_always_deal_per_minute_from_non_critical_spell_damage"]);
        Assert.Equal((int)Math.Round(10 * 2 * .3 * 60), rows["maximum_poison_damage_to_always_deal_per_minute_from_critical_spell_damage"]);
    }

    [Fact]
    public void Prohibitions_zero_the_rows_and_frenzy_charges_scale_them()
    {
        var raw = DamagePacket.Of(DamageType.Physical, 100, 100);
        Assert.All(Rows(MainHand, raw, null, ("never_damaging_ailments", 1)).Values, v => Assert.Equal(0, v));
        Assert.All(Rows(MainHand, raw, null, ("base_deal_no_damage", 1)).Values, v => Assert.Equal(0, v));
        var charged = Rows(MainHand, raw, null, ("object_inherent_damage_+%_final_per_frenzy_charge", 4), ("current_frenzy_charges", 3));
        Assert.Equal((int)Math.Round(100 * 1.12 * .7 * 60), charged["minimum_bleeding_damage_to_deal_per_minute_from_non_critical_main_hand_damage"]);
    }

    [Fact]
    public void Every_row_of_every_context_exists_in_Stats_dat()
    {
        foreach (string hand in new[] { "main_hand", "off_hand", "spell", "secondary" })
        {
            var names = AilmentPanelStats.Rows(hand).ToList();
            Assert.Equal(hand is "main_hand" or "off_hand" ? 16 : 12, names.Count);
            Assert.All(names, n => Assert.NotNull(Stats.Value.Key(n)));
            var computed = AilmentPanelStats.Compute(new(hand, Spell: hand is "spell"), DamagePacket.Of(DamageType.Physical, 1, 1),
                new DamageConversion(), n => n == "chaos_damage_chance_to_poison_%" ? 1 : 0).Select(r => r.Stat).ToHashSet();
            Assert.True(names.ToHashSet().SetEquals(computed));
        }
    }
}
