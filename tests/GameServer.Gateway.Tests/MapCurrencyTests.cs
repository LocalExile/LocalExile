using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public sealed class MapCurrencyTests
{
    static readonly Lazy<ItemCatalog> Catalog = new(() => new ItemCatalog("data"));
    static ItemCatalog Items => Catalog.Value;
    static ItemRolls Rolls => new(Items);

    static ItemCatalog.BaseType MapBase =>
        Items.All.FirstOrDefault(t => t.Supported && CurrencyEffects.IsMap(t))
        ?? throw new InvalidOperationException(Items.Error ?? "no Map/MapKey in data (need data/item-types.json)");

    [Fact]
    public void EveryMapQualityChiselUsesAlternateQualityTypes()
    {
        Assert.True(string.IsNullOrEmpty(Items.Error), Items.Error);
        var chisels = Items.All.Where(t => Items.CurrencyAction(t) == "add_map_alt_quality").ToList();
        Assert.True(chisels.Count >= 6, $"only {chisels.Count} add_map_alt_quality currencies");
        Assert.All(chisels, t => Assert.True(Items.AlternateQualityOf(t) is not null, t.Name));
        Assert.All(chisels, t => Assert.True(CurrencyEffects.HandlesAction(Items.CurrencyAction(t)), t.Name));
        Assert.All(chisels, t => Assert.Contains(MapVendorStock.Catalogue(), l => l.Row == t.Row && l.Tab == "Currency"));
    }

    [Fact]
    public void VaalCorruptsAWhiteMapAndMakesItRare()
    {
        var map = MapBase;
        Assert.True(CurrencyEffects.IsMap(map), map.Id);
        Assert.True(CurrencyEffects.TryAction(Items, Rolls,
            ItemCatalog.Create(map) with { ItemLevel = (byte)Math.Clamp(map.DropLevel, 1, 100) },
            Items.All.First(t => Items.CurrencyAction(t) is "corrupt_item"),
            new Random(1), out var vaal));
        Assert.NotNull(vaal);
        Assert.True(vaal!.Corrupted);
        Assert.Equal(ItemRarity.Rare, vaal.Rarity);
        Assert.Contains(vaal.Mods, m => m.Section == ModSection.Explicit);
    }

    [Fact]
    public void AMapQualityChiselSetsTheTablesQualityType()
    {
        var map = MapBase;
        var chisels = Items.All.Where(t => Items.CurrencyAction(t) == "add_map_alt_quality"
            && Items.AlternateQualityOf(t) is not null).ToList();
        Assert.True(chisels.Count >= 6, $"only {chisels.Count} map quality chisels");
        foreach (var chisel in chisels)
        {
            Assert.True(CurrencyEffects.TryAction(Items, Rolls, ItemCatalog.Create(map), chisel, new Random(1), out var chiselled));
            Assert.NotNull(chiselled);
            Assert.Equal(1, chiselled!.Quality);
            Assert.Equal(Items.AlternateQualityOf(chisel), chiselled.AlternateQualityType);
            Assert.Null(CurrencyEffects.Chisel(chiselled with { Quality = 20 }, map, chiselled.AlternateQualityType!.Value));
        }
    }
}
