using System.Collections.Concurrent;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Modules.Settlers.Operations;

/// <summary>
/// Unified Kingsmarch reward stash for both Shipping (<c>sourceKind=0</c>, slots 0..2) and Mapping (<c>sourceKind=1</c>,
/// slots 0..2), backed by <c>village_reward_items</c> exclusively through <see cref="IOperationsHost"/>.
/// <para>
/// Verified from <c>Client/Game.exe</c> (<c>0x140514ae0..0x140514f50</c> &amp; <c>0x1403148b0..0x140314b80</c>):
/// <c>S→C 0x02c9</c> subtags <c>0x09</c> (Shipping) and <c>0x0a</c> (Mapping) send a list of Manager-1 (<see cref="NpcShops.Manager"/>)
/// inventory IDs registered via <c>S→C 0x00c0</c>. Every take/discard claims entries atomically under the session lock
/// BEFORE modifying player inventory so rapid duplicate clicks can never duplicate reward items.
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
        public readonly Lock Gate = new();
        public required byte SourceKind { get; init; }
        public required byte SlotIndex { get; init; }
        public required List<uint> PageInventoryIds { get; init; }
        public Dictionary<uint, RewardEntry> ByItemId { get; } = [];
        public Dictionary<uint, uint> PageRevisions { get; } = [];
    }

    sealed record RewardEntry(string ItemJson, uint PageInventoryId, InventoryItem InvItem);

    readonly ConcurrentDictionary<Player, OpenRewardSession> _sessions = new();

    public void ClearSession(Player player) => _sessions.TryRemove(player, out _);

    /// <summary>Appends newly generated reward items to <c>village_reward_items</c> via <see cref="IOperationsHost"/>.</summary>
    public int AddRewards(string account, string league, byte sourceKind, int sourceIndex, IReadOnlyList<Item> items, ModCatalog? mods)
    {
        if (items.Count == 0) return 0;
        return host.MutateSnapshot(account, league, snap =>
        {
            var existing = snap.RewardItems.ToList();
            long nextRowId = existing.Count == 0 ? 1 : existing.Max(r => r.Id) + 1;
            int added = 0;
            foreach (var item in items)
            {
                string json = OperationsData.SerializeItem(item, mods);
                existing.Add(new VillageStore.RewardItemRow(nextRowId++, sourceKind, sourceIndex, 0, 0, json));
                added++;
            }
            return (snap with { RewardItems = existing }, added);
        }, persistNow: true);
    }

    public bool HasAnyRewards(string account, string league, byte? sourceKind = null, int? sourceIndex = null)
    {
        var snap = host.ReadSnapshot(account, league);
        if (snap is null) return false;
        foreach (var r in snap.RewardItems)
        {
            if (sourceKind is byte sk && r.SourceKind != sk) continue;
            if (sourceIndex is int si && r.SourceIndex != si) continue;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Notifies the client about updated reward availability (<c>0x02c9 0x08</c> + <c>0x001e check_reward_icon_visibility</c>).
    /// In 3.29, <c>ActiveRewardNotice</c> carries the 1-based ship slot (<c>1..3</c>) whose shipping reward stash is currently
    /// opened (locking other ships' REWARDS buttons until collected/discarded), or <c>0</c> when none is locked.
    /// </summary>
    public async Task SyncRewardVisibilityAsync(Player player, byte? activeNoticeValue = null)
    {
        byte notice = activeNoticeValue ?? 0;
        if (activeNoticeValue is null && _sessions.TryGetValue(player, out var open) && open.SourceKind == SourceShipping)
        {
            bool hasLeft;
            lock (open.Gate) hasLeft = open.ByItemId.Count > 0;
            if (hasLeft) notice = (byte)(open.SlotIndex + 1);
        }
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

        if (_sessions.TryRemove(player, out var prev))
        {
            foreach (uint oldInv in prev.PageInventoryIds)
                await Send(player, InstanceInventories.RemoveInventory(oldInv, NpcShops.Manager),
                    $"0x00c1 remove previous village reward inventory 0x{oldInv:x}");
        }

        var loaded = new List<(string ItemJson, Item Item)>();
        if (host.ReadSnapshot(account, league) is { } snap)
        {
            foreach (var r in snap.RewardItems)
            {
                if (r.SourceKind != cmd.SourceKind || r.SourceIndex != cmd.SlotIndex) continue;
                if (OperationsData.DeserializeItem(r.ItemJson, catalog) is { } item)
                    loaded.Add((r.ItemJson, item));
            }
        }

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
        if (cmd.SourceKind == SourceShipping && session.ByItemId.Count > 0)
            await Send(player, InstanceVillage.ActiveRewardNotice((byte)(cmd.SlotIndex + 1)),
                $"0x02c9 village ActiveRewardNotice({cmd.SlotIndex + 1})");
        return true;
    }

    /// <summary>C→S <c>0x02ca</c> subtag <c>0x13</c>: TakeRewardCtrlClick.</summary>
    public async Task<bool> HandleTakeRewardCtrlClickAsync(Player player, InstanceVillage.TakeRewardCtrlClickCommand cmd)
    {
        if (!_sessions.TryGetValue(player, out var session)) return true;

        // Atomically claim entry BEFORE modifying player inventory so duplicate clicks cannot dupe.
        RewardEntry? entry;
        lock (session.Gate)
        {
            if (!session.ByItemId.Remove(cmd.ItemId, out entry)) return true;
        }

        var items = ItemsOf(player);
        var targetRows = ExpandedInventoryGranted(player)
            ? new[] { PlayerInventories.MainInventory, PlayerInventories.ExpandedInventory }
            : new[] { PlayerInventories.MainInventory };

        var preview = items.StackAcross(targetRows, entry.InvItem.Item, ShapeOf, preview: true);
        if (preview.Values.Min(s => s.Left) > 0)
        {
            RestoreEntries(session, [entry]);
            return true;
        }

        // Commit both the player inventory addition and the reward deletion atomically before sending packets
        // so a crash or disconnect mid-Send can never duplicate the claimed reward.
        IReadOnlyDictionary<byte, PlayerInventories.Stacking> stacked = null!;
        Saves.Atomically(() =>
        {
            stacked = items.StackAcross(targetRows, entry.InvItem.Item, ShapeOf, preview: false);
            DeleteClaimedFromStore(player, session, [entry]);
            return true;
        });

        foreach (var (row, st) in stacked)
        {
            if (!st.Any) continue;
            await Send(player, InstanceItems.InventoryChange(PlayerInventories.InventoryId(row),
                items.NextRevision(row), st.RemovedIds, st.AddedItems),
                $"0x00bf village reward ctrl-take to row {row}");
        }

        await NotifyClaimedEntriesAsync(player, session, [entry]);
        return true;
    }

    /// <summary>C→S <c>0x02ca</c> subtag <c>0x14</c>: TakeRewardToSlot.</summary>
    public async Task<bool> HandleTakeRewardToSlotAsync(Player player, InstanceVillage.TakeRewardToSlotCommand cmd)
    {
        if (!_sessions.TryGetValue(player, out var session)) return true;

        RewardEntry? entry;
        lock (session.Gate)
        {
            if (!session.ByItemId.Remove(cmd.ItemId, out entry)) return true;
        }

        uint maskedInv = cmd.DstInventoryId & 0xc01fffffu;
        if (PlayerInventories.Row(maskedInv) is not byte row)
        {
            RestoreEntries(session, [entry]);
            return true;
        }

        var items = ItemsOf(player);
        bool expandedAllowed = ExpandedInventoryGranted(player);
        var rewardItem = entry.InvItem.Item;

        if (LevelTooLow(player, row, rewardItem) is { } low)
        {
            RestoreEntries(session, [entry]);
            if (player.Commands is { } cmds) await cmds.Reply(low);
            return true;
        }

        // Direct placement into passive jewel sockets must go through Items.PlaceItem (allocation/cluster/limit checks).
        if (row == PlayerInventories.PassiveJewels)
        {
            RestoreEntries(session, [entry]);
            return true;
        }

        bool allowDisplace = row is >= 1 and <= 11 or 14 or 15 or 102;
        var displacedPreview = row == PlayerInventories.Cursor
            ? null
            : items.PreviewPurchase(row, (int)cmd.X, (int)cmd.Y, rewardItem, ShapeOf, expandedAllowed, allowDisplace)?.Displaced;
        if (GameEvents.FirstInventoryRefusal(player, row, rewardItem, displacedPreview, (byte)cmd.X, (byte)cmd.Y) is not null)
        {
            RestoreEntries(session, [entry]);
            return true;
        }

        if (row == PlayerInventories.Cursor)
        {
            if (items.Items(PlayerInventories.Cursor).Count > 0)
            {
                RestoreEntries(session, [entry]);
                return true;
            }
            InventoryItem added = null!;
            Saves.Atomically(() =>
            {
                added = items.Add(PlayerInventories.Cursor, 0, 0, rewardItem);
                DeleteClaimedFromStore(player, session, [entry]);
                return true;
            });
            await Send(player, InstanceItems.InventoryChange(PlayerInventories.InventoryId(PlayerInventories.Cursor),
                items.NextRevision(PlayerInventories.Cursor), [], [added]), "0x00bf village reward to cursor");
            await NotifyClaimedEntriesAsync(player, session, [entry]);
            return true;
        }

        PlayerInventories.Placement? placement = null;
        Saves.Atomically(() =>
        {
            placement = items.PlaceIncoming(row, (int)cmd.X, (int)cmd.Y, rewardItem, ShapeOf, out _, expandedAllowed, allowDisplace);
            if (placement is null) return false;
            DeleteClaimedFromStore(player, session, [entry]);
            return true;
        });
        if (placement is null)
        {
            RestoreEntries(session, [entry]);
            return true;
        }

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
        if (placement.Row == PlayerInventories.Flask)
        {
            await ReconcileTincturesAfterFlaskBarChange(player);
            await RefreshMagebloodFlasks(player);
        }
        await GameEvents.RunInventoryChanged(player, placement.Row);

        await NotifyClaimedEntriesAsync(player, session, [entry]);
        return true;
    }

    /// <summary>C→S <c>0x02ca</c> subtag <c>0x15</c>: TakeRewardAndSocket.</summary>
    public async Task<bool> HandleTakeRewardAndSocketAsync(Player player, InstanceVillage.TakeRewardAndSocketCommand cmd)
    {
        if (!_sessions.TryGetValue(player, out var session)) return true;

        RewardEntry? entry;
        lock (session.Gate)
        {
            if (!session.ByItemId.Remove(cmd.OfferItemId, out entry)) return true;
        }

        uint maskedInv = cmd.DstInventoryId & 0xc01fffffu;
        if (PlayerInventories.Row(maskedInv) is not byte targetRow)
        {
            RestoreEntries(session, [entry]);
            return true;
        }

        var items = ItemsOf(player);
        var rewardItem = entry.InvItem.Item;
        if (GemRequirementsUnmetAt(player, rewardItem, null, targetItemId: cmd.TargetItemId) is not null)
        {
            RestoreEntries(session, [entry]);
            return true;
        }

        (InventoryItem Host, InventoryItem? Displaced)? result = null;
        Saves.Atomically(() =>
        {
            result = items.SocketItem(targetRow, cmd.TargetItemId, (int)cmd.SocketIndex, rewardItem, ShapeOf, items.NewItemId, out _);
            if (result is null) return false;
            DeleteClaimedFromStore(player, session, [entry]);
            return true;
        });
        if (result is not var (updatedHost, displacedGem))
        {
            RestoreEntries(session, [entry]);
            return true;
        }

        if (displacedGem is not null)
            await Send(player, InstanceItems.InventoryChange(PlayerInventories.InventoryId(PlayerInventories.Cursor),
                items.NextRevision(PlayerInventories.Cursor), [], [displacedGem]),
                $"0x00bf village reward socket displaced gem {displacedGem.Id}");

        await Send(player, InstanceItems.InventoryChange(maskedInv, items.NextRevision(targetRow),
            [updatedHost.Id], [updatedHost]),
            $"0x00bf village reward socketed into item {updatedHost.Id}");

        if (ItemModStats.EquipmentRows.Contains(targetRow)) await SendEquipment(player, items);
        await GameEvents.RunInventoryChanged(player, targetRow);
        await NotifyClaimedEntriesAsync(player, session, [entry]);
        return true;
    }

    /// <summary>C→S <c>0x02ca</c> subtag <c>0x16</c>: TakeAllRewards (optional <c>baseItemTypeHash32</c> filter, 0 = all).</summary>
    public async Task<bool> HandleTakeAllRewardsAsync(Player player, InstanceVillage.TakeAllRewardsCommand cmd)
    {
        if (!_sessions.TryGetValue(player, out var session)) return true;

        // Atomically claim all matching entries under session.Gate before touching player inventory.
        List<RewardEntry> candidates;
        lock (session.Gate)
        {
            candidates = session.ByItemId.Values
                .Where(e => cmd.BaseItemTypeHash32 == 0 || e.InvItem.Item.TypeHash == cmd.BaseItemTypeHash32)
                .OrderBy(e => e.PageInventoryId).ThenBy(e => e.InvItem.Y).ThenBy(e => e.InvItem.X)
                .ToList();
            foreach (var c in candidates) session.ByItemId.Remove(c.InvItem.Id);
        }
        if (candidates.Count == 0) return true;

        var items = ItemsOf(player);
        var targetRows = ExpandedInventoryGranted(player)
            ? new[] { PlayerInventories.MainInventory, PlayerInventories.ExpandedInventory }
            : new[] { PlayerInventories.MainInventory };

        var taken = new List<RewardEntry>();
        var leftover = new List<RewardEntry>();
        var removedByRow = new Dictionary<byte, List<uint>>();
        var addedByRow = new Dictionary<byte, List<InventoryItem>>();
        // Track pre-existing item IDs per row so newly created stacks merged within this batch are never sent in removedIds.
        var preExistingByRow = targetRows.ToDictionary(r => r, r => items.Items(r).Select(i => i.Id).ToHashSet());

        Saves.Atomically(() =>
        {
            foreach (var entry in candidates)
            {
                var preview = items.StackAcross(targetRows, entry.InvItem.Item, ShapeOf, preview: true);
                if (preview.Values.Min(s => s.Left) > 0)
                {
                    leftover.Add(entry);
                    continue;
                }

                var stacked = items.StackAcross(targetRows, entry.InvItem.Item, ShapeOf, preview: false);
                foreach (var (row, st) in stacked)
                {
                    if (!st.Any) continue;
                    (removedByRow.TryGetValue(row, out var rl) ? rl : removedByRow[row] = []).AddRange(st.RemovedIds);
                    (addedByRow.TryGetValue(row, out var al) ? al : addedByRow[row] = []).AddRange(st.AddedItems);
                }
                taken.Add(entry);
            }
            if (taken.Count > 0) DeleteClaimedFromStore(player, session, taken);
            return taken.Count > 0;
        });

        if (leftover.Count > 0) RestoreEntries(session, leftover);
        if (taken.Count == 0) return true;

        foreach (var (row, added) in addedByRow)
        {
            var latestAdded = new Dictionary<uint, InventoryItem>();
            foreach (var it in added) latestAdded[it.Id] = it;
            var preExisting = preExistingByRow[row];
            var distinctRemoved = removedByRow.TryGetValue(row, out var rl)
                ? rl.Where(id => preExisting.Contains(id)).Distinct().ToArray()
                : [];
            await Send(player, InstanceItems.InventoryChange(PlayerInventories.InventoryId(row),
                items.NextRevision(row), distinctRemoved, [.. latestAdded.Values]),
                $"0x00bf village take-all rewards ({taken.Count} items) -> row {row}");
        }

        await NotifyClaimedEntriesAsync(player, session, taken);
        return true;
    }

    /// <summary>C→S <c>0x02ca</c> subtag <c>0x17</c>: DiscardAllRewards (clears all rows for this slot, including overflow/unparseable rows).</summary>
    public async Task<bool> HandleDiscardAllRewardsAsync(Player player)
    {
        if (!_sessions.TryGetValue(player, out var session)) return true;
        List<RewardEntry> all;
        lock (session.Gate)
        {
            all = session.ByItemId.Values.ToList();
            session.ByItemId.Clear();
        }
        var (account, league) = VillageStore.ScopeOf(player);
        host.MutateSnapshot(account, league, snap =>
        {
            var remaining = snap.RewardItems
                .Where(r => !(r.SourceKind == session.SourceKind && r.SourceIndex == session.SlotIndex))
                .ToList();
            return (snap with { RewardItems = remaining }, remaining.Count);
        }, persistNow: true);
        await NotifyClaimedEntriesAsync(player, session, all);
        return true;
    }

    /// <summary>C→S <c>0x02ca</c> subtag <c>0x1b</c>: HarbourNpcNotifAck.</summary>
    public async Task<bool> HandleHarbourNotifAckAsync(Player player, InstanceVillage.HarbourNpcNotifAckCommand cmd)
    {
        await SyncRewardVisibilityAsync(player, activeNoticeValue: null);
        return true;
    }

    /// <summary>C→S <c>0x02ca</c> subtag <c>0x1c</c>: MappingNpcNotifAck.</summary>
    public async Task<bool> HandleMappingNotifAckAsync(Player player, InstanceVillage.MappingNpcNotifAckCommand cmd)
    {
        await SyncRewardVisibilityAsync(player, activeNoticeValue: null);
        return true;
    }

    static void RestoreEntries(OpenRewardSession session, IReadOnlyList<RewardEntry> entries)
    {
        lock (session.Gate)
        {
            foreach (var e in entries)
                session.ByItemId[e.InvItem.Id] = e;
        }
    }

    void DeleteClaimedFromStore(Player player, OpenRewardSession session, IReadOnlyList<RewardEntry> entries)
    {
        var (account, league) = VillageStore.ScopeOf(player);
        var removeCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var e in entries)
            removeCounts[e.ItemJson] = removeCounts.GetValueOrDefault(e.ItemJson) + 1;

        host.MutateSnapshot(account, league, snap =>
        {
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
            return (snap with { RewardItems = remaining }, remaining.Count);
        }, persistNow: true);
    }

    async Task NotifyClaimedEntriesAsync(Player player, OpenRewardSession session, IReadOnlyList<RewardEntry> entries)
    {
        var (account, league) = VillageStore.ScopeOf(player);
        var byPage = new Dictionary<uint, List<uint>>();
        foreach (var e in entries)
            (byPage.TryGetValue(e.PageInventoryId, out var list) ? list : byPage[e.PageInventoryId] = []).Add(e.InvItem.Id);

        foreach (var (invId, removedIds) in byPage)
        {
            uint rev;
            lock (session.Gate)
            {
                rev = session.PageRevisions.GetValueOrDefault(invId) + 1;
                session.PageRevisions[invId] = rev;
            }
            await Send(player, InstanceItems.InventoryChange(invId, rev, removedIds, [], NpcShops.Manager),
                $"0x00bf village reward tab 0x{invId:x} removed {removedIds.Count} item(s)");
        }

        bool slotEmpty = !HasAnyRewards(account, league, session.SourceKind, session.SlotIndex);
        if (slotEmpty)
        {
            if (session.SourceKind == SourceShipping)
                await OperationsSubsystem.Shipping.OnRewardsEmptiedAsync(player, session.SlotIndex);
            else if (session.SourceKind == SourceMapping)
                await OperationsSubsystem.Mapping.OnRewardsEmptiedAsync(player, session.SlotIndex);
            await SyncRewardVisibilityAsync(player, activeNoticeValue: 0);
        }
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
