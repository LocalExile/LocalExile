using System.Text.Json;
using GameServer.Core.Protocol.Instance;

/// <summary>Native ItemCosts, selected by item level and rarity; explicit prices remain authoritative.</summary>
sealed class VendorPrices
{
    readonly Dictionary<int, List<(int Level, int Cost)>> levels = [];
    readonly Dictionary<int, int> mapCosts = [];
    readonly List<(uint Currency, int Amount)[][]> costs = [];
    readonly ItemCatalog catalog;
    public VendorPrices(ItemCatalog items, bool hardMode = false, string folder = "data")
    {
        catalog = items;
        using var c = GameTables.Open(Path.Combine(folder, "tables/ItemCosts.json"));
        foreach (var row in c.RootElement.EnumerateArray())
            costs.Add(Enumerable.Range(1, 4).Select(rarity => row.GetProperty($"Cost{rarity}Currencies").EnumerateArray()
                .Zip(row.GetProperty($"Cost{rarity}Values").EnumerateArray(), (currency, amount) =>
                    (items.Row(currency.GetInt32())?.Hash ?? 0, amount.GetInt32())).ToArray()).ToArray());
        using var l = GameTables.Open(Path.Combine(folder, "tables/ItemCostPerLevel.json"));
        foreach (var row in l.RootElement.EnumerateArray())
        {
            int b = row.GetProperty("BaseItem").GetInt32();
            int cost = hardMode && row.GetProperty("CostHardmode") is { ValueKind: JsonValueKind.Number } h
                ? h.GetInt32() : row.GetProperty("Cost").GetInt32();
            if (!levels.TryGetValue(b, out var entries)) levels[b] = entries = [];
            entries.Add((row.GetProperty("Level").GetInt32(), cost));
        }
        using var m = GameTables.Open(Path.Combine(folder, "tables/MapPurchaseCosts.json"));
        foreach (var row in m.RootElement.EnumerateArray())
            mapCosts[row.GetProperty("Tier").GetInt32()] = row.GetProperty(hardMode ? "CostHardmode" : "Cost").GetInt32();
    }

    public Item? Price(Item item)
    {
        if (item.Price.Count > 0) return item;
        if (catalog.ByHash(item.TypeHash) is not { } type) return null;
        int level = item.Components.Contains("Mods") ? item.ItemLevel : 1;
        int cost = levels.GetValueOrDefault(type.Row)?.Where(e => e.Level <= level)
            .OrderByDescending(e => e.Level).Select(e => (int?)e.Cost).FirstOrDefault() ?? -1;
        int rarity = (int)item.Rarity;
        (uint Currency, int Amount)[] raw = cost >= 0 && cost < costs.Count ? costs[cost][rarity] : [];
        if (raw.Length == 0 && type.Id.StartsWith("Metadata/Items/Maps/MapKeyTier")
            && int.TryParse(type.Id["Metadata/Items/Maps/MapKeyTier".Length..], out int tier)
            && mapCosts.TryGetValue(tier, out cost)) raw = costs[cost][rarity];
        if (raw.Length == 0 || raw.Any(p => p.Currency == 0 || p.Amount == 0)) return null;
        var price = raw.GroupBy(p => p.Currency).ToDictionary(g => g.Key,
            g => checked((uint)g.Sum(p => p.Amount > 0 ? (long)p.Amount * item.StackSize : -(long)p.Amount)));
        if (!item.Identified && item.Rarity is ItemRarity.Magic or ItemRarity.Rare)
        {
            var extra = catalog.Find(item.Rarity == ItemRarity.Magic ? "Scroll of Wisdom" : "Orb of Chance")!;
            price[extra.Hash] = price.GetValueOrDefault(extra.Hash) + 1;
        }
        return item with { Price = [.. price.Select(p => (p.Key, p.Value))] };
    }
}
