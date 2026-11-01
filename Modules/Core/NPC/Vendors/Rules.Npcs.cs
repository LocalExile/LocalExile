using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

static partial class VendorsRules
{
    internal static readonly Lazy<VendorRecipes> VendorSales = new(() => new(Items));

    internal static async Task ClaimNpcReward(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, uint itemId)
    {
        if (conn.NpcRewardOffer is not int offer || !conn.NpcRewardItems.TryGetValue(itemId, out var reward)) return;
        SpawnedNpc? npc;
        lock (conn.Npcs) npc = conn.Npcs.GetValueOrDefault(conn.TalkingTo);
        if (npc is null || !Npcs.Value.HasAvailableReward(npc.Npc, offer, Quests.Names(PlayerQuestFlags(conn)))) return;
        var catalog = QuestRewardCatalog.Load(hardMode: LeagueFor(conn).HardMode, supportGems: LeagueFor(conn).Rules.SupportGemRewards);
        if (catalog.Find(offer) is not { } data) return;
        var items = ItemsOf(conn);
        var claimed = QuestRewardCatalog.Claim(CharacterName(conn), data.TakenFlag, items, reward, ShapeOf);
        if (claimed is null) return;
        await Send(remote, stream, ciphers, InstanceItems.InventoryChange(PlayerInventories.InventoryId(PlayerInventories.MainInventory),
            items.NextRevision(PlayerInventories.MainInventory), claimed.RemovedIds, claimed.AddedItems), "0x00bf claimed quest reward");
        // Empty the offer inventory so the native reward window finishes its pending purchase and closes.
        await Send(remote, stream, ciphers, InstanceItems.InventoryChange(0x600, 1, [.. conn.NpcRewardItems.Keys], [], NpcShops.Manager),
            $"0x00bf quest reward offer {offer}: all choices removed");
        conn.NpcRewardOffer = null; conn.NpcRewardItems.Clear();
        await SetQuestFlags(remote, stream, ciphers, conn, [data.TakenFlag]);
        conn.InNpcTalk = false; conn.NpcFinishFlag = null; conn.TalkingTo = 0;
        await Send(remote, stream, ciphers, InstanceNpcs.Talk(0), "0x0084 quest reward claimed: dialogue ended");
    }
}
