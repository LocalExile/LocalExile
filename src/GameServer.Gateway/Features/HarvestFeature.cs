using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;
using GameServer.Modding;
using static Program;

/// <summary>Horticrafting, as a built-in module (Program.Mods.cs): the station's recipe browser and its crafts. The hideout's scene
/// places the station while this module is loaded.</summary>
sealed class HarvestFeature : IModule
{
    public const string Id = "harvest";

    public void Load(IModuleContext mod)
    {
        mod.OnObjectUsed(async use =>
        {
            var conn = ConnOf(use.Player);
            if (conn.HorticraftingObject == 0 || use.ObjectId != conn.HorticraftingObject) return false;
            var (remote, stream, ciphers) = conn.Wire;
            await OpenHorticrafting(remote, stream, ciphers, conn, use.RequestedAt);
            return true;
        });
        mod.OnPacket(InstanceHarvest.CraftId, async packet =>
        {
            var conn = ConnOf(packet.Player);
            var (remote, stream, ciphers) = conn.Wire;
            await CraftHarvest(remote, stream, ciphers, conn, InstanceHarvest.ReadCraft(packet.Bytes.ToArray()));
            return true;
        });
    }

    // Opens the native recipe browser. Harvest recipe execution is a separate feature.
    static async Task OpenHorticrafting(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, double requestedAt)
    {
        if (!TryBeginObjectWalk(conn, requestedAt, conn.HorticraftingAt.X, conn.HorticraftingAt.Y, conn.StationaryInteractionSize(conn.HorticraftingObject), out var objectWalk)) return;
        var (action, now, _, stop, arrive) = objectWalk;
        if (arrive > now) await Task.Delay(TimeSpan.FromSeconds(arrive - now));
        if (conn.Closed || !PlayerActionCurrent(conn, action) || !PlayerWalk.InWalkUpRange(stop, conn.HorticraftingAt, conn.StationaryInteractionSize(conn.HorticraftingObject))) return;
        conn.BenchOpen = false;
        conn.HarvestOpen = true;
        await RefreshBenchBalance(remote, stream, ciphers, conn, clear: true);
        await Send(remote, stream, ciphers, InstanceWaypoints.OpenUi(InstanceWaypoints.HorticraftingUi),
            "0x007b open Horticrafting Station recipe browser", still: () => PlayerActionCurrent(conn, action));
    }

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
