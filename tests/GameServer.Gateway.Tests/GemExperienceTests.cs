using GameServer.Core.World;
using Rarity = GameServer.Core.Protocol.Instance.MonsterRarity;

namespace GameServer.Gateway.Tests;

// "Socketed gems gain 10% of the experience your character earns, before any level penalties."
public sealed class GemExperienceTests
{
    [Fact]
    public void Gem_experience_base_ignores_the_level_penalty()
    {
        // A level 90 character killing a level 1 monster: the character's share is penalised, the gems' is not.
        uint penalised = Progression.MonsterExperience(1_000, 100, Rarity.Normal, 90, 1);
        uint beforePenalty = Progression.MonsterExperience(1_000, 100, Rarity.Normal, 90, 1, levelPenalty: false);
        uint safe = Progression.MonsterExperience(1_000, 100, Rarity.Normal, 1, 1);
        Assert.True(penalised < beforePenalty);
        Assert.Equal(safe, beforePenalty);
    }
}
