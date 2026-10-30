using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

// Increased rarity of items found (Gold Flask, gear, passives) in the drops of a kill (ItemDrops.WithIncreasedRarity).
public class ItemRarityDropsTests
{
    static string Data()
    {
        for (var dir = new DirectoryInfo(TestEnvironment.RuntimeRoot); dir is not null; dir = dir.Parent)
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

}
