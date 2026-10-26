using GameServer.Core.World;

namespace GameServer.Core.Tests;

/// <summary>
/// HitResistances, the critical-strike elemental rules of a player's outgoing hit: Inevitable Judgement's
/// critical_strikes_ignore_elemental_resistances resolves an effective resistance of zero (negative included),
/// the attack-only variant covers attacks only, the non-critical strike's penetration stacks with the hit's
/// general one and subtracts after the cap, the suppressor restores the plain capped resistance, and physical,
/// chaos and damage over time are outside the rules.
/// </summary>
public class HitResistancesTests
{
    static HitResistances.Mods Judgement(int pen = 10) => new(CriticalIgnoreElemental: 1, AttackCriticalIgnoreElemental: 0,
        NonCriticalPenetration: pen, CannotPenetrateOrIgnore: 0);

    static HitResistances.Mods AttackOnly() => new(CriticalIgnoreElemental: 0, AttackCriticalIgnoreElemental: 1,
        NonCriticalPenetration: 0, CannotPenetrateOrIgnore: 0);

    static HitResistances.Mods Suppressed() => new(CriticalIgnoreElemental: 1, AttackCriticalIgnoreElemental: 0,
        NonCriticalPenetration: 10, CannotPenetrateOrIgnore: 1);

    static HitResistances.Strike Hit(bool critical = false, bool attack = true, bool overTime = false) =>
        new(Critical: critical, Attack: attack, OverTime: overTime);

    [Theory]
    [InlineData(DamageType.Fire, 40)]
    [InlineData(DamageType.Cold, 75)]
    [InlineData(DamageType.Lightning, -30)]
    public void A_critical_elemental_hit_ignores_the_resistance_whatever_it_is(DamageType type, int resistance)
    {
        // 40% fire, the 75% cap and a negative -30% all mitigate as zero: no reduction and no amplification, and
        // no penetration is left either.
        var resolved = HitResistances.Effective(type, new(resistance, 75, 20), Hit(critical: true), Judgement());
        Assert.Equal(new DamageModel.ResistanceValue(0), resolved);
        Assert.Equal(100.0, DamageModel.MitigateBy(100, DamageModel.Capped(resolved.Value, resolved.Maximum) - resolved.Penetration));
    }

    [Fact]
    public void A_critical_hit_with_the_ignore_overrides_penetration()
    {
        // The non-critical penetration stays out of a critical hit even without the ignore, and the ignore leaves
        // nothing for any penetration to penetrate — the general one on the target included.
        var withoutIgnore = HitResistances.Effective(DamageType.Fire, new(90, 75, 20), Hit(critical: true),
            Judgement() with { CriticalIgnoreElemental = 0 });
        Assert.Equal(20, withoutIgnore.Penetration);
        Assert.Equal(55, DamageModel.Capped(withoutIgnore.Value) - withoutIgnore.Penetration);
        var ignored = HitResistances.Effective(DamageType.Fire, new(90, 75, 20), Hit(critical: true), Judgement());
        Assert.Equal(new DamageModel.ResistanceValue(0), ignored);
    }

    [Theory]
    [InlineData(-30)]
    [InlineData(0)]
    [InlineData(90)]
    public void Ignore_is_independent_of_the_targets_maximum_resistance(int maximum)
    {
        var resolved = HitResistances.Effective(DamageType.Fire, new(40, maximum, 20),
            Hit(critical: true), Judgement());
        var mitigated = DamageModel.Mitigate(DamagePacket.Of(DamageType.Fire, 100, 100), _ => resolved);
        Assert.Equal(100, mitigated[DamageType.Fire].Min);
    }

    [Fact]
    public void A_noncritical_hit_stacks_its_penetration_with_the_general_one_after_the_cap()
    {
        // The target's own 20 (general penetration) plus the conditional 10: 90 capped at 75 → 75 − 30 = 45.
        var resolved = HitResistances.Effective(DamageType.Fire, new(90, 75, 20), Hit(), Judgement());
        Assert.Equal(30, resolved.Penetration);
        Assert.Equal(45, DamageModel.Capped(resolved.Value) - resolved.Penetration);
        // A negative resistance keeps its value, its general penetration and the conditional one.
        Assert.Equal(30, HitResistances.Effective(DamageType.Lightning, new(-30, 75, 20), Hit(), Judgement()).Penetration);
        // A critical hit without the ignore keeps the general penetration but takes no conditional one: the
        // stat's condition is a negated CriticalStrike.
        Assert.Equal(20, HitResistances.Effective(DamageType.Fire, new(40, 75, 20), Hit(critical: true),
            Judgement() with { CriticalIgnoreElemental = 0 }).Penetration);
    }

    [Fact]
    public void The_penetrated_resistance_sits_below_the_floor_without_being_capped_again()
    {
        // PoB's effMult subtracts the penetration from the floored resistance without another clamp: −200 − 30.
        var resolved = HitResistances.Effective(DamageType.Cold, new(DamageModel.ResistFloor - 50, 75, 20), Hit(), Judgement());
        Assert.Equal(DamageModel.ResistFloor - 30, DamageModel.Capped(resolved.Value, resolved.Maximum) - resolved.Penetration);
        Assert.Equal(330.0, DamageModel.MitigateBy(100, DamageModel.ResistFloor - 30));
    }

    [Theory]
    [InlineData(DamageType.Fire)]
    [InlineData(DamageType.Cold)]
    [InlineData(DamageType.Lightning)]
    public void All_three_elements_ignore_on_a_critical_hit(DamageType type)
    {
        Assert.Equal(new DamageModel.ResistanceValue(0), HitResistances.Effective(type, new(40, 75, 20), Hit(critical: true), Judgement()));
    }

    [Theory]
    [InlineData(DamageType.Physical)]
    [InlineData(DamageType.Chaos)]
    public void Physical_and_chaos_are_never_ignored_or_conditionally_penetrated(DamageType type)
    {
        // The rules pass non-elemental parts through untouched: the value and the target's own penetration (an
        // attack-only chaos stat, never a physical one) stay as the caller set them.
        var target = new DamageModel.ResistanceValue(40, 75, type == DamageType.Chaos ? 15 : 0);
        Assert.Equal(target, HitResistances.Effective(type, target, Hit(critical: true), Judgement()));
        Assert.Equal(target, HitResistances.Effective(type, target, Hit(), Judgement()));
        Assert.Equal(target, HitResistances.Effective(type, target, Hit(critical: true), AttackOnly()));
        Assert.Null(HitResistances.Describe(type, target, Hit(critical: true), Judgement() with { NonCriticalPenetration = 0 }));
    }

    [Fact]
    public void The_attack_only_ignore_covers_attacks_but_not_spells()
    {
        var target = new DamageModel.ResistanceValue(40);
        Assert.Equal(new DamageModel.ResistanceValue(0), HitResistances.Effective(DamageType.Fire, target, Hit(critical: true, attack: true), AttackOnly()));
        Assert.Equal(target, HitResistances.Effective(DamageType.Fire, target, Hit(critical: true, attack: false), AttackOnly()));
        // The generic flag covers spells as well.
        Assert.Equal(new DamageModel.ResistanceValue(0), HitResistances.Effective(DamageType.Fire, target, Hit(critical: true, attack: false), Judgement()));
        // A non-critical attack gets no ignore from the attack-only flag and no penetration from it.
        Assert.Equal(target, HitResistances.Effective(DamageType.Fire, target, Hit(attack: true), AttackOnly()));
    }

    [Fact]
    public void The_suppressor_restores_the_plain_capped_resistance()
    {
        // cannot_penetrate_or_ignore_elemental_resistances holds back the critical ignore and every elemental
        // penetration, the target's general one included; the pass caps the plain value (90 → 75).
        Assert.Equal(new DamageModel.ResistanceValue(40), HitResistances.Effective(DamageType.Fire, new(40, 75, 20), Hit(critical: true), Suppressed()));
        Assert.Equal(new DamageModel.ResistanceValue(40), HitResistances.Effective(DamageType.Fire, new(40, 75, 20), Hit(), Suppressed()));
        Assert.Equal(new DamageModel.ResistanceValue(90), HitResistances.Effective(DamageType.Fire, new(90, 75, 20), Hit(critical: true), Suppressed()));
        Assert.Equal(25, DamageModel.MitigateBy(100, DamageModel.Capped(90)));
    }

    [Fact]
    public void A_damage_over_time_tick_is_never_a_strike()
    {
        // A tick cannot critically strike and is not a strike: no ignore, no conditional penetration.
        var target = new DamageModel.ResistanceValue(40, 75, 20);
        Assert.Equal(target, HitResistances.Effective(DamageType.Fire, target, Hit(critical: true, overTime: true), Judgement()));
        Assert.Equal(target, HitResistances.Effective(DamageType.Fire, target, Hit(overTime: true), Judgement()));
        Assert.Null(HitResistances.Describe(DamageType.Fire, target, Hit(critical: true, overTime: true), Judgement()));
    }

    [Fact]
    public void A_zero_resistance_hit_is_unchanged_without_any_rule()
    {
        // Without the rules a 0 resistance mitigates as 0; with them the non-critical penetration still subtracts
        // from that zero (PoB's resist − pen), so a 0 becomes −10.
        var target = new DamageModel.ResistanceValue(0);
        Assert.Equal(target, HitResistances.Effective(DamageType.Fire, target, Hit(), default));
        Assert.Equal(-10, DamageModel.Capped(HitResistances.Effective(DamageType.Fire, target, Hit(), Judgement()).Value) - 10);
        Assert.Equal(110.0, DamageModel.MitigateBy(100, -10));
    }

    [Fact]
    public void The_pass_mitigates_each_type_with_its_own_resolved_resistance()
    {
        // 100 of every type against 40% of every resistance and a 20 general penetration: the critical strike
        // ignores the elemental part entirely and physical and chaos keep their plain capped value minus the
        // general penetration (40 − 20 = 20% → 80 damage).
        var rolled = DamagePacket.Empty
            .Add(DamageType.Physical, new DamageRange(100, 100))
            .Add(DamageType.Lightning, new DamageRange(100, 100))
            .Add(DamageType.Cold, new DamageRange(100, 100))
            .Add(DamageType.Fire, new DamageRange(100, 100))
            .Add(DamageType.Chaos, new DamageRange(100, 100));
        var resolved = DamageModel.Mitigate(rolled, type => HitResistances.Effective(type, new(40, 75, 20), Hit(critical: true), Judgement()));
        Assert.Equal(80.0, resolved[DamageType.Physical].Min);
        Assert.Equal(100.0, resolved[DamageType.Fire].Min);
        Assert.Equal(100.0, resolved[DamageType.Cold].Min);
        Assert.Equal(100.0, resolved[DamageType.Lightning].Min);
        Assert.Equal(80.0, resolved[DamageType.Chaos].Min);
    }

    [Fact]
    public void The_mitigated_components_stay_fractional_until_the_total()
    {
        // 3 fire + 3 cold at 50%: 1.5 + 1.5 per type and the total rounds once to 3 — not banker's 2 + 2 = 4.
        var rolled = DamagePacket.Of(DamageType.Fire, 3, 3).Add(DamageType.Cold, new DamageRange(3, 3));
        var resolved = DamageModel.Mitigate(rolled, _ => new DamageModel.ResistanceValue(50));
        Assert.Equal(1.5, resolved[DamageType.Fire].Min);
        Assert.Equal(1.5, resolved[DamageType.Cold].Min);
        Assert.Equal(3.0, resolved.MinTotal);
        Assert.Equal(0.0, DamageModel.MitigateBy(100, 110));
    }

    [Fact]
    public void The_rules_pass_keeps_the_same_fractional_components()
    {
        // 3 fire + 3 chaos at 50% through the rules: a critical hit ignores the fire (0% resistance → the whole 3)
        // and the chaos keeps its 1.5, total 4.5 fractional until the caller rounds; a non-critical hit takes
        // 50 − 10 = 40% on the fire (1.8) and the plain 50% on the chaos (1.5, no elemental pen there), 3.3 → 3.
        var rolled = DamagePacket.Of(DamageType.Fire, 3, 3).Add(DamageType.Chaos, new DamageRange(3, 3));
        var ignored = DamageModel.Mitigate(rolled, type => HitResistances.Effective(type, new(50), Hit(critical: true), Judgement()));
        Assert.Equal(3.0, ignored[DamageType.Fire].Min);
        Assert.Equal(1.5, ignored[DamageType.Chaos].Min);
        Assert.Equal(4.5, ignored.MinTotal);
        var penetrated = DamageModel.Mitigate(rolled, type => HitResistances.Effective(type, new(50), Hit(), Judgement()));
        Assert.Equal(1.8, penetrated[DamageType.Fire].Min);
        Assert.Equal(3, (int)Math.Round(penetrated.MinTotal));
    }

    [Fact]
    public void The_describe_text_names_the_changed_resolutions()
    {
        // The ignore on a critical hit, the stacked penetration on a non-critical one, and the suppressor's hold.
        Assert.Equal("fire 40 ignored", HitResistances.Describe(DamageType.Fire, new(40, 75, 20), Hit(critical: true), Judgement()));
        Assert.Equal("lightning -30 ignored", HitResistances.Describe(DamageType.Lightning, new(-30, 75, 20), Hit(critical: true), Judgement()));
        Assert.Equal("fire 40→10 (pen 30)", HitResistances.Describe(DamageType.Fire, new(40, 75, 20), Hit(), Judgement()));
        Assert.Equal("fire 40 (cannot penetrate or ignore)", HitResistances.Describe(DamageType.Fire, new(40, 75, 20), Hit(critical: true), Suppressed()));
        // A rule that changes nothing names nothing, and physical or a plain hit never do.
        Assert.Null(HitResistances.Describe(DamageType.Fire, new(40), Hit(critical: true), Judgement() with { CriticalIgnoreElemental = 0 }));
        Assert.Null(HitResistances.Describe(DamageType.Physical, new(40, 75, 20), Hit(critical: true), Judgement()));
        Assert.Null(HitResistances.Describe(DamageType.Fire, new(40), Hit(), Judgement() with { NonCriticalPenetration = 0, CriticalIgnoreElemental = 0 }));
    }

    [Fact]
    public void The_stats_read_by_name_from_a_dictionary()
    {
        // Key = Stats.dat row + 1: the ignore 3626, the attack-only variant 12972, the penetration 3885 and the
        // suppressor 12052 (Inevitable Judgement's rows verified against the 3.29.3b Stats.json).
        var stats = new Dictionary<uint, int> { [3626] = 1, [12972] = 1, [3885] = 10, [12052] = 1 };
        var mods = HitResistances.Mods.From(stats, name => name switch
        {
            "critical_strikes_ignore_elemental_resistances" => 3626,
            "attack_critical_strikes_ignore_elemental_resistances" => 12972,
            "non_critical_strikes_penetrate_elemental_resistances_%" => 3885,
            "cannot_penetrate_or_ignore_elemental_resistances" => 12052,
            _ => null,
        });
        Assert.Equal(new HitResistances.Mods(1, 1, 10, 1), mods);
        // A stat the source does not carry reads 0: unknown names and absent keys both.
        Assert.Equal(default, HitResistances.Mods.From(new Dictionary<uint, int>(), _ => 9999));
        Assert.Equal(new HitResistances.Mods(1, 0, 0, 0), HitResistances.Mods.From(stats,
            name => name == "critical_strikes_ignore_elemental_resistances" ? 3626 : null));
    }
}
