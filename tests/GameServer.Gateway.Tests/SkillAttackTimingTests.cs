using System.Text.Json;

namespace GameServer.Gateway.Tests;

public class SkillAttackTimingTests
{
    static readonly Lazy<SkillCatalog> Skills = new(() => new(BenchCraftingTests.Data()));

    [Theory]
    [InlineData("BurningArrow", -30)]
    public void Extracted_inherent_speed_is_not_an_additive_player_increase(string effect, int expected)
    {
        int row = Skills.Value.EffectRow(effect);
        Assert.Equal(expected, Skills.Value.AttackSpeedMultiplier(row, 1));
        Assert.Equal(expected, Skills.Value.AttackSpeedMultiplier(row, 20));
    }

}
