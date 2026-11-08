using GameServer.Core.World;
using Rarity = GameServer.Core.Protocol.Instance.MonsterRarity;

namespace GameServer.Gateway.Tests;

// The wiki's Experience page: the level penalty (safe zone, level 95+, effective monster level), the hidden rarity
// mods and the party split.
public sealed class ExperienceFormulaTests
{
    [Fact]
    public void Experience_penalties_follow_the_wiki()
    {
        // The wiki's worked example: a level 24 character, 95% at monster level 19, 52.5% at 17, 20% at 15.
        Assert.Equal(0.95, Progression.LevelPenalty(24, 19), 3);
        Assert.Equal(0.525, Progression.LevelPenalty(24, 17), 3);
        Assert.Equal(0.20, Progression.LevelPenalty(24, 15), 3);
        // Levels 95 and above keep the extra 1 / (1 + 0.1 * (level - 94)): a level 95 player at monster level 70 gets
        // ((100 / (100 + 17^2.5))^1.5) / 1.1.
        Assert.Equal(0.0196, Progression.LevelPenalty(95, 70), 4);
        // Monsters above level 70 count as a lower level for the penalty only.
        Assert.Equal(74.10, Progression.EffectiveMonsterLevel(75), 2);
        Assert.Equal(77.32, Progression.EffectiveMonsterLevel(82), 2);
        // The hidden rarity mods (Mods.json, monster_slain_experience_+%): magic 250%, rare 750%, unique 450%.
        Assert.Equal(3.5, Progression.RarityMultiplier(Rarity.Magic));
        Assert.Equal(8.5, Progression.RarityMultiplier(Rarity.Rare));
        Assert.Equal(5.5, Progression.RarityMultiplier(Rarity.Unique));
        // Party play: 10 and 30 split by (level + 10)^2.71, the level 30 player capped at their solo experience.
        Assert.InRange(Progression.PartyMultiplier([10, 30], 0), 0.26, 0.27);
        Assert.Equal(1, Progression.PartyMultiplier([10, 30], 1));
    }
}
