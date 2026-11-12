using GameServer.Core;
using System.Text.Json;
using GameServer.Core.Protocol.Instance;

/// <summary>Native ItemCosts, selected by item level and rarity; explicit prices remain authoritative.</summary>
public sealed class VendorPrices
{
    sealed class Modes(ItemCatalog items)
    {
        public readonly Lazy<VendorPrices> Normal = new(() => new(items));
        public readonly Lazy<VendorPrices> Hard = new(() => new(items,true));
    }
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ItemCatalog,Modes> Cache = new();
    public static VendorPrices For(ItemCatalog items, bool hardMode = false)
    {
        var modes = Cache.GetValue(items, static catalog => new(catalog));
        return (hardMode ? modes.Hard : modes.Normal).Value;
    }
    readonly Dictionary<int, List<(int Level, int Cost)>> levels = [];
    readonly Dictionary<int, int> mapCosts = [];
    readonly List<(uint Currency, int Amount)[][]> costs = [];
    readonly ItemCatalog catalog;
    public VendorPrices(ItemCatalog items, bool hardMode = false)
    {
        catalog = items;
        foreach (var row in ClientTables.Table("ItemCosts"))
            costs.Add(Enumerable.Range(1, 4).Select(rarity => row.Ints($"Cost{rarity}Currencies")
                .Zip(row.Ints($"Cost{rarity}Values"), (currency, amount) =>
                    (items.Row(currency)?.Hash ?? 0, amount)).ToArray()).ToArray());
        foreach (var row in ClientTables.Table("ItemCostPerLevel"))
        {
            int b = row.Int("BaseItem");
            int cost = hardMode && row.IntOrNull("CostHardmode") is int h
                ? h : row.Int("Cost");
            if (!levels.TryGetValue(b, out var entries)) levels[b] = entries = [];
            entries.Add((row.Int("Level"), cost));
        }
        foreach (var entries in levels.Values) entries.Sort((a,b) => b.Level.CompareTo(a.Level));
        foreach (var row in ClientTables.Table("MapPurchaseCosts"))
            mapCosts[row.Int("Tier")] = row.Int(hardMode ? "CostHardmode" : "Cost");
    }

    public Item? Price(Item item)
    {
        if (item.Price.Count > 0) return item;
        if (catalog.ByHash(item.TypeHash) is not { } type) return null;
        int level = item.Components.Contains("Mods") ? item.ItemLevel : 1;
        int cost = -1;
        if (levels.TryGetValue(type.Row,out var entries))
            foreach (var entry in entries)
                if (entry.Level <= level) { cost = entry.Cost; break; }
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
