using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public class MetacraftTests
{
    [Fact]
    public void Both_locks_prevent_removal_and_value_changes_and_fractures_survive_scour()
    {
        var (catalog, rolls, bench, types) = BenchCraftingTests.Setup.Value;
        var type = types.First(t => t.Class == "One Hand Sword");
        var rng = new Random(14);
        var locks = new[] { "item_generation_cannot_change_prefixes", "item_generation_cannot_change_suffixes" }
            .Select(stat => bench.All.First(r => r.Mod?.Stats.Any(s => s.Id == stat) == true).Mod!.Roll(ModSection.Explicit, rng)).ToArray();
        var item = rolls.Roll(ItemCatalog.Create(type) with { ItemLevel = 100 }, ItemRarity.Rare, rng, (4, 3));
        item = item with { Mods = [.. item.Mods, .. locks] };
        Assert.Null(CurrencyEffects.Scouring(item, rolls));
        Assert.Null(CurrencyEffects.Annulment(item, rolls, rng));
        Assert.Null(rolls.Reforge(item, rng));
        var fractured = item with { Mods = [item.Mods.First(m => m.Section == ModSection.Explicit)], FracturedCount = 1 };
        var scoured = CurrencyEffects.Scouring(fractured, rolls)!;
        Assert.Equal(ItemRarity.Magic, scoured.Rarity);
        Assert.Equal(fractured.Mods, scoured.Mods);
        Assert.Equal(1, scoured.FracturedCount);
    }

}
