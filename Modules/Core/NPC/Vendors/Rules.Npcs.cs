using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

static partial class VendorsRules
{
    /// <summary>Fills this type's game data at startup (the world loader, before the Gateway listens).</summary>
    public static void LoadRulesNpcsData()
    {
        VendorSales = new(Items);
    }

    internal static VendorRecipes VendorSales = default!;

    internal static async Task ClaimNpcReward(Player conn, uint itemId,
        byte row = PlayerInventories.MainInventory)
    {
        if (conn.NpcRewardOffer is not int offer || !conn.NpcRewardItems.TryGetValue(itemId, out var reward)) return;
        SpawnedNpc? npc;
        lock (conn.Npcs) npc = conn.Npcs.GetValueOrDefault(conn.TalkingTo);
        if (npc is null || !Npcs.HasAvailableReward(npc.Npc, offer, Quests.Names(PlayerQuestFlags(conn)))) return;
        var catalog = QuestRewardCatalog.Load(hardMode: LeagueFor(conn).HardMode, supportGems: LeagueFor(conn).Rules.SupportGemRewards);
        if (catalog.Find(offer) is not { } data) return;
        var items = ItemsOf(conn);
        var quest = ItemsFor(conn).ByHash(reward.TypeHash)?.Quest;
        var claimed = QuestRewardCatalog.Claim(CharacterName(conn), data.TakenFlag, quest, items, reward, ShapeOf, row,
            expandedAllowed: ExpandedInventoryGranted(conn));
        if (claimed is null) return;
        await Send(conn, InstanceItems.InventoryChange(PlayerInventories.InventoryId(row),
            items.NextRevision(row), claimed.RemovedIds, claimed.AddedItems), "0x00bf claimed quest reward");
        // Empty the offer inventory so the native reward window finishes its pending purchase and closes.
        await Send(conn, InstanceItems.InventoryChange(0x600, 1, [.. conn.NpcRewardItems.Keys], [], NpcShops.Manager),
            $"0x00bf quest reward offer {offer}: all choices removed");
        conn.NpcRewardOffer = null; conn.NpcRewardItems.Clear();
        // The taken flag (or, for a quest item, its pickup flags: an Act 1 book's HaveRewardBook), in memory and to the client.
        var takenFlags = new List<string>();
        if (data.TakenFlag is { } taken) takenFlags.Add(taken);
        if (quest is not null) takenFlags.AddRange(new[] { quest.Grant, quest.Extra }.OfType<string>());
        if (takenFlags.Count > 0) await SetQuestFlags(conn, takenFlags);
        conn.InNpcTalk = false; conn.NpcFinishFlag = null; conn.TalkingTo = 0;
        await Send(conn, InstanceNpcs.Talk(0), "0x0084 quest reward claimed: dialogue ended");
    }
}
