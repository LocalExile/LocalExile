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
        byte row = PlayerInventories.MainInventory, byte? x = null, byte? y = null)
    {
        if (player.NpcRewardOffer is not int offer || !player.NpcRewardItems.TryGetValue(itemId, out var reward)) return;
        SpawnedNpc? npc;
        lock (player.Npcs) npc = player.Npcs.GetValueOrDefault(player.TalkingTo);
        if (npc is null || !HasAvailableReward(player, npc.Npc, offer)) return;
        var catalog = QuestRewardCatalog.Load(hardMode: LeagueFor(player).HardMode, supportGems: LeagueFor(player).Rules.SupportGemRewards);
        if (catalog.Find(offer) is not { } data) return;
        var items = ItemsOf(player);
        var quest = ItemsFor(player).ByHash(reward.TypeHash)?.Quest;
        bool expandedAllowed = ExpandedInventoryGranted(player);
        var leagueStash = data.TakenLeagueFlag is null ? null : PersonalStashOf(player);
        if (x is byte px && y is byte py)
        {
            if (LevelTooLow(player, row, reward) is { } low) { await player.Commands!.Reply(low); return; }
            var displacedPreview = row == PlayerInventories.Cursor ? null
                : items.PreviewPurchase(row, px, py, reward, ShapeOf, expandedAllowed,
                    allowDisplace: row is >= 1 and <= 11 or 14 or 15 or 102)?.Displaced;
            if (GameEvents.FirstInventoryRefusal(player, row, reward, displacedPreview) is { } objection)
            {
                Console.WriteLine($"[gateway] {player.Connection.Remote} <= 0x0088 quest reward {itemId}: refused ({objection})");
                return;
            }
            PlayerInventories.Placement? placement;
            if (PlayerInventories.ShowsOnModel(row))
            {
                using (ProjectileMutation(player, advance: false, phase: "quest reward equipment"))
                using (new PlayerStatScope(player, fresh: true))
                {
                    var sourcesBefore = CaptureGemSourceRowsLocked(player);
                    placement = QuestRewardCatalog.ClaimAt(CharacterName(player), data.TakenFlag, quest, items, reward,
                        ShapeOf, row, px, py, expandedAllowed, data.TakenLeagueFlag, leagueStash, data.Reclaimable);
                    if (placement is not null)
                    {
                        RecomputePoolsAndSpeed(player);
                        ReconcileBeastCompanionsLocked(player);
                        ReconcileEquipmentSourceLifetimesLocked(player, sourcesBefore);
                    }
                }
            }
            else
            {
                placement = QuestRewardCatalog.ClaimAt(CharacterName(player), data.TakenFlag, quest, items, reward,
                    ShapeOf, row, px, py, expandedAllowed, data.TakenLeagueFlag, leagueStash, data.Reclaimable);
            }
            if (placement is null) return;
            if (placement.Row == PlayerInventories.Flask)
                placement = placement with { Placed = EmptyIfEquippedInHostileArea(player, items, placement.Placed) };
            uint[] removed = placement.Merged ? [placement.Placed.Id]
                : placement.Displaced is { } old ? [old.Id] : [];
            await Send(player, InstanceItems.InventoryChange(PlayerInventories.InventoryId(placement.Row),
                items.NextRevision(placement.Row), removed, [placement.Placed]),
                $"0x00bf claimed quest reward at ({placement.Placed.X}, {placement.Placed.Y})");
            if (placement.Displaced is { } displaced)
                await Send(player, InstanceItems.InventoryChange(PlayerInventories.InventoryId(PlayerInventories.Cursor),
                    items.NextRevision(PlayerInventories.Cursor), [], [displaced]), "0x00bf replaced item to cursor");
            else if (placement.Rest is { } rest)
                await Send(player, InstanceItems.InventoryChange(PlayerInventories.InventoryId(PlayerInventories.Cursor),
                    items.NextRevision(PlayerInventories.Cursor), [], [rest]), "0x00bf quest reward overflow to cursor");
            if (PlayerInventories.ShowsOnModel(placement.Row)) await SendEquipment(player, items);
            if (placement.Row == PlayerInventories.Flask)
            {
                await ReconcileTincturesAfterFlaskBarChange(player);
                await RefreshMagebloodFlasks(player);
            }
        }
        else
        {
            // A league quest flag taken flag is read and set in the account's stash inside the claim's transaction.
            var claimed = QuestRewardCatalog.Claim(CharacterName(player), data.TakenFlag, quest, items, reward, ShapeOf, row,
                expandedAllowed: expandedAllowed, leagueFlag: data.TakenLeagueFlag,
                leagueFlags: leagueStash, reclaimable: data.Reclaimable);
            if (claimed is null) return;
            await Send(player, InstanceItems.InventoryChange(PlayerInventories.InventoryId(row),
                items.NextRevision(row), claimed.RemovedIds, claimed.AddedItems), "0x00bf claimed quest reward");
        }
        await FinishClaimedNpcReward(player, offer, data, quest);
    }

    internal static async Task ClaimNpcRewardIntoSocket(Player player, InstanceNpcs.BuyAndSocket buy)
    {
        if (player.NpcRewardOffer is not int offer || !player.NpcRewardItems.TryGetValue(buy.OfferItemId, out var reward)) return;
        SpawnedNpc? npc;
        lock (player.Npcs) npc = player.Npcs.GetValueOrDefault(player.TalkingTo);
        if (npc is null || !HasAvailableReward(player, npc.Npc, offer)) return;
        var catalog = QuestRewardCatalog.Load(hardMode: LeagueFor(player).HardMode, supportGems: LeagueFor(player).Rules.SupportGemRewards);
        if (catalog.Find(offer) is not { } data) return;
        if (PlayerInventories.Row(buy.InventoryId) is not byte targetRow)
        {
            Console.WriteLine($"[gateway] {player.Connection.Remote} <= 0x0089 quest reward socket {buy}: refused (unknown inventory)");
            return;
        }
        var items = ItemsOf(player);
        var quest = ItemsFor(player).ByHash(reward.TypeHash)?.Quest;
        var leagueStash = data.TakenLeagueFlag is null ? null : PersonalStashOf(player);
        bool equipped = ItemModStats.EquipmentRows.Contains(targetRow);
        (InventoryItem Item, InventoryItem? Displaced)? result = null;
        string why = "";
        string? missing = null;
        using (ProjectileMutation(player, advance: false, phase: "quest reward gem socket"))
        {
            IReadOnlyList<PlayerSkillSource> displacedSources = [];
            if (equipped && items.Items(targetRow).FirstOrDefault(i => i.Id == buy.TargetItemId) is { } host
                && buy.Socket < host.Item.Sockets.Count && host.Item.Sockets[(int)buy.Socket].Gem is { } oldGem)
                displacedSources = GemSourceRowsLocked(player, host.Id, (int)buy.Socket, oldGem);
            if (equipped && GemRequirementsUnmetAt(player, reward, null, targetItemId: buy.TargetItemId) is { } unmet)
                missing = unmet;
            else
            {
                result = QuestRewardCatalog.ClaimIntoSocket(CharacterName(player), data.TakenFlag, quest, items, reward,
                    targetRow, buy.TargetItemId, (int)buy.Socket, ShapeOf, out why,
                    data.TakenLeagueFlag, leagueStash, data.Reclaimable);
                if (result is not null && equipped)
                {
                    if (result.Value.Displaced is { } displaced)
                    {
                        RetireDirectMinionSourcesLocked(player, displacedSources);
                        ForgetGemActivation(player, displaced.Item);
                    }
                    RecomputePoolsAndSpeed(player);
                    ReconcileBeastCompanionsLocked(player);
                }
            }
        }
        if (missing is not null)
        {
            Console.WriteLine($"[gateway] {player.Connection.Remote} <= 0x0089 quest reward socket {buy}: refused ({missing})");
            return;
        }
        if (result is not var (updatedHost, displacedGem))
        {
            Console.WriteLine($"[gateway] {player.Connection.Remote} <= 0x0089 quest reward socket {buy}: refused ({why})");
            return;
        }
        if (displacedGem is not null)
            await Send(player, InstanceItems.InventoryChange(PlayerInventories.InventoryId(PlayerInventories.Cursor),
                items.NextRevision(PlayerInventories.Cursor), [], [displacedGem]),
                $"0x00bf cursor: hold displaced gem {displacedGem.Id} from socket {buy.Socket}");
        await Send(player, InstanceItems.InventoryChange(buy.InventoryId, items.NextRevision(targetRow),
            [updatedHost.Id], [updatedHost]),
            $"0x00bf inventory {buy.InventoryId}: item {updatedHost.Id} with quest reward gem in socket {buy.Socket}");
        if (equipped) await SendEquipment(player, items);
        await FinishClaimedNpcReward(player, offer, data, quest);
    }

    static async Task FinishClaimedNpcReward(Player player, int offer, QuestRewardCatalog.Offer data, QuestItemFlags? quest)
    {
        // Empty the offer inventory so the native reward window finishes its pending purchase and closes.
        await Send(player, InstanceItems.InventoryChange(0x600, 1, [.. player.NpcRewardItems.Keys], [], NpcShops.Manager),
            $"0x00bf quest reward offer {offer}: all choices removed");
        player.NpcRewardOffer = null; player.NpcRewardItems.Clear();
        // The taken flag (or, for a quest item, its pickup flags: an Act 1 book's HaveRewardBook), in memory and to the client.
        var takenFlags = new List<string>();
        if (data.TakenFlag is { } taken) takenFlags.Add(taken);
        if (quest is not null) takenFlags.AddRange(new[] { quest.Grant, quest.Extra }.OfType<string>());
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
