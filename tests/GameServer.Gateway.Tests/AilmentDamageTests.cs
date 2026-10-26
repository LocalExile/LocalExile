using GameServer.Core.World;
using static AilmentDamage;

namespace GameServer.Gateway.Tests;

public class AilmentDamageTests
{
    static Seed Fixed(DamageType type, int damage, Tags tags = Tags.Attack) =>
        Seed.FromBase(DamagePacket.Of(type, damage, damage), tags);

    [Fact]
    public void Base_coefficients_and_durations_use_unmodified_eligible_damage()
    {
        var seed = Seed.FromBase(DamagePacket.Of(DamageType.Physical, 100, 100)
            .Add(DamageType.Fire, new(100, 100)).Add(DamageType.Chaos, new(50, 50)), Tags.Attack);

        var ignite = Calculate(seed, Ailment.Ignite, []).Roll(new Random(1));
        var bleed = Calculate(seed, Ailment.Bleed, []).Roll(new Random(1));
        var poison = Calculate(seed, Ailment.Poison, []).Roll(new Random(1));
        Assert.Equal((DamageType.Fire, 90d, 4d), (ignite.Type, ignite.DamagePerSecond, ignite.RemainingSeconds));
        Assert.Equal((DamageType.Physical, 70d, 5d), (bleed.Type, bleed.DamagePerSecond, bleed.RemainingSeconds));
        Assert.Equal((DamageType.Chaos, 45d, 2d), (poison.Type, poison.DamagePerSecond, poison.RemainingSeconds));
    }

    [Fact]
    public void Physical_poison_adds_physical_and_chaos_increases_once_in_one_pool()
    {
        Modifier[] mods = [new(100, DamageTypes: Types.Physical), new(100, DamageTypes: Types.Chaos),
            new(900, DamageTypes: Types.Fire)];
        var poison = Calculate(Fixed(DamageType.Physical, 100), Ailment.Poison, mods);

        Assert.Equal(90, poison.DamagePerSecond.Min, 8); // 100 * (1 + 1 + 1) * .3, not 100 * 2 * 2 * .3
        Assert.Equal(200, Fixed(DamageType.Physical, 100).Hit(mods).MinTotal);
    }

    [Fact]
    public void Converted_physical_fire_ignite_keeps_both_type_increases_without_double_dipping()
    {
        var seed = Fixed(DamageType.Physical, 100)
            .Convert(new Conversion(DamageType.Physical, DamageType.Fire, 50));
        Modifier[] mods = [new(100, DamageTypes: Types.Physical), new(100, DamageTypes: Types.Fire)];

        Assert.Equal(135, Calculate(seed, Ailment.Ignite, mods).DamagePerSecond.Min, 8); // 50 * (1 + 1 + 1) * .9
        Assert.Equal(70, Calculate(seed, Ailment.Bleed, mods).DamagePerSecond.Min, 8); // remaining physical50 * 2 * .7
        Assert.Equal(250, seed.Hit(mods).MinTotal, 8); // physical100 + converted fire150
    }

    [Fact]
    public void Chained_conversion_counts_each_stat_once_even_when_elemental_matches_three_types()
    {
        var seed = Fixed(DamageType.Physical, 100).Convert(
            new(DamageType.Physical, DamageType.Lightning, 100),
            new(DamageType.Lightning, DamageType.Cold, 100),
            new(DamageType.Cold, DamageType.Fire, 100));
        Modifier[] mods = [new(100, DamageTypes: Types.Elemental), new(20, DamageTypes: Types.Physical),
            new(30, DamageTypes: Types.Lightning), new(40, DamageTypes: Types.Cold), new(50, DamageTypes: Types.Fire), new(10)];

        Assert.Equal(315, Calculate(seed, Ailment.Ignite, mods).DamagePerSecond.Min, 8);
        Assert.Equal(Types.Physical | Types.Lightning | Types.Cold | Types.Fire, Assert.Single(seed.Parts).Ancestry);
    }

    [Fact]
    public void Gain_as_extra_does_not_consume_its_source_and_added_effectiveness_is_applied_once()
    {
        var seed = Fixed(DamageType.Physical, 100)
            .Add(DamagePacket.Of(DamageType.Fire, 20, 20), effectiveness: .5)
            .Convert(new Conversion(DamageType.Physical, DamageType.Fire, 50, ConversionKind.GainAsExtra));

        Assert.Equal(70, Calculate(seed, Ailment.Bleed, []).DamagePerSecond.Min, 8);
        Assert.Equal(54, Calculate(seed, Ailment.Ignite, []).DamagePerSecond.Min, 8); // (10 + 50) * .9
        Assert.Equal(160, seed.Hit([]).MinTotal, 8);
    }

    [Fact]
    public void Paths_reaching_the_same_final_type_do_not_share_unvisited_ancestral_modifiers()
    {
        var seed = Fixed(DamageType.Physical, 100).Convert(
            new(DamageType.Physical, DamageType.Cold, 50), new(DamageType.Physical, DamageType.Fire, 50),
            new(DamageType.Cold, DamageType.Fire, 100));
        Modifier[] mods = [new(100, DamageTypes: Types.Physical), new(100, DamageTypes: Types.Cold),
            new(100, DamageTypes: Types.Fire)];

        // 50 via cold gets physical+cold+fire =200; direct50 gets physical+fire =150.
        Assert.Equal(315, Calculate(seed, Ailment.Ignite, mods).DamagePerSecond.Min, 8);
        Assert.Equal(2, seed.Parts.Count);
    }

    [Fact]
    public void Skill_conversion_has_priority_and_global_conversions_share_the_remaining_budget()
    {
        var seed = Fixed(DamageType.Physical, 100).Convert(
            new(DamageType.Physical, DamageType.Fire, 75, ConversionKind.Skill),
            new(DamageType.Physical, DamageType.Cold, 100));

        Assert.Equal(75, seed.Hit([])[DamageType.Fire].Min, 8);
        Assert.Equal(25, seed.Hit([])[DamageType.Cold].Min, 8);
        Assert.Equal(67.5, Calculate(seed, Ailment.Ignite, []).DamagePerSecond.Min, 8);
        Assert.False(Calculate(seed, Ailment.Bleed, []).HasDamage);

        var globals = Fixed(DamageType.Physical, 100).Convert(
            new(DamageType.Physical, DamageType.Fire, 100), new(DamageType.Physical, DamageType.Cold, 100));
        Assert.Equal(50, globals.Hit([])[DamageType.Fire].Min, 8);
        Assert.Equal(50, globals.Hit([])[DamageType.Cold].Min, 8);
    }

    [Fact]
    public void Hit_only_attack_melee_spell_projectile_and_more_damage_never_enter_ailment_damage()
    {
        var seed = Fixed(DamageType.Fire, 100, Tags.Attack | Tags.Melee | Tags.Projectile);
        Modifier[] mods = [new(100, AppliesTo: Scope.Hit, RequiredTags: Tags.Attack),
            new(100, AppliesTo: Scope.Hit, RequiredTags: Tags.Melee),
            new(100, AppliesTo: Scope.Hit, RequiredTags: Tags.Spell),
            new(100, AppliesTo: Scope.Hit, RequiredTags: Tags.Projectile),
            new(50, ModifierKind.More, Scope.Hit)]; // e.g. an attack's final Rage scale

        Assert.Equal(600, seed.Hit(mods).MinTotal, 8); // spell-only increase does not match this source
        Assert.Equal(90, Calculate(seed, Ailment.Ignite, mods).DamagePerSecond.Min, 8);
        Assert.Equal(90, Calculate(Fixed(DamageType.Fire, 100, Tags.Spell), Ailment.Ignite, mods)
            .DamagePerSecond.Min, 8);
    }

    [Fact]
    public void Explicit_damage_with_attack_skills_can_affect_ailments_without_leaking_to_spells()
    {
        Modifier[] mods = [new(50, AppliesTo: Scope.Both, DamageTypes: Types.Elemental, RequiredTags: Tags.Attack)];
        Assert.Equal(135, Calculate(Fixed(DamageType.Fire, 100, Tags.Attack), Ailment.Ignite, mods)
            .DamagePerSecond.Min, 8);
        Assert.Equal(90, Calculate(Fixed(DamageType.Fire, 100, Tags.Spell), Ailment.Ignite, mods)
            .DamagePerSecond.Min, 8);
    }

    [Fact]
    public void Critical_ailment_bonus_and_fire_dot_multiplier_are_additive_and_ignore_hit_critical_multiplier()
    {
        var seed = Fixed(DamageType.Fire, 100);
        Modifier[] mods = [new(20, ModifierKind.DotMultiplier, Scope.Ailment, Types.Fire)];

        Assert.Equal(108, Calculate(seed, Ailment.Ignite, mods).DamagePerSecond.Min, 8);
        Assert.Equal(153, Calculate(seed, Ailment.Ignite, mods, critical: true).DamagePerSecond.Min, 8);
        Assert.Equal(500, seed.Hit(mods).Critical(500).MinTotal, 8);
        Assert.Equal(153, Calculate(seed, Ailment.Ignite, mods, critical: true).DamagePerSecond.Min, 8);
    }

    [Fact]
    public void Monster_and_minion_critical_ailments_use_their_own_complete_critical_dot_stat()
    {
        var seed = Fixed(DamageType.Physical, 100, Tags.Attack | Tags.Minion);
        Assert.Equal(39, Calculate(seed, Ailment.Poison, [], critical: true,
            criticalDotMultiplier: MonsterCriticalDotMultiplier).DamagePerSecond.Min, 8);
        Assert.Equal(45, Calculate(seed, Ailment.Poison, [], critical: true,
            criticalDotMultiplier: PlayerCriticalDotMultiplier).DamagePerSecond.Min, 8);
        Assert.Equal(30, Calculate(seed, Ailment.Poison, [], critical: true,
            criticalDotMultiplier: 0).DamagePerSecond.Min, 8); // e.g. an explicit no-critical-ailment rule
    }

    [Fact]
    public void Dot_multiplier_matches_the_resulting_dot_type_not_conversion_ancestry()
    {
        var seed = Fixed(DamageType.Physical, 100).Convert(new Conversion(DamageType.Physical, DamageType.Fire, 100));
        Modifier[] mods = [new(100, ModifierKind.DotMultiplier, Scope.Ailment, Types.Physical),
            new(20, ModifierKind.DotMultiplier, Scope.Ailment, Types.Fire),
            new(900, ModifierKind.DotMultiplier, Scope.Ailment, OnlyAilment: Ailment.Poison)];

        Assert.Equal(108, Calculate(seed, Ailment.Ignite, mods).DamagePerSecond.Min, 8);
    }

    [Fact]
    public void Separate_more_less_factors_multiply_and_ailment_only_modifiers_do_not_affect_the_hit()
    {
        var seed = Fixed(DamageType.Fire, 100);
        Modifier[] mods = [new(-30, ModifierKind.More), // Gambler's all-damage penalty
            new(40, ModifierKind.More, Scope.Ailment), // Bog Shaman's more DoT
            new(50, AppliesTo: Scope.Ailment, OnlyAilment: Ailment.Poison)];

        Assert.Equal(88.2, Calculate(seed, Ailment.Ignite, mods).DamagePerSecond.Min, 8);
        Assert.Equal(70, seed.Hit(mods).MinTotal, 8);
    }

    [Fact]
    public void Explicit_all_damage_eligibility_adds_types_without_converting_the_hit()
    {
        var seed = Fixed(DamageType.Lightning, 100);
        Assert.False(Calculate(seed, Ailment.Ignite, []).HasDamage);
        Assert.False(Calculate(seed, Ailment.Poison, []).HasDamage);
        Assert.Equal(90, Calculate(seed, Ailment.Ignite, [], additionalEligibleTypes: Types.All)
            .DamagePerSecond.Min, 8);
        Assert.Equal(30, Calculate(seed, Ailment.Poison, [], additionalEligibleTypes: Types.Elemental)
            .DamagePerSecond.Min, 8);
        Assert.Equal(100, seed.Hit([])[DamageType.Lightning].Min);
        Assert.True(seed.Hit([])[DamageType.Fire].IsZero);
    }

    [Fact]
    public void Ailment_uses_a_new_roll_instead_of_the_hits_rolled_damage()
    {
        var seed = Seed.FromBase(DamagePacket.Of(DamageType.Fire, 10, 20), Tags.Spell);
        var random = new SequenceRandom(10, 20);

        Assert.Equal(10, seed.Hit([]).Roll(random).MinTotal);
        Assert.Equal(18, Calculate(seed, Ailment.Ignite, []).Roll(random).DamagePerSecond, 8);
        Assert.Equal(2, random.Calls);
    }

    [Fact]
    public void Fixed_ailment_damage_preserves_fractional_dps_after_the_source_roll()
    {
        var ignite = Calculate(Fixed(DamageType.Fire, 101), Ailment.Ignite, []).Roll(new Random(1));
        Assert.Equal(90.9, ignite.DamagePerSecond, 8);
        Assert.Equal(363.6, ignite.TotalRemainingDamage, 8);
    }

    [Fact]
    public void Bleeding_requires_an_attack_and_other_dot_kinds_cannot_enter_the_hit_based_formula()
    {
        Assert.False(Calculate(Fixed(DamageType.Physical, 100, Tags.Spell), Ailment.Bleed, [],
            additionalEligibleTypes: Types.All).HasDamage);
        Assert.Throws<ArgumentOutOfRangeException>(() => Calculate(Fixed(DamageType.Fire, 100), Ailment.Burning, []));
        Assert.Throws<ArgumentOutOfRangeException>(() => Calculate(Fixed(DamageType.Physical, 100), Ailment.CorruptedBlood, []));
        Assert.Throws<ArgumentOutOfRangeException>(() => Fixed(DamageType.Fire, 100)
            .Convert(new Conversion(DamageType.Fire, DamageType.Physical, 50)));
    }

    sealed class SequenceRandom(params int[] values) : Random
    {
        public int Calls { get; private set; }
        public override int Next(int minValue, int maxValue)
        {
            int value = values[Calls++];
            Assert.InRange(value, minValue, maxValue - 1);
            return value;
        }
    }
}
