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

    static bool HasAvailableReward(Player player, NpcCatalog.Npc npc, int offer)
    {
        var flags = Quests.Names(PlayerQuestFlags(player));
        if (!GameEvents.UsesNpcLeagueFlags(player, npc))
            return Npcs.HasAvailableReward(npc, offer, flags);
        HashSet<int> leagueFlags = [.. player.Character.LeagueQuestFlags];
        return Npcs.HasAvailableReward(npc, offer, flags, leagueFlags);
    }

    internal static async Task ClaimNpcReward(Player player, uint itemId,
        byte row = PlayerInventories.MainInventory)
    {
        if (player.NpcRewardOffer is not int offer || !player.NpcRewardItems.TryGetValue(itemId, out var reward)) return;
        SpawnedNpc? npc;
        lock (player.Npcs) npc = player.Npcs.GetValueOrDefault(player.TalkingTo);
        if (npc is null || !HasAvailableReward(player, npc.Npc, offer)) return;
        var catalog = QuestRewardCatalog.Load(hardMode: LeagueFor(player).HardMode, supportGems: LeagueFor(player).Rules.SupportGemRewards);
        if (catalog.Find(offer) is not { } data) return;
        var items = ItemsOf(player);
        var quest = ItemsFor(player).ByHash(reward.TypeHash)?.Quest;
        // A league quest flag taken flag is read and set in the account's stash inside the claim's transaction.
        var claimed = QuestRewardCatalog.Claim(CharacterName(player), data.TakenFlag, quest, items, reward, ShapeOf, row,
            expandedAllowed: ExpandedInventoryGranted(player), leagueFlag: data.TakenLeagueFlag,
            leagueFlags: data.TakenLeagueFlag is null ? null : PersonalStashOf(player), reclaimable: data.Reclaimable);
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
        if (quest is not null) takenFlags.AddRange(new[] { quest.Grant, quest.Extra, quest.Extra2 }.OfType<string>());
        if (takenFlags.Count > 0) await SetQuestFlags(player, takenFlags);
        // The claim saved the league flag already; this applies it in memory and sends it.
        if (data.TakenLeagueFlag is int league) await SetLeagueQuestFlags(player, [league]);
        player.InNpcTalk = false; player.NpcFinishFlag = null; player.NpcFinishLeagueFlag = null; player.TalkingTo = 0;
        await Send(player, InstanceNpcs.Talk(0), "0x0084 quest reward claimed: dialogue ended");
    }

    internal static async Task ClaimAllNpcRewards(Player player)
    {
        if (player.NpcRewardOffer is not int offer || player.NpcRewardItems.Count == 0) return;
        SpawnedNpc? npc;
        lock (player.Npcs) npc = player.Npcs.GetValueOrDefault(player.TalkingTo);
        if (npc is null || !HasAvailableReward(player, npc.Npc, offer)) return;
        var catalog = QuestRewardCatalog.Load(hardMode: LeagueFor(player).HardMode, supportGems: LeagueFor(player).Rules.SupportGemRewards);
        if (catalog.Find(offer) is not { Reclaimable: true } data) return;
        var items = ItemsOf(player);
        var itemCatalog = ItemsFor(player);
        var rewards = player.NpcRewardItems
            .OrderBy(kv => kv.Key)
            .Select(kv => (kv.Key, kv.Value, itemCatalog.ByHash(kv.Value.TypeHash)?.Quest))
            .ToArray();
        var claimed = QuestRewardCatalog.ClaimAll(CharacterName(player), data.TakenFlag, items, rewards, ShapeOf,
            expandedAllowed: ExpandedInventoryGranted(player), leagueFlag: data.TakenLeagueFlag,
            leagueFlags: data.TakenLeagueFlag is null ? null : PersonalStashOf(player), reclaimable: data.Reclaimable);
        if (claimed is null) return;
        if (claimed.AddedMain.Count > 0 || claimed.RemovedMainIds.Count > 0)
            await Send(player, InstanceItems.InventoryChange(PlayerInventories.InventoryId(PlayerInventories.MainInventory),
                items.NextRevision(PlayerInventories.MainInventory), claimed.RemovedMainIds, claimed.AddedMain),
                $"0x00bf claimed {claimed.AddedMain.Count} quest reward(s) (take all)");
        if (claimed.AddedExpanded.Count > 0 || claimed.RemovedExpandedIds.Count > 0)
            await Send(player, InstanceItems.InventoryChange(PlayerInventories.InventoryId(PlayerInventories.ExpandedInventory),
                items.NextRevision(PlayerInventories.ExpandedInventory), claimed.RemovedExpandedIds, claimed.AddedExpanded),
                $"0x00bf claimed {claimed.AddedExpanded.Count} quest reward(s) into expanded inventory (take all)");
        await Send(player, InstanceItems.InventoryChange(0x600, 1, [.. player.NpcRewardItems.Keys], [], NpcShops.Manager),
            $"0x00bf quest reward offer {offer}: all choices removed (take all)");
        player.NpcRewardOffer = null; player.NpcRewardItems.Clear();
        if (claimed.GrantedFlags.Count > 0) await SetQuestFlags(player, claimed.GrantedFlags);
        if (claimed.LeagueFlagSet && data.TakenLeagueFlag is int league) await SetLeagueQuestFlags(player, [league]);
        player.InNpcTalk = false; player.NpcFinishFlag = null; player.NpcFinishLeagueFlag = null; player.TalkingTo = 0;
        await Send(player, InstanceNpcs.Talk(0), "0x0084 quest rewards claimed (take all): dialogue ended");
    }
}
