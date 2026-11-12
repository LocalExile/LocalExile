using System.Text.Json;
using GameServer.Core;

/// <summary>The copied client's HardMode purchase costs. Unmodelled/multi-currency costs are omitted, never free.</summary>
public sealed class HardModeShopPrices
{
    readonly Dictionary<(int Base, int Level), (uint Currency, uint Amount)> _prices = [];
    readonly Dictionary<int, (uint Currency, uint Amount)> _maps = [];

    public HardModeShopPrices(ItemCatalog items, string folder = "data")
    {
        ClientTable Read(string name) => ClientTables.Table(name);
        var costs = Read("ItemCosts");
        var levels = Read("ItemCostPerLevel");
        var maps = Read("Maps");
        var mapCosts = Read("MapPurchaseCosts");
        (uint Currency, uint Amount)? Price(int row)
        {
            if (row < 0 || row >= costs.Count) return null;
            var cost = costs[row];
            var currencies = cost.Ints("Cost1Currencies").ToArray();
            var amounts = cost.Ints("Cost1Values").ToArray();
            if (currencies.Length != 1 || amounts.Length != 1 || amounts[0] <= 0
                || items.Row(currencies[0]) is not { Supported: true } currency) return null;
            return (currency.Hash, (uint)amounts[0]);
        }
        foreach (var row in levels)
        {
            int cost = row.IntOrNull("CostHardmode") is int hard
                ? hard : row.Int("Cost");
            if (Price(cost) is { } price) _prices.TryAdd((row.Int("BaseItem"), row.Int("Level")), price);
        }
        var byTier = mapCosts.ToDictionary(r => r.Int("Tier"),
            r => r.Int("CostHardmode"));
        foreach (var map in maps)
            if (byTier.TryGetValue(map.Int("Tier"), out int cost) && Price(cost) is { } price)
                _maps.TryAdd(map.Int("BaseItemTypesKey"), price);
    }

    public (uint Currency, uint Amount)? For(ItemCatalog.BaseType type) => type.Class == "Map"
        ? _maps.GetValueOrDefault(type.Row) is var map && map.Amount > 0 ? map : null
        : _prices.GetValueOrDefault((type.Row, 1)) is var price && price.Amount > 0 ? price : null;
}
