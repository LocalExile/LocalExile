using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

static partial class VendorsRules
{
    internal static readonly Lazy<VendorRecipes> VendorSales = new(() => new(Items));

    static bool HasAvailableReward(ClientState conn, NpcCatalog.Npc npc, int offer)
    {
        var flags = Quests.Names(PlayerQuestFlags(conn));
        if (!GameEvents.UsesNpcLeagueFlags(conn, npc))
            return Npcs.Value.HasAvailableReward(npc, offer, flags);
        HashSet<int> leagueFlags;
        lock (conn.Monsters) leagueFlags = [.. conn.LeagueQuestFlags];
        return Npcs.Value.HasAvailableReward(npc, offer, flags, leagueFlags);
    }

    internal static async Task ClaimNpcReward(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, uint itemId,
        byte row = PlayerInventories.MainInventory)
    {
        if (conn.NpcRewardOffer is not int offer || !conn.NpcRewardItems.TryGetValue(itemId, out var reward)) return;
        SpawnedNpc? npc;
        lock (conn.Npcs) npc = conn.Npcs.GetValueOrDefault(conn.TalkingTo);
        if (npc is null || !HasAvailableReward(conn, npc.Npc, offer)) return;
        var catalog = QuestRewardCatalog.Load(hardMode: LeagueFor(conn).HardMode, supportGems: LeagueFor(conn).Rules.SupportGemRewards);
        if (catalog.Find(offer) is not { } data) return;
        var items = ItemsOf(conn);
        var quest = ItemsFor(conn).ByHash(reward.TypeHash)?.Quest;
        // A league quest flag taken flag is read and set in the account's stash inside the claim's transaction.
        var claimed = QuestRewardCatalog.Claim(CharacterName(conn), data.TakenFlag, quest, items, reward, ShapeOf, row,
            expandedAllowed: ExpandedInventoryGranted(conn), leagueFlag: data.TakenLeagueFlag,
            leagueFlags: data.TakenLeagueFlag is null ? null : PersonalStashOf(conn), reclaimable: data.Reclaimable);
        if (claimed is null) return;
        await Send(remote, stream, ciphers, InstanceItems.InventoryChange(PlayerInventories.InventoryId(row),
            items.NextRevision(row), claimed.RemovedIds, claimed.AddedItems), "0x00bf claimed quest reward");
        // Empty the offer inventory so the native reward window finishes its pending purchase and closes.
        await Send(remote, stream, ciphers, InstanceItems.InventoryChange(0x600, 1, [.. conn.NpcRewardItems.Keys], [], NpcShops.Manager),
            $"0x00bf quest reward offer {offer}: all choices removed");
        conn.NpcRewardOffer = null; conn.NpcRewardItems.Clear();
        // The taken flag (or, for a quest item, its pickup flags: an Act 1 book's HaveRewardBook), in memory and to the client.
        var takenFlags = new List<string>();
        if (data.TakenFlag is { } taken) takenFlags.Add(taken);
        if (quest is not null) takenFlags.AddRange(new[] { quest.Grant, quest.Extra }.OfType<string>());
        if (takenFlags.Count > 0) await SetQuestFlags(remote, stream, ciphers, conn, takenFlags);
        // The claim saved the league flag already; this applies it in memory and sends it.
        if (data.TakenLeagueFlag is int league) await SetLeagueQuestFlags(remote, stream, ciphers, conn, [league]);
        conn.InNpcTalk = false; conn.NpcFinishFlag = null; conn.NpcFinishLeagueFlag = null; conn.TalkingTo = 0;
        await Send(remote, stream, ciphers, InstanceNpcs.Talk(0), "0x0084 quest reward claimed: dialogue ended");
    }

    internal static async Task ClaimAllNpcRewards(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
    {
        if (conn.NpcRewardOffer is not int offer || conn.NpcRewardItems.Count == 0) return;
        SpawnedNpc? npc;
        lock (conn.Npcs) npc = conn.Npcs.GetValueOrDefault(conn.TalkingTo);
        if (npc is null || !HasAvailableReward(conn, npc.Npc, offer)) return;
        var catalog = QuestRewardCatalog.Load(hardMode: LeagueFor(conn).HardMode, supportGems: LeagueFor(conn).Rules.SupportGemRewards);
        if (catalog.Find(offer) is not { Reclaimable: true } data) return;
        var items = ItemsOf(conn);
        var itemCatalog = ItemsFor(conn);
        var rewards = conn.NpcRewardItems
            .OrderBy(kv => kv.Key)
            .Select(kv => (kv.Key, kv.Value, itemCatalog.ByHash(kv.Value.TypeHash)?.Quest))
            .ToArray();
        var claimed = QuestRewardCatalog.ClaimAll(CharacterName(conn), data.TakenFlag, items, rewards, ShapeOf,
            expandedAllowed: ExpandedInventoryGranted(conn), leagueFlag: data.TakenLeagueFlag,
            leagueFlags: data.TakenLeagueFlag is null ? null : PersonalStashOf(conn), reclaimable: data.Reclaimable);
        if (claimed is null) return;
        if (claimed.AddedMain.Count > 0 || claimed.RemovedMainIds.Count > 0)
            await Send(remote, stream, ciphers, InstanceItems.InventoryChange(PlayerInventories.InventoryId(PlayerInventories.MainInventory),
                items.NextRevision(PlayerInventories.MainInventory), claimed.RemovedMainIds, claimed.AddedMain),
                $"0x00bf claimed {claimed.AddedMain.Count} quest reward(s) (take all)");
        if (claimed.AddedExpanded.Count > 0 || claimed.RemovedExpandedIds.Count > 0)
            await Send(remote, stream, ciphers, InstanceItems.InventoryChange(PlayerInventories.InventoryId(PlayerInventories.ExpandedInventory),
                items.NextRevision(PlayerInventories.ExpandedInventory), claimed.RemovedExpandedIds, claimed.AddedExpanded),
                $"0x00bf claimed {claimed.AddedExpanded.Count} quest reward(s) into expanded inventory (take all)");
        await Send(remote, stream, ciphers, InstanceItems.InventoryChange(0x600, 1, [.. conn.NpcRewardItems.Keys], [], NpcShops.Manager),
            $"0x00bf quest reward offer {offer}: all choices removed (take all)");
        conn.NpcRewardOffer = null; conn.NpcRewardItems.Clear();
        if (claimed.GrantedFlags.Count > 0) await SetQuestFlags(remote, stream, ciphers, conn, claimed.GrantedFlags);
        if (claimed.LeagueFlagSet && data.TakenLeagueFlag is int league) await SetLeagueQuestFlags(remote, stream, ciphers, conn, [league]);
        conn.InNpcTalk = false; conn.NpcFinishFlag = null; conn.NpcFinishLeagueFlag = null; conn.TalkingTo = 0;
        await Send(remote, stream, ciphers, InstanceNpcs.Talk(0), "0x0084 quest rewards claimed (take all): dialogue ended");
    }
}
