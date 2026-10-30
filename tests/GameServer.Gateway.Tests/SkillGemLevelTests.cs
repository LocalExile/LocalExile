namespace GameServer.Gateway.Tests;

public class SkillGemLevelTests
{
    [Theory]
    [InlineData(20, 0, 3, 23)]
    public void Modifiers_sum_before_the_one_final_clamp(int intrinsic, int supported, int family, int expected) =>
        Assert.Equal(expected, SkillGemLevel.Resolve(intrinsic, supported, [family]));

}
