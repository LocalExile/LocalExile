using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

static partial class Game
{
    internal static async Task NativeReclaimSkin(string remote, NetworkStream stream, ConnectionCiphers ciphers,
        ClientState conn, InstanceSkinTransfers.Reclaim pick)
    {
        string why = "Invalid skin-transfer inventory";
        var change = PlayerInventories.Row(pick.InventoryId) is byte row
            ? ItemsOf(conn).UnbindSkin(pick.ItemId, out why, row) : null;
        Console.WriteLine($"[gateway] {remote} <= 0x0122 reclaim skin {pick}: {(change is null ? why : "returned B")}");
        if (change is not null) await SendSkinTransfer(remote, stream, ciphers, conn, change, bind: false);
        else await RefuseSkinTransfer(remote, stream, ciphers, conn, why, [(pick.InventoryId, pick.ItemId)]);
    }

    internal static async Task NativeSkinTransfer(string remote, NetworkStream stream, ConnectionCiphers ciphers,
        ClientState conn, InstanceSkinTransfers.Bind pick)
    {
        string why = "Invalid skin-transfer record or inventory";
        var items = ItemsOf(conn);
        var change = Mtx.Find(pick.RecordId)?.MtxTypeHash == InstanceSkinTransfers.MtxTypeHash
            && PlayerInventories.Row(pick.SourceInventoryId) is byte sourceRow
            && PlayerInventories.Row(pick.TargetInventoryId) is byte targetRow
            ? items.BindSkin(pick.TargetId, targetRow, sourceRow, pick.SourceId, ShapeOf, out why) : null;
        Console.WriteLine($"[gateway] {remote} <= 0x0207 skin transfer {pick}: {(change is null ? why : "bound")}");
        if (change is not null) await SendSkinTransfer(remote, stream, ciphers, conn, change, bind: true);
        else await RefuseSkinTransfer(remote, stream, ciphers, conn, why,
            [(pick.TargetInventoryId, pick.TargetId), (pick.SourceInventoryId, pick.SourceId)]);
    }

    static async Task RefuseSkinTransfer(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        string why, (uint Inventory, uint Item)[] requested)
    {
        await Send(remote, stream, ciphers, InstanceChat.ChatLine(why), "skin transfer refused");
        // The native picker marks its selected items pending. Reconcile them even when the request is stale/refused.
        var items = ItemsOf(conn);
        foreach (var group in requested.GroupBy(i => i.Inventory))
            if (PlayerInventories.Row(group.Key) is byte row)
            {
                var ids = group.Select(i => i.Item).Distinct().ToArray();
                await Send(remote, stream, ciphers, InstanceItems.InventoryChange(group.Key, items.NextRevision(row), ids,
                    items.Items(row).Where(i => ids.Contains(i.Id)).ToArray()), "skin transfer: reconcile refused pick");
            }
    }

    public static async Task SendSkinTransfer(string remote, NetworkStream stream, ConnectionCiphers ciphers,
        ClientState conn, PlayerInventories.SkinTransferChange change, bool bind)
    {
        var items = ItemsOf(conn);
        bool same = bind && change.Row == change.SourceRow;
        await Send(remote, stream, ciphers, InstanceItems.InventoryChange(PlayerInventories.InventoryId(change.Row),
            items.NextRevision(change.Row), same ? [change.Target.Id, change.Source.Id] : [change.Target.Id],
            [change.Target]), "skin transfer: update A");
        if (!same)
            await Send(remote, stream, ciphers, InstanceItems.InventoryChange(PlayerInventories.InventoryId(change.SourceRow),
                items.NextRevision(change.SourceRow), bind ? [change.Source.Id] : [], bind ? [] : [change.Source]),
                bind ? "skin transfer: bind B" : "skin transfer: return B");
        await SendEquipment(remote, stream, ciphers, conn, items);
    }
}
