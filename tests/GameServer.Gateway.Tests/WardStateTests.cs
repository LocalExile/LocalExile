namespace GameServer.Gateway.Tests;

public class WardStateTests
{
    static int None(string _) => 0;

    [Fact]
    public void Converted_defences_keep_fractions_until_the_final_ward_value()
    {
        Assert.Equal(10u, WardState.Max(19m * .5m, 10));
        Assert.Equal(20u, WardState.Max(19m * .5m, 120));
    }

    [Fact]
    public void One_hit_breaks_the_entire_ward_even_when_it_absorbs_all_damage()
    {
        var ward = new WardState(); ward.SetMaximum(100);
        Assert.Equal(0, ward.Damage(40, 0, None));
        Assert.True(ward.Broken); Assert.Equal(0u, ward.Current);
        Assert.Equal(40, ward.Damage(40, 1.5, None));
        Assert.False(ward.Advance(1.999)); Assert.True(ward.Advance(2));
        Assert.Equal(100u, ward.Current);
        Assert.Equal(20, ward.Damage(120, 2, None));
    }

    [Fact]
    public void Restoration_speed_changes_the_delay_and_zero_damage_does_not_break_ward()
    {
        var ward = new WardState(); ward.SetMaximum(100);
        Assert.Equal(0, ward.Damage(0, 0, None)); Assert.False(ward.Broken);
        int Faster(string name) => name == "ward_delay_recovery_+%" ? 100 : 0;
        ward.Damage(1, 10, Faster);
        Assert.False(ward.Advance(10.999)); Assert.True(ward.Advance(11));
    }

    [Fact]
    public void Unbreakable_ward_applies_to_each_hit_and_removing_the_maximum_clears_broken_state()
    {
        var ward = new WardState(); ward.SetMaximum(100);
        int Unbreakable(string name) => name == "ward_does_not_break" ? 1 : 0;
        Assert.Equal(20, ward.Damage(120, 0, Unbreakable));
        Assert.Equal(20, ward.Damage(120, .1, Unbreakable)); Assert.False(ward.Broken);
        ward.Damage(1, .2, None); Assert.True(ward.Broken);
        ward.SetMaximum(0); Assert.False(ward.Broken); Assert.Equal(50, ward.Damage(50, .3, None));
    }

    [Fact]
    public void Paid_flask_restore_requires_the_actual_item_flag_and_cancels_the_remaining_delay()
    {
        var ward = new WardState(); ward.SetMaximum(100); ward.Damage(1, 0, None);
        Assert.False(UtilityFlasks.RestoreWardAfterPaidUse(ward, None, .1));
        Assert.True(ward.Broken);
        Assert.True(UtilityFlasks.RestoreWardAfterPaidUse(ward, name => name == "local_flask_restore_ward" ? 1 : 0, .2));
        Assert.False(ward.Broken); Assert.Equal(100u, ward.Current);
        Assert.Equal(double.PositiveInfinity, ward.RestoresAt);
    }
}
