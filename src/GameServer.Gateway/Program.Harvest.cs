using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

partial class Program
{
    /// <summary>C→S 0x274: execute the HarvestCraftOptions recipe whose HASH16 the native panel sent, on the item in
    /// HarvestCraftingItem (row 43). The item and all lifeforce costs commit together; a refusal spends nothing.</summary>
    static async Task CraftHarvest(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, ushort recipe)
    {
        if (!conn.HarvestOpen || HideoutCatalog.ByArea(conn.Layout.Hash) is null) return;
        var items = ItemsOf(conn);
        var stash = PersonalStashOf(conn);
        var harvest = HarvestFor(conn);
        var name = harvest.ByHash(recipe)?.Id ?? $"0x{recipe:x4}";
        var result = ItemMoves.HarvestAtStation(items, stash, harvest, recipe, Random.Shared);
        if (result is null)
        {
            Console.WriteLine($"[gateway] {remote} <= 0x0274 Harvest recipe {name} refused (item, recipe or lifeforce)");
            await Send(remote, stream, ciphers,
                InstanceHarvest.CraftResult((ushort)(items.Items(PlayerInventories.HarvestCrafting).FirstOrDefault()?.Id ?? 0)),
                "0x0276 Harvest item event (refused)");
            await Send(remote, stream, ciphers, InstanceChat.ChatLine("Harvest craft refused: incompatible item or insufficient lifeforce."),
                "Harvest craft refused");
            return;
        }
        // Clear and refill the slot in separate changes, then send the slot item's 0x276 event: that is what clears the
        // panel's pending state and refreshes the item for another craft.
        uint harvestInventory = PlayerInventories.InventoryId(PlayerInventories.HarvestCrafting);
        await Send(remote, stream, ciphers, InstanceItems.InventoryChange(harvestInventory,
            items.NextRevision(PlayerInventories.HarvestCrafting), [result.Changed.Id], []),
            $"Harvest recipe {name}: remove crafted item {result.Changed.Id} from the slot");
        await Send(remote, stream, ciphers, InstanceItems.InventoryChange(harvestInventory,
            items.NextRevision(PlayerInventories.HarvestCrafting), [], [result.Changed]),
            $"Harvest recipe {name}: crafted item {result.Changed.Id}");
        foreach (var payment in result.Payments)
        {
            if (payment.Inventory is { } paid)
                await Send(remote, stream, ciphers, InstanceItems.InventoryChange(PlayerInventories.InventoryId(PlayerInventories.MainInventory),
                    items.NextRevision(PlayerInventories.MainInventory), [.. paid.Removed, .. paid.Changed.Select(c => c.Id)], paid.Changed),
                    "Harvest lifeforce paid from inventory");
            await SendStashPayment(remote, stream, ciphers, conn, stash, payment.Stash);
        }
        await Send(remote, stream, ciphers, InstanceHarvest.CraftResult((ushort)result.Changed.Id),
            $"0x0276 Harvest item event {result.Changed.Id} ({name})");
    }
}
