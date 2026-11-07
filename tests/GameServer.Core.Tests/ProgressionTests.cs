using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

namespace GameServer.Core.Tests;

public class ProgressionTests
{
    // ExperienceLevels.dat's first levels.
    static readonly uint[] Starts = [0, 525, 1760, 3781, 7184];

    [Fact]
    public void Level_from_experience()
    {
        Assert.Equal(1, Progression.LevelFor(0, Starts));
        Assert.Equal(1, Progression.LevelFor(524, Starts));
        Assert.Equal(2, Progression.LevelFor(525, Starts));
        Assert.Equal(4, Progression.LevelFor(5000, Starts));
        Assert.Equal(5, Progression.LevelFor(1_000_000, Starts));
    }

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
        Assert.Equal(3.5, Progression.RarityMultiplier(MonsterRarity.Magic));
        Assert.Equal(8.5, Progression.RarityMultiplier(MonsterRarity.Rare));
        Assert.Equal(5.5, Progression.RarityMultiplier(MonsterRarity.Unique));
        // Party play: 10 and 30 split by (level + 10)^2.71, the level 30 player capped at their solo experience.
        Assert.InRange(Progression.PartyMultiplier([10, 30], 0), 0.26, 0.27);
        Assert.Equal(1, Progression.PartyMultiplier([10, 30], 1));
    }

}
