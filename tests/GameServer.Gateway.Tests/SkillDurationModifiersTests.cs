namespace GameServer.Gateway.Tests;

public class SkillDurationModifiersTests
{
    [Theory]
    [InlineData(-10, 1, 0)]
    [InlineData(0, 1, 0)]
    [InlineData(9, 1, 0)]
    [InlineData(10, 1, 1)]
    [InlineData(19, 1, 1)]
    [InlineData(20, 1, 2)]
    [InlineData(455, 1, 45)]
    [InlineData(455, -2, -90)]
    public void Only_complete_groups_of_final_Strength_contribute(int strength, int perTen, int expected) =>
        Assert.Equal(expected, SkillDurationModifiers.StrengthBonus(strength, perTen));

    [Fact]
    public void The_increase_is_additive_and_does_not_change_the_raw_inputs()
    {
        const int raw = 20, strength = 455, perTen = 1;
        Assert.Equal(65, SkillDurationModifiers.Total(raw, strength, perTen));
        Assert.Equal(65, SkillDurationModifiers.Total(raw, strength, perTen));
        Assert.Equal(20, SkillDurationModifiers.Total(raw, strength, 0));
        Assert.Equal(15, SkillDurationModifiers.Total(raw, 55, -1));
    }

    [Fact]
    public void Large_stat_inputs_saturate_instead_of_wrapping()
    {
        Assert.Equal(int.MaxValue, SkillDurationModifiers.StrengthBonus(int.MaxValue, int.MaxValue));
        Assert.Equal(int.MinValue, SkillDurationModifiers.StrengthBonus(int.MaxValue, int.MinValue));
        Assert.Equal(int.MaxValue, SkillDurationModifiers.Total(int.MaxValue, 10, 1));
        Assert.Equal(int.MinValue, SkillDurationModifiers.Total(int.MinValue, 10, -1));
        Assert.Equal(int.MaxValue, SkillDurationModifiers.Total(int.MinValue, int.MaxValue, int.MaxValue));
        Assert.Equal(int.MinValue, SkillDurationModifiers.Total(int.MaxValue, int.MaxValue, int.MinValue));
    }
}
