using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

// Increased rarity of items found (Gold Flask, gear, passives) in the drops of a kill (ItemDrops.WithIncreasedRarity).
public class ItemRarityDropsTests
{
    static string Data()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "data", "tables"))) return Path.Combine(dir.FullName, "data");
        throw new DirectoryNotFoundException("data/tables");
    }

    [Fact]
    public void Increased_rarity_raises_the_magic_and_rare_chances()
    {
        Assert.Equal((0.25, 0.05), ItemDrops.WithIncreasedRarity((0.25, 0.05), 0));
        // The Gold Flask's 30%.
        var gold = ItemDrops.WithIncreasedRarity((0.25, 0.05), 30);
        Assert.Equal(0.325, gold.Magic, 9);
        Assert.Equal(0.065, gold.Rare, 9);
        // Past 100% together they are scaled down to it, keeping their ratio.
        var a = ItemDrops.WithIncreasedRarity((0.30, 0.50), 100);
        Assert.Equal(1, a.Magic + a.Rare, 9);
        Assert.Equal(0.6 / 1.6, a.Magic, 9);
        // Reduced rarity lowers them.
        Assert.Equal(0.125, ItemDrops.WithIncreasedRarity((0.25, 0.05), -50).Magic, 9);
    }

    [Fact]
    public void A_kill_drops_more_magic_and_rare_equipment_with_increased_rarity()
    {
        var catalog = new ItemCatalog(Data());
        var drops = new ItemDrops(catalog, new ItemRolls(catalog, Data()));
        int Better(int increased)
        {
            int better = 0;
            for (int seed = 0; seed < 400; seed++)
                better += drops.Roll(30, MonsterRarity.Rare, new Random(seed), increased).Count(d => d.Item.Rarity != ItemRarity.Normal);
            return better;
        }
        int plain = Better(0), gold = Better(30), lots = Better(200);
        Assert.True(gold > plain, $"{gold} > {plain}");
        Assert.True(lots > gold, $"{lots} > {gold}");
    }
}
