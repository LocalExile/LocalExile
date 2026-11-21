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

    internal static async Task ClaimNpcReward(Player player, uint itemId,
        byte row = PlayerInventories.MainInventory)
    {
        if (player.NpcRewardOffer is not int offer || !player.NpcRewardItems.TryGetValue(itemId, out var reward)) return;
        SpawnedNpc? npc;
        lock (player.Npcs) npc = player.Npcs.GetValueOrDefault(player.TalkingTo);
        if (npc is null || !Npcs.HasAvailableReward(npc.Npc, offer, Quests.Names(PlayerQuestFlags(player)))) return;
        var catalog = QuestRewardCatalog.Load(hardMode: LeagueFor(player).HardMode, supportGems: LeagueFor(player).Rules.SupportGemRewards);
        if (catalog.Find(offer) is not { } data) return;
        var items = ItemsOf(player);
        var quest = ItemsFor(player).ByHash(reward.TypeHash)?.Quest;
        var claimed = QuestRewardCatalog.Claim(CharacterName(player), data.TakenFlag, quest, items, reward, ShapeOf, row,
            expandedAllowed: ExpandedInventoryGranted(player));
        if (claimed is null) return;
        await Send(player, InstanceItems.InventoryChange(PlayerInventories.InventoryId(row),
            items.NextRevision(row), claimed.RemovedIds, claimed.AddedItems), "0x00bf claimed quest reward");
        // Empty the offer inventory so the native reward window finishes its pending purchase and closes.
        await Send(player, InstanceItems.InventoryChange(0x600, 1, [.. player.NpcRewardItems.Keys], [], NpcShops.Manager),
            $"0x00bf quest reward offer {offer}: all choices removed");
        player.NpcRewardOffer = null; player.NpcRewardItems.Clear();
        // The taken flag (or, for a quest item, its pickup flags: an Act 1 book's HaveRewardBook), in memory and to the client.
        var takenFlags = new List<string>();
        if (data.TakenFlag is { } taken) takenFlags.Add(taken);
        if (quest is not null) takenFlags.AddRange(new[] { quest.Grant, quest.Extra }.OfType<string>());
        if (takenFlags.Count > 0) await SetQuestFlags(player, takenFlags);
        player.InNpcTalk = false; player.NpcFinishFlag = null; player.TalkingTo = 0;
        await Send(player, InstanceNpcs.Talk(0), "0x0084 quest reward claimed: dialogue ended");
    }
}
