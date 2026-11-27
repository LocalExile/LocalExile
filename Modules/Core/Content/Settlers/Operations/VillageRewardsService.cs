using System.Collections.Concurrent;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Modules.Settlers.Operations;

/// <summary>
/// Unified Kingsmarch reward stash for both Shipping (<c>sourceKind=0</c>, slots 0..2) and Mapping (<c>sourceKind=1</c>,
/// slots 0..2), backed by <c>village_reward_items</c> in <see cref="VillageStore"/>.
/// <para>
/// Verified from <c>Client/Game.exe</c> (<c>0x140514ae0..0x140514f50</c> &amp; <c>0x1403148b0..0x140314b80</c>):
/// <c>S→C 0x02c9</c> subtags <c>0x09</c> (Shipping) and <c>0x0a</c> (Mapping) send a list of Manager-1 (<see cref="NpcShops.Manager"/>)
/// inventory IDs registered via <c>S→C 0x00c0</c>. Clicking items inside that window sends <c>C→S 0x02ca</c> subtags
/// <c>0x13</c> (Ctrl+click), <c>0x14</c> (to slot), <c>0x15</c> (socket), <c>0x16</c> (take all / take matching hash), and
/// <c>0x17</c> (discard all).
/// </para>
/// </summary>
public sealed class VillageRewardsService(IOperationsHost host)
{
    public const byte SourceShipping = 0;
    public const byte SourceMapping = 1;

    // Distinct Manager-1 inventory ID ranges above NpcShops (0x400..) and QuestRewards (0x600).
    const uint ShippingRewardInvBase = 0x700;
    const uint MappingRewardInvBase = 0x740;
    const int MaxPagesPerSlot = 8;
    const byte PageWidth = 12;
    const byte PageHeight = 11;

    sealed class OpenRewardSession
    {
        public required byte SourceKind { get; init; }
        public required byte SlotIndex { get; init; }
        public required List<uint> PageInventoryIds { get; init; }
        public Dictionary<uint, RewardEntry> ByItemId { get; } = [];
        public Dictionary<uint, uint> PageRevisions { get; } = [];
    }

    sealed record RewardEntry(string ItemJson, uint PageInventoryId, InventoryItem InvItem);

    readonly ConcurrentDictionary<Player, OpenRewardSession> _sessions = new();

    public void ClearSession(Player player) => _sessions.TryRemove(player, out _);

    /// <summary>Appends newly generated reward items to <c>village_reward_items</c> for the given source/slot.</summary>
    public int AddRewards(string account, string league, byte sourceKind, int sourceIndex, IReadOnlyList<Item> items, ModCatalog? mods)
    {
        if (items.Count == 0) return 0;
        return host.WithTownLock(account, league, () =>
        {
            var snap = StandaloneOperationsHost.LoadOrCreate(account, league);
            var existing = snap.RewardItems.ToList();
            long nextRowId = existing.Count == 0 ? 1 : existing.Max(r => r.Id) + 1;
            int added = 0;
            foreach (var item in items)
            {
                string json = OperationsData.SerializeItem(item, mods);
                existing.Add(new VillageStore.RewardItemRow(nextRowId++, sourceKind, sourceIndex, 0, 0, json));
                added++;
            }
            VillageStore.Save(snap with { RewardItems = existing });
            return added;
        });
    }

    public bool HasAnyRewards(string account, string league, byte? sourceKind = null, int? sourceIndex = null) =>
        host.WithTownLock(account, league, () =>
        {
            var snap = VillageStore.Load(account, league);
            if (snap is null) return false;
            foreach (var r in snap.RewardItems)
            {
                if (sourceKind is byte sk && r.SourceKind != sk) continue;
                if (sourceIndex is int si && r.SourceIndex != si) continue;
                return true;
            }
            return false;
        });

    /// <summary>Notifies the client about updated reward availability (<c>0x02c9 0x08</c> + <c>0x001e check_reward_icon_visibility</c>).</summary>
    public async Task SyncRewardVisibilityAsync(Player player, byte? activeNoticeValue = null)
    {
        var (account, league) = VillageStore.ScopeOf(player);
        bool hasShip = HasAnyRewards(account, league, SourceShipping);
        bool hasMap = HasAnyRewards(account, league, SourceMapping);
        byte notice = activeNoticeValue ?? (hasShip ? (byte)1 : hasMap ? (byte)2 : (byte)0);
        await Send(player, InstanceVillage.ActiveRewardNotice(notice), $"0x02c9 village ActiveRewardNotice({notice})");
        await Send(player, InstanceVillage.RulesetEvent(InstanceVillage.EventCheckRewardIconVisibility),
            "0x001e village check_reward_icon_visibility");
    }

    /// <summary>C→S <c>0x02ca</c> subtag <c>0x12</c>: OpenRewardInventory(sourceKind, slotIdx).</summary>
    public async Task<bool> HandleOpenRewardInventoryAsync(Player player, InstanceVillage.OpenRewardInventoryCommand cmd)
    {
        if (cmd.SourceKind is not (SourceShipping or SourceMapping)) return true;
        if (cmd.SlotIndex >= 3) return true;

        var (account, league) = VillageStore.ScopeOf(player);
        var catalog = ItemsFor(player);

        // Unregister any previously opened reward tabs for this player cleanly.
        if (_sessions.TryRemove(player, out var prev))
        {
            foreach (uint oldInv in prev.PageInventoryIds)
                await Send(player, InstanceInventories.RemoveInventory(oldInv, NpcShops.Manager),
                    $"0x00c1 remove previous village reward inventory 0x{oldInv:x}");
        }

        List<(string ItemJson, Item Item)> loaded = host.WithTownLock(account, league, () =>
        {
            var snap = VillageStore.Load(account, league);
            if (snap is null) return [];
            var list = new List<(string, Item)>();
            foreach (var r in snap.RewardItems)
            {
                if (r.SourceKind != cmd.SourceKind || r.SourceIndex != cmd.SlotIndex) continue;
                if (OperationsData.DeserializeItem(r.ItemJson, catalog) is { } item)
                    list.Add((r.ItemJson, item));
            }
            return list;
        });

        uint invBase = (cmd.SourceKind == SourceShipping ? ShippingRewardInvBase : MappingRewardInvBase)
            + (uint)(cmd.SlotIndex * MaxPagesPerSlot);

        var pages = LayoutIntoPages(loaded, catalog, invBase, out var entriesByItemId);
        if (pages.Count == 0)
            pages.Add((invBase, []));

        var session = new OpenRewardSession
        {
            SourceKind = cmd.SourceKind,
            SlotIndex = cmd.SlotIndex,
            PageInventoryIds = [.. pages.Select(p => p.InvId)],
        };
        foreach (var kv in entriesByItemId) session.ByItemId[kv.Key] = kv.Value;
        foreach (var (invId, _) in pages) session.PageRevisions[invId] = 0;
        _sessions[player] = session;

        // Register each Manager-1 reward tab via S→C 0x00c0 before announcing via 0x02c9 0x09 / 0x0a.
        foreach (var (invId, pageItems) in pages)
        {
            await Send(player, InstanceInventories.AddInventory(invId, NpcShops.Tab, NpcShops.Manager, pageItems),
                $"0x00c0 village reward tab 0x{invId:x} ({pageItems.Count} items)");
            await Send(player, InstanceInventories.InventoryPermissions(invId, notWritable: true, skipScans: true, manager: NpcShops.Manager),
                $"0x00c2 village reward tab 0x{invId:x} read-only");
        }

        byte[] openPkt = cmd.SourceKind == SourceShipping
            ? InstanceVillage.ShippingRewardItems(cmd.SlotIndex, session.PageInventoryIds)
            : InstanceVillage.MappingRewardItems(cmd.SlotIndex, session.PageInventoryIds);
        await Send(player, openPkt, $"0x02c9 village {(cmd.SourceKind == SourceShipping ? "Shipping" : "Mapping")}RewardItems(slot={cmd.SlotIndex}, tabs={session.PageInventoryIds.Count})");
        return true;
    }

    /// <summary>C→S <c>0x02ca</c> subtag <c>0x13</c>: TakeRewardCtrlClick.</summary>
    public async Task<bool> HandleTakeRewardCtrlClickAsync(Player player, InstanceVillage.TakeRewardCtrlClickCommand cmd)
    {
        if (!_sessions.TryGetValue(player, out var session)) return true;
        if (!session.ByItemId.TryGetValue(cmd.ItemId, out var entry)) return true;

        var items = ItemsOf(player);
        var targetRows = ExpandedInventoryGranted(player)
            ? new[] { PlayerInventories.MainInventory, PlayerInventories.ExpandedInventory }
            : new[] { PlayerInventories.MainInventory };

        var preview = items.StackAcross(targetRows, entry.InvItem.Item, ShapeOf, preview: true);
        int leftAfter = preview.Values.Min(s => s.Left);
        if (leftAfter > 0) return true; // Not enough inventory space

        var stacked = items.StackAcross(targetRows, entry.InvItem.Item, ShapeOf, preview: false);
        foreach (var (row, st) in stacked)
        {
            if (!st.Any) continue;
            await Send(player, InstanceItems.InventoryChange(PlayerInventories.InventoryId(row),
                items.NextRevision(row), st.RemovedIds, st.AddedItems),
                $"0x00bf village reward ctrl-take to row {row}");
        }

        await RemoveRewardEntriesAsync(player, session, [entry]);
        return true;
    }

    /// <summary>C→S <c>0x02ca</c> subtag <c>0x14</c>: TakeRewardToSlot.</summary>
    public async Task<bool> HandleTakeRewardToSlotAsync(Player player, InstanceVillage.TakeRewardToSlotCommand cmd)
    {
        if (!_sessions.TryGetValue(player, out var session)) return true;
        if (!session.ByItemId.TryGetValue(cmd.ItemId, out var entry)) return true;

        uint maskedInv = cmd.DstInventoryId & 0xc01fffffu;
        if (PlayerInventories.Row(maskedInv) is not byte row) return true;

        var items = ItemsOf(player);
        bool expandedAllowed = ExpandedInventoryGranted(player);
        var rewardItem = entry.InvItem.Item;

        if (LevelTooLow(player, row, rewardItem) is { } low)
        {
            if (player.Commands is { } cmds) await cmds.Reply(low);
            return true;
        }
        bool allowDisplace = row is >= 1 and <= 11 or 14 or 15 or 102;
        var displacedPreview = row == PlayerInventories.Cursor
            ? null
            : items.PreviewPurchase(row, (int)cmd.X, (int)cmd.Y, rewardItem, ShapeOf, expandedAllowed, allowDisplace)?.Displaced;
        if (GameEvents.FirstInventoryRefusal(player, row, rewardItem, displacedPreview) is not null)
            return true;

        PlayerInventories.Placement? placement;
        if (row == PlayerInventories.Cursor)
        {
            if (items.Items(PlayerInventories.Cursor).Count > 0) return true;
            var added = items.Add(PlayerInventories.Cursor, 0, 0, rewardItem);
            await Send(player, InstanceItems.InventoryChange(PlayerInventories.InventoryId(PlayerInventories.Cursor),
                items.NextRevision(PlayerInventories.Cursor), [], [added]), "0x00bf village reward to cursor");
            await RemoveRewardEntriesAsync(player, session, [entry]);
            return true;
        }

        placement = items.PlaceIncoming(row, (int)cmd.X, (int)cmd.Y, rewardItem, ShapeOf, out _, expandedAllowed, allowDisplace);
        if (placement is null) return true;

        uint[] removed = placement.Merged ? [placement.Placed.Id]
            : placement.Displaced is { } old ? [old.Id] : [];
        await Send(player, InstanceItems.InventoryChange(PlayerInventories.InventoryId(placement.Row),
            items.NextRevision(placement.Row), removed, [placement.Placed]),
            $"0x00bf village reward to row {placement.Row} ({placement.Placed.X},{placement.Placed.Y})");

        if (placement.Displaced is { } displaced)
            await Send(player, InstanceItems.InventoryChange(PlayerInventories.InventoryId(PlayerInventories.Cursor),
                items.NextRevision(PlayerInventories.Cursor), [], [displaced]), "0x00bf village reward displaced to cursor");
        else if (placement.Rest is { } rest)
            await Send(player, InstanceItems.InventoryChange(PlayerInventories.InventoryId(PlayerInventories.Cursor),
                items.NextRevision(PlayerInventories.Cursor), [], [rest]), "0x00bf village reward overflow to cursor");

        if (PlayerInventories.ShowsOnModel(placement.Row)) await SendEquipment(player, items);

        await RemoveRewardEntriesAsync(player, session, [entry]);
        return true;
    }

    /// <summary>C→S <c>0x02ca</c> subtag <c>0x15</c>: TakeRewardAndSocket.</summary>
    public async Task<bool> HandleTakeRewardAndSocketAsync(Player player, InstanceVillage.TakeRewardAndSocketCommand cmd)
    {
        if (!_sessions.TryGetValue(player, out var session)) return true;
        if (!session.ByItemId.TryGetValue(cmd.OfferItemId, out var entry)) return true;

        uint maskedInv = cmd.DstInventoryId & 0xc01fffffu;
        if (PlayerInventories.Row(maskedInv) is not byte targetRow) return true;

        var items = ItemsOf(player);
        var rewardItem = entry.InvItem.Item;
        if (GemRequirementsUnmetAt(player, rewardItem, null, targetItemId: cmd.TargetItemId) is not null)
            return true;

        var result = items.SocketItem(targetRow, cmd.TargetItemId, (int)cmd.SocketIndex, rewardItem, ShapeOf, items.NewItemId, out _);
        if (result is not var (updatedHost, displacedGem)) return true;

        if (displacedGem is not null)
            await Send(player, InstanceItems.InventoryChange(PlayerInventories.InventoryId(PlayerInventories.Cursor),
                items.NextRevision(PlayerInventories.Cursor), [], [displacedGem]),
                $"0x00bf village reward socket displaced gem {displacedGem.Id}");

        await Send(player, InstanceItems.InventoryChange(maskedInv, items.NextRevision(targetRow),
            [updatedHost.Id], [updatedHost]),
            $"0x00bf village reward socketed into item {updatedHost.Id}");

        if (ItemModStats.EquipmentRows.Contains(targetRow)) await SendEquipment(player, items);
        await RemoveRewardEntriesAsync(player, session, [entry]);
        return true;
    }

    /// <summary>C→S <c>0x02ca</c> subtag <c>0x16</c>: TakeAllRewards (optional <c>baseItemTypeHash32</c> filter, 0 = all).</summary>
    public async Task<bool> HandleTakeAllRewardsAsync(Player player, InstanceVillage.TakeAllRewardsCommand cmd)
    {
        if (!_sessions.TryGetValue(player, out var session) || session.ByItemId.Count == 0) return true;

        var items = ItemsOf(player);
        var targetRows = ExpandedInventoryGranted(player)
            ? new[] { PlayerInventories.MainInventory, PlayerInventories.ExpandedInventory }
            : new[] { PlayerInventories.MainInventory };

        var candidates = session.ByItemId.Values
            .Where(e => cmd.BaseItemTypeHash32 == 0 || e.InvItem.Item.TypeHash == cmd.BaseItemTypeHash32)
            .OrderBy(e => e.PageInventoryId).ThenBy(e => e.InvItem.Y).ThenBy(e => e.InvItem.X)
            .ToList();
        if (candidates.Count == 0) return true;

        var taken = new List<RewardEntry>();
        var removedByRow = new Dictionary<byte, List<uint>>();
        var addedByRow = new Dictionary<byte, List<InventoryItem>>();

        foreach (var entry in candidates)
        {
            var preview = items.StackAcross(targetRows, entry.InvItem.Item, ShapeOf, preview: true);
            if (preview.Values.Min(s => s.Left) > 0) continue;

            var stacked = items.StackAcross(targetRows, entry.InvItem.Item, ShapeOf, preview: false);
            foreach (var (row, st) in stacked)
            {
                if (!st.Any) continue;
                (removedByRow.TryGetValue(row, out var rl) ? rl : removedByRow[row] = []).AddRange(st.RemovedIds);
                (addedByRow.TryGetValue(row, out var al) ? al : addedByRow[row] = []).AddRange(st.AddedItems);
            }
            taken.Add(entry);
        }

        if (taken.Count == 0) return true;

        foreach (var (row, added) in addedByRow)
        {
            // Deduplicate: an item grown twice in one batch should only appear once with its latest state.
            var latestAdded = new Dictionary<uint, InventoryItem>();
            foreach (var it in added) latestAdded[it.Id] = it;
            var distinctRemoved = removedByRow.TryGetValue(row, out var rl) ? rl.Distinct().ToArray() : [];
            await Send(player, InstanceItems.InventoryChange(PlayerInventories.InventoryId(row),
                items.NextRevision(row), distinctRemoved, [.. latestAdded.Values]),
                $"0x00bf village take-all rewards ({taken.Count} items) -> row {row}");
        }

        await RemoveRewardEntriesAsync(player, session, taken);
        return true;
    }

    /// <summary>C→S <c>0x02ca</c> subtag <c>0x17</c>: DiscardAllRewards.</summary>
    public async Task<bool> HandleDiscardAllRewardsAsync(Player player)
    {
        if (!_sessions.TryGetValue(player, out var session) || session.ByItemId.Count == 0) return true;
        var all = session.ByItemId.Values.ToList();
        await RemoveRewardEntriesAsync(player, session, all);
        return true;
    }

    /// <summary>C→S <c>0x02ca</c> subtag <c>0x1b</c>: HarbourNpcNotifAck.</summary>
    public async Task<bool> HandleHarbourNotifAckAsync(Player player, InstanceVillage.HarbourNpcNotifAckCommand cmd)
    {
        await SyncRewardVisibilityAsync(player, activeNoticeValue: 0);
        return true;
    }

    /// <summary>C→S <c>0x02ca</c> subtag <c>0x1c</c>: MappingNpcNotifAck.</summary>
    public async Task<bool> HandleMappingNotifAckAsync(Player player, InstanceVillage.MappingNpcNotifAckCommand cmd)
    {
        await SyncRewardVisibilityAsync(player, activeNoticeValue: 0);
        return true;
    }

    async Task RemoveRewardEntriesAsync(Player player, OpenRewardSession session, IReadOnlyList<RewardEntry> entries)
    {
        var (account, league) = VillageStore.ScopeOf(player);
        var removeCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var e in entries)
            removeCounts[e.ItemJson] = removeCounts.GetValueOrDefault(e.ItemJson) + 1;

        host.WithTownLock(account, league, () =>
        {
            var snap = VillageStore.Load(account, league);
            if (snap is null) return 0;
            var remaining = new List<VillageStore.RewardItemRow>(snap.RewardItems.Count);
            foreach (var r in snap.RewardItems)
            {
                if (r.SourceKind == session.SourceKind && r.SourceIndex == session.SlotIndex
                    && removeCounts.TryGetValue(r.ItemJson, out int pending) && pending > 0)
                {
                    removeCounts[r.ItemJson] = pending - 1;
                    continue;
                }
                remaining.Add(r);
            }
            VillageStore.Save(snap with { RewardItems = remaining });
            return remaining.Count;
        });

        var byPage = new Dictionary<uint, List<uint>>();
        foreach (var e in entries)
        {
            session.ByItemId.Remove(e.InvItem.Id);
            (byPage.TryGetValue(e.PageInventoryId, out var list) ? list : byPage[e.PageInventoryId] = []).Add(e.InvItem.Id);
        }

        foreach (var (invId, removedIds) in byPage)
        {
            uint rev = session.PageRevisions.GetValueOrDefault(invId) + 1;
            session.PageRevisions[invId] = rev;
            await Send(player, InstanceItems.InventoryChange(invId, rev, removedIds, [], NpcShops.Manager),
                $"0x00bf village reward tab 0x{invId:x} removed {removedIds.Count} item(s)");
        }

        if (session.ByItemId.Count == 0)
            await SyncRewardVisibilityAsync(player);
    }

    static List<(uint InvId, List<InventoryItem> Items)> LayoutIntoPages(
        List<(string ItemJson, Item Item)> rewards,
        ItemCatalog catalog,
        uint invBase,
        out Dictionary<uint, RewardEntry> entriesByItemId)
    {
        entriesByItemId = [];
        var pages = new List<(uint InvId, List<InventoryItem> Items)>();
        var grids = new List<bool[,]>();
        uint nextItemId = 1;

        foreach (var (itemJson, item) in rewards)
        {
            var baseType = catalog.ByHash(item.TypeHash);
            byte w = Math.Clamp(baseType?.Width ?? 1, (byte)1, PageWidth);
            byte h = Math.Clamp(baseType?.Height ?? 1, (byte)1, PageHeight);

            bool placed = false;
            for (int p = 0; p < pages.Count; p++)
            {
                if (TryFindCell(grids[p], w, h, out byte x, out byte y))
                {
                    MarkCells(grids[p], x, y, w, h);
                    var invItem = new InventoryItem(nextItemId++, x, y, item);
                    pages[p].Items.Add(invItem);
                    entriesByItemId[invItem.Id] = new RewardEntry(itemJson, pages[p].InvId, invItem);
                    placed = true;
                    break;
                }
            }
            if (!placed && pages.Count < MaxPagesPerSlot)
            {
                uint invId = invBase + (uint)pages.Count;
                var grid = new bool[PageWidth, PageHeight];
                if (TryFindCell(grid, w, h, out byte x, out byte y))
                {
                    MarkCells(grid, x, y, w, h);
                    var invItem = new InventoryItem(nextItemId++, x, y, item);
                    pages.Add((invId, [invItem]));
                    grids.Add(grid);
                    entriesByItemId[invItem.Id] = new RewardEntry(itemJson, invId, invItem);
                }
            }
        }
        return pages;
    }

    static bool TryFindCell(bool[,] grid, byte w, byte h, out byte fx, out byte fy)
    {
        for (int y = 0; y + h <= PageHeight; y++)
            for (int x = 0; x + w <= PageWidth; x++)
            {
                bool free = true;
                for (int dy = 0; dy < h && free; dy++)
                    for (int dx = 0; dx < w && free; dx++)
                        if (grid[x + dx, y + dy]) free = false;
                if (free)
                {
                    fx = (byte)x;
                    fy = (byte)y;
                    return true;
                }
            }
        fx = 0;
        fy = 0;
        return false;
    }

    static void MarkCells(bool[,] grid, byte x, byte y, byte w, byte h)
    {
        for (int dy = 0; dy < h; dy++)
            for (int dx = 0; dx < w; dx++)
                grid[x + dx, y + dy] = true;
    }
}
