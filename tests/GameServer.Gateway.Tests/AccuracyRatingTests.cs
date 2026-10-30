namespace GameServer.Gateway.Tests;

public class AccuracyRatingTests
{
    [Theory]
    [InlineData(10L, 130L, 23)]
    [InlineData(101L, 33L, 134)]
    [InlineData(101L, -33L, 67)]
    [InlineData(-100L, -200L, 0)]
    [InlineData(100L, -100L, 0)]
    [InlineData(long.MaxValue, long.MaxValue, int.MaxValue)]
    public void Accuracy_floors_the_exact_nonnegative_product_once(long basis, long increased, int expected) =>
        Assert.Equal(expected, AccuracyRating.Calculate(basis, increased));

    [Fact]
    public void Selected_flat_and_increased_sources_join_the_original_buckets_before_flooring()
    {
        var pools = CharacterStats.For(SkillCatalog.ClassInfo.Witch, 1,
            new CharacterStats.Bonus(Accuracy: -27, AccuracyPct: 33));
        Assert.Equal(3, pools.AccuracyBase);
        Assert.Equal(3, pools.Accuracy);
        Assert.Equal(5, AccuracyRating.Selected(pools, default, name => name == "accuracy_rating" ? 1 : 0));
        Assert.Equal(6, AccuracyRating.Selected(pools, default, name => name switch
        { "accuracy_rating" => 1, "accuracy_rating_+%" => 20, _ => 0 }));
    }

    [Fact]
    public void A_suppressed_actor_base_is_not_lost_before_a_selected_flat_source_can_offset_it()
    {
        var pools = CharacterStats.For(SkillCatalog.ClassInfo.Witch, 1,
            new CharacterStats.Bonus(Accuracy: -35));
        Assert.Equal(-5, pools.AccuracyBase);
        Assert.Equal(0, pools.Accuracy);
        Assert.Equal(0, AccuracyRating.Selected(pools, default, name => name == "accuracy_rating" ? 4 : 0));
        Assert.Equal(1, AccuracyRating.Selected(pools, default, name => name == "accuracy_rating" ? 6 : 0));
    }

    [Fact]
    public void Independent_weapon_sources_sum_wide_and_the_selected_skill_does_not_repeat_local_accuracy()
    {
        var weapon = new WeaponProfile(false, false, false, false, false,
            Sword: true, OneHandMelee: true, LocalAccuracy: 123);
        long Flat(string name) => name is "accuracy_rating" or "sword_accuracy_rating" ? int.MaxValue : 0;
        Assert.Equal(2L * int.MaxValue + 123, AccuracyRating.Flat(weapon, Flat));
        Assert.Equal(2L * int.MaxValue, AccuracyRating.Flat(weapon, Flat, includeLocal: false));
        long Increased(string name) => name is "accuracy_rating_+%" or "sword_accuracy_rating_+%"
            or "one_handed_melee_accuracy_rating_+%" ? int.MaxValue : 0;
        Assert.Equal(3L * int.MaxValue, AccuracyRating.Increased(weapon, Increased));
        Assert.True(AccuracyRating.IsInput("sword_accuracy_rating_+%"));
        Assert.False(AccuracyRating.IsInput("main_hand_accuracy_rating"));
    }
}
