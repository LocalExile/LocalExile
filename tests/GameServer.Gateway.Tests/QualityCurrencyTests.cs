using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public class QualityCurrencyTests
{
    sealed class FixedRandom(double value) : Random
    {
        public override double NextDouble() => value;
    }

    sealed class GemCorruptionRandom(int outcome, bool increase = true) : Random
    {
        public override int Next(int maxValue) => maxValue == 4 ? outcome : increase ? 1 : 0;
        public override int Next(int minValue, int maxValue) => maxValue - 1;
    }

    [Fact]
    public void Vaal_orbs_corrupt_skill_and_support_gems_and_keep_the_matching_variant()
    {
        using var fixture = new MinionUtilityTests.Fixture();
        var items = Game.ItemsFor(fixture.Player);
        var (type, variant) = items.FindGem("Ground Slam of Earthshaking")!.Value;
        var gem = items.Gem(type)!;
        var original = ItemCatalog.Create(type) with
        {
            GemExperience = gem.ExperienceFor(20), GemVariant = variant.Index, Quality = 20, GemFlags = 4,
        };
        var currency = items.Find(CurrencyEffects.VaalOrb)!;
        Item Apply(Item target, int outcome, bool increase = true) =>
            Assert.IsType<Item>(Game.CurrencyResult(fixture.Player, target, currency.Hash, new GemCorruptionRandom(outcome, increase)));
        Assert.Equal(original with { Corrupted = true }, Apply(original, 0));
        Assert.Equal(21, items.GemLevel(Apply(original, 1)));
        Assert.Equal(19, items.GemLevel(Apply(original, 1, increase: false)));
        Assert.Equal(23, Apply(original, 2).Quality);
        Assert.Equal(10, Apply(original, 2, increase: false).Quality);
        var vaal = Apply(original, 3);
        Assert.Equal("Vaal Ground Slam of Earthshaking", items.GemName(vaal));
        Assert.Contains(variant.Effect, items.GemEffects(vaal));
        Assert.Equal(20, items.GemLevel(vaal));
        Assert.Equal(original.Quality, vaal.Quality);
        Assert.Equal(original.GemFlags, vaal.GemFlags);
        Assert.True(vaal.Corrupted);
        Assert.NotEmpty(vaal.Payload());
        Assert.Null(Game.CurrencyResult(fixture.Player, vaal, currency.Hash, new GemCorruptionRandom(0)));
        Assert.Null(Game.CurrencyResult(fixture.Player, original with { Mirrored = true }, currency.Hash, new GemCorruptionRandom(0)));

        var support = ItemCatalog.Create(items.FindGem("Added Cold Damage Support")!.Value.Type);
        Assert.Equal(support with { Corrupted = true }, Apply(support, 3));
        Assert.Equal(1, items.GemLevel(Apply(support, 1, increase: false)));
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
