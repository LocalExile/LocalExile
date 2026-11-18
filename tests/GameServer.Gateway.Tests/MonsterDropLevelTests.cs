using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public class MonsterDropLevelTests
{
    [Theory]
    [InlineData(MonsterRarity.Normal, 45)]
    [InlineData(MonsterRarity.Magic, 46)]
    [InlineData(MonsterRarity.Rare, 47)]
    [InlineData(MonsterRarity.Unique, 47)]
    public void Equipment_from_a_level_45_monster_has_the_rarity_bonus(MonsterRarity rarity, int expected)
    {
        var drops = new ItemDrops(new ItemCatalog());
        var random = new Random(13);
        var equipment = Enumerable.Range(0, 64).SelectMany(_ => drops.Roll(45, rarity, random))
            .Where(drop => ItemDrops.EquipmentClasses.Contains(drop.Type.Class)).ToList();
        Assert.NotEmpty(equipment);
        Assert.All(equipment, drop => Assert.Equal(expected, drop.Item.ItemLevel));
    }
}
