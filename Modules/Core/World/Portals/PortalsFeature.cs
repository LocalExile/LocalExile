using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;
using static Program;

static class PortalsFeature
{
    // What the chat says when a run can no longer be entered and its portals are closed with it: the client's own
    // BackendErrors texts (InstanceDoesNotExist, FailedToConnectToInstance).
    const string MapInstanceGone = "The instance does not exist.";
    const string MapNotEntered = "Failed to connect to instance";

    static MapDevice.Origin? MapReturnOrigin(Player player) =>
        MapDevice.ForInstance(player) is { } map ? map.ReturnTo ?? map.Entrance : null;

    // A click on a portal back: at its cell by the device, in the instance the portal was opened from while that instance
    // is live, else in a new one of the area when it is gone and the area is recreated on entry (a hideout is always seed 1,
    // so the cell is the same, and the run's portals follow to its device: FollowDeviceToThisInstance). A transfer to an
    // instance Auth no longer has is refused ("instance N unavailable in this league") and leaves the player in the map
    // with a portal that does nothing (live 2026-11-18: the Simulacrum, six minutes after its hideout stood empty).
    internal static PendingTransfer ReturnTransfer(MapDevice.Origin origin, bool instanceGone) =>
        new(origin.Area, instanceGone ? 0 : origin.Instance, false, ArriveAt: (origin.X, origin.Y));

    // In a map's area (one a map item opens): a portal back to the hideout next to the arrival point, listed in 0x8f without
    // a device once the world is active (21-hideouts.md §5). It is not used up.
    internal static async Task SpawnMapReturnPortal(Player player)
    {
        var origin = MapReturnOrigin(player);
        var hideout = HomeHideout(player);
        if (origin is null && (!MapDevices.IsMapArea(player.Layout.Hash) || hideout is null)) return;
        // The player's own portal in this instance stands instead (SpawnTownPortals): one portal per player.
        if (MapTownPortals.TryGetValue(CharacterName(player), out var own) && own.Area == player.Layout.Hash
            && own.Instance == player.InstanceNumber) return;
        ushort destination = origin?.Area ?? hideout!.AreaHash16;
        var (x, y) = player.Layout.Near((player.Layout.SpawnX, player.Layout.SpawnY));
        uint id = player.TakeObjectId();
        await global::Game.ListStandingPortal(player, id, destination, "the return portal");
        await global::Game.SendWorldCreate(player, id, () => InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(MapPortalObject.MetadataPath),
            new MapPortalObject(x, y, -1, MapPortalType, Open: 1, Rotation: player.PortalRotationRadians).Blob()),
                $"0x02bb create the return portal 0x{id:x} at ({x}, {y})", wake: true, awaitBirth: false);
        player.ReturnPortal = (id, x, y);
        player.ReturnPortalTo = 0;
        global::Game.ListPortal(player, id, destination, "the return portal", MapPortalEntryMs);
    }

    // A click on a map portal (C→S 0x24): walk there, then the portal is used up and the player goes into the open map, a new
    // instance for the first portal and that same instance for the others.
    internal static async Task UseMapPortal(Player player,
        InstanceTransfer.UseSkillOnObject use, (int X, int Y, int Index) at, double requestedAt)
    {
        var run = player.MapPortalRun;
        if (run is null) return;
        if (!TryBeginObjectWalk(player, requestedAt, at.X, at.Y, AreaTransitionObjectSize, out var objectWalk)) return;
        var (action, now, here, stop, arrive) = objectWalk;
        Console.WriteLine($"[gateway] {player.Connection.Remote} <= 0x0024 map portal 0x{use.TargetId:x} from ({here.X}, {here.Y}): reached at ({stop.X}, {stop.Y}) in {arrive - now:0.00} s");
        if (arrive > now) await AreaDelay(player, arrive - now);
        if (!PlayerActionCurrent(player, action) || player.PendingTransfer is not null) return;
        lock (player.MapPortals)
            if (!player.MapPortals.ContainsKey(use.TargetId) || player.MapPortalRun?.Run != run.Run) return;
        string character = run.Owner;
        // The map's instance is gone (it expired while the hideout stood empty): the run ends here, before a portal is
        // spent on a transfer that cannot arrive and the rest are left behind (live 2026-11-18). It asks for the open map's
        // instance, not the run this connection's portals were placed with (still 0 for a guest who saw them before the
        // first portal was taken).
        ulong instance = MapDevice.OpenOf(character) is { } open && open.Run == run.Run ? open.Instance : 0;
        if (instance != 0 && await AreaInstances.Gone(player, run.Area.Hash16, instance))
        {
            lock (player.MapPortals)
                if (player.MapPortalRun?.Run != run.Run) return;   // another Activate replaced them meanwhile
            await EndMapRun(player, run, MapInstanceGone);
            return;
        }
        // The portal at this place is used up: it is not there when the player comes back (the others are).
        if (MapDevice.TakePortal(character, at.Index, run.Run) is not var (map, left))
        {
            // A first entrant may still be receiving its instance. Keep the unspent portal available for a retry.
            return;
        }
        await RetireMapPortal(run, at.Index);
        var spot = MapEntrySpot(CharacterName(player), map);
        Console.WriteLine($"[gateway] {player.Connection.Remote} map portal at place {at.Index} taken: {left} left, into {(map.Instance == 0 ? "a new instance" : $"instance {map.Instance}")} of {map.Area.Name}"
            + (spot is { } own ? $", at the player's own portal ({own.X}, {own.Y})" : ""));
        await ChangeArea(player, new PendingTransfer(map.Area.Hash16, map.Instance, map.Instance == 0, ArriveAt: spot)
        {
            Map = MapDevice.Run.Of(character, map),
            Done = r =>
            {
                if (r.Ok) MapDevice.Entered(map, r.Instance);
                // The instance went between the check and the transfer (or Auth refused it): the run is over, and the
                // portals left must not stay as objects that do nothing when clicked.
                else _ = EndMapRun(player, run, MapNotEntered);
            },
        }, $"map portal to {map.Area.Name}");
    }

    // Where an entry through the map device's portals arrives in an entered map: at the character's own town portal in
    // that instance, opened before it left (a Portal Scroll's, or before a death, issue #1002), else at the map's start.
    internal static (int X, int Y)? MapEntrySpot(string character, MapDevice.OpenMap map) =>
        map.Instance != 0 && MapTownPortals.TryGetValue(character, out var own)
        && own.Area == map.Area.Hash16 && own.Instance == map.Instance ? (own.X, own.Y) : null;

    // C→S 0x00d8, the map device window's Activate (21-hideouts.md §5): one map (its affixes and the scarabs/fragments)
    // or a MapDeviceRecipes set with no map. Then one item from each slot's stack is consumed, the area opens (the first
    // portal taken makes its instance) and its portals appear around the device, replacing any open before. Otherwise the chat says why.
    internal static Task ActivateMapDevice(Player player, (ushort Target, byte Index) activate) =>
        ActivateDeviceSlots(player, PlayerInventories.MapDevice, ItemsOf(player).Items(PlayerInventories.MapDevice),
            activate.Target, activate.Index, $"0x00d8 activate the map device (target 0x{activate.Target:x4}, index {activate.Index})",
            allowCampaign: true);

    // C→S 0x0297, the map receptacle's Memory Line Activate (mode 0x5e, inventory row 94 MemoryLineMaps1).
    internal static Task ActivateMemoryLine(Player player, byte bossOption) =>
        ActivateDeviceSlots(player, PlayerInventories.MemoryLineMaps, ItemsOf(player).Items(PlayerInventories.MemoryLineMaps),
            target: 0, bossOption, $"0x0297 activate memory line (index {bossOption})", allowCampaign: false);

    sealed class ActivationConsumeAbortedException : Exception;

    // C→S 0x0298, the map receptacle's Maven Invitation Activate (mode 0x56, inventory row 86 MavenInvitations, slot 0..10).
    internal static async Task ActivateMavenInvitation(Player player, byte invitationIndex)
    {
        if (invitationIndex > 10)
        {
            Console.WriteLine($"[gateway] {player.Connection.Remote} <= 0x0298 activate Maven invitation slot {invitationIndex}: out of range");
            await Send(player, InstanceChat.ChatLine("Invalid Maven invitation slot."),
                "0x0017 chat line \"Invalid Maven invitation slot.\"");
            return;
        }
        var all = ItemsOf(player).Items(PlayerInventories.MavenInvitations);
        var chosen = all.Where(i => i.X == invitationIndex).ToList();
        if (chosen.Count == 0)
        {
            Console.WriteLine($"[gateway] {player.Connection.Remote} <= 0x0298 activate Maven invitation slot {invitationIndex}: empty");
            await Send(player, InstanceChat.ChatLine("No Maven invitation is placed in that slot."),
                "0x0017 chat line \"No Maven invitation is placed in that slot.\"");
            return;
        }
        if (chosen.Count != 1 || !MapDevice.IsMavenInvitation(chosen[0].Item.TypeHash))
        {
            Console.WriteLine($"[gateway] {player.Connection.Remote} <= 0x0298 activate Maven invitation slot {invitationIndex}: not a Maven invitation");
            await Send(player, InstanceChat.ChatLine("Only a Maven invitation can be activated in that slot."),
                "0x0017 chat line \"Only a Maven invitation can be activated in that slot.\"");
            return;
        }
        await ActivateDeviceSlots(player, PlayerInventories.MavenInvitations, chosen,
            target: 0, bossOptionInput: 0, $"0x0298 activate Maven invitation slot {invitationIndex}", allowCampaign: false);
    }

    static async Task ActivateDeviceSlots(Player player,
        byte inventoryRow, IReadOnlyList<InventoryItem> slots, ushort target, byte bossOptionInput, string logHeader, bool allowCampaign)
    {
        var device = MapDeviceHere(player);
        var items = ItemsOf(player);
        Console.WriteLine($"[gateway] {player.Connection.Remote} <= {logHeader}: " +
            $"{string.Join(", ", slots.Select(i =>
            {
                string name = Items.ByHash(i.Item.TypeHash)?.Name ?? $"0x{i.Item.TypeHash:x8}";
                return i.Item.StackSize > 1 ? $"{name} ×{i.Item.StackSize}" : name;
            }).DefaultIfEmpty("empty"))}");
        Task Tell(string text) => Send(player, InstanceChat.ChatLine(text), $"0x0017 chat line \"{text}\"");
        if (device is null) { await Tell("There is no Map Device here."); return; }
        bool campaign = allowCampaign && device.Value.Campaign;
        if (!device.Value.Campaign && !OwnHideout(player)) { await Tell("Only the hideout owner can activate this Map Device."); return; }
        if (!allowCampaign && device.Value.Campaign) { await Tell("This Map Device cannot activate that mode."); return; }
        MapDevice.Slot[] checkedSlots = [.. slots.Select(i => Items.ByHash(i.Item.TypeHash) is { } t
            ? new MapDevice.Slot(i.Item.TypeHash, t.Class, t.Name, t.Row, i.Item.Mods, i.Item.Quality)
            {
                AlternateQualityType = i.Item.AlternateQualityType, Components = t.Components,
                Rarity = i.Item.Rarity, Corrupted = i.Item.Corrupted, BaseId = t.Id,
            }
            : new MapDevice.Slot(i.Item.TypeHash, "", $"Item 0x{i.Item.TypeHash:x8}", -1, [], 0))];
        byte bossOption = !campaign && bossOptionInput is >= 1 and <= 3 ? bossOptionInput : (byte)0;
        var opening = MapDevices.Check(checkedSlots, CharacterName(player), Items.Mods, out string why, campaign: campaign);
        if (opening is not null && bossOption != 0)
            opening = opening with { BossOption = global::Game.EligibleBossOption(player, opening, bossOption) };
        var request = new MapOpening(player, checkedSlots, campaign, opening, why) { Target = target };
        GameEvents.ResolveMapOpening(request);
        opening = request.Opening;
        why = request.Reason;
        int[] consumedIndices = request.ConsumeSlots?.ToArray() ?? Enumerable.Range(0, slots.Count).ToArray();
        // Only an opening the check and the handlers allowed can have bad inputs; a refusal keeps its own reason.
        if (opening is not null && (consumedIndices.Length == 0 || consumedIndices.Distinct().Count() != consumedIndices.Length
            || consumedIndices.Any(i => i < 0 || i >= slots.Count)))
        { opening = null; why = "The selected recipe has no valid inputs."; }
        if (opening is null)
        {
            Console.WriteLine($"[gateway] {player.Connection.Remote} map device not activated: {why}");
            await Tell(why);
            return;
        }
        var area = opening.Area;
        byte resolvedBossOption = !campaign
            ? global::Game.EligibleBossOption(player, opening, opening.BossOption != 0 ? opening.BossOption : bossOption)
            : (byte)0;
        // One from each slot's stack, in one transaction: leftover stacks stay in the device (a shop scarab stack is 20).
        (List<uint> gone, List<InventoryItem> left) consumed;
        try
        {
            consumed = Saves.Atomically(() =>
            {
                var current = items.Items(inventoryRow);
                foreach (int index in consumedIndices)
                    if (!current.Any(i => i.Id == slots[index].Id))
                        throw new ActivationConsumeAbortedException();
                var gone = new List<uint>();
                var left = new List<InventoryItem>();
                foreach (int index in consumedIndices)
                {
                    var slot = slots[index];
                    if (items.ConsumeOne(inventoryRow, slot.Id) is not { } take)
                        throw new ActivationConsumeAbortedException();
                    gone.Add(slot.Id);
                    if (take.Left is { } remain) left.Add(remain);
                }
                return (gone, left);
            });
        }
        catch (ActivationConsumeAbortedException)
        {
            Console.WriteLine($"[gateway] {player.Connection.Remote} map device activation aborted: input slot(s) changed before consumption");
            await Tell("The items in the Map Device changed; activation cancelled.");
            return;
        }
        await Send(player, InstanceItems.InventoryChange(PlayerInventories.InventoryId(inventoryRow),
            items.NextRevision(inventoryRow), consumed.gone, consumed.left),
            $"0x00bf inventory {PlayerInventories.InventoryId(inventoryRow)}: the map device consumed {consumed.gone.Count} slot(s)"
            + (consumed.left.Count == 0 ? "" : $", {consumed.left.Count} stack(s) left"));
        var entrance = player.Layout.Near((device.Value.X, device.Value.Y));
        var open = MapDevice.Activate(CharacterName(player), area, opening.Mods, opening.ItemRow,
            campaign || request.ReturnToSource ? new(player.Layout.Hash, player.InstanceNumber, device.Value.X, device.Value.Y) : null,
            entrance: new(player.Layout.Hash, player.InstanceNumber, entrance.X, entrance.Y), bossOption: resolvedBossOption);
        if (campaign)
            await SetQuestFlags(player, ["a7q2-DeliveredItem"]);
        string extra = opening.Mods.ExtraMagicPacks > 0 || opening.Mods.PacksDropCards > 0
            ? $", +{opening.Mods.ExtraMagicPacks} magic packs, {opening.Mods.PacksDropCards} pack(s) drop cards"
            : "";
        string bossLabel = resolvedBossOption switch { 1 => ", boss Maven", 2 => ", boss Eater of Worlds", 3 => ", boss Searing Exarch", _ => "" };
        Console.WriteLine($"[gateway] {player.Connection.Remote} map device opened {area.Name} ({area.Id}, 0x{area.Hash16:x4}, tier {area.Tier}, {opening.Mods}{extra}{bossLabel}): {open.PortalsLeft} portals");
        foreach (var visitor in DeviceVisitors(player))
        {
            try
            {
                await RemoveMapPortals(visitor);
                if (visitor.InstanceNumber == player.InstanceNumber && visitor.Layout.Hash == player.Layout.Hash)
                    await SpawnMapPortals(visitor, open);
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException) { } // Another visitor disconnected.
        }
    }

    // A click on the portal back (C→S 0x24, the map's own or a Portal Scroll's): walk there, then to the hideout next to its
    // waypoint, or to the town the scroll's portal leads to (ReturnPortalTo), at its spawn.
    internal static async Task UseMapReturnPortal(Player player,
        InstanceTransfer.UseSkillOnObject use, (int X, int Y) at, double requestedAt)
    {
        if (!TryBeginObjectWalk(player, requestedAt, at.X, at.Y, AreaTransitionObjectSize, out var objectWalk)) return;
        var (action, now, here, stop, arrive) = objectWalk;
        Console.WriteLine($"[gateway] {player.Connection.Remote} <= 0x0024 portal back 0x{use.TargetId:x} from ({here.X}, {here.Y}): reached at ({stop.X}, {stop.Y}) in {arrive - now:0.00} s");
        if (arrive > now) await AreaDelay(player, arrive - now);
        if (!PlayerActionCurrent(player, action)) return;
        if (MapReturnOrigin(player) is { } origin)
        {
            bool gone = false;
            if (AreaInstances.RecreatedOnEntry(origin.Area) && origin.Instance != 0)
                gone = await AreaInstances.Gone(player, origin.Area, origin.Instance);
            if (!PlayerActionCurrent(player, action)) return;   // the instance list took a moment
            await ChangeArea(player, ReturnTransfer(origin, gone),
                gone ? "return to the map device: its instance is gone, a new one" : "return to the map device");
        }
        else if (player.ReturnPortalTo is ushort town and not 0)
            await ChangeArea(player, new PendingTransfer(town, 0, false, AtSpawn: true), "portal to town");
        else
            await WarpToHideout(player, "map portal back");
    }

    // A click on the map device (C→S 0x24): walk there, then S→C 0x7b kind 5 opens the map device window (21-hideouts.md §4).
    // Its slots and the activation are not served yet.
    internal static async Task UseMapDevice(Player player,
        InstanceTransfer.UseSkillOnObject use, HideoutCatalog.Doodad device, double requestedAt)
    {
        if (!TryBeginObjectWalk(player, requestedAt, device.X, device.Y, StashReach, out var objectWalk, centreRange: true)) return;
        var (action, now, here, stop, arrive) = objectWalk;
        Console.WriteLine($"[gateway] {player.Connection.Remote} <= 0x0024 {device.Name} ({use}) from ({here.X}, {here.Y}): reached at ({stop.X}, {stop.Y}) in {arrive - now:0.00} s");
        if (arrive > now) await AreaDelay(player, arrive - now);
        if (!PlayerActionCurrent(player, action)) return;
        await Send(player, InstanceWaypoints.OpenUi(InstanceWaypoints.HideoutMapDeviceUi, use.TargetId),
            "0x007b open the map device window", still: () => PlayerActionCurrent(player, action));
    }

    // C→S 0xe8: Ctrl + click with the Atlas map device open. A map/fragment in the inventory goes into Map1's first
    // free cell; an item already in the slots goes back to the inventory. The client only sends this after the window's
    // +0x10b8 check (0x14030e970 → 0x14216e750).
    internal static async Task MoveToMapDevice(Player player,
        InstanceMapDevice.Move request)
    {
        var items = ItemsOf(player);
        if (ItemMoves.MoveToMapDevice(items, request.ItemId, ShapeOf) is not { } move)
        {
            Console.WriteLine($"[gateway] {player.Connection.Remote} <= 0x00e8 Ctrl + click item {request.ItemId} on the map device: refused (not there, wrong class or no room) ({request})");
            return;
        }
        await Send(player, InstanceItems.InventoryChange(PlayerInventories.InventoryId(move.From),
            items.NextRevision(move.From), [request.ItemId], []),
            $"0x00e8 Ctrl + click: item {request.ItemId} leaves row {move.From}");
        await Send(player, InstanceItems.InventoryChange(PlayerInventories.InventoryId(move.To),
            items.NextRevision(move.To), [], [move.Placed]),
            $"0x00e8 Ctrl + click: item {request.ItemId} into row {move.To} at ({move.Placed.X}, {move.Placed.Y})");
    }

    // Drops the character's town portal whose source instance Auth no longer has: the area it leads to then gets no
    // portal back to an instance that is gone.
    internal static async Task<bool> ExpireTownPortal(Player player, OpenPortal portal)
    {
        if (!await AreaInstances.Gone(player, portal.Area, portal.Instance)) return false;
        OpenPortals.TryRemove(new KeyValuePair<string, OpenPortal>(CharacterName(player), portal));
        return true;
    }

    // A run that can no longer be entered ends: the map closes, the portals left go from every visitor, and the player
    // who clicked is told why, instead of dead portals that do nothing when clicked staying until the next Activate
    // (live 2026-11-18: a failed transfer removed the run while the client kept its three portals).
    internal static async Task EndMapRun(Player player, MapDevice.OpenMap run, string why)
    {
        MapDevice.Close(run.Owner, run.Run);
        Console.WriteLine($"[gateway] {player.Connection.Remote} map run {run.Run} of {run.Area.Id} ends: {why}");
        // The clicker is on its way out until a refused transfer resumes it, so it is added although DeviceVisitors
        // leaves out a connection that is leaving the area.
        foreach (var visitor in DeviceVisitors(player).Prepend(player).Distinct())
        {
            if (visitor.Closed) continue;
            try
            {
                await RemoveMapPortals(visitor, run);
                if (visitor == player) await Send(visitor, InstanceChat.ChatLine(why), $"0x0017 chat line \"{why}\"");
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException) { } // Another visitor disconnected.
        }
    }

    // The character's open town portal (OpenPortals) on entering an area: in the area it stands in (the same instance), the
    // portal again at its cell; in the town or hideout it leads to, a portal back beside the arrival, which brings the player
    // to that cell (SpotArrivals).
    internal static async Task SpawnTownPortals(Player player)
    {
        // A map's own portal stands again in its instance; it has no portal back beside the hideout's device.
        if (MapTownPortals.TryGetValue(CharacterName(player), out var inMap) && inMap.Area == player.Layout.Hash
            && inMap.Instance == player.InstanceNumber)
        {
            await OpenTownPortal(player, inMap.X, inMap.Y, "town portal again", replace: false);
            return;
        }
        if (!OpenPortals.TryGetValue(CharacterName(player), out var open)) return;
        if (open.Area == player.Layout.Hash && open.Instance == player.InstanceNumber)
        {
            await OpenTownPortal(player, open.X, open.Y, "town portal again", replace: false);
            return;
        }
        if (open.LeadsTo != player.Layout.Hash || await ExpireTownPortal(player, open) || player.Closed || player.LeavingArea
            || !OpenPortals.TryGetValue(CharacterName(player), out var current) || current != open) return;
        var (x, y) = TownPortalPosition(player);
        uint id = player.TakeObjectId();
        await global::Game.ListStandingPortal(player, id, open.Area, "the portal back");
        if (!await global::Game.SendWorldCreate(player, id, InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(MapPortalObject.MetadataPath),
            new MapPortalObject(x, y, -1, MapPortalType, Open: 1).Blob()), $"0x02bb create the portal back 0x{id:x} at ({x}, {y}) to 0x{open.Area:x4}")) return;
        await global::Game.SendWorldRecord(player, id, InstanceProtocol.EnterWakeZone(id), null);
        player.BackPortal = (id, x, y);
        global::Game.ListPortal(player, id, open.Area, "the portal back", MapPortalListMs);
    }

    // The player's spawn follows its latest entrance or waypoint arrival; the standing portal does not.
    internal static (int X, int Y) TownPortalPosition(Player player) =>
        player.Layout.Near((player.Layout.Anchor.X + 4, player.Layout.Anchor.Y));

    // A Portal Scroll used (C→S 0x58) outside towns and hideouts: one scroll is spent and a portal opens where the player
    // stands, replacing the one there was (the scroll's earlier one, or a map's own portal back). From a map it leads to the
    // hideout (the player came through its map device), anywhere else to the character's last town. In a town or a hideout
    // nothing happens and nothing is spent.
    internal static async Task UsePortalScroll(Player player, byte row, InventoryItem item,
        ItemCatalog.BaseType type)
    {
        if (!PortalAllowed(player))
        {
            Console.WriteLine($"[gateway] {player.Connection.Remote} <= 0x0058 use {type.Name}: in a town or hideout, or {player.Layout.Id} forbids portals: nothing happens");
            return;
        }
        var items = ItemsOf(player);
        if (items.ConsumeOne(row, item.Id) is not { } taken) return;
        uint inventoryId = PlayerInventories.InventoryId(row);
        await Send(player, InstanceItems.InventoryChange(inventoryId, items.NextRevision(row), [item.Id], taken.Left is { } left ? [left] : []),
            $"0x00bf inventory {inventoryId}: {type.Name} used" + (taken.Left is { } now ? $", {now.Item.StackSize} left" : ", stack empty"));
        var (px, py) = player.Walk.Position(AreaTime(player));
        await OpenTownPortal(player, px, py, type.Name);
    }

    // Whether a town portal can open here: not in a town or a hideout, and not where the area's own mods forbid portals.
    internal static bool PortalAllowed(Player player) =>
        WaypointCatalog.Areas.Find(player.Layout.Hash) is not { IsTown: true } && HideoutCatalog.Hideouts.All(h => h.AreaHash16 != player.Layout.Hash)
        && !AreaMods.DisablesPortals(player.Layout.Id);

    // A town portal (Portal Scroll, the Portal skill) beside (px, py): from a map to the hideout, anywhere else to the
    // character's last town. It replaces the one there was, and the character's town / hideout gets a portal back to it
    // (OpenPortals, SpawnBackPortal), which brings the player back to this cell of this instance.
    internal static async Task OpenTownPortal(Player player, int px, int py, string why,
        bool replace = true)
    {
        ushort here = player.Layout.Hash;
        var home = HomeHideout(player);
        var mapOrigin = MapReturnOrigin(player);
        bool toHideout = mapOrigin is not null || MapDevices.IsMapArea(here) && home is not null;
        ushort town = player.Character.LastTown != 0 ? player.Character.LastTown : AreaLayouts.LioneyesWatch;
        // One portal per player: the old one closes (0x8f with area 0) and goes.
        if (player.ReturnPortal is { } old)
        {
            await Send(player, InstanceMapDevice.Portals(0, [new(old.Id, 0)]), null);
            await global::Game.SendWorldRemoval(player, old.Id, $"0x{InstanceProtocol.RemoveObjectId:x4} remove the old portal 0x{old.Id:x}");
            player.ReturnPortal = null;
            player.ReturnPortalTo = 0;
        }
        // Beside the player, so the click that opens it is not on the player (a reopened portal stands where it stood).
        var (x, y) = !replace || player.Layout.Walkable is not { } walkable || !walkable(px + 3, py) ? (px, py) : (px + 3, py);
        uint id = player.TakeObjectId();
        ushort leadsTo = toHideout ? mapOrigin?.Area ?? home!.AreaHash16 : town;
        // Standing again on entry (not replace): listed before its create, so it shows idle rather than opening.
        if (!replace) await global::Game.ListStandingPortal(player, id, leadsTo, "the town portal");
        if (!await global::Game.SendWorldCreate(player, id, InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(MapPortalObject.MetadataPath),
            new MapPortalObject(x, y, -1, MapPortalType, Open: 1).Blob()), $"0x02bb create the Portal Scroll's portal 0x{id:x} at ({x}, {y})")) return;
        await global::Game.SendWorldRecord(player, id, InstanceProtocol.EnterWakeZone(id), null);
        player.ReturnPortal = (id, x, y);
        player.ReturnPortalTo = toHideout ? (ushort)0 : town;
        string owner = CharacterName(player);
        // Map return portals never create an extra entrance beside the device: re-entry spends its shared six portals, and
        // arrives at this portal (MapEntrySpot).
        var portal = new OpenPortal(here, player.InstanceNumber, x, y, leadsTo);
        if (toHideout) { OpenPortals.TryRemove(owner, out _); MapTownPortals[owner] = portal; }
        else { MapTownPortals.TryRemove(owner, out _); OpenPortals[owner] = portal; }
        Console.WriteLine($"[gateway] {player.Connection.Remote} {why}: town portal at ({x}, {y}) of 0x{here:x4} instance {player.InstanceNumber} → 0x{leadsTo:x4}");
        // 0x8f only reaches a portal the client has finished creating (Game.ListPortal).
        global::Game.ListPortal(player, id, leadsTo,
            $"the Portal Scroll's portal, to {(toHideout ? "the hideout" : "the last town")}", MapPortalListMs);
    }
    // C→S 0x00e7: an Atlas map node picked in the hideout map device (u16 WorldAreas HASH16, u8). The layout becomes
    // what the next generic map key opens.
    internal static async Task SelectAtlasMap(Player player, (ushort Area, byte Mode) picked)
    {
        var layout = MapDevices.Atlas?.ByHash16(picked.Area);
        if (layout is not null && CharacterName(player) is { } character)
        {
            MapDevices.Atlas!.Select(character, layout);
            CharacterSaves.SetAtlasLayout(character, layout.Id);
            Console.WriteLine($"[gateway] {player.Connection.Remote} <= 0x00e7 Atlas node 0x{picked.Area:x4} selected: {layout.Name} ({layout.Id}), mode 0x{picked.Mode:x2}");
        }
        else Console.WriteLine($"[gateway] {player.Connection.Remote} <= 0x00e7 Atlas node 0x{picked.Area:x4} (mode 0x{picked.Mode:x2}) is not a known layout");
    }

    // A hideout's placed doodads (HideoutCatalog.IsPlaced: the map device) from its decoration, with their HideoutDoodad data.
    internal static async Task SpawnHideoutDoodads(Player player)
    {
        if (HideoutFurniture(player) is not { } scene) return;
        foreach (var d in new[] { scene.Device })
        {
            if (HideoutCatalog.TypeOf(d.Type!) is not var (components, states) || !HideoutObject.CanSerialize(components))
            {
                Console.WriteLine($"[gateway] {player.Connection.Remote} hideout doodad {d.Name} ({d.Type}): unknown type or unsupported components, not created");
                continue;
            }
            uint id = player.TakeObjectId();
            // "active" 1: the map device stands open and idle (its .ot: per-player quest flag MapDeviceActive).
            var obj = new HideoutObject(d.X, d.Y, d.Rotation, components, new HideoutDoodad(d.Hash, (byte)d.Variation),
                [.. states.Select(s => s == "active" ? 1 : 0)]);
            await global::Game.SendWorldCreate(player, id, InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(d.Type!), obj.Blob()),
                $"0x02bb create hideout doodad 0x{id:x} {d.Name} ({d.Type}) at ({d.X}, {d.Y}), components {string.Join(", ", components)}");
            await global::Game.SendWorldRecord(player, id, InstanceProtocol.EnterWakeZone(id), $"0x01a9 hideout doodad 0x{id:x} enters wake zone");
            lock (player.HideoutDoodads) player.HideoutDoodads[id] = d;
            await RegisterStationaryCollision(player, id, d.Type!, d.X, d.Y, d.Rotation, "map-device", d.Model);
        }
    }
}
