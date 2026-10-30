namespace GameServer.Gateway.Tests;

public sealed class AddedBaseCriticalStrikeTests
{
    static Func<string, int> Raw(params (string Name, int Value)[] values) =>
        name => values.FirstOrDefault(v => v.Name == name).Value;

    [Theory]
    [InlineData(false, 500, 0, 0, 0, 0, 0, 0, 500)]
    [InlineData(true, 500, 0, 0, 0, 200, 0, 60, 1120)] // Existing ICS own200, not a new missing source.
    [InlineData(true, 500, 0, 50, 0, 0, 0, 0, 550)]
    [InlineData(false, 500, 0, 50, 0, 0, 0, 0, 500)] // Spell-only actor BASE stays out of attacks.
    [InlineData(false, 500, 75, 0, 50, 200, 0, 80, 1485)]
    [InlineData(true, 500, 75, 50, 0, 200, 0, 80, 1485)]
    [InlineData(false, 600, 75, 0, 50, 200, 0, 80, 1665)]
    [InlineData(false, 800, 75, 0, 50, 200, 0, 80, 2025)]
    [InlineData(false, 0, 0, 0, 50, 200, 0, 60, 400)]
    [InlineData(true, 0, 0, 50, 0, 200, 0, 60, 400)]
    [InlineData(true, 500, 75, 50, 0, 0, 0, 20, 750)]
    [InlineData(false, 1080, 0, 0, 50, 200, 0, 60, 2128)]
    [InlineData(true, 500, 0, 0, 50, 0, 0, 0, 500)]
    [InlineData(false, 500, 0, 0, 0, 0, 50, 20, 660)]
    [InlineData(true, 500, 0, 0, 0, 0, 50, 20, 660)]
    public void Actor_and_own_BASE_join_the_completed_intrinsic_before_INC_once(bool spell, int intrinsic,
        int generic, int spellBase, int attackBase, int ownGeneric, int ownTyped, int increased, int expected)
    {
        var player = Raw((AddedBaseCriticalStrike.Generic, generic), (AddedBaseCriticalStrike.Spell, spellBase),
            (AddedBaseCriticalStrike.Attack, attackBase));
        var own = Raw((AddedBaseCriticalStrike.Generic, ownGeneric),
            (spell ? AddedBaseCriticalStrike.Spell : AddedBaseCriticalStrike.Attack, ownTyped));
        int actual = spell
            ? SpellCriticalStrike.Chance(intrinsic, player, own, playerIncrease: increased)
            : AttackBaseCriticalStrike.ChanceOfBase(intrinsic, increased, player, own);
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Inapplicable_selected_typed_BASE_does_not_leak(bool spell)
    {
        var own = Raw((spell ? AddedBaseCriticalStrike.Attack : AddedBaseCriticalStrike.Spell, 500));
        Assert.Equal(500, AddedBaseCriticalStrike.Chance(500, 0, spell, Raw(), own));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Signed_sources_cancel_widely_before_projection_and_extremes_stay_in_the_chance_range(bool spell)
    {
        string typed = spell ? AddedBaseCriticalStrike.Spell : AddedBaseCriticalStrike.Attack;
        var player = Raw((AddedBaseCriticalStrike.Generic, int.MaxValue), (typed, int.MinValue));
        var own = Raw((AddedBaseCriticalStrike.Generic, 100), (typed, -99));
        Assert.Equal(500, AddedBaseCriticalStrike.Chance(500, 0, spell, player, own));
        Assert.Equal(10000, AddedBaseCriticalStrike.Chance(int.MaxValue, long.MaxValue, spell, _ => int.MaxValue, _ => int.MaxValue));
        Assert.Equal(0, AddedBaseCriticalStrike.Chance(int.MaxValue, long.MinValue, spell, _ => int.MaxValue, _ => int.MaxValue));
        Assert.Equal(0, AddedBaseCriticalStrike.Chance(500, 0, spell, Raw((typed, -500))));
    }

    [Theory]
    [InlineData(0, 3, 515)]
    [InlineData(250, 3, 773)]
    [InlineData(250, -3, 728)]
    public void Final_integer_permyriad_rounding_remains_half_up(int added, int increased, int expected) =>
        Assert.Equal(expected, SpellCriticalStrike.Chance(500, Raw((AddedBaseCriticalStrike.Spell, added),
            ("spell_critical_strike_chance_+%", increased))));

    [Theory]
    [InlineData("global_cannot_crit")]
    [InlineData("virtual_global_cannot_crit")]
    [InlineData("cannot_critical_strike_with_attacks")]
    public void Actor_and_own_attack_prohibitions_win_over_added_base(string flag)
    {
        var added = Raw((AddedBaseCriticalStrike.Generic, 500));
        Assert.Equal(0, AttackBaseCriticalStrike.Chance(true, 500, 100, Raw((flag, 1)), added));
        Assert.Equal(0, AttackBaseCriticalStrike.Chance(true, 500, 100, added, Raw((flag, 1))));
    }

    [Theory]
    [InlineData("global_cannot_crit")]
    [InlineData("virtual_global_cannot_crit")]
    public void Actor_and_own_spell_prohibitions_win_over_added_base(string flag)
    {
        Assert.Equal(0, SpellCriticalStrike.Chance(500, Raw((flag, 1)), Raw((AddedBaseCriticalStrike.Generic, 500))));
        Assert.Equal(0, SpellCriticalStrike.Chance(500, Raw((AddedBaseCriticalStrike.Generic, 500)), Raw((flag, 1))));
        Assert.Equal(1000, SpellCriticalStrike.Chance(500, Raw(("cannot_critical_strike_with_attacks", 1)),
            Raw((AddedBaseCriticalStrike.Generic, 500))));
    }

    [Fact]
    public void Unknown_or_invalid_intrinsic_is_distinct_from_a_known_zero_spell_base()
    {
        var source = Raw((AddedBaseCriticalStrike.Generic, 200));
        Assert.Equal(0, SpellCriticalStrike.Chance(null, source));
        Assert.Equal(0, SpellCriticalStrike.Chance(-1, source));
        Assert.Equal(200, SpellCriticalStrike.Chance(0, source));
    }

    [Theory]
    [InlineData(500, 400, 20, 1080)]
    [InlineData(830, 0, 20, 996)]
    [InlineData(830, 0, 15, 955)]
    [InlineData(500, 400, 0, 900)]
    [InlineData(500, 0, 0, 500)]
    [InlineData(9000, 3000, 0, 12000)] // The global100% chance cap belongs to the later stage.
    public void Local_BASE_precedes_local_INC_and_rounds_before_global_actor_sources(int basis, int flat, int increase, int expected) =>
        Assert.Equal(expected, AddedBaseCriticalStrike.LocalWeaponChance(basis, flat, increase));

    [Fact]
    public void Weapon_override_replaces_local_base_but_actor_additions_remain_outside_it_and_unarmed_is_separate()
    {
        var source = Raw((AttackBaseCriticalStrike.WeaponOverrideStat, 800), (AttackBaseCriticalStrike.UnarmedBaseStat, 1000),
            (AddedBaseCriticalStrike.Attack, 50));
        Assert.Equal(850, AttackBaseCriticalStrike.Chance(true, 1080, 0, source));
        Assert.Equal(1050, AttackBaseCriticalStrike.Chance(false, 1080, 0, source));
        Assert.Equal(int.MaxValue, AddedBaseCriticalStrike.LocalWeaponChance(int.MaxValue, int.MaxValue, 0));
        Assert.Equal(10000, AttackBaseCriticalStrike.Chance(true, int.MaxValue, 0, Raw()));
    }

    [Theory]
    [InlineData(AddedBaseCriticalStrike.Generic)]
    [InlineData(AddedBaseCriticalStrike.Attack)]
    [InlineData(AddedBaseCriticalStrike.Spell)]
    public void Every_added_base_source_requests_actual_pool_recalculation(string name) =>
        Assert.True(AttackBaseCriticalStrike.IsInput(name));
}
