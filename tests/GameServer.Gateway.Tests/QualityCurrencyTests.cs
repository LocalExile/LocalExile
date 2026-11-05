using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public class QualityCurrencyTests
{
    sealed class FixedRandom(double value) : Random
    {
        public override double NextDouble() => value;
    }

    [Fact]
    public void Quality_currencies_follow_item_level_and_unique_rules()
    {
        var items = Program.Items;
        var armour = ItemCatalog.Create(items.Find("Metadata/Items/Armours/Helmets/HelmetStr1")!);
        var weapon = ItemCatalog.Create(items.All.First(t => t.Supported && t.Components.Contains("Weapon") && t.Components.Contains("Quality")));
        var flask = ItemCatalog.Create(items.All.First(t => t.Supported && t.Components.Contains("Flask") && t.Components.Contains("Quality")));
        Assert.Equal(20, CurrencyEffects.ArmourerScrap(armour)!.Quality);
        Assert.Equal(20, CurrencyEffects.Whetstone(weapon)!.Quality);
        Assert.Equal(20, CurrencyEffects.Glassblower(flask)!.Quality);
        Assert.Equal(1, CurrencyEffects.ArmourerScrap(armour with { Rarity = ItemRarity.Unique })!.Quality);
        Assert.Equal(1, CurrencyEffects.Whetstone(weapon with { ItemLevel = 100 })!.Quality);
        Assert.Equal(20, CurrencyEffects.Glassblower(flask with { Quality = 19 })!.Quality);
        // The wiki's item-level 12 table gives 80.96% for 20 quality and 19.04% for 19.
        Assert.Equal(20, CurrencyEffects.ArmourerScrap(armour with { ItemLevel = 12 }, new FixedRandom(0.8095))!.Quality);
        Assert.Equal(19, CurrencyEffects.ArmourerScrap(armour with { ItemLevel = 12 }, new FixedRandom(0.8097))!.Quality);
        Assert.Equal(11, CurrencyEffects.ArmourerScrap(armour with { ItemLevel = 30 }, new FixedRandom(0))!.Quality);
        Assert.Equal(10, CurrencyEffects.ArmourerScrap(armour with { ItemLevel = 30 }, new FixedRandom(0.99))!.Quality);
    }
}
