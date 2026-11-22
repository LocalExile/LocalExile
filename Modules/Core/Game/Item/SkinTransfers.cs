using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

public static partial class Game
{
    internal static async Task NativeReclaimSkin(Player player, InstanceSkinTransfers.Reclaim pick)
    {
        string why = "Invalid skin-transfer inventory";
        var change = PlayerInventories.Row(pick.InventoryId) is byte row
            ? ItemsOf(player).UnbindSkin(pick.ItemId, out why, row) : null;
        Console.WriteLine($"[gateway] {player.Connection.Remote} <= 0x0122 reclaim skin {pick}: {(change is null ? why : "returned B")}");
        if (change is not null) await SendSkinTransfer(player, change, bind: false);
        else await RefuseSkinTransfer(player, why, [(pick.InventoryId, pick.ItemId)]);
    }

    internal static async Task NativeSkinTransfer(Player player, InstanceSkinTransfers.Bind pick)
    {
        string why = "Invalid skin-transfer record or inventory";
        var items = ItemsOf(player);
        var change = Mtx.Find(pick.RecordId)?.MtxTypeHash == InstanceSkinTransfers.MtxTypeHash
            && PlayerInventories.Row(pick.SourceInventoryId) is byte sourceRow
            && PlayerInventories.Row(pick.TargetInventoryId) is byte targetRow
            ? items.BindSkin(pick.TargetId, targetRow, sourceRow, pick.SourceId, ShapeOf, out why) : null;
        Console.WriteLine($"[gateway] {player.Connection.Remote} <= 0x0207 skin transfer {pick}: {(change is null ? why : "bound")}");
        if (change is not null) await SendSkinTransfer(player, change, bind: true);
        else await RefuseSkinTransfer(player, why,
            [(pick.TargetInventoryId, pick.TargetId), (pick.SourceInventoryId, pick.SourceId)]);
    }

    static async Task RefuseSkinTransfer(Player player, string why, (uint Inventory, uint Item)[] requested)
    {
        await Send(player, InstanceChat.ChatLine(why), "skin transfer refused");
        // The native picker marks its selected items pending. Reconcile them even when the request is stale/refused.
        var items = ItemsOf(player);
        foreach (var group in requested.GroupBy(i => i.Inventory))
            if (PlayerInventories.Row(group.Key) is byte row)
            {
                var ids = group.Select(i => i.Item).Distinct().ToArray();
                await Send(player, InstanceItems.InventoryChange(group.Key, items.NextRevision(row), ids,
                    items.Items(row).Where(i => ids.Contains(i.Id)).ToArray()), "skin transfer: reconcile refused pick");
            }
    }

    public static async Task SendSkinTransfer(Player player, PlayerInventories.SkinTransferChange change, bool bind)
    {
        var items = ItemsOf(player);
        bool same = bind && change.Row == change.SourceRow;
        await Send(player, InstanceItems.InventoryChange(PlayerInventories.InventoryId(change.Row),
            items.NextRevision(change.Row), same ? [change.Target.Id, change.Source.Id] : [change.Target.Id],
            [change.Target]), "skin transfer: update A");
        if (!same)
            await Send(player, InstanceItems.InventoryChange(PlayerInventories.InventoryId(change.SourceRow),
                items.NextRevision(change.SourceRow), bind ? [change.Source.Id] : [], bind ? [] : [change.Source]),
                bind ? "skin transfer: bind B" : "skin transfer: return B");
        await SendEquipment(player, items);
    }
}
