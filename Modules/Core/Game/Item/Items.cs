using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

public static partial class Game
{
    // C→S 0x28: a click on an item. It moves to the cursor inventory (row 12) in one save (PlayerInventories.PickUp): one 0xbf
    // removes it from its inventory, another adds it to the cursor at (0, 0). Refused (logged) when the cursor already holds an
    // item or the item is not there.
    public static async Task PickUpItem(Player player, InstanceItems.PickUpItem pick)
    {
        if (await GameEvents.AnyInventoryPick(player, pick)) return;
        if (StashOf(player).TabOf(pick.InventoryId) is int tab) { await PickUpFromStash(player, tab, pick); return; }
        var items = ItemsOf(player);
        if (PlayerInventories.Row(pick.InventoryId) is not byte row)
        {
            Console.WriteLine($"[gateway] {player.Connection.Remote} <= 0x0028 pick up {pick}: refused (unknown inventory)");
            return;
        }
        InventoryItem? picked = null;
        InventoryItem? promoted = null;
        List<(byte[] Packet, string Label)> equipmentOutputs = [];
        bool visible = PlayerInventories.ShowsOnModel(row);
        bool promotesOffhand = row is PlayerInventories.Weapon1 or PlayerInventories.Weapon2;
        string? passiveRefusal = null;
        bool spentClusterRemoval = false;
        if (visible || promotesOffhand)
        {
            // The pickup, offhand promotion and (for a visible row) source reconciliation are complete before either
            // inventory output can suspend. Allocate revisions only after that mutation, in their actual wire order.
            using (ProjectileMutation(player, advance: false, phase: "equipment pickup"))
            {
                var sourcesBefore = visible ? PlayerSkillSourcesLocked(player) : [];
                picked = items.PickUp(row, pick.ItemId);
                if (picked is not null && promotesOffhand) promoted = items.PromoteOffhand(row, ShapeOf);
                if (picked is not null && visible)
                {
                    using (new PlayerStatScope(player, fresh: true))
                    {
                        RecomputePoolsAndSpeed(player);
                        ReconcileBeastCompanionsLocked(player);
                        ReconcileEquipmentSourceLifetimesLocked(player, sourcesBefore);
                    }
                }
                if (picked is { } taken)
                {
                    equipmentOutputs.Add((InstanceItems.InventoryChange(pick.InventoryId, items.NextRevision(row),
                        [taken.Id], []),
                        $"0x00bf inventory {pick.InventoryId}: remove item {taken.Id} (picked up {pick})"));
                    equipmentOutputs.Add((InstanceItems.InventoryChange(
                        PlayerInventories.InventoryId(PlayerInventories.Cursor),
                        items.NextRevision(PlayerInventories.Cursor), [], [taken]),
                        $"0x00bf cursor: add item {taken.Id}"));
                    if (promoted is { } up)
                    {
                        byte off = (byte)(row + 1);
                        equipmentOutputs.Add((InstanceItems.InventoryChange(PlayerInventories.InventoryId(off),
                            items.NextRevision(off), [up.Id], []),
                            $"0x00bf inventory {PlayerInventories.InventoryId(off)}: remove item {up.Id} (to the main hand)"));
                        equipmentOutputs.Add((InstanceItems.InventoryChange(PlayerInventories.InventoryId(row),
                            items.NextRevision(row), [], [up]),
                            $"0x00bf inventory {PlayerInventories.InventoryId(row)}: add item {up.Id} (from the off hand)"));
                    }
                }
            }
        }
        else if (row == PlayerInventories.PassiveJewels)
        {
            using (ProjectileMutation(player, advance: false, phase: "passive jewel pickup"))
            {
                var leaving = items.Items(row).FirstOrDefault(i => i.Id == pick.ItemId);
                if (items.Items(PlayerInventories.Cursor).Count > 0) passiveRefusal = "something is held";
                else if (leaving is null) passiveRefusal = "no such item";
                else passiveRefusal = ClusterRemovalRefusal(player, leaving) ?? AllocationBypassRemovalRefusal(player, leaving);
                if (passiveRefusal is null)
                {
                    var sourcesBefore = CaptureGemSourceRowsLocked(player);
                    picked = items.PickUp(row, pick.ItemId);
                    if (picked is not null)
                    {
                        if (ClusterSize(picked.Item) is not null) { SpendClusterRemoval(player); spentClusterRemoval = true; }
                        CompletePassiveJewelMutationLocked(player, sourcesBefore);
                    }
                }
            }
        }
        else
        {
            picked = items.PickUp(row, pick.ItemId);
        }
        if (picked is not { } held)
        {
            Console.WriteLine($"[gateway] {player.Connection.Remote} <= 0x0028 pick up {pick}: refused" +
                (passiveRefusal is null ? "" : $" ({passiveRefusal})"));
            return;
        }
        if (equipmentOutputs.Count == 0)
        {
            // The non-visible branch keeps its historic immediate write/read outputs.
            equipmentOutputs.Add((InstanceItems.InventoryChange(pick.InventoryId, items.NextRevision(row), [held.Id], []),
                $"0x00bf inventory {pick.InventoryId}: remove item {held.Id} (picked up {pick})"));
            equipmentOutputs.Add((InstanceItems.InventoryChange(PlayerInventories.InventoryId(PlayerInventories.Cursor),
                items.NextRevision(PlayerInventories.Cursor), [], [held]), $"0x00bf cursor: add item {held.Id}"));
        }
        foreach (var (packet, label) in equipmentOutputs) await Send(player, packet, label);
        if (PlayerInventories.ShowsOnModel(row)) await SendEquipment(player, items);
        if (row == PlayerInventories.Flask)
        {
            await ReconcileTincturesAfterFlaskBarChange(player);
            await RefreshMagebloodFlasks(player);
        }
        if (row == PlayerInventories.PassiveJewels)
        {
            await SendPassiveJewelStats(player);
            if (spentClusterRemoval) await SendPassives(player);
        }
    }

    // C→S 0x30: the held gem goes into a socket. The cursor loses it (and holds the gem that was in the socket, if any); the
    // item is replaced in its inventory (0xbf removing and adding the same id). Gems in equipment grant skills.
    public static async Task SocketGem(Player player, InstanceItems.SocketGem s)
    {
        var items = ItemsOf(player);
        (InventoryItem Item, InventoryItem HeldBefore, InventoryItem? HeldNow)? done = null;
        Item? displacedBefore = null;
        IReadOnlyList<PlayerSkillSource> displacedSources = [];
        string why = "unknown inventory";
        string? missing = null;
        byte row = default;
        using (ProjectileMutation(player, advance: false, phase: "gem socket"))
        {
            if (PlayerInventories.Row(s.InventoryId) is byte currentRow)
            {
                row = currentRow;
                if (items.Items(row).FirstOrDefault(i => i.Id == s.ItemId) is { } host
                    && s.Socket < host.Item.Sockets.Count)
                {
                    displacedBefore = host.Item.Sockets[(int)s.Socket].Gem;
                    if (displacedBefore is not null)
                        displacedSources = GemSourceRowsLocked(player, host.Id, (int)s.Socket, displacedBefore);
                }
                if (items.Items(PlayerInventories.Cursor) is [var held, ..]
                    && GemRequirementsUnmetAt(player, held.Item, null, targetItemId: s.ItemId) is { } unmet)
                    missing = unmet;
                else
                {
                    done = items.Socket(row, s.ItemId, (int)s.Socket, ShapeOf, items.NewItemId, out why);
                    if (done is not null && ItemModStats.EquipmentRows.Contains(row))
                    {
                        if (done.Value.HeldNow is { } displaced)
                        {
                            RetireDirectMinionSourcesLocked(player, displacedSources);
                            ForgetGemActivation(player, displaced.Item);
                        }
                        RecomputePoolsAndSpeed(player);
                        ReconcileBeastCompanionsLocked(player);
                    }
                }
            }
        }
        if (missing is not null)
        {
            Console.WriteLine($"[gateway] {player.Connection.Remote} <= 0x0030 socket {s}: refused ({missing})");
            return;
        }
        if (done is not { } result)
        {
            Console.WriteLine($"[gateway] {player.Connection.Remote} <= 0x0030 socket {s}: refused ({why})");
            return;
        }
        var (item, before, now) = result;
        await Send(player, InstanceItems.InventoryChange(PlayerInventories.InventoryId(PlayerInventories.Cursor),
            items.NextRevision(PlayerInventories.Cursor), [before.Id], now is null ? [] : [now]),
            $"0x00bf cursor: remove gem {before.Id}" + (now is null ? "" : $", hold gem {now.Id} from the socket"));
        await Send(player, InstanceItems.InventoryChange(s.InventoryId, items.NextRevision(row), [item.Id], [item]),
            $"0x00bf inventory {s.InventoryId}: item {item.Id} with a gem in socket {s.Socket}");
        if (ItemModStats.EquipmentRows.Contains(row)) await SendEquipment(player, items);
    }

    // The gem one level up, or null with the reason it cannot level: the "+" (0x31) answers with the reason and Level Up All
    // (0x32) skips that gem without affecting the others. A locked gem, one at its highest level, and one whose next level's
    // requirements the character does not meet stay as they are.
    public static Item? NextGemLevel(Player player, Item gem, out string? why)
    {
        why = null;
        if (ItemsFor(player).ByHash(gem.TypeHash) is not { } baseType || ItemsFor(player).Gem(baseType) is not { } type) return null;
        if ((gem.GemFlags & 4) != 0) { why = $"{ItemsFor(player).GemName(gem)}'s level is locked"; return null; }
        int level = ItemsFor(player).GemLevel(gem);
        // The character's level can fall below the next level's requirement after the experience was gained (/set_level, or a
        // save from before the stall): the "+" is still there, so say what is missing rather than that the gem is at its last
        // level. The next level is only computed while the gem is under its type's cap (ExperienceFor clamps).
        if (level < type.MaxLevel && GemRequirementsUnmetAt(player, gem with { GemExperience = type.ExperienceFor(level + 1) },
            null, GemRequirementHost(player, gem, null, null)?.Id) is { } missing)
        {
            why = missing;
            return null;
        }
        int max = Math.Min(SkillsFor(player).MaxLevelAt(type.Variant(gem.GemVariant).Effect, player.Pools.Level), type.MaxLevel);
        if (level >= max) { why = $"{ItemsFor(player).GemName(gem)} is at its highest level ({max})"; return null; }
        return gem with { GemExperience = type.ExperienceFor(level + 1) };
    }

    // Whether the client draws this gem's level-up "+": its experience has reached the point GainGemExperience stops it at,
    // one below the next level's threshold (the client's check 0x142479880 draws the "+" for a full bar). The single "+"
    // needs no such check because the client sends 0x31 only for a gem it draws the "+" for; the button names no gem, so the
    // server uses this to leave gems that are not ready untouched.
    public static bool GemLevelUpReady(Player player, Item gem)
    {
        if (ItemsFor(player).ByHash(gem.TypeHash) is not { } baseType || ItemsFor(player).Gem(baseType) is not { } type) return false;
        int level = ItemsFor(player).GemLevel(gem);
        return level < type.MaxLevel && gem.GemExperience >= type.ExperienceFor(level + 1) - 1;
    }

    // Every ready socketed gem one level up, applied in one step: the rows that changed with each gem before and after. One
    // pass is what makes a click level each eligible gem exactly once; a second click finds nothing ready, because each
    // changed gem's experience is now its new level's threshold, not the next level's stall point.
    public static IReadOnlyList<(byte Row, InventoryItem Item, IReadOnlyList<(Item Before, Item After)> Gems)> LevelUpReadyGems(Player player)
    {
        var items = ItemsOf(player);
        var sourcesBefore = CaptureGemSourceRowsLocked(player);
        var changed = items.ChangeSocketedGems((itemId, gem) =>
            GemLevelUpReady(player, gem) ? NextGemLevel(player, gem, out _) : null);
        if (changed.Count != 0) ReconcileLeveledGemSourcesLocked(player, sourcesBefore);
        return changed;
    }

    // C→S 0x31: the "+" on a socketed gem. The client derives the level from the gem's experience on read (0x1424763d0), so
    // the server writes the next level's threshold and replaces the item; the client then derives the level and its skill.
    public static async Task LevelUpGem(Player player, InstanceItems.SocketGem s)
    {
        var items = ItemsOf(player);
        if (PlayerInventories.Row(s.InventoryId) is not byte row) return;
        InventoryItem? after = null;
        Item? next = null;
        string? why = null;
        int level = 0;
        if (items.Items(row).FirstOrDefault(i => i.Id == s.ItemId) is not { Item: { } worn } ||
            s.Socket >= worn.Sockets.Count || worn.Sockets[(int)s.Socket].Gem is not { } gem) return;
        if (NextGemLevel(player, gem, out why) is { } upgraded)
        {
            var sourcesBefore = CaptureGemSourceRowsLocked(player);
            level = ItemsFor(player).GemLevel(gem);
            next = PlayerInventories.PreserveGemIdentity(gem, upgraded);
            after = items.Change(row, s.ItemId, it =>
            {
                var sockets = it.Sockets.ToList();
                sockets[(int)s.Socket] = sockets[(int)s.Socket] with { Gem = next };
                return it with { Sockets = sockets };
            });
            if (after is not null) ReconcileLeveledGemSourcesLocked(player, sourcesBefore);
        }
        if (after is null || next is null)
        {
            if (why is not null) await player.Commands!.Reply(why);
            return;
        }
        await Send(player, InstanceItems.InventoryChange(s.InventoryId, items.NextRevision(row), [after.Id], [after]),
            $"0x00bf inventory {s.InventoryId}: item {after.Id} socket {s.Socket} gem to level {level + 1}");
        await SendSkills(player);
        Console.WriteLine($"[gateway] {player.Connection.Remote} {CharacterName(player)}'s {ItemsFor(player).GemName(next)} levels to {level + 1}");
        await CheckGemLevelAchievements(player, next, level + 1);
    }

    static void ReconcileLeveledGemSourcesLocked(Player player, IReadOnlyList<PlayerSkillSource> sourcesBefore)
    {
        using var scope = new PlayerStatScope(player, fresh: true);
        RecomputePoolsAndSpeed(player);
        ReconcileBeastCompanionsLocked(player);
        ReconcileEquipmentSourceLifetimesLocked(player, sourcesBefore);
    }

    // C→S 0x32 (2 bytes, id only): the gem level-up panel's "Click to level up ALL". Only this message levels the batch
    // (experience gains, logins, area changes and opening the inventory never do); one click levels every ready socketed gem
    // by one level in the same step, and a gem that is locked, at its highest level or missing the next level's requirements
    // is skipped without affecting the others. The client derives each new level from the experience, so the server replaces
    // every changed item (the remove and add idiom, one per row) and re-sends the skills once, so the prompts and XP bars
    // follow.
    public static async Task LevelUpAllGems(Player player)
    {
        var changed = LevelUpReadyGems(player);
        if (changed.Count == 0)
        {
            Console.WriteLine($"[gateway] {player.Connection.Remote} <= 0x0032 level up all gems: no socketed gem is ready");
            return;
        }
        var items = ItemsOf(player);
        items.SaveUnsaved();
        foreach (var row in changed.GroupBy(c => c.Row))
        {
            uint[] ids = [.. row.Select(c => c.Item.Id)];
            await Send(player, InstanceItems.InventoryChange(PlayerInventories.InventoryId(row.Key), items.NextRevision(row.Key),
                ids, [.. row.Select(c => c.Item)]),
                $"0x00bf inventory {PlayerInventories.InventoryId(row.Key)}: {ids.Length} item(s) with ready gems levelled at once");
        }
        foreach (var (before, after) in changed.SelectMany(c => c.Gems))
            Console.WriteLine($"[gateway] {player.Connection.Remote} {CharacterName(player)}'s {ItemsFor(player).GemName(after)} levels to " +
                $"{ItemsFor(player).GemLevel(after)} (from {ItemsFor(player).GemLevel(before)})");
        await SendSkills(player);
        foreach (var (_, after) in changed.SelectMany(c => c.Gems))
            await CheckGemLevelAchievements(player, after, ItemsFor(player).GemLevel(after));
    }

    // C→S 0xd7: a right-click on a socketed gem toggles its level lock (SkillGem +0x10 bit 2); the item is replaced with the
    // new flag so the client shows the lock again after a login.
    public static async Task LockGem(Player player, InstanceItems.LockGem s)
    {
        var items = ItemsOf(player);
        if (PlayerInventories.Row(s.InventoryId) is not byte row) return;
        if (items.Items(row).FirstOrDefault(i => i.Id == s.ItemId) is not { Item: { } worn } ||
            s.Socket >= worn.Sockets.Count || worn.Sockets[(int)s.Socket].Gem is not { } gem) return;
        byte flags = (byte)((gem.GemFlags & ~4) | (s.Locked ? 4 : 0));
        var after = items.Change(row, s.ItemId, it =>
        {
            var sockets = it.Sockets.ToList();
            sockets[(int)s.Socket] = sockets[(int)s.Socket] with { Gem = PlayerInventories.PreserveGemIdentity(gem, gem with { GemFlags = flags }) };
            return it with { Sockets = sockets };
        });
        if (after is null) return;
        await Send(player, InstanceItems.InventoryChange(s.InventoryId, items.NextRevision(row), [after.Id], [after]),
            $"0x00bf inventory {s.InventoryId}: item {after.Id} socket {s.Socket} gem {(s.Locked ? "locked" : "unlocked")}");
        Console.WriteLine($"[gateway] {player.Connection.Remote} {CharacterName(player)}'s {ItemsFor(player).GemName(gem)} in item {after.Id} socket {s.Socket}: {(s.Locked ? "level locked" : "level unlocked")}");
    }

    // C→S 0x2f: a socketed gem goes onto the cursor; the item is replaced in its inventory.
    public static async Task UnsocketGem(Player player, InstanceItems.SocketGem s)
    {
        var items = ItemsOf(player);
        (InventoryItem Item, InventoryItem Held)? done = null;
        Item? removedBefore = null;
        IReadOnlyList<PlayerSkillSource> removedSources = [];
        string why = "unknown inventory";
        byte row = default;
        using (ProjectileMutation(player, advance: false, phase: "gem unsocket"))
        {
            if (PlayerInventories.Row(s.InventoryId) is byte currentRow)
            {
                row = currentRow;
                if (items.Items(row).FirstOrDefault(i => i.Id == s.ItemId) is { } host
                    && s.Socket < host.Item.Sockets.Count)
                {
                    removedBefore = host.Item.Sockets[(int)s.Socket].Gem;
                    if (removedBefore is not null)
                        removedSources = GemSourceRowsLocked(player, host.Id, (int)s.Socket, removedBefore);
                }
                done = items.Unsocket(row, s.ItemId, (int)s.Socket, items.NewItemId, out why);
                if (done is not null && ItemModStats.EquipmentRows.Contains(row))
                {
                    RetireDirectMinionSourcesLocked(player, removedSources);
                    ForgetGemActivation(player, done.Value.Held.Item);
                    RecomputePoolsAndSpeed(player);
                    ReconcileBeastCompanionsLocked(player);
                }
            }
        }
        if (done is not { } result)
        {
            Console.WriteLine($"[gateway] {player.Connection.Remote} <= 0x002f unsocket {s}: refused ({why})");
            return;
        }
        var (item, held) = result;
        await Send(player, InstanceItems.InventoryChange(s.InventoryId, items.NextRevision(row), [item.Id], [item]),
            $"0x00bf inventory {s.InventoryId}: item {item.Id} without the gem in socket {s.Socket}");
        await Send(player, InstanceItems.InventoryChange(PlayerInventories.InventoryId(PlayerInventories.Cursor),
            items.NextRevision(PlayerInventories.Cursor), [], [held]), $"0x00bf cursor: hold gem {held.Id}");
        if (ItemModStats.EquipmentRows.Contains(row)) await SendEquipment(player, items);
    }

    // Removing a physical source is an explicit deactivation, unlike town travel or a temporary actor restriction.
    public static void ForgetGemActivation(Player player, Item gem)
    {
        var effects = ItemsFor(player).GemEffects(gem).Select(e => SkillsFor(player).EffectId(e)).OfType<string>().ToArray();
        foreach (string effect in effects)
        {
            CharacterSaves.SetAuraActive(CharacterName(player), effect, false);
            if (effect == PetrifiedBlood.EffectId)
                CharacterSaves.SetPetrifiedBlood(CharacterName(player), false);
        }
    }

    // A normal (non-cluster) jewel. The PassiveJewels1 pane (Inventories.dat row 23) holds one per allocated passive tree
    // socket; cluster jewels use the expansion cells (see IsLargeClusterJewel).
    public static bool IsNormalJewel(Item item) => Items.ByHash(item.TypeHash) is { Class: "Jewel" or "AbyssJewel" } type
        && !type.Id.StartsWith("Metadata/Items/Jewels/JewelPassiveTreeExpansion", StringComparison.Ordinal);

    /// <summary>A Wildwood charm (ItemClasses AnimalCharm): a single-modifier magic item that goes in one of the
    /// Primalist's three charm sockets, not in an ordinary jewel socket.</summary>
    public static bool IsCharm(Item item) => Items.ByHash(item.TypeHash) is { Class: "AnimalCharm" };

    /// <summary>The Large Cluster Jewel base type: the only cluster jewel the tree sockets take so far (Voices).</summary>
    public const string LargeClusterJewelId = "Metadata/Items/Jewels/JewelPassiveTreeExpansionLarge";

    /// <summary>The cluster jewel size of an item: 2 large, 1 medium, 0 small, null for a normal jewel.</summary>
    public static int? ClusterSize(Item item) => Items.ByHash(item.TypeHash)?.Id switch
    {
        "Metadata/Items/Jewels/JewelPassiveTreeExpansionLarge" => 2,
        "Metadata/Items/Jewels/JewelPassiveTreeExpansionMedium" => 1,
        "Metadata/Items/Jewels/JewelPassiveTreeExpansionSmall" => 0,
        _ => null,
    };

    public static bool IsLargeClusterJewel(Item item) => ClusterSize(item) == 2;

    /// <summary>Whether a jewel fits a pane cell: a normal jewel anywhere, a cluster jewel only in a socket of its own
    /// size (a large tree socket takes a large jewel, a large jewel's cells medium ones, a medium one's cell small
    /// ones). The client builds the layout from the cell's size and pads a jewel that cannot fill it with the empty
    /// <c>affliction_empty_node_</c> ("Nothingness"), so a smaller jewel would offer nodes that grant nothing.</summary>
    public static bool JewelFitsCell(int cell, Item item)
    {
        if (IsNormalJewel(item)) return true;
        // A cluster jewel of a smaller size also fits a larger socket: the client pads the cell with empty nodes, so
        // the jewel's own passives are the only ones that give stats.
        return ClusterSize(item) is int size && size <= Passives.ClusterCellSize(cell);
    }

    /// <summary>Why this cluster jewel cannot leave its socket, or null: jewels socketed inside it (in its own sockets,
    /// at any depth) must come out first, and removing it costs one refund point.</summary>
    public static string? ClusterRemovalRefusal(Player player, InventoryItem leaving)
    {
        if (ClusterSize(leaving.Item) is null) return null;
        var items = ItemsOf(player);
        foreach (int cell in ClusterCells(leaving.X))
            if (items.Items(PlayerInventories.PassiveJewels).Any(i => i.X == cell))
                return "take the jewels out of the cluster first";
        // The client checks every cluster node the tree lists, so a jewel may not leave while its own passives are
        // still allocated: they would be listed for a jewel the client no longer has and it fails
        // PassiveAllocation. Another cluster jewel's passives do not block it.
        if (ClusterOwnPassives(player, leaving) > 0) return "refund the cluster jewel's passives first";
        return player.Character.RefundPoints > 0 ? null : "removing a cluster jewel costs 1 refund point (none left)";
    }

    /// <summary>Every expansion cell a cluster jewel in this pane cell creates, its own sockets' included.</summary>
    public static IEnumerable<int> ClusterCells(int cell)
    {
        foreach (int child in Passives.ClusterSlots(cell))
        {
            yield return child;
            foreach (int nested in ClusterCells(child)) yield return nested;
        }
    }

    /// <summary>The allocated passives of that one cluster jewel: the ids in a tree socket's block (its sockets
    /// excluded), or an expansion cell's share of the ids outside every tree socket block, in pane order.</summary>
    public static int ClusterOwnPassives(Player player, InventoryItem item)
    {
        if (ClusterSize(item.Item) is null) return 0;
        var allocated = player.Character.ClusterPassives;
        // A mapped jewel (a tree socket or a measured expansion cell) owns exactly the ids its layout puts on a small or
        // notable position: an id outside that block, such as a stale one the client no longer builds, must not block
        // the jewel's removal.
        if (ClusterJewelLayout.For(item.X, item.Item) is { } layout)
            return allocated.Count(id => id >= layout.Base && id < layout.Base + layout.Total
                && layout.ByOffset.GetValueOrDefault(id - layout.Base) != ClusterJewelLayout.Kind.None);
        if (Passives.ClusterBlockBase(item.X) is int block)
            return allocated.Count(id => id >= block && id < block + 12);
        var items = ItemsOf(player);
        int known = 0;
        foreach (var other in items.Items(PlayerInventories.PassiveJewels).Where(i => ClusterSize(i.Item) is not null))
            if (Passives.ClusterBlockBase(other.X) is int otherBlock)
                known += allocated.Count(id => id >= otherBlock && id < otherBlock + 12);
        int left = allocated.Count - known;
        foreach (var other in items.Items(PlayerInventories.PassiveJewels)
                     .Where(i => ClusterSize(i.Item) is not null).OrderBy(i => i.X))
        {
            if (Passives.ClusterBlockBase(other.X) is not null) continue;
            int share = Math.Min(left, ClusterNodeBudget(other.Item));
            if (other.Id == item.Id) return share;
            left -= share;
        }
        return 0;
    }

    /// <summary>Spends the refund point a cluster jewel's removal costs.</summary>
    public static void SpendClusterRemoval(Player player)
    {
        if (player.Character.RefundPoints == 0) return;
        player.Character.RefundPoints--;
        CharacterSaves.SetRefundPoints(CharacterName(player), player.Character.RefundPoints);
        player.Character.SavedPassiveProgress = new(player.Character.MainAscendancy,
            player.Character.Passives, player.Character.MasteryEffects, player.Character.RefundPoints);
    }

    /// <summary>The Adorned's modifier: increased effect of jewel socket passives containing corrupted magic jewels.</summary>
    public static ushort? AdornedModHash => Items.Mods.ById("CorruptedMagicJewelModEffectUnique__1")?.Hash;

    /// <summary>The total increased effect The Adorned gives to corrupted magic jewels in passive tree sockets.</summary>
    public static int AdornedPercent(IEnumerable<InventoryItem> treeJewels)
    {
        ushort? adorned = AdornedModHash;
        int total = 0;
        foreach (var jewel in treeJewels)
        {
            if (jewel.Item.Rarity != ItemRarity.Unique) continue;
            foreach (var mod in jewel.Item.Mods)
                if (adorned is ushort hash && mod.Hash == hash && mod.Values.Count > 0) total += mod.Values[0];
        }
        return total;
    }

    /// <summary>A corrupted magic jewel's mods scaled by The Adorned's increased effect. Non-magic, uncorrupted and
    /// other jewels are unchanged; the scaling is the game's increased-effect multiplication on each integer value.</summary>
    public static Item AdornedJewel(Item item, int percent)
    {
        if (percent == 0 || item.Rarity != ItemRarity.Magic || !item.Corrupted) return item;
        // One scaled copy per jewel and percentage: the stat sources' cache compares worn items by reference (and then
        // their mod lists), so a new copy at every build missed it each time.
        if (AdornedJewels.TryGetValue(item, out var kept) && kept.Percent == percent) return kept.Item;
        var scaled = ScaledModifiers(item, percent);
        AdornedJewels.AddOrUpdate(item, new(percent, scaled));
        return scaled;
    }

    /// <summary>Every modifier of one item raised by an increased-modifier-effect percentage, in every list (implicit,
    /// explicit, enchant …): the game's increased-effect multiplication on each integer value, truncated toward zero
    /// as The Adorned's jewel effect and <c>ItemModStats</c>' own explicit-effect stats are.</summary>
    public static Item ScaledModifiers(Item item, int percent) => percent == 0 ? item : item with
    {
        Mods = [.. item.Mods.Select(mod => mod with
        {
            Values = [.. mod.Values.Select(value =>
                (int)Math.Clamp((long)value * Math.Max(0, 100 + percent) / 100, int.MinValue, int.MaxValue))],
        })],
    };

    static Item CachedScaledQuiver(Item item, int percent)
    {
        if (percent == 0) return item;
        if (ScaledQuivers.TryGetValue(item, out var kept) && kept.Percent == percent) return kept.Item;
        var scaled = ScaledModifiers(item, percent);
        ScaledQuivers.AddOrUpdate(item, new(percent, scaled));
        return scaled;
    }

    /// <summary>The equipped quiver's modifiers raised by <c>quiver_mod_effect_+%</c> (the Bow Mastery's Bow5, "20%
    /// increased bonuses gained from your Equipped Quiver"; Widowhail and Crucible weapon-tree mods also grant it).
    /// Scales every modifier on the quiver, implicit and explicit alike, on a cached copy (preserving reference
    /// equality for stat-source caches) and leaves the item in inventory unchanged. The percentage comes from the
    /// allocated passives and from the other equipped items, never from the quiver's own scaled values.</summary>
    public static IReadOnlyList<Item> WithQuiverModifierEffect(IReadOnlyList<Item> active, ItemCatalog catalog,
        IReadOnlyDictionary<uint, int> passives)
    {
        bool IsQuiver(Item i) => catalog.ByHash(i.TypeHash)?.Class == "Quiver";
        if (!active.Any(IsQuiver)) return active;
        int percent = passives.GetValueOrDefault(Stat.QuiverModEffectPlusPct);
        foreach (var item in active)
            if (!IsQuiver(item))
                foreach (var (id, value) in ItemModStats.Values(item, catalog.Mods))
                    if (id == Stat.QuiverModEffectPlusPct) percent += value;
        return percent == 0 ? active : [.. active.Select(i => IsQuiver(i) ? CachedScaledQuiver(i, percent) : i)];
    }

    sealed record ScaledJewel(int Percent, Item Item);
    static readonly RuleTable<Item, ScaledJewel> AdornedJewels = new();
    static readonly RuleTable<Item, ScaledJewel> ScaledQuivers = new();

    /// <summary>Why a held item cannot go into PassiveJewels1 at (x, y), or null: the slot must be an ordinary large
    /// jewel socket whose passive node is allocated, or a cluster jewel's expansion socket whose jewel is socketed,
    /// and the item a normal jewel (a large cluster jewel goes into a tree socket only).</summary>
    public static string? PassiveJewelRefusal(Player player, int x, int y, Item? item)
    {
        if (y != 0) return "a passive jewel socket is one cell";
        if (Passives.CharmNodeForSlot(x) is { } charmNode)
        {
            if (item is null) return "no held item";
            if (ItemsFor(player).ByHash(item.TypeHash) is not { } charmType) return "unknown item type";
            if (charmType.Class != "AnimalCharm") return "only a Wildwood charm goes in a charm socket";
            if (!player.Character.Passives.Contains(charmNode) && !player.Character.DescendancyNodes.Contains(charmNode))
                return "the charm socket is not allocated";
            int charmNeeds = ItemModStats.LevelRequirement(item, charmType.DropLevel, ItemsFor(player).Mods);
            return player.Pools.Level < charmNeeds
                ? $"{charmType.Name} requires level {charmNeeds} (character is {player.Pools.Level})" : null;
        }
        bool expansion = Passives.ClusterNodeForSlot(x) is not null;
        if (!expansion && Passives.JewelNodeForSlot(x) is not { } node) return "not a passive jewel socket";
        if (expansion)
        {
            if (!ClusterSlotOpen(player, x)) return "that cluster jewel socket is not open";
        }
        else
        {
            if (!player.Character.Passives.Contains(Passives.JewelNodeForSlot(x)!.Value)) return "the passive jewel socket is not allocated";
        }
        if (item is null) return "no held item";
        if (!item.Identified) return "identify the jewel before socketing it";
        if (ItemsFor(player).ByHash(item.TypeHash) is not { } type) return "unknown item type";
        if (type.Class is not ("Jewel" or "AbyssJewel")) return $"{type.Class} does not go in a passive jewel socket";
        if (!IsNormalJewel(item))
        {
            if (ClusterSize(item) is not int clusterSize) return "that cluster jewel size is not supported yet";
            if (ClusterNodeBudget(item) <= 0) return "this cluster jewel has no passive skills to expand";
            if (expansion && Passives.ClusterParent(x) is int parentCell
                && ItemsOf(player).Items(PlayerInventories.PassiveJewels).FirstOrDefault(i => i.X == parentCell) is { } parentHolder
                && ClusterSize(parentHolder.Item) is int parentSize && clusterSize >= parentSize)
                return $"{type.Name} does not fit inside this cluster socket";
        }
        var jewelMods = ItemsFor(player).Mods;
        bool spectrum = GrandSpectrum.IsSpectrum(item, jewelMods);
        if (spectrum)
        {
            int limit = ItemsFor(player).Limits.Get("grand_spectrum", 3);
            if (limit > 0 && ItemsOf(player).Items(PlayerInventories.PassiveJewels)
                .Count(placed => placed.X != x && GrandSpectrum.IsSpectrum(placed.Item, jewelMods)) >= limit)
                return $"only {limit} Grand Spectrum jewels can be socketed";
        }
        if (TimelessJewelCatalog.IsTimeless(item, ItemsFor(player)))
        {
            if (expansion) return "timeless jewels go in an ordinary passive tree socket";
            int limit = ItemsFor(player).Limits.Get("timeless_jewel", SystemSettings.Default.TimelessJewelLimit);
            if (limit > 0)
            {
                var items = ItemsOf(player);
                int held = items.Items(PlayerInventories.PassiveJewels)
                    .Count(i => i.X != x && TimelessJewelCatalog.IsTimeless(i.Item, ItemsFor(player)));
                if (held >= limit) return limit == 1
                    ? "only one Historic timeless jewel can be socketed"
                    : $"only {limit} Historic timeless jewels can be socketed";
            }
        }
        var radius = RadiusJewelCatalogue.ReadConversion(item, jewelMods);
        if (!spectrum && radius is not { TattooEffectPct: > 0 } && item.Rarity == ItemRarity.Unique && item.NameWords is [var nameWord])
        {
            var uniques = UniquesFor(player);
            var limitedJewel = uniques.All.FirstOrDefault(d => d.JewelLimit > 0 && uniques.NameWord(d) == nameWord);
            if (limitedJewel is not null && ItemsOf(player).Items(PlayerInventories.PassiveJewels)
                .Count(j => j.X != x && j.Item.Rarity == ItemRarity.Unique && j.Item.NameWords.Contains(nameWord)) >= limitedJewel.JewelLimit)
                return $"only {limitedJewel.JewelLimit} {limitedJewel.Name} can be socketed";
        }
        if (ImpossibleEscapeCatalogue.Read(item, ItemsFor(player).Mods) is not null)
        {
            int limit = ItemsFor(player).Limits.Get("impossible_escape", SystemSettings.Default.ImpossibleEscapeLimit);
            if (limit > 0)
            {
                var items = ItemsOf(player);
                int held = items.Items(PlayerInventories.PassiveJewels)
                    .Count(i => i.X != x && ImpossibleEscapeCatalogue.Read(i.Item, ItemsFor(player).Mods) is not null);
                if (held >= limit) return limit == 1
                    ? "only 1 Impossible Escape can be socketed"
                    : $"only {limit} Impossible Escape can be socketed";
            }
        }
        if (radius is { TattooEffectPct: > 0 })
        {
            int limit = ItemsFor(player).Limits.Get("radius:Warrior's Tale", 1);
            if (limit > 0 && ItemsOf(player).Items(PlayerInventories.PassiveJewels).Count(placed =>
                placed.X != x && RadiusJewelCatalogue.ReadConversion(placed.Item, jewelMods) is { TattooEffectPct: > 0 }) >= limit)
                return $"only {limit} Warrior's Tale can be socketed";
        }
        if (expansion && radius is not null)
            return "radius unique jewels go in an ordinary passive tree socket";
        if (RadiusJewelCatalogue.EntryOf(item, ItemsFor(player).Mods) is { } limited)
        {
            int baseLimit = limited.Limit > 0 ? limited.Limit : SystemSettings.Default.RadiusJewelDefaultLimit;
            int limit = ItemsFor(player).Limits.Get($"radius:{limited.Name}", baseLimit);
            var items = ItemsOf(player);
            if (limit > 0 && items.Items(PlayerInventories.PassiveJewels)
                .Count(i => i.X != x && RadiusJewelCatalogue.IsSameEntry(i.Item, item, ItemsFor(player).Mods)) >= limit)
                return $"only {limit} {limited.Name} can be socketed";
        }
        if (!JewelFitsCell(x, item)) return $"{type.Name} does not fit this socket";
        int needs = ItemModStats.LevelRequirement(item, type.DropLevel, ItemsFor(player).Mods);
        return player.Pools.Level < needs ? $"{type.Name} requires level {needs} (character is {player.Pools.Level})" : null;
    }

    /// <summary>The passive nodes a cluster jewel adds, its sockets excluded. "Adds N Passive Skills" counts the sockets
    /// as passive skills too, so a large jewel with 8 passives and 2 sockets has 6 small/notable passives and 2 sockets
    /// (Voices is the exception: its empty passives come on top of its sockets).</summary>
    public static int ClusterPassiveCount(Item item) =>
        Math.Max(0, ClusterNodeBudget(item) - ClusterSocketCount(item));

    /// <summary>The allocatable nodes of one cluster jewel: the "Adds N Passive Skills" count (sockets included), or
    /// Voices' empty passives plus its sockets.</summary>
    public static int ClusterNodeBudget(Item item)
    {
        if (ClusterSize(item) is null) return 0;
        var local = ItemModStats.Local(item, Items.Mods);
        int empty = local.GetValueOrDefault(Stat.LocalUniqueJewelGrantsXEmptyPassives);
        if (empty > 0) return empty + ClusterSocketCount(item);
        if (ClusterSize(item) == 0 && ClusterJewelStats.Keystone(item) is not null) return 1;
        return Math.Max(0, local.GetValueOrDefault(Stat.LocalJewelExpansionPassiveNodeCount));
    }

    /// <summary>The cluster jewel nodes a character can have allocated: the allocatable nodes of every socketed cluster
    /// jewel.</summary>
    public static int ClusterNodeBudget(Player player) =>
        ItemsOf(player).Items(PlayerInventories.PassiveJewels)
            .Where(i => ClusterSize(i.Item) is not null
                && (Passives.JewelNodeForSlot(i.X) is not null || ClusterSlotOpen(player, i.X)))
            .Sum(i => ClusterNodeBudget(i.Item));

    /// <summary>The jewel sockets a cluster jewel creates: its own override (Voices adds three) or the base size's
    /// two for large, one for medium, none for small (the 3.11 socket enchantment is not read yet).</summary>
    public static int ClusterSocketCount(Item item)
    {
        int size = ClusterSize(item) ?? -1;
        if (size < 0) return 0;
        int over = ItemModStats.Local(item, Items.Mods).GetValueOrDefault(Stat.LocalJewelExpansionJewelsCountOverride);
        if (over > 0) return over;
        if (size == 1 && ClusterJewelStats.Entries(item).Any(e => e.Stat == Stat.LocalAfflictionJewelDisplaySmallNodesGrantNothing && e.Value != 0))
            return 0; // Megalomaniac: one empty small and three notables.
        return size switch { 2 => 2, 1 => 1, _ => 0 };
    }

    /// <summary>Whether the pane cell's cluster jewel socket exists: it is an expansion cell whose parent cell holds a
    /// cluster jewel one size larger (a large one opens its medium cells, a medium one its small cell) and the cell is
    /// one of the sockets that jewel has. The tree socket must be allocated for the jewel to be there at all, which the
    /// socketing path already checks.</summary>
    public static bool ClusterSlotOpen(Player player, int cell)
    {
        if (Passives.ClusterNodeForSlot(cell) is null) return false;
        if (Passives.ClusterParent(cell) is not int parent) return false;
        var items = ItemsOf(player);
        var holder = items.Items(PlayerInventories.PassiveJewels).FirstOrDefault(i => i.X == parent);
        if (holder is null || ClusterSize(holder.Item) is not int size || size <= 0) return false;
        if (ClusterSocketCount(holder.Item) <= 0 || !Passives.ClusterSlots(parent).Contains(cell)) return false;
        if (ClusterJewelLayout.For(parent, holder.Item) is { } layout)
            return layout.ChildSockets.ContainsKey(cell);
        return size > Passives.ClusterCellSize(cell);
    }

    /// <summary>Whether a cluster jewel is socketed: in a tree socket (any size the socket takes, so a medium cluster
    /// jewel in a large socket included) or in another cluster jewel's socket. Such a jewel gives the client expansion
    /// nodes, which is what the tree message carries in its cluster list.</summary>
    public static bool ClusterJewelSocketed(Player player)
    {
        var items = ItemsOf(player);
        return items.Items(PlayerInventories.PassiveJewels).Any(i => ClusterSize(i.Item) is not null
            && (Passives.JewelNodeForSlot(i.X) is not null || ClusterSlotOpen(player, i.X)));
    }

    /// <summary>Recomputes and sends the stats after a jewel entered or left a passive tree socket. The pane's own 0xbf
    /// is sent by the item move; this is the 0x1b3/0x2bc half.</summary>
    public static async Task SendPassiveJewelStats(Player player)
    {
        RecomputePoolsAndSpeed(player);
        player.PlayerLife = LifeReservation.Clamp(player.PlayerLife, LifeReservationOf(player));
        player.PlayerMana = Math.Min(player.PlayerMana, (uint)UnreservedMana(player));
        await SendCharacterStats(player, PlayerStatChange.Build);
        // 0x1b3 carries the new maximum; the current life needs its own 0x1b1, or the client keeps it above a maximum
        // that a refund lowered (a "5000 of 4997" overfill).
        var timeless = ItemsOf(player).Items(PlayerInventories.PassiveJewels)
            .Select(i => TimelessJewelCatalog.Read(i.Item, ItemsFor(player).Mods)).FirstOrDefault(s => s is not null);
        Console.WriteLine($"[gateway] {player.Connection.Remote} passive jewel stats: life {player.Pools.Life}, mana {player.Pools.Mana}, " +
            $"melee {player.Pools.MeleePhysicalDamagePct}, " +
            $"{player.Character.ClusterPassives.Count}/{ClusterNodeBudget(player)} cluster node(s)" +
            (timeless is null ? "" : $", timeless {timeless.Name} {timeless.DisplayedSeed} {timeless.ConquerorName}"));
        Note($"0x01b1 player life {player.PlayerLife} (passive jewels)");
        await SendPlayerMana(player, $"0x01b1 player mana {player.PlayerMana} (passive jewels)");
    }

    /// <summary>Why these ordinary nodes cannot be refunded, or null: a jewel socket that still holds a jewel must be
    /// emptied first (the client has no automatic return of a passive tree jewel).</summary>
    public static string? PassiveJewelRemovalRefusal(Player player, IEnumerable<ushort> removed)
    {
        var items = ItemsOf(player);
        foreach (var node in removed)
        {
            if (Passives.JewelSlotForNode(node) is not int slot) continue;
            if (items.Items(PlayerInventories.PassiveJewels).Any(i => i.X == slot))
                return $"{Passives.Find(node)?.Name ?? "the jewel socket"} has a jewel; take it out first";
        }
        return null;
    }

    // Taking an Intuitive Leap/Thread of Hope out while a passive still depends on its bypass would leave an
    // unconnected node granting stats. Refuse the move until those passives are refunded; the tree validation then
    // proves the remaining allocation.
    public static string? AllocationBypassRemovalRefusal(Player player, InventoryItem leaving)
    {
        if (Passives.ClusterNodeForSlot(leaving.X) is not null) return null;
        bool bypassJewel = RadiusJewelCatalogue.ReadConversion(leaving.Item, ItemsFor(player).Mods)
                is { AllocateDisconnected: true }
            || ImpossibleEscapeCatalogue.Read(leaving.Item, ItemsFor(player).Mods) is not null;
        if (!bypassJewel) return null;
        var bypass = AllocationBypassNodes(player, player.Character.Passives, leaving.Id);
        try
        {
            ValidateOrdinaryPassives(player, player.Character.Passives, player.Character.MainAscendancy, bypass);
            return null;
        }
        catch (AscendancyRuleException)
        {
            return "refund the passives allocated through this jewel first";
        }
    }

    /// <summary>Moves jewels of sockets that are no longer allocated (an old save, a level trim or a refund by another
    /// path) into the main inventory. A jewel in an unknown slot, a cluster jewel in a socket that does not take it and
    /// an unsupported cluster size are moved the same way; when the inventory is full it stays in the pane but its stats
    /// are not counted.</summary>
    public static void NormalizePassiveJewels(Player player)
    {
        var items = ItemsOf(player);
        HashSet<ushort> allocated;
        allocated = [.. player.Character.Passives, .. player.Character.DescendancyNodes];
        static int SocketDepth(int cell) =>
            Passives.JewelNodeForSlot(cell) is not null || Passives.CharmNodeForSlot(cell) is not null ? 0
            : Passives.ClusterParent(cell) is int p && Passives.JewelNodeForSlot(p) is not null ? 1 : 2;
        foreach (var item in items.Items(PlayerInventories.PassiveJewels).OrderBy(i => SocketDepth(i.X)).ToArray())
        {
            bool valid = item.Item.Identified && (Passives.CharmNodeForSlot(item.X) is { } charmNode
                ? IsCharm(item.Item) && allocated.Contains(charmNode)
                : Passives.ClusterNodeForSlot(item.X) is not null
                    ? ClusterSlotOpen(player, item.X) && JewelFitsCell(item.X, item.Item)
                    : Passives.JewelNodeForSlot(item.X) is { } node && allocated.Contains(node) && JewelFitsCell(item.X, item.Item));
            if (valid) continue;
            var removed = items.Remove(PlayerInventories.PassiveJewels, item.Id);
            if (removed is null) continue;
            var shape = ShapeOf(removed.Item);
            if (shape is { } s && items.FreeCell(PlayerInventories.MainInventory, s.Width, s.Height, ShapeOf) is { } cell)
            {
                var placed = items.Add(PlayerInventories.MainInventory, cell.X, cell.Y, removed.Item);
                Console.WriteLine($"[gateway] passive jewel {removed.Id} at slot {removed.X} is not in an allocated ordinary socket; moved to the inventory as {placed.Id}");
            }
            else
            {
                items.Put(PlayerInventories.PassiveJewels, removed);
                Console.WriteLine($"[gateway] passive jewel {removed.Id} at slot {removed.X} is not valid and the inventory is full; its stats are not counted");
            }
        }
    }

    // C→S 0x16 /destroy (the town popup's Destroy): the held item is gone.
    public static async Task DestroyHeldItem(Player player)
    {
        if (await TakeHeld(player, ItemsOf(player), "destroyed") is null)
            Console.WriteLine($"[gateway] {player.Connection.Remote} <= 0x0016 /destroy: nothing held");
    }

    // The instance's items on the ground, where they were left (InstanceStore): dropped or looted on an earlier connection.
    public static Task SendGroundItems(Player player, bool generated)
    {
        SyncGround(player, generated);
        return Task.CompletedTask;
    }

    // C→S 0x70: a flask key. The flask in that cell of the flask bar spends its charges (S→C 0xbf with the changed item) and
    // its life or mana comes back, instantly or over its recovery time, under the flask_effect_life / _mana buff
    // (13-items.md §8): its quality, its mods and the character's flask stats count (FlaskCharges, FlaskRecovery). Without
    // enough charges, while dead, or when its recovery pools are full, nothing happens. A utility flask goes to UseUtilityFlask.
    public static async Task UseFlask(Player player, uint slot)
    {
        if (!CurrentGameWork) return;
        var items = ItemsOf(player);
        var flask = items.Items(PlayerInventories.Flask).FirstOrDefault(i => i.X == slot);
        if (flask is not null && ItemsFor(player).ByHash(flask.Item.TypeHash) is { } tinctureType && ItemsFor(player).TinctureOf(tinctureType) is { } tincture)
        {
            await UseTincture(player, slot, flask, tinctureType, tincture);
            return;
        }
        if (flask is not null && ItemsFor(player).ByHash(flask.Item.TypeHash) is { } utilityType && ItemsFor(player).UtilityFlaskOf(utilityType) is { } utility)
        {
            await UseUtilityFlask(player, slot, flask, utilityType, utility);
            return;
        }
        if (flask is null || ItemsFor(player).ByHash(flask.Item.TypeHash) is not { } type || ItemsFor(player).FlaskOf(type) is not { } use)
        {
            Console.WriteLine($"[gateway] {player.Connection.Remote} <= 0x0070 use flask {slot}: {(flask is null ? "no flask there" : "not a flask (not handled)")}");
            return;
        }
        Dictionary<uint, int> character;
        int playerLevel;
        // Keep level and coefficient sources in one use snapshot, including across the asynchronous charge spend.
        playerLevel = player.Pools.Level;
        character = new Dictionary<uint, int>(CharacterPlayerStats(player));
        int Get(uint key) => character.GetValueOrDefault(key);
        var local = ItemModStats.Local(flask.Item, ItemsFor(player).Mods);
        var charges = FlaskCharges.Of(use, local, Get);
        if (player.PlayerLife == 0 || Flasks.Spend(flask.Item, charges) is null)
        {
            Console.WriteLine($"[gateway] {player.Connection.Remote} <= 0x0070 use flask {slot} ({type.Name}): {(player.PlayerLife == 0 ? "dead" : $"{flask.Item.Charges}/{charges.MaxCharges} charges, a use needs {charges.ChargesPerUse}")}");
            return;
        }
        bool canRecover, copyLife;
        FlaskRecovery.Use r;
        ResourceEffectsState recoveryOwner;
        int adjacentEffect = AdjacentTinctureFlaskEffect(player, (byte)slot, Get, AreaTime(player));
        Func<uint, int> flaskCharacter = adjacentEffect == 0 ? Get
            : stat => stat == Stat.FlaskEffectPlusPct ? Get(stat) + adjacentEffect : Get(stat);
        double finalMultiplier = MapFlaskEffectMultiplier(player);
        recoveryOwner = ResourceEffectsOf(player);
        r = FlaskRecovery.For(use, flask.Item, local,
            stat => flaskCharacter(stat) + (stat == Stat.FlaskLifeToRecoverPlusPct ? Lifetap.FlaskRecovery(player) : 0),
            player.PlayerLife, player.PlayerMaxLife, playerLevel, finalMultiplier,
            lifeAmountMultiplier: Get(Stat.KeystoneEmperorsHeart) != 0 ? .7 : 1);
        copyLife = ManaFlaskCopiesLife(r, character);
        string? RecoveryUseRestriction(Func<uint, int> read)
        {
            if (use.LifePerUse > 0 && read(Stat.CannotUseLifeFlasks) != 0) return "life flask use disabled";
            if (use.ManaPerUse > 0 && read(Stat.CannotUseManaFlasks) != 0) return "mana flask use disabled";
            return null;
        }
        string? blockedReason = RecoveryUseRestriction(Get);
        bool RecoveryTargetAvailable(Func<uint, int> read)
        {
            uint lifeCap = LifeCap(player);
            return read(Stat.CannotBeAffectedByFlasks) == 0
                && (Flasks.CanRecover(use, player.PlayerLife, lifeCap, player.PlayerMana, (uint)UnreservedMana(player))
                    || copyLife && player.PlayerLife < lifeCap || use.LifePerUse > 0 && CanRecoverFlaskEnergyShield(player, read)
                    || ConditionalFlaskAilmentProtectionTriggered(player, local, AreaTime(player)))
                || CanRecoverRaisedMinionsWithFlask(player, use, read);
        }
        canRecover = blockedReason is null && RecoveryTargetAvailable(Get);
        bool CurrentRecovery()
        {
            if (!ResourceEffectsCurrent(player, recoveryOwner) || !CurrentGameWork || player.LeavingArea || player.PlayerLife == 0
                || !items.Items(PlayerInventories.Flask).Any(i => i.Id == flask.Id && i.X == slot)) return false;
            var current = CharacterPlayerStats(player);
            return RecoveryUseRestriction(current.GetValueOrDefault) is null && RecoveryTargetAvailable(current.GetValueOrDefault);
        }
        if (!canRecover)
        {
            string reason = blockedReason ?? "recovery pools full";
            await Send(player, InstanceItems.ItemUsed(flask.Item.TypeHash,
                PlayerInventories.InventoryId(PlayerInventories.Flask), flask.Id, result: 0x4e2),
                $"0x0263 flask {slot} ({type.Name}) refused: {reason} (0x4e2), charges unchanged");
            // The client can hold a lower pool than the server (a use it started but the server did not pay for): the
            // refusal carries the authoritative value, so the orb and the next press agree.
            if (use.LifePerUse > 0)
            {
                uint life;
                life = player.PlayerLife;
                Note($"0x01b1 player life {life} (full pool, flask refused)");
            }
            if (use.ManaPerUse > 0)
                await SendPlayerMana(player, $"0x01b1 player mana {player.PlayerMana} (full pool, flask refused)");
            return;
        }
        if (!CurrentRecovery()) return;
        // The flask's "during effect" mods (Divination Distillate), scaled by its increased effect.
        var during = UtilityFlasks.DuringEffectSources(local,
            FlaskEffectScaling.Multiplier(flaskCharacter, playerLevel, local.GetValueOrDefault(Stat.LocalFlaskEffectPlusPct),
                finalMultiplier: finalMultiplier));
        // This flag stays with its paid recovery rather than entering the reusable character stat snapshot.
        uint freeMana = Stat.LocalUniqueFlaskNoManaCostWhileHealing;
        if (local.GetValueOrDefault(freeMana) != 0)
            during.Add(new("recovery mana cost waiver", freeMana, 1));
        int? deathEpoch = null;
        await SpendFlask(player, flask, charges, slot, CurrentRecovery, spent =>
        {
            var current = CharacterPlayerStats(player);
            deathEpoch = AcceptRecoveryFlaskLocked(player, slot, spent, type, use, r, copyLife, local, current.GetValueOrDefault,
                current.GetValueOrDefault(Stat.CannotBeAffectedByFlasks) == 0, during);
        });
        if (deathEpoch is int epoch)
            await RunOnAreaFrame(player, () => PlayerDiedAtEpoch(player, $"death from {type.Name}", epoch));
    }

    // Gives each life, mana and utility flask in the flask bar up to `charges` charges (int.MaxValue: full) and saves them (or, for
    // the refills of kills and the like, lets them wait for the next save: PlayerInventories.ChangeUnsaved): the
    // flasks that changed.
    public static List<InventoryItem> FillFlasks(PlayerInventories items, int charges, bool saveNow = true, ItemCatalog? catalog = null) =>
        Flasks.Fill(items, catalog ?? Items, charges, saveNow);

    // FillFlasks, and the changed flasks to the client (one S→C 0xbf).
    public static Task GainFlaskCharges(Player player, int charges, string why) =>
        charges != int.MaxValue
            ? GainFlaskCharges(player, (_, _) => charges, why)
            : ChangeFlaskBar(player, items => Flasks.Fill(items, ItemsFor(player), charges, saveNow: false,
                    canGain: flask => CanGainFlaskCharges(player, flask)) is { Count: > 0 } changed
                    ? ([.. changed.Select(c => c.Id)], changed) : null,
                changed => $"0x00c6 flasks: {string.Join(", ", changed.Select(c => $"{c.X}: {c.Item.Charges}"))} charges ({why})");

    // A new character's class weapon lies on the ground a few cells from where it appears, as on the real game's Twilight
    // Strand, until it is picked up (PlayerInventories.StartWeapon): put once into each instance it enters outside towns.
    public static async Task PlaceStartWeapon(Player player)
    {
        if (!GameEvents.AllowsLoot(player)) return;
        if (ItemsOf(player).StartWeapon is not { } weapon || WaypointCatalog.Areas.Find(player.Layout.Hash) is { IsTown: true }) return;
        // PoE's isometric screen-up axis is (+1, +1). Place the starter weapon ~100 px forward/up on screen from the
        // player's spawn (~(+10, +10) grid cells) rather than Layout.Near's (-6, -6) behind the player toward the water.
        var (x, y) = PlaceForwardOfSpawn(player.Layout);
        var ground = new WorldItemObject(x, y, weapon);
        // Put here on an earlier connection: SendGroundItems has sent it already, if it still lies here.
        if (player.Instance.PlaceStartWeapon(CharacterName(player), ground, player) is not { } id) return;
        await SendGroundItem(player, id, ground,
            $"0x02bb create world item 0x{id:x} start weapon {ItemsFor(player).ByHash(weapon.TypeHash)?.Name} at ({x}, {y}), spawn ({player.Layout.SpawnX}, {player.Layout.SpawnY})");
    }

    static (int X, int Y) PlaceForwardOfSpawn(AreaLayout layout)
    {
        int tx = layout.SpawnX + 10, ty = layout.SpawnY + 10;
        if (layout.Walkable is not { } walkable) return (tx, ty);
        if (walkable(tx, ty)) return (tx, ty);
        for (int r = 1; r <= 12; r++)
            for (int dy = -r; dy <= r; dy++)
                for (int dx = -r; dx <= r; dx++)
                    if (Math.Max(Math.Abs(dx), Math.Abs(dy)) == r && walkable(tx + dx, ty + dy))
                        return (tx + dx, ty + dy);
        return layout.Near((layout.SpawnX, layout.SpawnY));
    }

    // C→S 0x29: drop the held item where the player stands, as a WorldItem object (0x2bb + wake zone 0x1a9).
    public static async Task DropItem(Player player)
    {
        if (await TakeHeld(player, ItemsOf(player), "dropped") is not { } held)
        {
            Console.WriteLine($"[gateway] {player.Connection.Remote} <= 0x0029 drop: nothing held");
            return;
        }
        var (x, y) = player.Walk.Position(AreaTime(player));
        var ground = new WorldItemObject(x, y, held.Item) { PlayerDropped = true };
        uint id = player.Instance.Ground.Add(ground, player);
        await SendGroundItem(player, id, ground,
            $"0x02bb create world item 0x{id:x} at ({x}, {y})", justDropped: true);
    }

    // C→S 0x24 / 0x018f on a world item: walk there, then auto-equip into an empty compatible slot (when AutoEquipFlag
    // is set and the inventory panel is closed) or put it in the cursor / main inventory, and remove the object (0x2bd).
    public static async Task PickUpGroundItem(Player player, uint id, WorldItemObject ground,
        InstanceTransfer.UseSkillOnObject use, double requestedAt)
    {
        // A quest item done with (its remove_flag set: a glyph after the placement, a Medicine Chest after Nessa has one, a
        // copy left in another instance) is not needed any more: it goes, unpicked.
        var questFlags = QuestFlagsOf(player, ground.Item);
        if (questFlags is not null && QuestItems.Removed(questFlags, f => ClientHasQuestFlag(player, f)))
        {
            bool removed;
            if (HardcoreProgressBlocked(player)) return;
            removed = player.Instance.Ground.Take(id) is not null;
            if (removed)
                await Game.SendWorldRemoval(player, id, $"0x02bd remove quest item 0x{id:x}: {questFlags.Remove} is set");
            return;
        }
        int tolerance = PickupToleranceOf(player, ground.Item);
        if (!TryBeginObjectWalk(player, requestedAt, ground.GridX, ground.GridY, PickupReach, out var objectWalk, centreRange: true, tolerance: tolerance)) return;
        var (action, now, here, stop, arrive) = objectWalk;
        if (!player.Walk.InReachOf(stop, (ground.GridX, ground.GridY), (int)PickupReach, tolerance))
        {
            Console.WriteLine($"[gateway] {player.Connection.Remote} <= 0x0024 pick up world item 0x{id:x} at ({ground.GridX}, {ground.GridY}) from ({here.X}, {here.Y}): out of reach (stopped at ({stop.X}, {stop.Y}))");
            return;
        }
        bool inReachAtStart = player.Walk.InReachOf(here, (ground.GridX, ground.GridY), (int)PickupReach, tolerance);
        Console.WriteLine($"[gateway] {player.Connection.Remote} <= 0x0024 pick up world item 0x{id:x} at ({ground.GridX}, {ground.GridY}) {use} from ({here.X}, {here.Y}): reached at ({stop.X}, {stop.Y}) in {arrive - now:0.00} s");
        // Echo the interaction at once, from where the player is now (as a move's echo does): the client then walks to its
        // own pickup range itself. An echo sent on arrival carried the server's estimated stop, and the client snapped to it.
        // A lockstep client got its start from the frame pass already (use.Started).
        if (!use.Started)
            await SendWorldRecord(player, player.Id, InstanceProtocol.ActionStarted(player.Id, here.X, here.Y, ground.GridX, ground.GridY, use.SkillId, use.Sequence, use.Flags, targetObjectId: id),
                $"0x01ab player picks up world item 0x{id:x}", still: () => PlayerActionCurrent(player, action));
        if (player.Connection.LockstepClient && use.Started)
        {
            // When already in reach at the action's frame start, the client's Interaction starts its 0.1 s completion in
            // this frame while MoveTo finishes any deferred stop step; do not delay another frame. When walking up,
            // step frame-by-frame instead of AreaDelay(arrive - now), which rounds/overshoots and misses blocker stops.
            if (!inReachAtStart)
            {
                double previousStep = double.NegativeInfinity;
                while (PlayerActionCurrent(player, action) && !player.Closed && !player.LeavingArea)
                {
                    double stepAt = player.Connection.LockstepFrame?.Start ?? AreaTime(player);
                    if (player.Walk.InReachOf(player.Walk.Position(stepAt), (ground.GridX, ground.GridY), (int)PickupReach, tolerance)
                        || !player.Walk.Moving(stepAt)) break;
                    if (stepAt <= previousStep) break;
                    previousStep = stepAt;
                    await AreaDelay(player, FrameMs / 1000d);
                }
            }
        }
        else if (arrive > now) await AreaDelay(player, arrive - now);
        if (!PlayerActionCurrent(player, action)) return;
        double arrivalAt = player.Connection.LockstepClient ? player.Connection.LockstepFrame?.Start ?? AreaTime(player) : AreaTime(player);
        if (!player.Walk.InReachOf(player.Walk.Position(arrivalAt), (ground.GridX, ground.GridY), (int)PickupReach, tolerance)) return;
        // Interaction's default completion lasts 0.1 s (3.29.3c, 0x1419478b0). A Move queued before it ends
        // starts in Actor's completion callback, then its path is discarded by 0x141e9b860. Keep the request
        // pending on the server until that completion has passed; the walk-up itself remains interruptible.
        if (player.Connection.LockstepClient && use.Started)
            player.LockstepBusyUntil = Math.Max(player.LockstepBusyUntil, arrivalAt + .1);
        if (IsGold(ground))
        {
            await PickUpGold(player, id, ground, "picked up");
            return;
        }
        var items = ItemsOf(player);
        var shape = ShapeOf(ground.Item) ?? new(1, 1, "");
        // With the inventory panel open the item goes to the cursor (when it is free), otherwise to the first free cell.
        bool toCursor = (use.Flags & InstanceTransfer.InventoryOpenFlag) != 0 && items.Items(PlayerInventories.Cursor).Count == 0;
        byte row = toCursor ? PlayerInventories.Cursor : PlayerInventories.MainInventory;
        if (!toCursor && shape.MaxStack > 1)
        {
            if (HardcoreProgressBlocked(player)) return;
            if (TakeGroundStack(player, id, ground, out bool stackFull) is not { } pickup)
            {
                if (stackFull) await RedropFullInventory(player, id, ground);
                return;
            }
            await SendPickup(player, id, pickup);
            return;
        }
        InventoryItem? placed = null;
        byte[]? inventoryPacket = null;
        List<string> pickupFlags = [];
        (byte X, byte Y) cell = (0, 0);
        bool inventoryFull = false;
        if (HardcoreProgressBlocked(player)) return;
        using (ProjectileMutation(player, advance: false, phase: "ground item pickup"))
        {
            if (!toCursor)
            {
                if ((use.Flags & InstanceTransfer.InventoryOpenFlag) == 0 && use.AutoEquip
                    && AutoEquipSlotLocked(player, items, ground.Item, shape) is var (equipRow, equipCell))
                {
                    row = equipRow;
                    cell = equipCell;
                }
                else if (items.FreeCell(PlayerInventories.MainInventory, shape.Width, shape.Height, ShapeOf) is { } free)
                {
                    cell = free;
                }
                else
                {
                    Console.WriteLine($"[gateway] {player.Connection.Remote} world item 0x{id:x}: no room in the inventory");
                    inventoryFull = true;
                }
            }
            if (!inventoryFull && player.Instance.Ground.Take(id) is not null)
            {
                // The start weapon stops waiting; the Put below saves both changes at once.
                if (player.Instance.IsStartWeapon(CharacterName(player), id) && items.ClaimStartWeapon())
                    Console.WriteLine($"[gateway] {player.Connection.Remote} start weapon picked up");

                bool isEquipment = ItemModStats.EquipmentRows.Contains(row);
                var sourcesBefore = isEquipment ? PlayerSkillSourcesLocked(player) : [];
                var itemToPut = row == PlayerInventories.Flask
                    ? FlaskCharges.WhenEquipped(ground.Item, safeArea: InTown(player) || HideoutCatalog.ByArea(player.Layout.Hash) is not null)
                    : ground.Item;

                // A quest item sets its flags (grant_flag, extra_flag) with the pickup, in the same save.
                bool HasLeague(int flag) => player.Character.LeagueQuestFlags.Contains(flag);
                pickupFlags = questFlags is null ? [] : QuestItems.PickupFlags(questFlags, f => ClientHasQuestFlag(player, f), HasLeague);
                placed = KeepOnGroundIfNotSaved(player, ground, () =>
                {
                    InventoryItem Put() => items.Put(row, new InventoryItem(items.NewItemId(), cell.X, cell.Y, itemToPut));
                    if (CharacterSaves.Load(CharacterName(player))?.Tutorial is { } tutorial)
                        foreach (bool support in new[] { false, true })
                            if (player.Instance.IsStartWeapon(TutorialObjects.DropKey(CharacterName(player), tutorial, support), id))
                                return TutorialObjects.Claim(CharacterName(player), support, ground.Item, Put);
                    return questFlags is not null ? QuestItems.Claim(CharacterName(player), questFlags, Put, HasLeague) : Put();
                }, id);
                if (placed is not null)
                {
                    if (isEquipment)
                    {
                        using (new PlayerStatScope(player, fresh: true))
                        {
                            RecomputePoolsAndSpeed(player);
                            ReconcileBeastCompanionsLocked(player);
                            ReconcileEquipmentSourceLifetimesLocked(player, sourcesBefore);
                        }
                    }
                    inventoryPacket = InstanceItems.InventoryChange(PlayerInventories.InventoryId(row), items.NextRevision(row), [], [placed]);
                }
            }
            else if (!inventoryFull) return;   // already picked up
        }
        if (inventoryFull)
        {
            await RedropFullInventory(player, id, ground);
            return;
        }
        await Game.SendWorldRemoval(player, id, $"0x02bd remove world item 0x{id:x} (picked up from ({stop.X}, {stop.Y}))");
        if (placed is null || inventoryPacket is null) return;
        uint inventoryId = PlayerInventories.InventoryId(row);
        await Send(player, inventoryPacket,
            toCursor ? $"0x00bf cursor: hold item {placed.Id}"
            : row == PlayerInventories.MainInventory ? $"0x00bf inventory 1: add item {placed.Id} at ({cell.X}, {cell.Y})"
            : $"0x00bf inventory {inventoryId}: auto-equip item {placed.Id} at ({cell.X}, {cell.Y})");
        if (ItemModStats.EquipmentRows.Contains(row)) await SendEquipment(player, items);
        if (row == PlayerInventories.Flask)
        {
            await ReconcileTincturesAfterFlaskBarChange(player);
            await RefreshMagebloodFlasks(player);
        }
        if (pickupFlags.Count > 0) await SetQuestFlags(player, pickupFlags);
        if (questFlags is not null) await SyncQuestItems(player, removeItems: false);
    }

    static readonly byte[] AutoEquipRows =
    [
        PlayerInventories.Weapon1, PlayerInventories.Offhand1,
        PlayerInventories.BodyArmour, PlayerInventories.Helmet, PlayerInventories.Gloves,
        PlayerInventories.Boots, PlayerInventories.Belt, PlayerInventories.Amulet,
        PlayerInventories.Ring1, PlayerInventories.Ring2, PlayerInventories.Flask,
    ];

    // Finds an empty compatible equipment or flask slot for a ground item when the client's auto_equip flag (0x0008) is
    // set, checking identification, level/attribute/gem requirements and inventory-refusal hooks. Weapon1/Offhand1 always
    // hold the active weapon set (SwapWeaponSets trades them with rows 14/15). Call under ProjectileMutation.
    static (byte Row, (byte X, byte Y) Cell)? AutoEquipSlotLocked(Player player, PlayerInventories items, Item item,
        PlayerInventories.Shape shape)
    {
        if (!item.Identified || string.IsNullOrEmpty(shape.Class)) return null;
        var catalog = ItemsFor(player);
        if (catalog.ByHash(item.TypeHash) is not { } type) return null;
        List<byte>? candidates = null;
        foreach (byte candidate in AutoEquipRows)
        {
            if (!PlayerInventories.Accepts(candidate, shape.Class)) continue;
            if (GameEvents.FirstInventoryRefusal(player, candidate, item) is not null) continue;
            (candidates ??= []).Add(candidate);
        }
        var target = candidates is null ? null : items.FindEmptySlot(candidates, shape, ShapeOf);
        if (target is null && shape.Class == "Ring"
            && GameEvents.FirstInventoryRefusal(player, PlayerInventories.Ring3, item) is null
            && CharacterPlayerStats(player).GetValueOrDefault(Stat.EnableRingSlot3) != 0)
            target = items.FindEmptySlot([PlayerInventories.Ring3], shape, ShapeOf);
        if (target is null) return null;
        return AutoEquipRequirementsMetLocked(player, catalog, item, type) ? target : null;
    }

    static bool AutoEquipRequirementsMetLocked(Player player, ItemCatalog catalog, Item item, ItemCatalog.BaseType type)
    {
        int reqLevel = ItemModStats.LevelRequirement(item, type.DropLevel, catalog.Mods);
        var (rawStr, rawDex, rawInt) = ItemModStats.AttributeRequirements(catalog, item, type);
        int reqPct = AttributeRequirementsPct(player);
        int reqStr = ScaledRequirement(rawStr, reqPct);
        int reqDex = ScaledRequirement(rawDex, reqPct);
        int reqInt = ScaledRequirement(rawInt, reqPct);
        bool ignoreAttributes = player.Pools.IgnoreAttributeRequirements;
        bool ignoreGemAttributes = ignoreAttributes || SocketedGemAttributeRequirementsIgnored(catalog, item);
        foreach (var socket in item.Sockets)
        {
            if (socket is { Colour: SocketColour.Abyss, Gem: { } jewel } && catalog.ByHash(jewel.TypeHash) is { Class: "AbyssJewel" } eye)
                reqLevel = Math.Max(reqLevel, ItemModStats.LevelRequirement(jewel, eye.DropLevel, catalog.Mods));
            else if (socket.Gem is { } gem)
            {
                if (catalog.ByHash(gem.TypeHash) is { } gemBase && !LeagueFor(player).Rules.AllowsGemBase(gemBase.Id))
                    return false;
                var g = SocketedGemRequirements(player, gem, reqPct, ignoreGemAttributes);
                reqLevel = Math.Max(reqLevel, g.Level);
                reqStr = Math.Max(reqStr, g.Str);
                reqDex = Math.Max(reqDex, g.Dex);
                reqInt = Math.Max(reqInt, g.Int);
            }
        }
        if (player.Pools.Level < reqLevel) return false;
        if (ignoreAttributes) return true;
        if (reqStr > 0 && RequirementAttribute(player, Stat.Strength, player.Pools.Strength) < reqStr) return false;
        if (reqDex > 0 && RequirementAttribute(player, Stat.Dexterity, player.Pools.Dexterity) < reqDex) return false;
        if (reqInt > 0 && RequirementAttribute(player, Stat.Intelligence, player.Pools.Intelligence) < reqInt) return false;
        return true;
    }

    public static bool IsGold(WorldItemObject ground) => Items.ByHash(ground.Item.TypeHash)?.Class == ItemDrops.GoldClass;

    // The longest step between two gold checks that counts as a path walked (a dash, a late tick): a longer one is a
    // teleport (a gate, a portal), whose line crosses ground the player never stood near.
    const double GoldPathLimit = 80;

    /// <summary>Picks up the gold within the auto-loot radius of the way the player went since the last check
    /// (<paramref name="from"/>, as a straight line), not only of where it stands now: at a walk's 37 cells/s a 250 ms
    /// tick moves 9 cells, a movement skill or a late tick many more, and gold passed between two checks stayed there.
    /// Returns where the player stood, for the next check.</summary>
    internal static async Task<(int X, int Y)> PickUpNearbyGold(Player player,
        (int X, int Y)? from = null)
    {
        var at = player.Walk.Position(AreaTime(player));
        if (!CurrentGameWork || player.Closed || player.LeavingArea || player.Paused || player.PlayerLife == 0
            || !GameEvents.AllowsLoot(player)) return at;
        var start = from is { } f && Dist(f, at) <= GoldPathLimit ? f : at;
        double radius = SystemSettings.Default.GoldAutoLootRadius;
        var middle = ((start.X + at.X) / 2, (start.Y + at.Y) / 2);
        List<(uint Id, WorldItemObject Gold)>? batch = null;
        (uint Id, WorldItemObject Gold) first = default;
        int found = 0;
        foreach (var (id, gold) in player.Instance.Ground.Within(middle.Item1, middle.Item2, radius + Dist(start, at) / 2 + 1, IsGold))
        {
            if (DistanceToPath((gold.GridX, gold.GridY), start, at) > radius) continue;
            if (found == 0) first = (id, gold);
            else
            {
                if (batch is null) { batch = [first]; }
                batch.Add((id, gold));
            }
            found++;
        }
        if (found == 0) return at;
        if (found == 1)
        {
            await PickUpGold(player, first.Id, first.Gold!, "auto-looted", start);
            return at;
        }
        await PickUpGoldBatch(player, batch!, start);
        return at;
    }

    // Auto-loots multiple gold piles in one send gate and one stash Persist() instead of N sequential SQLite writes.
    static async Task PickUpGoldBatch(Player player, List<(uint Id, WorldItemObject Gold)> piles, (int X, int Y) pathFrom)
    {
        if (player.Connection.LockstepClient || InAreaFrame(player))
        {
            var ready = new List<(uint Id, WorldItemObject Gold, WorldObject Target)>(piles.Count);
            bool inFrame = InAreaFrame(player);
            for (int i = 0; i < piles.Count; i++)
            {
                var (id, gold) = piles[i];
                if (!inFrame && !player.Connection.WorldOutput.Has(id)) continue;
                var target = player.Connection.WorldOutput.PublishedObject(id);
                if (target.IsReady) ready.Add((id, gold, target));
                else
                {
                    await PickUpGold(player, id, gold, "auto-looted", pathFrom);
                    if (!CurrentGameWork || player.Closed || player.LeavingArea) return;
                }
            }
            if (ready.Count == 0) return;
            WorldObject[] targets = new WorldObject[ready.Count];
            for (int i = 0; i < ready.Count; i++) targets[i] = ready[i].Target;
            byte[]? goldUpdate = null;
            await SendWorldBatch(player, targets, _ =>
            {
                if (!CurrentGameWork || player.Closed || player.LeavingArea || player.PlayerLife == 0 || player.Paused
                    || HardcoreProgressBlocked(player) || !GameEvents.AllowsLoot(player)) return null;
                var here = player.Walk.Position(AreaTime(player));
                double radius = SystemSettings.Default.GoldAutoLootRadius;
                List<(uint Id, WorldItemObject Gold, WorldObject Target, (object? By, bool Shared) Origin)>? taken = null;
                long added = 0;
                for (int i = 0; i < ready.Count; i++)
                {
                    var (id, gold, target) = ready[i];
                    if (!IsGold(gold) || DistanceToPath((gold.GridX, gold.GridY), pathFrom, here) > radius) continue;
                    var origin = player.Instance.Ground.Origin(id);
                    if (!origin.Shared && !ReferenceEquals(origin.By, player)) continue;
                    if (player.Instance.Ground.Take(id, gold) is null) continue;
                    (taken ??= new(ready.Count)).Add((id, gold, target, origin));
                    added += gold.Item.StackSize;
                }
                if (taken is null) return null;
                uint total;
                try { total = PersonalStashOf(player).AddGold(added); }
                catch
                {
                    for (int i = 0; i < taken.Count; i++)
                        player.Instance.Ground.Restore(taken[i].Id, taken[i].Gold, taken[i].Origin.By, taken[i].Origin.Shared);
                    throw;
                }
                int popupAmount = (int)Math.Min(added, int.MaxValue);
                goldUpdate =
                [
                    .. InstanceEntities.Gold(total),
                    .. InstanceEntities.WorldPopupIcon(here.X, here.Y, player.Id, WorldPopupGoldCoin, popupAmount),
                ];
                var records = new OutputRecord[taken.Count * 2];
                for (int i = 0; i < taken.Count; i++)
                {
                    records[i * 2] = PresentedRecord(taken[i].Target,
                        InstanceEntities.UpdateTask(taken[i].Id, AutoPickupWorldItemTask, []), null);
                    var removeBytes = InstanceProtocol.RemoveObject(taken[i].Id);
                    System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(removeBytes.AsSpan(6), uint.MaxValue);
                    records[i * 2 + 1] = OutputRecord.Remove(taken[i].Target, WorldOrder.Last, removeBytes,
                        i == 0 ? $"gold auto-looted x{taken.Count} (+{added})" : null);
                }
                return new OutputBatch(records);
            });
            if (goldUpdate is { Length: > 0 }) await Send(player, goldUpdate, null);
            return;
        }
        foreach (var (id, gold) in piles)
        {
            await PickUpGold(player, id, gold, "auto-looted", pathFrom);
            if (!CurrentGameWork || player.Closed || player.LeavingArea) return;
        }
    }

    // How far a cell is from the straight way between two others.
    static double DistanceToPath((int X, int Y) at, (int X, int Y) from, (int X, int Y) to)
    {
        double dx = to.X - from.X, dy = to.Y - from.Y, length = dx * dx + dy * dy;
        double t = length <= 0 ? 0 : Math.Clamp(((at.X - from.X) * dx + (at.Y - from.Y) * dy) / length, 0, 1);
        double x = from.X + t * dx - at.X, y = from.Y + t * dy - at.Y;
        return Math.Sqrt(x * x + y * y);
    }

    // Gold goes to the stash's gold of the character's league (S→C 0x2aa), not into the inventory. Auto-loot takes it
    // within the radius of the way from pathFrom to where the player is when the pickup is sent.
    public static async Task PickUpGold(Player player, uint id, WorldItemObject ground, string how,
        (int X, int Y)? pathFrom = null)
    {
        // The removal goes as a world record, after the pile's create: on a lockstep client the create applies only at
        // its frame's end, and a removal sent at once beside it (gold dropped at the player's feet) came first, was
        // ignored, and left a pile on the ground that was already taken.
        byte[]? update = null;
        string log = $"gold 0x{id:x} {how} (+{ground.Item.StackSize})";
        if (player.Connection.LockstepClient || InAreaFrame(player))
        {
            // Only a pile whose create was admitted (SeenGround is set at that gate): asking for the removal of one not yet
            // sent marked its id as published, and its create, still to come in the kill's output, then failed and closed
            // the connection (live 2026-11-16). The next tick takes it.
            // SeenGround alone is not that: the ground sync marks a pile the player's own kill put down as seen before
            // the kill's output creates it (a projectile kill's drops wait in its session), so the world output must also
            // hold the pile's create (live 2026-11-17: Fireball kills, auto-looted gold, the create failed).
            if (!InAreaFrame(player) && !player.Connection.WorldOutput.Has(id)) return;
            await SendWorldRecordsEndingInRemoval(player, id, () =>
            {
                if (Take() is not { } pickupUpdate) return null;
                update = pickupUpdate;
                return [InstanceEntities.UpdateTask(id, AutoPickupWorldItemTask, []), InstanceProtocol.RemoveObject(id)];
            }, log);
            if (update is { Length: > 0 }) await Send(player, update, null);
            return;
        }
        await Send(player, [], log, atGate: () =>
        {
            if (Take() is not { } pickupUpdate) return null;
            return [.. InstanceEntities.UpdateTask(id, AutoPickupWorldItemTask, []), .. InstanceProtocol.RemoveObject(id), .. pickupUpdate];
        });

        byte[]? Take()
        {
            if (!CurrentGameWork || player.Closed || player.LeavingArea || player.PlayerLife == 0 || player.Paused
                || HardcoreProgressBlocked(player) || !GameEvents.AllowsLoot(player) || !IsGold(ground)) return null;
            if (how == "auto-looted")
            {
                var here = player.Walk.Position(AreaTime(player));
                if (DistanceToPath((ground.GridX, ground.GridY), pathFrom ?? here, here) > SystemSettings.Default.GoldAutoLootRadius) return null;
            }
            var (by, shared) = player.Instance.Ground.Origin(id);
            if (!shared && !ReferenceEquals(by, player)) return null;
            if (TakeGroundStack(player, id, ground) is not { } pickup) return null;
            return [.. pickup.Update, .. InstanceEntities.WorldPopupIcon(ground.GridX, ground.GridY, player.Id, WorldPopupGoldCoin, ground.Item.StackSize)];
        }
    }

    static bool IsTrackedGroundDrop(Player player, uint id)
    {
        string name = CharacterName(player);
        if (player.Instance.IsStartWeapon(name, id)) return true;
        if (CharacterSaves.Load(name)?.Tutorial is { } tutorial)
            foreach (bool support in new[] { false, true })
                if (player.Instance.IsStartWeapon(TutorialObjects.DropKey(name, tutorial, support), id))
                    return true;
        return false;
    }

    // Clicking a ground item with no room plays the character's InvFull voice line (S→C 0x0117) and re-drops the item
    // on the ground (remove old id + create a fresh id with WorldItem +0x50 = 1 so the client replays its bounce and
    // drop/filter sound). Tracked tutorial/start-weapon ids keep their id intact so claim tracking stays valid.
    static async Task RedropFullInventory(Player player, uint id, WorldItemObject ground)
    {
        await Send(player, InstanceItems.InventoryFull(), $"0x0117 inventory full (world item 0x{id:x})");
        if (IsTrackedGroundDrop(player, id)) return;
        var origin = player.Instance.Ground.Origin(id);
        if (!origin.Shared && !ReferenceEquals(origin.By, player)) return;
        if (player.Instance.Ground.Take(id, ground) is null) return;
        uint redroppedId = player.Instance.Ground.Add(ground, origin.By, shared: origin.Shared, justDropped: true);
        await SendWorldRemoval(player, id, $"0x02bd remove world item 0x{id:x} (inventory full re-drop)");
        await SendGroundItem(player, redroppedId, ground,
            $"0x02bb re-drop world item 0x{redroppedId:x} at ({ground.GridX}, {ground.GridY}) (inventory full)", justDropped: true);
    }

    public sealed record GroundPickup(byte[] Update, string Log, uint RestId = 0, WorldItemObject? Rest = null);

    public static async Task SendPickup(Player player,
        uint id, GroundPickup pickup)
    {
        if (pickup.Rest is { } rest)
            await SendWorldCreate(player, pickup.RestId,
                InstanceEntities.CreateObject(pickup.RestId, InstanceEntities.ObjectTypeHash(WorldItemObject.MetadataPath), rest.Blob()),
                $"0x02bb create remainder 0x{pickup.RestId:x} from 0x{id:x}");
        await SendWorldRemoval(player, id, $"0x02bd remove world item 0x{id:x} (pickup)");
        await Send(player, pickup.Update, pickup.Log);
    }

    // Commits the visible gold or stack once. The caller checks reach and sends the captured update after the lock.
    public static GroundPickup? TakeGroundStack(Player player, uint id, WorldItemObject ground) =>
        TakeGroundStack(player, id, ground, out _);

    public static GroundPickup? TakeGroundStack(Player player, uint id, WorldItemObject ground, out bool inventoryFull)
    {
        inventoryFull = false;
        var origin = player.Instance.Ground.Origin(id);
        if ((!origin.Shared && !ReferenceEquals(origin.By, player)) || HardcoreProgressBlocked(player)) return null;
        if (!IsGold(ground) && ShapeOf(ground.Item) is not { MaxStack: > 1 }) return null;
        if (IsGold(ground))
        {
            if (player.Instance.Ground.Take(id, ground) is null) return null;
            uint total = KeepOnGroundIfNotSaved(player, ground,
                () => PersonalStashOf(player).AddGold(ground.Item.StackSize), id, origin);
            return new(InstanceEntities.Gold(total), $"0x02aa gold: {total} (+{ground.Item.StackSize})");
        }
        var items = ItemsOf(player);
        byte[] rows = ExpandedInventoryGranted(player)
            ? [PlayerInventories.MainInventory, PlayerInventories.ExpandedInventory] : [PlayerInventories.MainInventory];
        if (items.StackAcross(rows, ground.Item, ShapeOf, preview: true).Values.Any(s => s.Left > 0))
        {
            inventoryFull = ReferenceEquals(player.Instance.Ground.Get(id), ground);
            return null;
        }
        if (player.Instance.Ground.Take(id, ground) is null) return null;
        var stacked = KeepOnGroundIfNotSaved(player, ground,
            () => items.StackAcross(rows, ground.Item, ShapeOf), id, origin);
        WorldItemObject? rest = null;
        uint restId = 0;
        int left = stacked.Values.First().Left;
        if (left > 0)
        {
            rest = ground with { Item = ground.Item with { StackSize = (ushort)left } };
            restId = player.Instance.Ground.Add(rest, origin.By, shared: origin.Shared, justDropped: false);
        }
        return new([.. stacked.Where(p => p.Value.Any).SelectMany(p =>
            InstanceItems.InventoryChange(PlayerInventories.InventoryId(p.Key),
                items.NextRevision(p.Key), p.Value.RemovedIds, p.Value.AddedItems))],
            "pickup inventory", restId, rest);
    }

    // A pickup whose save fails puts the item back on the ground before the error closes the connection: it left the ground
    // first, and the items are loaded again as saved (Saves).
    public static T KeepOnGroundIfNotSaved<T>(Player player, WorldItemObject ground, Func<T> pickUp, uint? originalId = null)
        => KeepOnGroundIfNotSaved(player, ground, pickUp, originalId, (null, true));

    static T KeepOnGroundIfNotSaved<T>(Player player, WorldItemObject ground, Func<T> pickUp, uint? originalId,
        (object? By, bool Shared) origin)
    {
        try { return pickUp(); }
        catch
        {
            if (originalId is uint id) player.Instance.Ground.Restore(id, ground, origin.By, origin.Shared);
            else player.Instance.Ground.Add(ground, player);
            throw;
        }
    }

    // C→S 0x7c: Shift + click on a stack, then the split window's amount. The stack shrinks in place (0xbf removing and adding
    // its id), the split-off part is held as a new item (0xbf cursor).
    public static async Task SplitStack(Player player, InstanceItems.SplitStack split, string hex)
    {
        var items = ItemsOf(player);
        int amount = (int)Math.Min(split.Amount, ushort.MaxValue);
        if (StashOf(player).TabOf(split.InventoryId) is int tab)
        {
            // From a stash tab: the tab's stack shrinks and the split-off part is held with a character id, in one transaction.
            var stash = StashOf(player);
            if (ItemMoves.SplitFromStash(stash, items, tab, split.ItemId, amount, ShapeOf) is not var (left, part))
            {
                Console.WriteLine($"[gateway] {player.Connection.Remote} <= 0x007c split {split} in stash tab {tab}: refused: {hex}");
                return;
            }
            Console.WriteLine($"[gateway] {player.Connection.Remote} <= 0x007c split {split} in stash tab {tab}" + (left is null ? " (the whole stack: picked up)" : ""));
            await SendTabChange(player, stash, tab, [left?.Id ?? part.Id], left is null ? [] : [left],
                left is null ? $"0x00bf stash tab {tab}: remove item {part.Id}" : $"0x00bf stash tab {tab}: stack {left.Id} now {left.Item.StackSize}");
            await Send(player, InstanceItems.InventoryChange(PlayerInventories.InventoryId(PlayerInventories.Cursor),
                items.NextRevision(PlayerInventories.Cursor), [], [part]), $"0x00bf cursor: hold {part.Item.StackSize} as item {part.Id}");
            return;
        }
        string why = "unknown inventory";
        var done = PlayerInventories.Row(split.InventoryId) is byte row ? items.Split(row, split.ItemId, amount, out why) : null;
        if (done is not var (stack, held) || PlayerInventories.Row(split.InventoryId) is not byte r)
        {
            Console.WriteLine($"[gateway] {player.Connection.Remote} <= 0x007c split {split}: refused ({why}): {hex}");
            return;
        }
        Console.WriteLine($"[gateway] {player.Connection.Remote} <= 0x007c split {split}");
        await Send(player, InstanceItems.InventoryChange(split.InventoryId, items.NextRevision(r), [stack.Id], [stack]),
            $"0x00bf inventory {split.InventoryId}: stack {stack.Id} now {stack.Item.StackSize}");
        await Send(player, InstanceItems.InventoryChange(PlayerInventories.InventoryId(PlayerInventories.Cursor),
            items.NextRevision(PlayerInventories.Cursor), [], [held]), $"0x00bf cursor: hold {held.Item.StackSize} as item {held.Id}");
    }

    // C→S 0x57 / 0x58: use an item that needs no target (a right-click), such as Stacked Deck or an Orb of Regret.
    // C→S 0x57 (sendResult: true) receives a companion S→C 0x0174 ItemUseResult reply.
    public static async Task UseItem(Player player,
        InstanceItems.UseItemRequest use, string hex, bool sendResult = false)
    {
        bool ok = await UseItemCore(player, use, hex);
        if (sendResult && !player.Closed && !player.LeavingArea)
            await Send(player, InstanceItems.ItemUseResult(use.InventoryId, use.ItemId, ok ? (byte)1 : (byte)0),
                $"0x0174 item use result inv=0x{use.InventoryId:x} item=0x{use.ItemId:x} ok={(ok ? 1 : 0)}");
    }

    static async Task<bool> UseItemCore(Player player,
        InstanceItems.UseItemRequest use, string hex)
    {
        const string Regret = "Metadata/Items/Currency/CurrencyPassiveRefund";
        var items = ItemsOf(player);
        if (StashOf(player) is { } stash && stash.TabOf(use.InventoryId) is int stashTab)
        {
            if (stash.Inventory(stashTab).Items.FirstOrDefault(i => i.Id == use.ItemId) is not { } stashItem
                || ItemsFor(player).ByHash(stashItem.Item.TypeHash) is not { } stashType)
            {
                Console.WriteLine($"[gateway] {player.Connection.Remote} <= use item {use}: no such item in stash tab {stashTab}: {hex}");
                return false;
            }
            if (stash.IsPublic(stashTab))
            {
                Console.WriteLine($"[gateway] {player.Connection.Remote} <= use item {use}: refused, item is in public stash tab {stashTab}");
                return false;
            }
            if (stashType.Id == StackedDeck)
                return await UseStackedDeckFromStash(player, items, stash, stashTab, stashItem, stashType);
            if (stashType.Id == Regret)
                return await UseOrbOfRegretFromStash(player, stash, stashTab, stashItem, stashType);
            if (stashType.Id == CurrencyEffects.OrbOfUnmaking)
                return await UseOrbOfUnmakingFromStash(player, stash, stashTab, stashItem, stashType);
            Console.WriteLine($"[gateway] {player.Connection.Remote} <= use item {use}: {stashType.Name} from stash tab {stashTab} is not handled: {hex}");
            return false;
        }
        byte? row = null;
        foreach (uint inventory in new[] { use.InventoryId, use.InventoryId + 1 })
            if (PlayerInventories.Row(inventory) is byte candidate && items.Items(candidate).Any(i => i.Id == use.ItemId))
            {
                row = candidate;
                break;
            }
        if (row is not byte r || items.Items(r).FirstOrDefault(i => i.Id == use.ItemId) is not { } item)
        {
            Console.WriteLine($"[gateway] {player.Connection.Remote} <= 0x0058 use item {use}: no such item: {hex}");
            return false;
        }
        if (ItemsFor(player).ByHash(item.Item.TypeHash) is not { } type)
        {
            Console.WriteLine($"[gateway] {player.Connection.Remote} <= 0x0058 use item {use}: unknown type 0x{item.Item.TypeHash:x8}: {hex}");
            return false;
        }
        if (await GameEvents.AnyItemUse(player, r, item, type)) return true;
        if (await TryUseItemisedCorpse(player, items, r, item, type)) return true;
        if (BanditChoices.IsBookOfReform(type.Id))
            return await BanditChoices.UseBookOfReform(player, items, r, item, type);
        // A quest item with a use_flag (the quest books, Book of Regrets included): its flag, and QuestStaticRewards' points.
        if (type.Quest?.Use is not null)
            return await UseQuestItem(player, items, r, item, type);
        if (type.Name == SkillBook)
            return await UseSkillBook(player, items, r, item, type);
        if (type.Id == CurrencyEffects.OrbOfUnmaking)
            return await UseOrbOfUnmaking(player, items, r, item, type);
        if (type.Id == StackedDeck)
            return await UseStackedDeck(player, items, r, item, type);
        if (type.Id != Regret && type.Name != RegretBook)
        {
            Console.WriteLine($"[gateway] {player.Connection.Remote} <= 0x0058 use item {use}: {type.Name} is not handled: {hex}");
            return false;
        }
        // Read the current balance inside the same transaction as consumption. An older area must not restore
        // refund points another connection has already spent.
        var result = Saves.Atomically(() =>
        {
            ushort held = CharacterSaves.Load(CharacterName(player))?.RefundPoints ?? 0;
            if (held == ushort.MaxValue) return (Points: held, Taken: (PlayerInventories.Consumption?)null);
            // An Orb of Regret gives one refund point; a Book of Regrets two (ClientStrings ItemFunction2RespecPoints).
            ushort points = (ushort)Math.Min(ushort.MaxValue, held + (type.Name == RegretBook ? 2 : 1));
            return (Points: points, Taken: ItemMoves.UseForRefundPoint(items, CharacterName(player), r, use.ItemId, points));
        });
        if (result.Taken is not { } taken)
        {
            Console.WriteLine($"[gateway] {player.Connection.Remote} <= 0x0058 use {type.Name}: refund cap reached or item {use.ItemId} disappeared: {hex}");
            return false;
        }
        ushort refunds = result.Points;
        player.Character.RefundPoints = refunds;
        player.Character.SavedPassiveProgress = new(player.Character.MainAscendancy, player.Character.Passives, player.Character.MasteryEffects, refunds);
        uint inventoryId = PlayerInventories.InventoryId(r);
        // Every change removes the id first: the client keeps an item by id, so a stack that only shrinks needs the remove
        // and the add of the same id with its new Stack component (as SendMerged does).
        await Send(player,
            InstanceItems.InventoryChange(inventoryId, items.NextRevision(r), [item.Id],
                taken.Left is { } left ? [left] : []),
            $"0x00bf inventory {inventoryId}: {type.Name} used" + (taken.Left is { } now ? $", {now.Item.StackSize} left" : ", stack empty"));
        await SendPassives(player);
        Console.WriteLine($"[gateway] {player.Connection.Remote} <= 0x0058 use {type.Name} ({use.ItemId}): {refunds} refund point(s)");
        return true;
    }

    // Orb of Unmaking (C→S 0x58, right-click; CurrencyItems Action atlas_skill_point_respec, "Grants an atlas passive
    // skill refund point"): one Atlas passive respec point, which a refund (C→S 0x3f) spends; the orb is consumed.
    public static async Task<bool> UseOrbOfUnmaking(Player player,
        PlayerInventories items, byte row, InventoryItem item, ItemCatalog.BaseType type)
    {
        PlayerInventories.Consumption? taken;
        ushort respec;
        respec = AtlasRespecPoints(player);
        if (respec == ushort.MaxValue)
        {
            Console.WriteLine($"[gateway] {player.Connection.Remote} <= 0x0058 use {type.Name}: Atlas respec points are already at the maximum");
            return false;
        }
        if (items.ConsumeOne(row, item.Id) is not { } consumed) return false;
        taken = consumed;
        SetAtlasRespecPoints(player, ++respec);
        uint inventoryId = PlayerInventories.InventoryId(row);
        await Send(player,
            InstanceItems.InventoryChange(inventoryId, items.NextRevision(row), [item.Id],
                taken.Left is { } left ? [left] : []),
            $"0x00bf inventory {inventoryId}: {type.Name} used");
        await SendAtlasSlots(player, "Orb of Unmaking");
        Console.WriteLine($"[gateway] {player.Connection.Remote} <= 0x0058 use {type.Name} ({item.Id}): {respec} Atlas respec point(s)");
        return true;
    }

    static async Task<bool> UseOrbOfRegretFromStash(Player player, AccountStash stash, int tab, InventoryItem item, ItemCatalog.BaseType type)
    {
        if (HardcoreProgressBlocked(player)) return false;
        var consumed = Saves.Atomically<(ushort Points, InventoryItem? Left, bool Consumed)>(() =>
        {
            if (stash.IsPublic(tab)) return (0, null, false);
            if (stash.Inventory(tab).Items.FirstOrDefault(i => i.Id == item.Id) is not { } current) return (0, null, false);
            ushort held = CharacterSaves.Load(CharacterName(player))?.RefundPoints ?? 0;
            if (held == ushort.MaxValue) return (held, null, false);
            InventoryItem? left = null;
            if (current.Item.StackSize <= 1)
            {
                if (stash.Take(tab, item.Id) is null) return (held, null, false);
            }
            else
            {
                if (stash.Shrink(tab, item.Id, 1) is not { } shrunk) return (held, null, false);
                left = shrunk;
            }
            ushort next = (ushort)Math.Min(ushort.MaxValue, held + 1);
            CharacterSaves.SetRefundPoints(CharacterName(player), next);
            return (next, left, true);
        });
        if (!consumed.Consumed) return false;
        player.Character.RefundPoints = consumed.Points;
        player.Character.SavedPassiveProgress = new(player.Character.MainAscendancy, player.Character.Passives, player.Character.MasteryEffects, consumed.Points);
        await SendTabChange(player, stash, tab, [consumed.Left?.Id ?? item.Id],
            consumed.Left is { } remaining ? [remaining] : [],
            consumed.Left is null ? $"0x00bf stash tab {tab}: {type.Name} emptied" : $"0x00bf stash tab {tab}: {type.Name} now {consumed.Left.Item.StackSize}");
        await SendPassives(player);
        Console.WriteLine($"[gateway] {player.Connection.Remote} <= use {type.Name} ({item.Id}) from stash tab {tab}: {consumed.Points} refund point(s)");
        return true;
    }

    static async Task<bool> UseOrbOfUnmakingFromStash(Player player, AccountStash stash, int tab, InventoryItem item, ItemCatalog.BaseType type)
    {
        if (HardcoreProgressBlocked(player)) return false;
        var consumed = Saves.Atomically<(ushort Respec, InventoryItem? Left, bool Consumed)>(() =>
        {
            if (stash.IsPublic(tab)) return (0, null, false);
            if (stash.Inventory(tab).Items.FirstOrDefault(i => i.Id == item.Id) is not { } current) return (0, null, false);
            ushort respec = AtlasRespecPoints(player);
            if (respec == ushort.MaxValue) return (respec, null, false);
            InventoryItem? left = null;
            if (current.Item.StackSize <= 1)
            {
                if (stash.Take(tab, item.Id) is null) return (respec, null, false);
            }
            else
            {
                if (stash.Shrink(tab, item.Id, 1) is not { } shrunk) return (respec, null, false);
                left = shrunk;
            }
            ushort next = (ushort)(respec + 1);
            SetAtlasRespecPoints(player, next);
            return (next, left, true);
        });
        if (!consumed.Consumed) return false;
        await SendTabChange(player, stash, tab, [consumed.Left?.Id ?? item.Id],
            consumed.Left is { } remaining ? [remaining] : [],
            consumed.Left is null ? $"0x00bf stash tab {tab}: {type.Name} emptied" : $"0x00bf stash tab {tab}: {type.Name} now {consumed.Left.Item.StackSize}");
        await SendAtlasSlots(player, "Orb of Unmaking");
        Console.WriteLine($"[gateway] {player.Connection.Remote} <= use {type.Name} ({item.Id}) from stash tab {tab}: {consumed.Respec} Atlas respec point(s)");
        return true;
    }

    public const string StackedDeck = "Metadata/Items/DivinationCards/DivinationCardDeck";

    static Item? DrawStackedCard(Player player)
    {
        if (Drops.Card(AreaRng(player), 0)?.Item is { } card) return card;
        var fallback = ItemsFor(player).Find("The Gambler")
            ?? ItemsFor(player).All.FirstOrDefault(b => b.Class == "DivinationCard");
        return fallback is null ? null : ItemCatalog.Create(fallback) with { StackSize = 1 };
    }

    static async Task<bool> UseStackedDeckFromStash(Player player,
        PlayerInventories items, AccountStash stash, int tab, InventoryItem item, ItemCatalog.BaseType type)
    {
        if (DrawStackedCard(player) is not { } card) return false;
        var drawn = Saves.Atomically<
            (InventoryItem? Left, byte DestRow, IReadOnlyList<uint> Removed, IReadOnlyList<InventoryItem> Added)?>
            (() =>
        {
            if (stash.IsPublic(tab)) return null;
            if (stash.Inventory(tab).Items.FirstOrDefault(i => i.Id == item.Id) is not { } current) return null;
            bool toCursor = items.Items(PlayerInventories.Cursor).Count == 0;
            if (!toCursor && !items.Fits(PlayerInventories.MainInventory, card, ShapeOf)) return null;
            InventoryItem? left = null;
            if (current.Item.StackSize <= 1)
            {
                if (stash.Take(tab, item.Id) is null) return null;
            }
            else
            {
                if (stash.Shrink(tab, item.Id, 1) is not { } shrunk) return null;
                left = shrunk;
            }
            if (toCursor)
            {
                var onCursor = items.Add(PlayerInventories.Cursor, 0, 0, card);
                return (left, PlayerInventories.Cursor, [], [onCursor]);
            }
            var stacked = items.AddStacked(PlayerInventories.MainInventory, card, ShapeOf);
            return (left, PlayerInventories.MainInventory, [.. stacked.Changed.Select(i => i.Id)], [.. stacked.Changed, .. stacked.Added]);
        });
        if (drawn is not { } result)
        {
            Console.WriteLine($"[gateway] {player.Connection.Remote} <= use {type.Name} from stash tab {tab}: no room for drawn card or item {item.Id} disappeared");
            return false;
        }
        await SendTabChange(player, stash, tab, [result.Left?.Id ?? item.Id],
            result.Left is { } remaining ? [remaining] : [],
            result.Left is null ? $"0x00bf stash tab {tab}: {type.Name} emptied" : $"0x00bf stash tab {tab}: {type.Name} now {result.Left.Item.StackSize}");
        uint destInvId = PlayerInventories.InventoryId(result.DestRow);
        await Send(player,
            InstanceItems.InventoryChange(destInvId, items.NextRevision(result.DestRow), result.Removed, result.Added),
            $"0x00bf inventory {destInvId}: {type.Name} drew divination card");
        string cardName = ItemsFor(player).ByHash(card.TypeHash)?.Name ?? $"0x{card.TypeHash:x8}";
        Console.WriteLine($"[gateway] {player.Connection.Remote} <= use {type.Name} ({item.Id}) from stash tab {tab}: drew {cardName} -> row {result.DestRow}");
        return true;
    }

    // Stacked Deck (C->S 0x57 / 0x58, right-click; Metadata/Items/DivinationCards/DivinationCardDeck): consumes one Stacked Deck
    // and draws a random weighted Divination Card onto the cursor (if empty) or into the main inventory.
    public static async Task<bool> UseStackedDeck(Player player,
        PlayerInventories items, byte row, InventoryItem item, ItemCatalog.BaseType type)
    {
        if (DrawStackedCard(player) is not { } card) return false;
        var drawn = Saves.Atomically<
            (PlayerInventories.Consumption Taken, byte DestRow, IReadOnlyList<uint> Removed, IReadOnlyList<InventoryItem> Added)?>
            (() =>
        {
            bool toCursor = row != PlayerInventories.Cursor && items.Items(PlayerInventories.Cursor).Count == 0;
            if (!toCursor && !items.Fits(PlayerInventories.MainInventory, card, ShapeOf)) return null;
            if (items.ConsumeOne(row, item.Id) is not { } consumed) return null;
            if (toCursor)
            {
                var onCursor = items.Add(PlayerInventories.Cursor, 0, 0, card);
                return (consumed, PlayerInventories.Cursor, [], [onCursor]);
            }
            var stacked = items.AddStacked(PlayerInventories.MainInventory, card, ShapeOf);
            return (consumed, PlayerInventories.MainInventory, [.. stacked.Changed.Select(i => i.Id)], [.. stacked.Changed, .. stacked.Added]);
        });
        if (drawn is not { } result)
        {
            Console.WriteLine($"[gateway] {player.Connection.Remote} <= 0x0058 use {type.Name}: no room for drawn card or item {item.Id} disappeared");
            return false;
        }
        uint sourceInvId = PlayerInventories.InventoryId(row);
        if (result.DestRow == row)
        {
            var removed = new List<uint> { item.Id };
            foreach (uint cid in result.Removed) if (!removed.Contains(cid)) removed.Add(cid);
            var added = new List<InventoryItem>();
            if (result.Taken.Left is { } left) added.Add(left);
            added.AddRange(result.Added);
            await Send(player,
                InstanceItems.InventoryChange(sourceInvId, items.NextRevision(row), removed, added),
                $"0x00bf inventory {sourceInvId}: {type.Name} opened into same inventory");
        }
        else
        {
            await Send(player,
                InstanceItems.InventoryChange(sourceInvId, items.NextRevision(row), [item.Id],
                    result.Taken.Left is { } left ? [left] : []),
                $"0x00bf inventory {sourceInvId}: {type.Name} consumed");
            uint destInvId = PlayerInventories.InventoryId(result.DestRow);
            await Send(player,
                InstanceItems.InventoryChange(destInvId, items.NextRevision(result.DestRow), result.Removed, result.Added),
                $"0x00bf inventory {destInvId}: {type.Name} drew divination card");
        }
        string cardName = ItemsFor(player).ByHash(card.TypeHash)?.Name ?? $"0x{card.TypeHash:x8}";
        Console.WriteLine($"[gateway] {player.Connection.Remote} <= 0x0058 use {type.Name} ({item.Id}): drew {cardName} -> row {result.DestRow}");
        return true;
    }

    // A Book of Skill used (C→S 0x58, right-click; any of the quest books named so, Metadata/Items/QuestItems/SkillBooks/…):
    // the book is used up and the character gets one more passive skill point, as /add_passive_points 1 (S→C 0x33).
    public static async Task<bool> UseSkillBook(Player player, PlayerInventories items,
        byte row, InventoryItem item, ItemCatalog.BaseType type)
    {
        ushort extra;
        if (player.Character.ExtraPassivePoints == ushort.MaxValue)
        {
            Console.WriteLine($"[gateway] {player.Connection.Remote} <= 0x0058 use {type.Name}: extra passive points are already at the maximum");
            return false;
        }
        extra = (ushort)(player.Character.ExtraPassivePoints + 1);
        // The book and the point are saved together.
        if (ItemMoves.UseForPassivePoint(items, CharacterName(player), row, item.Id, extra) is not { } taken) return false;
        player.Character.ExtraPassivePoints = extra;
        uint inventoryId = PlayerInventories.InventoryId(row);
        await Send(player, InstanceItems.InventoryChange(inventoryId, items.NextRevision(row), [item.Id], taken.Left is { } left ? [left] : []),
            $"0x00bf inventory {inventoryId}: {type.Name} used");
        await SendPassives(player);
        Console.WriteLine($"[gateway] {player.Connection.Remote} <= 0x0058 use {type.Name} ({item.Id}, {type.Id}): +1 passive point, {extra} extra in all");
        return true;
    }

    /// <summary>The result of applying a currency to an item, or null when the currency refuses the target. The random is
    /// AreaRng(player) normally and <see cref="ItemRolls.ForeseeRandom"/> for a Hinekora preview/commit.</summary>
    internal static Item? CurrencyResult(Player player, Item target, uint currencyTypeHash, Random random)
    {
        const string Wisdom = "Metadata/Items/Currency/CurrencyIdentification";
        var type = ItemsFor(player).ByHash(currencyTypeHash);
        bool isEssence = type is not null && EssencesFor(player).Contains(type.Hash);
        return type?.Id switch
        {
            Wisdom => target.Identified || !target.Components.Contains("Mods") ? null : target with { Identified = true },
            CurrencyEffects.ChromaticOrb => ItemsFor(player).ByHash(target.TypeHash) is { } targetType
                ? CurrencyEffects.Chromatic(target, ItemsFor(player).Requirements(targetType), random) : null,
            CurrencyEffects.JewellersOrb => ItemsFor(player).ByHash(target.TypeHash) is { } socketType
                ? CurrencyEffects.Jewellers(target, socketType, random) : null,
            CurrencyEffects.OrbOfFusing => ItemsFor(player).ByHash(target.TypeHash) is { } linkType
                ? CurrencyEffects.Fusing(target, linkType, random) : null,
            CurrencyEffects.OrbOfAlchemy => CurrencyEffects.Alchemy(target, RollsFor(player), random),
            CurrencyEffects.ChaosOrb => CurrencyEffects.Chaos(target, RollsFor(player), random),
            CurrencyEffects.DivineOrb => RollsFor(player).Reforge(target, random, UniquesFor(player).RollsOf(target)),
            CurrencyEffects.BlessedOrb => RollsFor(player).Bless(target, random, UniquesFor(player).RollsOf(target)),
            CurrencyEffects.OrbOfScouring => CurrencyEffects.Scouring(target, RollsFor(player)),
            CurrencyEffects.OrbOfTransmutation => CurrencyEffects.Transmutation(target, RollsFor(player), random),
            CurrencyEffects.OrbOfAlteration => CurrencyEffects.Alteration(target, RollsFor(player), random),
            CurrencyEffects.OrbOfAugmentation => CurrencyEffects.Augmentation(target, RollsFor(player), random),
            CurrencyEffects.RegalOrb => CurrencyEffects.Regal(target, RollsFor(player), random),
            CurrencyEffects.ExaltedOrb => CurrencyEffects.Exalted(target, RollsFor(player), random),
            CurrencyEffects.OrbOfAnnulment => CurrencyEffects.Annulment(target, RollsFor(player), random),
            CurrencyEffects.LesserEldritchIchor or CurrencyEffects.GreaterEldritchIchor or
                CurrencyEffects.GrandEldritchIchor or CurrencyEffects.ExceptionalEldritchIchor =>
                ItemsFor(player).ByHash(target.TypeHash) is { } eldritchType &&
                CurrencyEffects.EldritchIchorTier(type!.Id) is int ichorTier
                    ? CurrencyEffects.EldritchIchor(target, eldritchType, ichorTier, RollsFor(player), random)
                    : null,
            CurrencyEffects.LesserEldritchEmber or CurrencyEffects.GreaterEldritchEmber or
                CurrencyEffects.GrandEldritchEmber or CurrencyEffects.ExceptionalEldritchEmber =>
                ItemsFor(player).ByHash(target.TypeHash) is { } emberType &&
                CurrencyEffects.EldritchEmberTier(type!.Id) is int emberTier
                    ? CurrencyEffects.EldritchEmber(target, emberType, emberTier, RollsFor(player), random)
                    : null,
            CurrencyEffects.ArmourersScrap => CurrencyEffects.ArmourerScrap(target, random),
            CurrencyEffects.SacredOrb => ItemsFor(player).ByHash(target.TypeHash) is { } defenceType
                ? CurrencyEffects.Sacred(target, ItemsFor(player).Defences(defenceType), random) : null,
            CurrencyEffects.BlacksmithsWhetstone => CurrencyEffects.Whetstone(target, random),
            CurrencyEffects.OrbOfChance => CurrencyEffects.Chance(target, RollsFor(player), random),
            CurrencyEffects.AncientOrb => UniquesFor(player).Reforge(target, DropsFor(player).UniqueWeight, random),
            FlaskEnchantments.Enkindling or FlaskEnchantments.Instilling => ItemsFor(player).ByHash(target.TypeHash) is { } flaskType
                ? FlaskEnchantments.Apply(target, flaskType, ItemsFor(player).Mods, type.Id == FlaskEnchantments.Instilling, random) : null,
            CurrencyEffects.GlassblowersBauble => CurrencyEffects.Glassblower(target, random),
            CurrencyEffects.GemcuttersPrism => CurrencyEffects.Gemcutter(target),
            CurrencyEffects.FracturingOrb => CurrencyEffects.Fracture(target, RollsFor(player), random),
            CurrencyEffects.VaalOrb => VaalCorruption.Apply(ItemsFor(player), RollsFor(player), UniquesFor(player),
                MapDevices, DropsFor(player).UniqueWeight, target, random),
            CurrencyEffects.EldritchChaosOrb => CurrencyEffects.EldritchChaos(target, RollsFor(player), random),
            CurrencyEffects.EldritchExaltedOrb => CurrencyEffects.EldritchExalted(target, RollsFor(player), random),
            CurrencyEffects.EldritchAnnulmentOrb => CurrencyEffects.EldritchAnnulment(target, RollsFor(player), random),
            CurrencyEffects.OrbOfConflict => CurrencyEffects.Conflict(target, RollsFor(player), random),
            CurrencyEffects.OrbOfBinding => ItemsFor(player).ByHash(target.TypeHash) is { } bindingType
                ? CurrencyEffects.Binding(target, bindingType, RollsFor(player), ItemsFor(player).Requirements(bindingType), random)
                : null,
            _ when CurrencyEffects.TaintedIds.Contains(type!.Id) => ItemsFor(player).ByHash(target.TypeHash) is { } taintedType
                ? CurrencyEffects.Tainted(type.Id, target, taintedType, RollsFor(player), random)
                : null,
            _ when isEssence => EssencesFor(player).Apply(type!.Hash, target, random),
            // Rows the Id list above does not name, by their CurrencyItems.Action (CurrencyEffects.TryAction): the map
            // chisels (add_map_alt_quality, Cartographer's and the Maven's ones) and any future row with that action.
            _ => type is not null && CurrencyEffects.TryAction(ItemsFor(player), RollsFor(player), target, type, random,
                    out var byAction, UniquesFor(player).RollsOf(target)) ? byAction : null,
        };
    }

    // C→S 0x71: an item used on another one. A Scroll of Wisdom identifies an unidentified item (its explicit mods and rare
    // name are sent from now on) and loses one from its stack. Socket currency uses the same update path, including Shift.
    public static async Task UseItemOn(Player player, InstanceItems.UseItemOn use, string hex)
    {
        const string Wisdom = "Metadata/Items/Currency/CurrencyIdentification";
        var items = ItemsOf(player);
        var stash = StashOf(player);
        // The currency and the target can each be in a stash tab (right-click the currency in the open stash; the target in
        // the currency tab's crafting slot, live 2026-10-23, or any tab): the message names the tab's inventory.
        var (usedSpot, used) = Locate(items, stash, use.InventoryId, use.ItemId);
        var (targetSpot, target) = Locate(items, stash, use.TargetInventoryId, use.TargetId);
        int? targetTab = targetSpot.Tab;
        byte targetRow = targetSpot.Row;
        string where = (usedSpot.Tab is int t ? $" (currency in stash tab {t})" : "") + (targetTab is int tt ? $" (target in stash tab {tt})" : "");
        if (used is null || target is null)
        {
            Console.WriteLine($"[gateway] {player.Connection.Remote} <= 0x0071 use item on item {use}: no such item{where}: {hex}");
            return;
        }
        if (usedSpot.Tab is int pubTab && stash.IsPublic(pubTab))
        {
            Console.WriteLine($"[gateway] {player.Connection.Remote} <= 0x0071 use item on item {use}: refused, currency is in public stash tab {pubTab}");
            return;
        }
        var type = ItemsFor(player).ByHash(used.Item.TypeHash);
        // A worn item (equipment, the flask bar, jewels in the passive tree) takes only quality and flask enchant
        // currency: the others' client validators refuse it with ItemErrorItemEquipped, and the client still sends 0x71
        // for one, so the server refuses it too, before anything is spent.
        if (RefusedOnWorn(player, used.Item, targetSpot))
        {
            Console.WriteLine($"[gateway] {player.Connection.Remote} <= 0x0071 use {type?.Name ?? "item"} on item {use.TargetId}: refused, item is equipped (row {targetRow})");
            await SendItemError(player, InstanceItems.ItemErrorItemEquipped);
            return;
        }
        if (RefusedByState(player, used.Item, target.Item) is ushort stateError)
        {
            Console.WriteLine($"[gateway] {player.Connection.Remote} <= 0x0071 use {type?.Name ?? "item"} on item {use.TargetId}: refused, target is {(target.Item.Mirrored ? "mirrored" : "corrupted")}{where}");
            await SendItemError(player, stateError);
            return;
        }
        bool isEssence = type is not null && EssencesFor(player).Contains(type.Hash);
        // Mirror of Kalandra: a mirrored copy of the target goes onto the (empty) cursor, as if picked up, for the player to
        // place; the target stays as it was and its place gets no message.
        if (type?.Id == CurrencyEffects.MirrorOfKalandra)
        {
            if (CurrencyEffects.Mirror(target.Item, RollsFor(player)) is not { } copy || items.Items(PlayerInventories.Cursor).Count > 0)
            {
                Console.WriteLine($"[gateway] {player.Connection.Remote} <= 0x0071 Mirror of Kalandra on item {target.Id}: refused (corrupted / mirrored / unique / unsupported class or map tier / unmodifiable / no mods / cursor not empty){where}");
                return;
            }
            if (await CommitUse(player, items, stash, usedSpot, targetSpot, type.Name, null, copy) is not { Held: { } held }) return;
            Console.WriteLine($"[gateway] {player.Connection.Remote} <= 0x0071 Mirror of Kalandra ({used.Id}) on item {target.Id}: copy {held.Id} on the cursor{where}");
            return;
        }
        // What the currency makes of the target, or null (nothing happens, nothing is spent).
        // A corrupted item takes no currency but a Scroll of Wisdom and Tainted currency (which needs one). A mirrored item takes
        // no currency at all: the wiki says a mirrored copy cannot be altered in any way except a Reflective Oil, which
        // is not modelled.
        bool foreseeing = target.Item.Foreseeing;
        var random = foreseeing ? ItemRolls.ForeseeRandom(target.Item, used.Item.TypeHash) : AreaRng(player);
        Item? result = type?.Id == CurrencyEffects.HinekorasLock
            ? RollsFor(player).ApplyLock(target.Item, random)
            : target.Item.Mirrored || target.Item.Vestigial ? null
            : target.Item.Corrupted && type?.Id != Wisdom && !CurrencyEffects.TaintedIds.Contains(type?.Id ?? "") ? null
            : type?.Id == CurrencyEffects.FacetorsLens
                ? (ItemsFor(player).ByHash(target.Item.TypeHash) is { } lensGemType
                    ? CurrencyEffects.ApplyFacetorsLens(target.Item, ItemsFor(player).Gem(lensGemType), used.Item.GemExperience)
                    : null)
            : (!foreseeing && await GameEvents.ResolveCurrencyCrafting(player, target.Item, used.Item.TypeHash, random) is { } crafted
                ? crafted
                : CurrencyResult(player, target.Item, used.Item.TypeHash, random));
        // Hinekora's Lock: the next currency commits exactly the previewed result (from 0x29d/0x2a0) and clears the
        // state; without a stored preview the seeded result is the same item. A refused currency keeps the state.
        if (foreseeing && type?.Id != CurrencyEffects.HinekorasLock)
        {
            Item? preview = player.ForesightRequest is { } request && request.TargetId == target.Id && request.CurrencyId == used.Id
                && request.Seed == target.Item.ForeseeSeed ? request.Preview : result;
            player.ForesightRequest = null;
            result = preview is null ? null : ItemRolls.ClearForesee(preview);
        }
        if (result is null)
        {
            if (type?.Id == CurrencyEffects.JewellersOrb && ItemsFor(player).ByHash(target.Item.TypeHash) is { } refusedType)
                Console.WriteLine($"[gateway] Jeweller target {target.Id}: {refusedType.Id}, class {refusedType.Class}, ilvl {target.Item.ItemLevel}, " +
                    $"sockets {target.Item.Sockets.Count}, roll cap {CurrencyEffects.MaximumSockets(target.Item, refusedType)}, " +
                    $"base cap {CurrencyEffects.SocketCapacity(target.Item, refusedType)}, gems {target.Item.Sockets.Count(s => s.Gem is not null)}");
            Console.WriteLine($"[gateway] {player.Connection.Remote} <= 0x0071 use {type?.Name ?? $"0x{used.Item.TypeHash:x8}"} on item {use.TargetId}: " +
                (type is not null && CurrencyEffects.Recognizes(ItemsFor(player), type, isEssence)
                    ? "target refused (corrupted / mirrored / identified / wrong rarity / no explicit mods / unsupported sockets / maximum sockets / socketed gems / protected links / fixed defence bases)" : $"not handled: {hex}"));
            return;
        }
        // The currency and the changed item are saved together.
        if (await CommitUse(player, items, stash, usedSpot, targetSpot, type!.Name, result, null) is not { Changed: { } identified }) return;
        if (targetTab is null && targetRow == PlayerInventories.Flask)
            await ReconcileTincturesAfterFlaskBarChange(player);
        await CheckCraftingAchievements(player, type!.Id, target.Item, identified.Item);
        if (type.Id == CurrencyEffects.FacetorsLens)
            await CheckGemLevelAchievements(player, identified.Item, ItemsFor(player).GemLevel(identified.Item));
        Console.WriteLine($"[gateway] {player.Connection.Remote} <= 0x0071 {type!.Name} ({used.Id}) on item {target.Id}: " + (type.Id == CurrencyEffects.SacredOrb ? $"defence base roll {target.Item.DefenceBaseRoll} -> {identified.Item.DefenceBaseRoll}"
            : isEssence || type.Id is Wisdom or FlaskEnchantments.Enkindling or FlaskEnchantments.Instilling or CurrencyEffects.OrbOfAlchemy or CurrencyEffects.ChaosOrb or CurrencyEffects.DivineOrb or CurrencyEffects.BlessedOrb or CurrencyEffects.OrbOfScouring or CurrencyEffects.OrbOfTransmutation or CurrencyEffects.OrbOfAlteration or CurrencyEffects.OrbOfAugmentation or CurrencyEffects.RegalOrb or CurrencyEffects.ExaltedOrb or CurrencyEffects.OrbOfAnnulment or CurrencyEffects.AncientOrb or CurrencyEffects.ArmourersScrap or CurrencyEffects.BlacksmithsWhetstone or CurrencyEffects.OrbOfChance or CurrencyEffects.VaalOrb or CurrencyEffects.GlassblowersBauble or CurrencyEffects.GemcuttersPrism or CurrencyEffects.FracturingOrb or CurrencyEffects.LesserEldritchIchor or CurrencyEffects.GreaterEldritchIchor or CurrencyEffects.GrandEldritchIchor or CurrencyEffects.ExceptionalEldritchIchor or CurrencyEffects.LesserEldritchEmber or CurrencyEffects.GreaterEldritchEmber or CurrencyEffects.GrandEldritchEmber or CurrencyEffects.ExceptionalEldritchEmber
            ? string.Join("; ", identified.Item.Mods.Select(ItemsFor(player).Mods.Describe))
            : $"sockets {string.Join(" ", identified.Item.Sockets.Select(k => k.Colour))}; link groups {string.Join(",", identified.Item.Links ?? [])}"));
    }

    // Where an item named by (inventory id, item id) is: a stash tab when the inventory is one (decided by the inventory, not
    // the item id: stash and character ids can be equal), otherwise the character inventory row holding the id.
    public static (ItemMoves.Spot Spot, InventoryItem? Item) Locate(PlayerInventories items, AccountStash stash, uint inventoryId, uint itemId)
    {
        if (stash.TabOf(inventoryId) is int tab)
            return (new(tab, 0, itemId), stash.Inventory(tab).Items.FirstOrDefault(i => i.Id == itemId));
        return items.RowOf(itemId) is byte row ? (new(null, row, itemId), items.Items(row).FirstOrDefault(i => i.Id == itemId)) : (new(null, 0, itemId), null);
    }

    // A worn item: one of the character's own slots (Inventories column 4, sent as flag bit 1: equipment and the flask bar)
    // or the passive tree's jewels (row 23). The client's currency validators (0x141f86f70, 0x141f89920) refuse these with
    // ItemErrorItemEquipped; the second weapon set's rows are not among them.
    public static bool Worn(ItemMoves.Spot spot) => spot.Tab is null
        && (spot.Row == PlayerInventories.PassiveJewels
            || spot.Row < InstanceInventories.PlayerDefaults.Length && InstanceInventories.PlayerDefaults[spot.Row] is { Equipment: true } slot && slot.Row == spot.Row);

    // Whether a currency is refused on its target because the target is worn. Quality currency (armour, weapon, flask and
    // jewellery quality, Tainted ones included) and the flask enchant orbs (Enkindling, Instilling) work on worn items in
    // the official game (live 2026-11-23); every other currency checked there (transmutation to scouring, Divine,
    // Blessed, Eldritch) is refused. Decided by the client's CurrencyItems action.
    public static bool RefusedOnWorn(Player player, Item currency, ItemMoves.Spot target) =>
        Worn(target) && !TakesWorn(ItemsFor(player).ByHash(currency.TypeHash) is { } t ? ItemsFor(player).CurrencyAction(t) : null);

    static bool TakesWorn(string? action) => action is not null
        && (action.StartsWith("add_", StringComparison.Ordinal) && action.Contains("_quality", StringComparison.Ordinal)
            && action != "add_gem_quality" && action != "add_map_alt_quality"
            || action is "add_flask_seal" or "add_flask_injector");

    // The item error a handled currency gets from its target's state, or null: a mirrored item takes no currency (a
    // Reflective Oil is not modelled), a corrupted one only a Scroll of Wisdom and Tainted currency. An item the server
    // does not handle keeps its "not handled" log instead of a reason. Vestigial items have no known string and stay silent.
    public static ushort? RefusedByState(Player player, Item currency, Item target)
    {
        if (!target.Mirrored && !target.Corrupted || ItemsFor(player).ByHash(currency.TypeHash) is not { } type
            || type.Id is not (CurrencyEffects.MirrorOfKalandra or CurrencyEffects.HinekorasLock)
                && !CurrencyEffects.Recognizes(ItemsFor(player), type, EssencesFor(player).Contains(type.Hash)))
            return null;
        if (target.Mirrored) return InstanceItems.ItemErrorItemMirrored;
        return type.Id == "Metadata/Items/Currency/CurrencyIdentification" || CurrencyEffects.TaintedIds.Contains(type.Id)
            ? null : InstanceItems.ItemErrorTargetCorrupted;
    }

    // The refusal line the player reads: BackendErrors FailedToApplyCurrency (149) "Failed to apply item: {0}" with the
    // item error's ClientStrings text (the error is the row + 1). Sent as a chat line (0x17) until the official message
    // that prints it is found.
    static async Task SendItemError(Player player, ushort error)
    {
        string reason = GameServer.Core.ClientTables.TableOrNull("ClientStrings")?.Row(error - 1)?.TextOrNull("Text") is { Length: > 0 } known
            ? known : $"error 0x{error:x4}";
        string text = $"Failed to apply item: {reason}";
        await Send(player, InstanceChat.ChatLine(text), $"0x0017 \"{text}\"");
    }

    // A currency use (C→S 0x71 / Shift 0x72) committed for any inventory and stash combination (ItemMoves.Use): one 0xbf per
    // place changed, one for both when the currency and the target share it, removing and adding the same ids as the client
    // needs; then the cursor when a new item is held. A tab the panel shows again after a tab switch unloaded it is loaded
    // again first (SendTabChange, #710). Null when nothing changed.
    static async Task<(InventoryItem? Changed, InventoryItem? Held)?> CommitUse(Player player, PlayerInventories items, AccountStash stash,
        ItemMoves.Spot used, ItemMoves.Spot target, string name, Item? change, Item? held)
    {
        if (ItemMoves.Use(items, stash, used, target, change, held) is not var (left, changed, heldItem))
        {
            Console.WriteLine($"[gateway] {player.Connection.Remote} <= 0x0071 {name} on item {target.Id}: refused (the currency or the target is gone, or something is held)");
            return null;
        }
        async Task SendPlace(ItemMoves.Spot spot, List<uint> gone, List<InventoryItem> now, string what)
        {
            if (spot.Tab is int t) await SendTabChange(player, stash, t, gone, now, $"0x00bf stash tab {t}: {what}");
            else await Send(player, InstanceItems.InventoryChange(PlayerInventories.InventoryId(spot.Row), items.NextRevision(spot.Row), gone, now),
                $"0x00bf inventory {PlayerInventories.InventoryId(spot.Row)}: {what}");
        }
        List<InventoryItem> usedNow = left is { } l ? [l] : [];
        string spent = $"{name} used" + (left is null ? ", stack gone" : $", {left.Item.StackSize} left");
        if (changed is null)
            await SendPlace(used, [used.Id], usedNow, spent);
        else if (used.Tab == target.Tab && (used.Tab is not null || used.Row == target.Row))
            await SendPlace(target, [used.Id, target.Id], [.. usedNow, changed], $"item {target.Id} changed, {spent}");
        else
        {
            await SendPlace(used, [used.Id], usedNow, spent);
            await SendPlace(target, [target.Id], [changed], $"item {target.Id} changed");
            if (target.Tab is null && PlayerInventories.ShowsOnModel(target.Row)) await SendEquipment(player, items);
        }
        if (heldItem is not null)
            await Send(player, InstanceItems.InventoryChange(PlayerInventories.InventoryId(PlayerInventories.Cursor), items.NextRevision(PlayerInventories.Cursor), [], [heldItem]),
                $"0x00bf cursor: {name} made item {heldItem.Id}");
        return (changed, heldItem);
    }

    // C→S 0x4a: the held item clicked on an item. A stack of the same type with room takes what fits (PlayerInventories.MergeOnto,
    // AccountStash.Merge); anything else (currency on an item …) is not handled yet.
    public static async Task HeldOntoItem(Player player, InstanceItems.ItemAction a, string hex)
    {
        var items = ItemsOf(player);
        if (StashOf(player).TabOf(a.InventoryId) is int tab)
        {
            // On a stash tab: as 0x2a on the clicked item's cell (merge, one with Shift, or a swap).
            if (StashOf(player).Inventory(tab).Items.FirstOrDefault(i => i.Id == a.ItemId) is not { } onto)
            {
                Console.WriteLine($"[gateway] {player.Connection.Remote} <= 0x004a held item onto {a} in stash tab {tab}: no such item: {hex}");
                return;
            }
            Console.WriteLine($"[gateway] {player.Connection.Remote} <= 0x004a held item onto {a} in stash tab {tab}: {hex}");
            await PlaceInStash(player, tab, new InstanceItems.PlaceItem(a.InventoryId, onto.X, onto.Y, (byte)(OneOf(a) ? 1 : 0), a.B, 0, 0));
            return;
        }
        Item? held = items.Items(PlayerInventories.Cursor).FirstOrDefault()?.Item;
        string why = "unknown inventory";
        PlayerInventories.Placement? done = null;
        bool spentClusterRemoval = false;
        if (PlayerInventories.Row(a.InventoryId) is not byte row) why = "unknown inventory";
        else if (row == PlayerInventories.PassiveJewels)
        {
            using (ProjectileMutation(player, advance: false, phase: "passive jewel held placement"))
            {
                held = items.Items(PlayerInventories.Cursor).FirstOrDefault()?.Item;
                var targetItem = items.Items(row).FirstOrDefault(i => i.Id == a.ItemId);
                if (targetItem is null) why = "no such item";
                else if (LevelTooLow(player, row, held) is { } low) why = low;
                else if (held is not null && GameEvents.FirstInventoryRefusal(player, row, held, items.DisplacedByMerge(row, a.ItemId, ShapeOf, OneOf(a))) is { } objection) why = objection;
                else if (PassiveJewelRefusal(player, targetItem.X, targetItem.Y, held) is { } refusal) why = refusal;
                else if (ClusterRemovalRefusal(player, targetItem) is { } clusterRefusal) why = clusterRefusal;
                else if (AllocationBypassRemovalRefusal(player, targetItem) is { } bypassRefusal) why = bypassRefusal;
                else
                {
                    var sourcesBefore = CaptureGemSourceRowsLocked(player);
                    done = items.MergeOnto(row, a.ItemId, ShapeOf, out why, OneOf(a));
                    if (done is not null)
                    {
                        if (done.Displaced is { } displaced && ClusterSize(displaced.Item) is not null)
                        {
                            SpendClusterRemoval(player);
                            spentClusterRemoval = true;
                        }
                        CompletePassiveJewelMutationLocked(player, sourcesBefore);
                    }
                }
            }
        }
        else if (LevelTooLow(player, row, held) is { } low) why = low;
        else if (held is not null && GameEvents.FirstInventoryRefusal(player, row, held, items.DisplacedByMerge(row, a.ItemId, ShapeOf, OneOf(a))) is { } objection) why = objection;
        else done = items.MergeOnto(row, a.ItemId, ShapeOf, out why, OneOf(a),
            expandedAllowed: row == PlayerInventories.ExpandedInventory && ExpandedInventoryGranted(player), player: player);
        if (done is null && PlayerInventories.Row(a.InventoryId) is byte lr && LevelTooLow(player, lr, held) is { } tooLow) why = tooLow;
        if (done is null)
        {
            Console.WriteLine($"[gateway] {player.Connection.Remote} <= 0x004a held item onto {a}: refused ({why}): {hex}");
            return;
        }
        Console.WriteLine($"[gateway] {player.Connection.Remote} <= 0x004a held item onto {a}: {hex}");
        if (done.Merged || done.Rest is not null) { await SendMerged(player, items, done); return; }
        // Not a stack with room: swapped, as 0x2a on that cell.
        if (done.Row == PlayerInventories.Flask && done.FromRow != PlayerInventories.Flask)
            done = done with { Placed = EmptyIfEquippedInHostileArea(player, items, done.Placed) };
        var d = done.Displaced;
        uint target = PlayerInventories.InventoryId(done.Row);
        await Send(player, InstanceItems.InventoryChange(target, items.NextRevision(done.Row), d is null ? [] : [d.Id], [done.Placed]),
            $"0x00bf inventory {target}: add item {done.Placed.Id} at ({done.Placed.X}, {done.Placed.Y})" + (d is null ? "" : $", remove item {d.Id}"));
        await Send(player, InstanceItems.InventoryChange(PlayerInventories.InventoryId(PlayerInventories.Cursor),
            items.NextRevision(PlayerInventories.Cursor), [done.Held.Id], d is null ? [] : [d]),
            $"0x00bf cursor: remove item {done.Held.Id}" + (d is null ? "" : $", hold item {d.Id}"));
        if (PlayerInventories.ShowsOnModel(done.Row)) await SendEquipment(player, items);
        if (done.Row == PlayerInventories.Flask)
        {
            await ReconcileTincturesAfterFlaskBarChange(player);
            await RefreshMagebloodFlasks(player);
        }
        if (done.Row == PlayerInventories.PassiveJewels)
        {
            await SendPassiveJewelStats(player);
            if (spentClusterRemoval) await SendPassives(player);
        }
    }

    // Shift held on a 0x4a: flag bit 2 (the sender's param 7; live 2026-10-19, Flags = 4 with Shift, 0 without).
    public static bool OneOf(InstanceItems.ItemAction a) => (a.Flags & 0x04) != 0;

    // A held stack went onto a stack of its type: that one grows in place (same id), the held one shrinks (same id) or is gone.
    public static async Task SendMerged(Player player, PlayerInventories items, PlayerInventories.Placement done)
    {
        uint target = PlayerInventories.InventoryId(done.Row);
        await Send(player, InstanceItems.InventoryChange(target, items.NextRevision(done.Row), done.Merged ? [done.Placed.Id] : [], [done.Placed]),
            $"0x00bf inventory {target}: " + (done.Merged ? $"stack {done.Placed.Id} now {done.Placed.Item.StackSize}" : $"add one as item {done.Placed.Id} at ({done.Placed.X}, {done.Placed.Y})")
            + (Items.ByHash(done.Placed.Item.TypeHash) is { } now && now.Hash != done.Held.Item.TypeHash ? $", became {now.Name}" : ""));
        await Send(player, InstanceItems.InventoryChange(PlayerInventories.InventoryId(PlayerInventories.Cursor),
            items.NextRevision(PlayerInventories.Cursor), [done.Held.Id], done.Rest is { } rest ? [rest] : []),
            $"0x00bf cursor: " + (done.Rest is { } r ? $"stack {r.Id} now {r.Item.StackSize}" : $"remove item {done.Held.Id} (merged)"));
    }

    // C→S 0x2a: put the held item into an inventory cell (PlayerInventories.Place). The target inventory gets one 0xbf (the
    // item it displaced removed, the held item added), the cursor another (the held item removed, the displaced one added).
    public static async Task PlaceItem(Player player, InstanceItems.PlaceItem place)
    {
        if (await GameEvents.AnyInventoryPlace(player, place)) return;
        if (StashOf(player).TabOf(place.InventoryId) is int tab) { await PlaceInStash(player, tab, place); return; }
        var items = ItemsOf(player);
        if (place.A != 0) Console.WriteLine($"[gateway] {player.Connection.Remote} <= 0x002a place {place}: one of the held stack (Shift)");
        PlayerInventories.Placement? done = null;
        string why = "unknown inventory";
        Item? placement;
        bool spentClusterRemoval = false;
        if (PlayerInventories.Row(place.InventoryId) is not byte row) why = "unknown inventory";
        else if (PlayerInventories.ShowsOnModel(row))
        {
            // Cursor validation, the visible write, replacement, qualification and source retirement all happen in the
            // same mutation. The first inventory output cannot overtake a replacement create or old actor removal.
            using (ProjectileMutation(player, advance: false, phase: "equipment placement"))
            {
                using (new PlayerStatScope(player, fresh: true))
                {
                    placement = items.Items(PlayerInventories.Cursor).FirstOrDefault()?.Item;
                    if (LevelTooLow(player, row, placement) is { } low) why = low;
                    else if (placement is not null && GameEvents.FirstInventoryRefusal(player, row, placement, items.DisplacedByPlace(row, (int)place.X, (int)place.Y, ShapeOf, place.A != 0)) is { } objection) why = objection;
                    else
                    {
                        var sourcesBefore = PlayerSkillSourcesLocked(player);
                        done = items.Place(row, (int)place.X, (int)place.Y, ShapeOf, out why, one: place.A != 0);
                        if (done is not null)
                        {
                            RecomputePoolsAndSpeed(player);
                            ReconcileBeastCompanionsLocked(player);
                            ReconcileEquipmentSourceLifetimesLocked(player, sourcesBefore);
                        }
                    }
                }
            }
        }
        else if (row == PlayerInventories.PassiveJewels)
        {
            using (ProjectileMutation(player, advance: false, phase: "passive jewel placement"))
            {
                placement = items.Items(PlayerInventories.Cursor).FirstOrDefault()?.Item;
                var leaving = items.Items(row).FirstOrDefault(i => i.X == place.X && i.Y == place.Y);
                if (LevelTooLow(player, row, placement) is { } low) why = low;
                else if (placement is not null && GameEvents.FirstInventoryRefusal(player, row, placement, items.DisplacedByPlace(row, (int)place.X, (int)place.Y, ShapeOf, place.A != 0)) is { } objection) why = objection;
                else if (leaving is not null && ClusterRemovalRefusal(player, leaving) is { } clusterRefusal)
                    why = clusterRefusal;
                else if (leaving is not null && AllocationBypassRemovalRefusal(player, leaving) is { } bypassRefusal)
                    why = bypassRefusal;
                else if (PassiveJewelRefusal(player, (int)place.X, (int)place.Y, placement) is { } refusal) why = refusal;
                else
                {
                    var sourcesBefore = CaptureGemSourceRowsLocked(player);
                    done = items.Place(row, (int)place.X, (int)place.Y, ShapeOf, out why, one: place.A != 0);
                    if (done is not null)
                    {
                        if (done.Displaced is { } displaced && ClusterSize(displaced.Item) is not null)
                        {
                            SpendClusterRemoval(player);
                            spentClusterRemoval = true;
                        }
                        CompletePassiveJewelMutationLocked(player, sourcesBefore);
                    }
                }
            }
        }
        else
        {
            placement = items.Items(PlayerInventories.Cursor).FirstOrDefault()?.Item;
            if (LevelTooLow(player, row, placement) is { } low) why = low;
            else if (placement is not null && GameEvents.FirstInventoryRefusal(player, row, placement, items.DisplacedByPlace(row, (int)place.X, (int)place.Y, ShapeOf, place.A != 0)) is { } objection) why = objection;
            else
            done = items.Place(row, (int)place.X, (int)place.Y, ShapeOf, out why, one: place.A != 0,
                expandedAllowed: row == PlayerInventories.ExpandedInventory && ExpandedInventoryGranted(player), player: player);
        }
        if (done is null)
        {
            Console.WriteLine($"[gateway] {player.Connection.Remote} <= 0x002a place {place}: refused ({why})");
            return;
        }
        if (done.Row == PlayerInventories.Flask && done.FromRow != PlayerInventories.Flask)
            done = done with { Placed = EmptyIfEquippedInHostileArea(player, items, done.Placed) };
        var d = done.Displaced;
        uint target = PlayerInventories.InventoryId(done.Row);   // may differ from the clicked one (off hand → main hand)
        if (done.Merged || done.Rest is not null) { await SendMerged(player, items, done); return; }
        await Send(player, InstanceItems.InventoryChange(target, items.NextRevision(done.Row), d is null ? [] : [d.Id], [done.Placed]),
            $"0x00bf inventory {target}: add item {done.Placed.Id} at ({done.Placed.X}, {done.Placed.Y})" + (d is null ? "" : $", remove item {d.Id}"));
        await Send(player, InstanceItems.InventoryChange(PlayerInventories.InventoryId(PlayerInventories.Cursor),
            items.NextRevision(PlayerInventories.Cursor), [done.Held.Id], d is null ? [] : [d]),
            $"0x00bf cursor: remove item {done.Held.Id}" + (d is null ? "" : $", hold item {d.Id}"));
        if (PlayerInventories.ShowsOnModel(done.Row)) await SendEquipment(player, items);
        if (done.Row == PlayerInventories.Flask)
        {
            await ReconcileTincturesAfterFlaskBarChange(player);
            await RefreshMagebloodFlasks(player);
        }
        if (done.Row == PlayerInventories.PassiveJewels)
        {
            await SendPassiveJewelStats(player);
            if (spentClusterRemoval) await SendPassives(player);
        }
    }

    static void CompletePassiveJewelMutationLocked(Player player, IReadOnlyList<PlayerSkillSource> sourcesBefore)
    {
        using (new PlayerStatScope(player, fresh: true))
        {
            RecomputePoolsAndSpeed(player);
            ReconcileBeastCompanionsLocked(player);
        }
        ReconcileEquipmentSourceLifetimesLocked(player, sourcesBefore);
        using (new PlayerStatScope(player, fresh: true))
        {
            player.PlayerLife = LifeReservation.Clamp(player.PlayerLife, LifeReservationOf(player));
            player.PlayerMana = Math.Min(player.PlayerMana, (uint)UnreservedMana(player));
        }
    }
}
