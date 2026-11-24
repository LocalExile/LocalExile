using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public class DropWeightsTests
{
    const string Wisdom = "Metadata/Items/Currency/CurrencyIdentification";
    const string Chaos = "Metadata/Items/Currency/CurrencyRerollRare";

    [Fact]
    public void Kind_and_type_factors_steer_the_roll()
    {
        Assert.NotNull(typeof(ItemDrops).GetMethod(nameof(ItemDrops.CurrencyDrop), [typeof(int), typeof(Random)]));
        Assert.NotNull(typeof(ItemDrops).GetMethod(nameof(ItemDrops.Roll), [typeof(int), typeof(MonsterRarity), typeof(Random),
            typeof(int), typeof(double), typeof(double), typeof(int), typeof(MapMods), typeof(double)]));
        var drops = new ItemDrops(new ItemCatalog());
        var weights = new DropWeights { Gems = 0, Flasks = 0, Equipment = 0 };
        weights.Items[Wisdom] = 0;
        weights.Items[Chaos] = 1000;
        var random = new Random(7);
        var rolled = Enumerable.Range(0, 64)
            .SelectMany(_ => drops.Roll(80, MonsterRarity.Unique, random, 0, 1, 1, 0, null, 1, weights)).ToList();

        Assert.NotEmpty(rolled);
        Assert.All(rolled, drop => Assert.Equal("StackableCurrency", drop.Type.Class));
        Assert.DoesNotContain(rolled, drop => drop.Type.Id == Wisdom);
        Assert.True(rolled.Count(drop => drop.Type.Id == Chaos) > rolled.Count * 0.9);
    }

    [Fact]
    public void Card_and_scarab_factors_scale_a_map_kills_base_chances()
    {
        var drops = new ItemDrops(new ItemCatalog());
        // Only the map's base chances are left; the scarab's 0.2% times 500 is every kill.
        var weights = new DropWeights { Currency = 0, Gems = 0, Flasks = 0, Equipment = 0, Cards = 0, Scarabs = 500 };
        var random = new Random(11);
        var rolled = Enumerable.Range(0, 32)
            .Select(_ => drops.Roll(80, MonsterRarity.Normal, random, 0, 1, 1, 1, null, 1, weights)).ToList();

        Assert.All(rolled, kill => Assert.Single(kill));
        Assert.All(rolled, kill => Assert.Contains("Scarab", kill[0].Type.Id));
    }
}
