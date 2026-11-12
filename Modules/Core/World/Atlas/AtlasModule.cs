using System.Net.Sockets;
using System.Text.RegularExpressions;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Modding;
using static Program;

namespace GameServer.Modules.Atlas;

/// <summary>The Atlas passive tree's choices and the Atlas's completed maps.</summary>
public sealed class AtlasModule : IModule
{
    /// <summary>The points the Atlas's completed maps are worth: one per map, up to the 100 the tree's map half has.
    /// Bonus objectives and the Maven's invitations are not counted yet (#382 item 3).</summary>
    internal const int MapCompletionPoints = 100;

    /// <summary>Gives the character <paramref name="maps"/> Atlas passive points for maps first completed, saves them and
    /// sends the three presets again so the tree's counter follows (S→C 0x3d, the message a click in the tree gets).</summary>
    static async Task AwardMapPoints(ClientState conn, IPlayer player, int maps, string label)
    {
        if (maps <= 0 || conn.AtlasPassivePoints >= MapCompletionPoints) return;
        int before = conn.AtlasPassivePoints;
        conn.AtlasPassivePoints = (ushort)Math.Min(MapCompletionPoints, before + maps);
        if (CharacterName(conn) is { } character)
            CharacterSaves.SetAtlasPassives(character, conn.AtlasPassives, conn.AtlasPassivePoints);
        await global::Game.SendAtlasSlots(conn.Wire.Remote, conn.Wire.Stream, conn.Wire.Ciphers, conn, label);
        await player.Tell($"Atlas: {conn.AtlasPassivePoints - before} passive point(s) for the maps completed " +
            $"({conn.AtlasPassivePoints}/{MapCompletionPoints}).");
    }

    /// <inheritdoc/>
    public void Load(IModuleContext module)
    {
        ExtraPlayerMessages = conn => AtlasFeature.Restore(conn);
        GameEvents.AreaGenerated.Add(module.Manifest.Id, conn => AtlasFeature.EnsureUnlocked(conn));
        GameEvents.Tick.Add(module.Manifest.Id, (conn, _) => AtlasFeature.EnsureUnlocked(conn));
        GameEvents.Command(module.Manifest.Id, "atlas_unlock", "/atlas_unlock (dev: grant Atlas beacons, invitations and primordial keys)", async (conn, args) =>
        {
            int n = await AtlasFeature.EnsureUnlocked(conn);
            await PlayerOf(conn).Tell(n == 0 ? "Atlas: beacons and invitations already unlocked." : $"Atlas: granted {n} beacon/invitation flag(s).");
        });
        GameEvents.MonsterKilled.Add(module.Manifest.Id, async kill =>
        {
            if (AtlasProgress.OfArea(kill.Player.AreaId) is not { } node || !AtlasProgress.CompletesMap(node, kill.Monster)
                || !AtlasProgress.Complete(WorldOf(kill.Player).CharacterData, [node])) return;
            var conn = ConnOf(kill.Player);
            await Send(conn.Wire.Remote, conn.Wire.Stream, conn.Wire.Ciphers, AtlasProgress.NodeMessage(node),
                $"0x01a4 Atlas map completed: {node.Name} ({node.Id}) by {kill.Monster.Name}");
            await kill.Player.Tell($"Atlas: {node.Name} completed.");
            await AwardMapPoints(conn, kill.Player, 1, "Atlas map completed");
        });
        GameEvents.Command(module.Manifest.Id, "atlas_complete", "/atlas_complete <map name or AtlasNode id | all> (dev: mark Atlas maps completed, saved)",
            async (conn, args) =>
            {
                string what = string.Join(' ', args);
                IReadOnlyList<AtlasProgress.Node> nodes = what.Equals("all", StringComparison.OrdinalIgnoreCase) ? AtlasProgress.Maps
                    : AtlasProgress.Find(what) is { } found ? [found] : [];
                if (nodes.Count == 0)
                {
                    await PlayerOf(conn).Tell($"No Atlas map '{what}'. Usage: /atlas_complete <map name or AtlasNode id | all>");
                    return;
                }
                var data = WorldOf(conn).CharacterData;
                var before = AtlasProgress.Completed(data);
                AtlasProgress.Complete(data, nodes);
                int earned = nodes.Count(n => !before.Contains(n.Id));
                if (nodes.Count == 1)
                    await PlayerOf(conn).Send(AtlasProgress.NodeMessage(nodes[0]),
                        $"0x01a4 /atlas_complete {nodes[0].Name} ({nodes[0].Id})");
                await PlayerOf(conn).Tell(nodes.Count == 1 ? $"Atlas: {nodes[0].Name} completed." : $"Atlas: {nodes.Count} maps completed.");
                await AwardMapPoints(conn, PlayerOf(conn), earned, "/atlas_complete");
            });
        GameEvents.Command(module.Manifest.Id, "atlas_reset", "/atlas_reset (dev: clear the Atlas's completed maps)", async (conn, args) =>
        {
            AtlasProgress.Reset(WorldOf(conn).CharacterData);
            var slots = AtlasFeature.SlotsOf(PlayerOf(conn));
            await PlayerOf(conn).Send(AtlasProgress.EmptyMessage(AtlasProgress.VoidstoneMask(slots), (byte)slots.Count),
                "0x01a3 /atlas_reset");
            await PlayerOf(conn).Tell("Atlas: progress cleared.");
        });
        GameEvents.Packet(InstanceEntities.AtlasAllocateId).Add(module.Manifest.Id, async (conn, bytes) =>
        {
            var (preset, node) = InstanceEntities.ReadAtlasAllocate(bytes);
            await AtlasFeature.ChangeAtlasPassive(conn.Wire.Remote, conn.Wire.Stream, conn.Wire.Ciphers, conn, preset, node, allocate: true);
            return true;
        });
        GameEvents.Packet(InstanceEntities.AtlasRefundId).Add(module.Manifest.Id, async (conn, bytes) =>
        {
            var (preset, node, _) = InstanceEntities.ReadAtlasRefund(bytes);
            await AtlasFeature.ChangeAtlasPassive(conn.Wire.Remote, conn.Wire.Stream, conn.Wire.Ciphers, conn, preset, node, allocate: false);
            return true;
        });
        GameEvents.Packet(InstanceMapDevice.SocketId).Add(module.Manifest.Id, async (conn, bytes) =>
        {
            await AtlasFeature.SocketVoidstone(conn.Wire.Remote, conn.Wire.Stream, conn.Wire.Ciphers, conn,
                InstanceMapDevice.ReadSocket(bytes).ItemId);
            return true;
        });
        // Taking a socketed stone off is C→S 0x28 from inventory 40, not 0xf6. Clicking a Voidstone in the bag
        // (without Ctrl) is also 0x28; the Atlas window does not send 0xf6 unless Ctrl is held, so that click
        // has to socket the stone itself. Dropping onto a socket is C→S 0x2a into inventory 40.
        GameEvents.Packet(InstanceItems.PickUpItemId).Add(module.Manifest.Id, async (conn, bytes) =>
        {
            return await AtlasFeature.PickVoidstone(conn.Wire.Remote, conn.Wire.Stream, conn.Wire.Ciphers, conn,
                InstanceItems.ReadPickUpItem(bytes));
        });
        GameEvents.InventoryPlace.Add(module.Manifest.Id, async (conn, place) =>
        {
            if (place.InventoryId != PlayerInventories.InventoryId(PlayerInventories.AtlasUpgrades)) return false;
            var held = ItemsOf(conn).Items(PlayerInventories.Cursor).FirstOrDefault();
            if (held is null || ItemsFor(conn).ByHash(held.Item.TypeHash) is not { } type
                || MapBossFights.Current.StoneByItem(type.Id) is null)
                return false;
            var (remote, stream, ciphers) = conn.Wire;
            await AtlasFeature.SocketVoidstone(remote, stream, ciphers, conn, held.Id);
            return true;
        });
        // Kirac's invitation rows FinishNPCChat + DispatchRulesetEvent("open_maven_invitation …"). The chat end ran
        // with no item. Hand the matching MavenFights invitation into the bag first; the empty FinishNPCChat still closes.
        GameEvents.NpcOptionPicked.Add(module.Manifest.Id, async (conn, talk) =>
        {
            await AtlasFeature.GiveMavenInvitation(conn, talk.Script);
            return true;
        });
    }
}

static class AtlasFeature
{
    /// <summary>Saved QuestFlags only. SessionQuestFlags (PickUpMavenAlteration, HaveTangleMapDeviceAlteration, …)
    /// are never written to the save: granting them on every area re-fires the client's "beacon complete" toast.</summary>
    internal static readonly string[] UnlockFlags =
    [
        "HaveMavenMapDeviceAlteration",
        "HaveMavenMapVoid1", "HaveMavenMapVoid2", "HaveMavenMapVoid3", "HaveMavenMapVoid4", "HaveMavenMapVoid5",
        "HaveMavenMapVoid6",
        "HaveMavenMapAtlas1", "HaveMavenMapAtlas2", "HaveMavenMapAtlas3", "HaveMavenMapAtlas4", "HaveMavenMapAtlas5",
        "MavenFirstMapBossCapture", "MavenFirstInvitation", "MavenFirstVoidBossCapture",
        "SeenMavenBoss", "MavenMapDropped", "Met-EnvoyMaven", "Met-EnvoyEater", "Met-EnvoyExarch",
        "HaveTangleKey1", "HaveTangleKey2",
        "HaveCleansingFireKey1", "HaveCleansingFireKey2",
        "CleansingBossDefeated", "ConsumeBossDefeated", "MavenEnragedDefeated",
        "KiracOnTheMapDeviceSeen", "KiracOnTangleLureSeen", "KiracOnCleansingFireLureSeen",
        "KiracOnCleansingFireKey1Seen", "KiracOnCleansingFireKey2Seen",
        "KiracOnTangleKey1Seen", "KiracOnTangleKey2Seen",
        "ShaperDefeated", "ElderDefeated", "SirusDefeated",
    ];

    /// <summary>Map-device beacons and Maven invitation gates (Player +0x1d0, bit = id − 1). Applied onto
    /// <see cref="ClientState.LeagueQuestFlags"/> before the player object is built so a later 0x02bc does not toast.
    /// AtlasMavenFightComplete1–4 (979–982) make Kirac's "Invitation: The Atlas" the fifth fight; 885 MavenBossComplete
    /// is omitted so the Nucleus still has its later phases.</summary>
    internal static readonly int[] UnlockLeagueFlags =
    [
        6, 7,                         // Unlocked5thMapDeviceSlot, Unlocked6thMapDeviceSlot
        225, 351, 352,                // MapDeviceMavenUnlocked, MapDeviceTangleUnlocked, MapDeviceCleansingFireUnlocked
        219, 220, 221, 222,           // PrimordialWatchstoneUnlocked Maven / UberElder / Tangled / CleansingFire
        290, 607,                     // VoidstoneSocketed, PickedUpVoidstone
        1019,                         // AtlasMavenCollectedEnoughMaps
        1100, 1101, 1102, 1103, 1104, 1161, // VoidMavenCollectedEnoughMaps1–6
        979, 980, 981, 982,           // AtlasMavenFightComplete1–4 → Kirac offers Atlas5
        1075, 426,                    // MavenBossUnlocked, MavenVoidQuestUnlocked
    ];

    /// <summary>Previously granted in error: they skip Maven/Kirac phases or toast on every area.</summary>
    internal static readonly int[] StripLeagueFlags = [885, 374, 212, 328, 317, 318, 347, 319];

    static readonly Dictionary<string, int> MavenInvitationItems = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Void1"] = 4125, ["Void2"] = 4126, ["Void3"] = 4127, ["Void4"] = 4128, ["Void5"] = 4129, ["Void6"] = 4130,
        ["Atlas1"] = 4131, ["Atlas2"] = 4132, ["Atlas3"] = 4133, ["Atlas4"] = 4134, ["Atlas5"] = 4136,
    };

    static readonly Regex OpenMavenInvitation = new(@"open_maven_invitation\s+(\w+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    internal static async Task<int> EnsureUnlocked(ClientState conn)
    {
        var missing = UnlockFlags.Where(f => !ClientHasQuestFlag(conn, f)).ToList();
        bool leagueChanged = ApplyLeagueFlags(conn);
        if (!conn.PlayerSent) return missing.Count + (leagueChanged ? 1 : 0);
        int n = 0;
        var (remote, stream, ciphers) = conn.Wire;
        if (missing.Count > 0)
        {
            await SetQuestFlags(remote, stream, ciphers, conn, missing);
            n += missing.Count;
        }
        if (leagueChanged)
        {
            await global::Game.SendWorldRecord(remote, stream, ciphers, conn, PlayerId, InstanceEntities.PlayerLeagueQuestFlagsUpdate(PlayerId, Quests.League.Bits(conn.LeagueQuestFlags)),
                "0x02bc Atlas league flags");
            n++;
        }
        return n;
    }

    /// <summary>One 0x1a3 before 0x21: the sets replace without a completion event. 0x1a4 is only for a new completion.
    /// League flags are put on the connection here, before <c>PlayerForCreation</c> reads them.</summary>
    internal static IEnumerable<(string Log, byte[] Message)> Restore(ClientState conn)
    {
        ApplyLeagueFlags(conn);
        var player = PlayerOf(conn);
        var data = WorldOf(player).CharacterData;
        var completed = AtlasProgress.Completed(data);
        // One Atlas passive point per map completed, up to the 100 the tree's map half has (#382 item 3). The completed set
        // is read from the save. Entering an area does not complete any new maps.
        bool pointsChanged = false;
        if (Math.Min(AtlasModule.MapCompletionPoints, completed.Count) is int earned and > 0 && earned > conn.AtlasPassivePoints)
        {
            conn.AtlasPassivePoints = (ushort)earned;
            if (CharacterName(conn) is { } character)
                CharacterSaves.SetAtlasPassives(character, conn.AtlasPassives, conn.AtlasPassivePoints);
            pointsChanged = true;
        }
        var slots = SlotsOf(player);
        byte stones = AtlasProgress.VoidstoneMask(slots);
        yield return ($"0x01a3 Atlas progress, {completed.Count} map(s) saved, {AtlasProgress.OnAtlasRows.Count} on-atlas row(s), voidstones 0x{stones:X2} ({slots.Count})",
            AtlasProgress.FullMessage(completed, stones, (byte)slots.Count));
        // The presets in the player batch were built before the budget above changed: send them again so the tree's
        // counter follows on this login.
        if (pointsChanged)
            for (int preset = 0; preset < conn.AtlasPassives.Length; preset++)
                yield return ($"0x003d atlas passive preset {preset} ({conn.AtlasPassives[preset].Count}/{conn.AtlasPassivePoints} points) after the map points",
                    global::Game.AtlasSlots(conn, preset));
    }

    static bool ApplyLeagueFlags(ClientState conn)
    {
        bool changed = false;
        foreach (int id in UnlockLeagueFlags) if (conn.LeagueQuestFlags.Add(id)) changed = true;
        foreach (int id in StripLeagueFlags) if (conn.LeagueQuestFlags.Remove(id)) changed = true;
        return changed;
    }

    /// <summary>Kirac <c>DispatchRulesetEvent("open_maven_invitation …")</c>: put that MavenFights invitation in the bag.</summary>
    internal static async Task GiveMavenInvitation(ClientState conn, string script)
    {
        var match = OpenMavenInvitation.Match(script ?? "");
        if (!match.Success || !MavenInvitationItems.TryGetValue(match.Groups[1].Value, out int row)
            || ItemsFor(conn).Row(row) is not { } type)
            return;
        var items = ItemsOf(conn);
        if (items.FreeCell(PlayerInventories.MainInventory, type.Width, type.Height, ShapeOf) is not { } cell)
        {
            await PlayerOf(conn).Tell($"Atlas: no room for {type.Name}.");
            return;
        }
        var added = items.Put(PlayerInventories.MainInventory,
            new InventoryItem(items.NewItemId(), cell.X, cell.Y, ItemCatalog.Create(type)));
        var (remote, stream, ciphers) = conn.Wire;
        await Send(remote, stream, ciphers, InstanceItems.InventoryChange(PlayerInventories.InventoryId(PlayerInventories.MainInventory),
            items.NextRevision(PlayerInventories.MainInventory), [], [added]),
            $"0x00bf inventory 1: Kirac invitation {type.Name}");
        await PlayerOf(conn).Tell($"Atlas: {type.Name}.");
    }

    // C→S 0x3e/0x3f: a click in the Atlas passive tree. Validate the node and budget, save the preset and send all three
    // presets back so the client and the save agree. The client presents the tree's own connectivity; the server does
    // not yet have the Atlas graph, so it does not re-check pathing.
    internal static async Task ChangeAtlasPassive(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        byte preset, uint node, bool allocate)
    {
        if (preset >= conn.AtlasPassives.Length || node > ushort.MaxValue
            || AtlasPassiveNodes.Find((ushort)node) is not { } passive || passive.Id == "atlas_start_node")
        {
            Console.WriteLine($"[gateway] {remote} <= Atlas passive {(allocate ? "allocate" : "refund")} {node} refused: " +
                "preset or node unknown");
            await SendAtlasSlots(remote, stream, ciphers, conn, "Atlas refused");
            return;
        }
        var nodes = conn.AtlasPassives[preset];
        var character = CharacterName(conn);
        if (allocate)
        {
            if (nodes.Contains((ushort)node))
            {
                await SendAtlasSlots(remote, stream, ciphers, conn, "Atlas already allocated");
                return;
            }
            if (nodes.Count >= conn.AtlasPassivePoints)
            {
                Console.WriteLine($"[gateway] {remote} <= Atlas allocate {passive.Name} ({node}) refused: no points left");
                await SendAtlasSlots(remote, stream, ciphers, conn, "Atlas no points");
                return;
            }
            nodes.Add((ushort)node);
        }
        else if (!nodes.Remove((ushort)node))
        {
            await SendAtlasSlots(remote, stream, ciphers, conn, "Atlas not allocated");
            return;
        }
        if (character is not null)
            CharacterSaves.SetAtlasPassives(character, conn.AtlasPassives, conn.AtlasPassivePoints);
        Console.WriteLine($"[gateway] {remote} <= Atlas preset {preset} {(allocate ? "allocate" : "refund")} " +
            $"{passive.Name} ({node}), {nodes.Count}/{conn.AtlasPassivePoints} points");
        await SendAtlasSlots(remote, stream, ciphers, conn, "Atlas changed");
    }

    static ItemMoves.BenchMove? PlaceVoidstone(PlayerInventories items, uint itemId, byte slot) =>
        Saves.Atomically<ItemMoves.BenchMove?>(() =>
        {
            byte main = PlayerInventories.MainInventory, atlas = PlayerInventories.AtlasUpgrades,
                cursor = PlayerInventories.Cursor;
            byte from;
            InventoryItem item;
            if (items.Items(main).FirstOrDefault(i => i.Id == itemId) is { } inMain) { from = main; item = inMain; }
            else if (items.Items(atlas).FirstOrDefault(i => i.Id == itemId) is { } inAtlas) { from = atlas; item = inAtlas; }
            else if (items.Items(cursor).FirstOrDefault(i => i.Id == itemId) is { } held) { from = cursor; item = held; }
            else return null;
            byte to = from == atlas ? main : atlas;
            if (ShapeOf(item.Item) is not { } shape) return null;
            (byte X, byte Y)? cell = to == atlas
                ? (slot > 3 || items.Items(to).Any(i => i.X == slot) ? null : ((byte)slot, (byte)0))
                : items.FreeCell(to, shape.Width, shape.Height, ShapeOf);
            if (cell is null || items.Remove(from, itemId) is not { } taken) return null;
            return new(from, to, items.Put(to, taken with { X = cell.Value.X, Y = cell.Value.Y }));
        });

    // C→S 0xf6 / a bag click / a drop onto inventory 40: a Voidstone into AtlasUpgrades1. GrantAtlasUpgrade is set
    // the first time a stone goes in. A filled 0x1a3 then refreshes the quadrant T16 bits while the Atlas is open.
    internal static async Task<bool> SocketVoidstone(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        uint itemId)
    {
        var items = ItemsOf(conn);
        InventoryItem? held = items.Items(PlayerInventories.MainInventory).FirstOrDefault(i => i.Id == itemId)
            ?? items.Items(PlayerInventories.AtlasUpgrades).FirstOrDefault(i => i.Id == itemId)
            ?? items.Items(PlayerInventories.Cursor).FirstOrDefault(i => i.Id == itemId);
        var type = held is { } item ? ItemsFor(conn).ByHash(item.Item.TypeHash) : null;
        var stone = type is { } t ? MapBossFights.Current.StoneByItem(t.Id) : null;
        if (held is null || stone is null
            || PlaceVoidstone(items, itemId, (byte)stone.Slot) is not { } move)
        {
            Console.WriteLine($"[gateway] {remote} <= Voidstone item {itemId}: refused (not there, not a stone or no room)");
            return false;
        }
        bool into = move.To == PlayerInventories.AtlasUpgrades;
        if (CharacterName(conn) is { } name) MapBossFights.Current.SetSocketed(name, stone.Slot, into);
        await Send(remote, stream, ciphers, InstanceItems.InventoryChange(PlayerInventories.InventoryId(move.From),
            items.NextRevision(move.From), [itemId], []),
            $"0x00f6 Voidstone: item {itemId} leaves row {move.From}");
        await Send(remote, stream, ciphers, InstanceItems.InventoryChange(PlayerInventories.InventoryId(move.To),
            items.NextRevision(move.To), [], [move.Placed]),
            $"0x00f6 Voidstone: item {itemId} into row {move.To} slot {move.Placed.X}");
        if (into && stone.Flag.Length > 0 && !ClientHasQuestFlag(conn, stone.Flag))
            await SetQuestFlags(remote, stream, ciphers, conn, [stone.Flag]);
        await Refresh(remote, stream, ciphers, conn);
        return true;
    }

    // C→S 0x28: inventory 40 unsockets onto the cursor; a Voidstone in the bag sockets into its Atlas slot (the
    // window only sends 0xf6 on Ctrl + click, so a plain click would otherwise do nothing).
    internal static async Task<bool> PickVoidstone(string remote, NetworkStream stream, ConnectionCiphers ciphers,
        ClientState conn, InstanceItems.PickUpItem pick)
    {
        uint atlas = PlayerInventories.InventoryId(PlayerInventories.AtlasUpgrades);
        uint main = PlayerInventories.InventoryId(PlayerInventories.MainInventory);
        uint cursor = PlayerInventories.InventoryId(PlayerInventories.Cursor);
        if (pick.InventoryId == atlas)
        {
            var items = ItemsOf(conn);
            if (items.PickUp(PlayerInventories.AtlasUpgrades, pick.ItemId) is not { } held)
            {
                Console.WriteLine($"[gateway] {remote} <= 0x0028 Voidstone pick {pick.ItemId}: refused (not in Atlas upgrades)");
                return true;
            }
            if (ItemsFor(conn).ByHash(held.Item.TypeHash) is { } type
                && MapBossFights.Current.StoneByItem(type.Id) is { } stone
                && CharacterName(conn) is { } name)
                MapBossFights.Current.SetSocketed(name, stone.Slot, false);
            await Send(remote, stream, ciphers, InstanceItems.InventoryChange(pick.InventoryId,
                items.NextRevision(PlayerInventories.AtlasUpgrades), [held.Id], []),
                $"0x0028 Voidstone: item {held.Id} leaves Atlas upgrades");
            await Send(remote, stream, ciphers, InstanceItems.InventoryChange(cursor,
                items.NextRevision(PlayerInventories.Cursor), [], [held]),
                $"0x00bf cursor: add item {held.Id} (unsocketed Voidstone)");
            await Refresh(remote, stream, ciphers, conn);
            return true;
        }
        if (pick.InventoryId != main) return false;
        var bag = ItemsOf(conn).Items(PlayerInventories.MainInventory).FirstOrDefault(i => i.Id == pick.ItemId);
        if (bag is null || ItemsFor(conn).ByHash(bag.Item.TypeHash) is not { } bagType
            || MapBossFights.Current.StoneByItem(bagType.Id) is null)
            return false;
        return await SocketVoidstone(remote, stream, ciphers, conn, pick.ItemId);
    }

    internal static async Task Refresh(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
    {
        await EnsureUnlocked(conn);
        var player = PlayerOf(conn);
        var completed = AtlasProgress.Completed(WorldOf(player).CharacterData);
        var slots = SlotsOf(player);
        byte stones = AtlasProgress.VoidstoneMask(slots);
        await Send(remote, stream, ciphers, AtlasProgress.FullMessage(completed, stones, (byte)slots.Count),
            $"0x01a3 Atlas voidstones 0x{stones:X2} ({slots.Count} socketed), {completed.Count} map(s)");
    }

    /// <summary>Stones actually in AtlasUpgrades1, not the save (a missing item still left the old 4-slot mask and
    /// the client then refused 0xf6 on the empty socket).</summary>
    internal static HashSet<int> SlotsOf(IPlayer player)
    {
        var conn = ConnOf(player);
        var slots = new HashSet<int>();
        foreach (var item in ItemsOf(conn).Items(PlayerInventories.AtlasUpgrades))
            if (ItemsFor(conn).ByHash(item.Item.TypeHash) is { } type
                && MapBossFights.Current.StoneByItem(type.Id) is { } stone)
                slots.Add(stone.Slot);
        return slots;
    }

    internal static byte MaskOf(IPlayer player) => AtlasProgress.VoidstoneMask(SlotsOf(player));
}
