using GameServer.Core.Protocol.Instance;


/// <summary>A client-only view of the personal stash's crafting currencies. Never saved or spendable as items.</summary>
static class BenchBalance
{
    public static IReadOnlyList<InventoryItem> Snapshot(AccountStash stash, ItemCatalog catalog, BenchCrafting bench)
    {
        var hashes = bench.All.SelectMany(r => r.Costs).Select(c => c.Currency).ToHashSet();
        if (catalog.Find(CurrencyEffects.VaalOrb) is { } vaal) hashes.Add(vaal.Hash);
        return DisplayStacks(catalog, hashes.Select(hash => (hash, stash.CountOf(hash))));
    }

    internal static IReadOnlyList<InventoryItem> DisplayStacks(ItemCatalog catalog, IEnumerable<(uint Hash, int Count)> balances)
    {
        var items = new List<InventoryItem>();
        foreach (var (hash, amount) in balances.OrderBy(b => b.Hash))
        {
            if (catalog.ByHash(hash) is not { } type) continue;
            int count = amount;
            while (count > 0)
            {
                int stack = Math.Min(count, ushort.MaxValue);
                int index = items.Count;
                if (index >= 255 * 255) throw new InvalidOperationException("Crafting balance exceeds display capacity");
                items.Add(new(0xef000000u + (uint)index, (byte)(index % 255), (byte)(index / 255),
                    ItemCatalog.Create(type) with { StackSize = (ushort)stack }));
                count -= stack;
            }
        }
        return items;
    }
}
