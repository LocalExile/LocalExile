using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;
using static Program;

static class PortalsFeature
{
    static MapDevice.Origin? MapReturnOrigin(Player conn) =>
        MapDevice.ForInstance(conn) is { } map ? map.ReturnTo ?? map.Entrance : null;

    // In a map's area (one a map item opens): a portal back to the hideout next to the arrival point, listed in 0x8f without
    // a device once the world is active (21-hideouts.md §5). It is not used up.
    internal static async Task SpawnMapReturnPortal(Player conn)
    {
        var origin = MapReturnOrigin(conn);
        var hideout = HomeHideout(conn);
        if (origin is null && (!MapDevices.IsMapArea(conn.Layout.Hash) || hideout is null)) return;
        // The player's own portal in this instance stands instead (SpawnTownPortals): one portal per player.
        if (MapTownPortals.TryGetValue(CharacterName(conn), out var own) && own.Area == conn.Layout.Hash
            && own.Instance == conn.InstanceNumber) return;
        ushort destination = origin?.Area ?? hideout!.AreaHash16;
        var (x, y) = conn.Layout.Near((conn.Layout.SpawnX, conn.Layout.SpawnY));
        uint id = conn.TakeObjectId();
        await global::Game.SendWorldCreate(conn, id, InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(MapPortalObject.MetadataPath),
            new MapPortalObject(x, y, -1, MapPortalType, Open: 1, Rotation: conn.PortalRotationRadians).Blob()),
            $"0x02bb create the return portal 0x{id:x} at ({x}, {y})");
        await global::Game.SendWorldRecord(conn, id, InstanceProtocol.EnterWakeZone(id), null);
        conn.ReturnPortal = (id, x, y);
        conn.ReturnPortalTo = 0;
        global::Game.ListPortal(conn, id, destination, "the return portal", MapPortalEntryMs);
    }

    // A click on a map portal (C→S 0x24): walk there, then the portal is used up and the player goes into the open map, a new
    // instance for the first portal and that same instance for the others.
    internal static async Task UseMapPortal(Player conn,
        InstanceTransfer.UseSkillOnObject use, (int X, int Y, int Index) at, double requestedAt)
    {
        var run = conn.MapPortalRun;
        if (run is null) return;
        if (!TryBeginObjectWalk(conn, requestedAt, at.X, at.Y, AreaTransitionObjectSize, out var objectWalk)) return;
        var (action, now, here, stop, arrive) = objectWalk;
        Console.WriteLine($"[gateway] {conn.Remote} <= 0x0024 map portal 0x{use.TargetId:x} from ({here.X}, {here.Y}): reached at ({stop.X}, {stop.Y}) in {arrive - now:0.00} s");
        if (arrive > now) await Task.Delay(TimeSpan.FromSeconds(arrive - now));
        if (!PlayerActionCurrent(conn, action) || conn.PendingTransfer is not null) return;
        lock (conn.MapPortals)
            if (!conn.MapPortals.ContainsKey(use.TargetId) || conn.MapPortalRun?.Run != run.Run) return;
        string character = run.Owner;
        // The portal at this place is used up: it is not there when the player comes back (the others are).
        if (MapDevice.TakePortal(character, at.Index, run.Run) is not var (map, left))
        {
            // A first entrant may still be receiving its instance. Keep the unspent portal available for a retry.
            return;
        }
        await RetireMapPortal(run, at.Index);
        var spot = MapEntrySpot(CharacterName(conn), map);
        Console.WriteLine($"[gateway] {conn.Remote} map portal at place {at.Index} taken: {left} left, into {(map.Instance == 0 ? "a new instance" : $"instance {map.Instance}")} of {map.Area.Name}"
            + (spot is { } own ? $", at the player's own portal ({own.X}, {own.Y})" : ""));
        await ChangeArea(conn, new PendingTransfer(map.Area.Hash16, map.Instance, map.Instance == 0, ArriveAt: spot)
        {
            Map = MapDevice.Run.Of(character, map),
            Done = r =>
            {
                if (r.Ok) MapDevice.Entered(map, r.Instance);
                else MapDevice.Close(character, run.Run);
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
    internal static async Task ActivateMapDevice(Player conn, (ushort Target, byte Index) activate)
    {
        var device = MapDeviceHere(conn);
        var items = ItemsOf(conn);
        var slots = items.Items(PlayerInventories.MapDevice);
        Console.WriteLine($"[gateway] {conn.Remote} <= 0x00d8 activate the map device (target 0x{activate.Target:x4}, index {activate.Index}): " +
            $"{string.Join(", ", slots.Select(i =>
            {
                string name = Items.ByHash(i.Item.TypeHash)?.Name ?? $"0x{i.Item.TypeHash:x8}";
                return i.Item.StackSize > 1 ? $"{name} ×{i.Item.StackSize}" : name;
            }).DefaultIfEmpty("empty"))}");
        Task Tell(string text) => Send(conn, InstanceChat.ChatLine(text), $"0x0017 chat line \"{text}\"");
        if (device is null) { await Tell("There is no Map Device here."); return; }
        if (!device.Value.Campaign && !OwnHideout(conn)) { await Tell("Only the hideout owner can activate this Map Device."); return; }
        MapDevice.Slot[] checkedSlots = [.. slots.Select(i => Items.ByHash(i.Item.TypeHash) is { } t
            ? new MapDevice.Slot(i.Item.TypeHash, t.Class, t.Name, t.Row, i.Item.Mods, i.Item.Quality)
            {
                AlternateQualityType = i.Item.AlternateQualityType, Components = t.Components,
                Rarity = i.Item.Rarity, Corrupted = i.Item.Corrupted, BaseId = t.Id,
            }
            : new MapDevice.Slot(i.Item.TypeHash, "", $"Item 0x{i.Item.TypeHash:x8}", -1, [], 0))];
        var opening = MapDevices.Check(checkedSlots, CharacterName(conn), Items.Mods, out string why, campaign: device.Value.Campaign);
        var request = new MapOpening(conn, checkedSlots, device.Value.Campaign, opening, why) { Target = activate.Target };
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
            Console.WriteLine($"[gateway] {conn.Remote} map device not activated: {why}");
            await Tell(why);
            return;
        }
        var area = opening.Area;
        // One from each slot's stack, in one transaction: leftover stacks stay in the device (a shop scarab stack is 20).
        var consumed = Saves.Atomically(() =>
        {
            var gone = new List<uint>();
            var left = new List<InventoryItem>();
            foreach (int index in consumedIndices)
            {
                var slot = slots[index];
                if (items.ConsumeOne(PlayerInventories.MapDevice, slot.Id) is not { } take) continue;
                gone.Add(slot.Id);
                if (take.Left is { } remain) left.Add(remain);
            }
            return (gone, left);
        });
        await Send(conn, InstanceItems.InventoryChange(PlayerInventories.InventoryId(PlayerInventories.MapDevice),
            items.NextRevision(PlayerInventories.MapDevice), consumed.gone, consumed.left),
            $"0x00bf inventory {PlayerInventories.InventoryId(PlayerInventories.MapDevice)}: the map device consumed {consumed.gone.Count} slot(s)"
            + (consumed.left.Count == 0 ? "" : $", {consumed.left.Count} stack(s) left"));
        var entrance = conn.Layout.Near((device.Value.X, device.Value.Y));
        var open = MapDevice.Activate(CharacterName(conn), area, opening.Mods, opening.ItemRow,
            device.Value.Campaign || request.ReturnToSource ? new(conn.Layout.Hash, conn.InstanceNumber, device.Value.X, device.Value.Y) : null,
            entrance: new(conn.Layout.Hash, conn.InstanceNumber, entrance.X, entrance.Y));
        if (device.Value.Campaign)
            await SetQuestFlags(conn, ["a7q2-DeliveredItem"]);
        string extra = opening.Mods.ExtraMagicPacks > 0 || opening.Mods.PacksDropCards > 0
            ? $", +{opening.Mods.ExtraMagicPacks} magic packs, {opening.Mods.PacksDropCards} pack(s) drop cards"
            : "";
        Console.WriteLine($"[gateway] {conn.Remote} map device opened {area.Name} ({area.Id}, 0x{area.Hash16:x4}, tier {area.Tier}, {opening.Mods}{extra}): {open.PortalsLeft} portals");
        foreach (var visitor in DeviceVisitors(conn))
        {
            try
            {
                await RemoveMapPortals(visitor);
                if (visitor.InstanceNumber == conn.InstanceNumber && visitor.Layout.Hash == conn.Layout.Hash)
                    await SpawnMapPortals(visitor, open);
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException) { } // Another visitor disconnected.
        }
    }

    // A click on the portal back (C→S 0x24, the map's own or a Portal Scroll's): walk there, then to the hideout next to its
    // waypoint, or to the town the scroll's portal leads to (ReturnPortalTo), at its spawn.
    internal static async Task UseMapReturnPortal(Player conn,
        InstanceTransfer.UseSkillOnObject use, (int X, int Y) at, double requestedAt)
    {
        if (!TryBeginObjectWalk(conn, requestedAt, at.X, at.Y, AreaTransitionObjectSize, out var objectWalk)) return;
        var (action, now, here, stop, arrive) = objectWalk;
        Console.WriteLine($"[gateway] {conn.Remote} <= 0x0024 portal back 0x{use.TargetId:x} from ({here.X}, {here.Y}): reached at ({stop.X}, {stop.Y}) in {arrive - now:0.00} s");
        if (arrive > now) await Task.Delay(TimeSpan.FromSeconds(arrive - now));
        if (!PlayerActionCurrent(conn, action)) return;
        if (MapReturnOrigin(conn) is { } origin)
            await ChangeArea(conn,
                new PendingTransfer(origin.Area, origin.Instance, false, ArriveAt: (origin.X, origin.Y)), "return to the map device");
        else if (conn.ReturnPortalTo is ushort town and not 0)
            await ChangeArea(conn, new PendingTransfer(town, 0, false, AtSpawn: true), "portal to town");
        else
            await WarpToHideout(conn, "map portal back");
    }

    // A click on the map device (C→S 0x24): walk there, then S→C 0x7b kind 5 opens the map device window (21-hideouts.md §4).
    // Its slots and the activation are not served yet.
    internal static async Task UseMapDevice(Player conn,
        InstanceTransfer.UseSkillOnObject use, HideoutCatalog.Doodad device, double requestedAt)
    {
        if (!TryBeginObjectWalk(conn, requestedAt, device.X, device.Y, StashReach, out var objectWalk, centreRange: true)) return;
        var (action, now, here, stop, arrive) = objectWalk;
        Console.WriteLine($"[gateway] {conn.Remote} <= 0x0024 {device.Name} ({use}) from ({here.X}, {here.Y}): reached at ({stop.X}, {stop.Y}) in {arrive - now:0.00} s");
        if (arrive > now) await Task.Delay(TimeSpan.FromSeconds(arrive - now));
        if (!PlayerActionCurrent(conn, action)) return;
        await Send(conn, InstanceWaypoints.OpenUi(InstanceWaypoints.HideoutMapDeviceUi, use.TargetId),
            "0x007b open the map device window", still: () => PlayerActionCurrent(conn, action));
    }

    // C→S 0xe8: Ctrl + click with the Atlas map device open. A map/fragment in the inventory goes into Map1's first
    // free cell; an item already in the slots goes back to the inventory. The client only sends this after the window's
    // +0x10b8 check (0x14030e970 → 0x14216e750).
    internal static async Task MoveToMapDevice(Player conn,
        InstanceMapDevice.Move request)
    {
        var items = ItemsOf(conn);
        if (ItemMoves.MoveToMapDevice(items, request.ItemId, ShapeOf) is not { } move)
        {
            Console.WriteLine($"[gateway] {conn.Remote} <= 0x00e8 Ctrl + click item {request.ItemId} on the map device: refused (not there, wrong class or no room) ({request})");
            return;
        }
        await Send(conn, InstanceItems.InventoryChange(PlayerInventories.InventoryId(move.From),
            items.NextRevision(move.From), [request.ItemId], []),
            $"0x00e8 Ctrl + click: item {request.ItemId} leaves row {move.From}");
        await Send(conn, InstanceItems.InventoryChange(PlayerInventories.InventoryId(move.To),
            items.NextRevision(move.To), [], [move.Placed]),
            $"0x00e8 Ctrl + click: item {request.ItemId} into row {move.To} at ({move.Placed.X}, {move.Placed.Y})");
    }

    // Whether Auth's list of the area's live instances, when it answered, no longer has the portal's instance.
    internal static bool OriginExpired(ControlResponse response, ulong instance) =>
        response.Ok && response.Instances is { } available && available.All(i => i.Id != instance);

    internal static async Task<bool> ExpireTownPortal(Player conn, OpenPortal portal)
    {
        var response = await InstanceControl.CallAsync(new ControlRequest { Op = "list", Ticket = conn.Ticket, Area = portal.Area });
        if (!OriginExpired(response, portal.Instance)) return false;
        OpenPortals.TryRemove(new KeyValuePair<string, OpenPortal>(CharacterName(conn), portal));
        return true;
    }

    // The character's open town portal (OpenPortals) on entering an area: in the area it stands in (the same instance), the
    // portal again at its cell; in the town or hideout it leads to, a portal back beside the arrival, which brings the player
    // to that cell (SpotArrivals).
    internal static async Task SpawnTownPortals(Player conn)
    {
        // A map's own portal stands again in its instance; it has no portal back beside the hideout's device.
        if (MapTownPortals.TryGetValue(CharacterName(conn), out var inMap) && inMap.Area == conn.Layout.Hash
            && inMap.Instance == conn.InstanceNumber)
        {
            await OpenTownPortal(conn, inMap.X, inMap.Y, "town portal again", replace: false);
            return;
        }
        if (!OpenPortals.TryGetValue(CharacterName(conn), out var open)) return;
        if (open.Area == conn.Layout.Hash && open.Instance == conn.InstanceNumber)
        {
            await OpenTownPortal(conn, open.X, open.Y, "town portal again", replace: false);
            return;
        }
        if (open.LeadsTo != conn.Layout.Hash || await ExpireTownPortal(conn, open) || conn.Closed || conn.LeavingArea
            || !OpenPortals.TryGetValue(CharacterName(conn), out var current) || current != open) return;
        var (x, y) = conn.Layout.Near((conn.Layout.SpawnX + 4, conn.Layout.SpawnY));
        uint id = conn.TakeObjectId();
        if (!await global::Game.SendWorldCreate(conn, id, InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(MapPortalObject.MetadataPath),
            new MapPortalObject(x, y, -1, MapPortalType, Open: 1).Blob()), $"0x02bb create the portal back 0x{id:x} at ({x}, {y}) to 0x{open.Area:x4}")) return;
        await global::Game.SendWorldRecord(conn, id, InstanceProtocol.EnterWakeZone(id), null);
        conn.BackPortal = (id, x, y);
        global::Game.ListPortal(conn, id, open.Area, "the portal back", MapPortalListMs);
    }

    // A Portal Scroll used (C→S 0x58) outside towns and hideouts: one scroll is spent and a portal opens where the player
    // stands, replacing the one there was (the scroll's earlier one, or a map's own portal back). From a map it leads to the
    // hideout (the player came through its map device), anywhere else to the character's last town. In a town or a hideout
    // nothing happens and nothing is spent.
    internal static async Task UsePortalScroll(Player conn, byte row, InventoryItem item,
        ItemCatalog.BaseType type)
    {
        if (!PortalAllowed(conn))
        {
            Console.WriteLine($"[gateway] {conn.Remote} <= 0x0058 use {type.Name}: in a town or hideout, or {conn.Layout.Id} forbids portals: nothing happens");
            return;
        }
        var items = ItemsOf(conn);
        if (items.ConsumeOne(row, item.Id) is not { } taken) return;
        uint inventoryId = PlayerInventories.InventoryId(row);
        await Send(conn, InstanceItems.InventoryChange(inventoryId, items.NextRevision(row), [item.Id], taken.Left is { } left ? [left] : []),
            $"0x00bf inventory {inventoryId}: {type.Name} used" + (taken.Left is { } now ? $", {now.Item.StackSize} left" : ", stack empty"));
        var (px, py) = conn.Walk.Position(AreaTime(conn));
        await OpenTownPortal(conn, px, py, type.Name);
    }

    // Whether a town portal can open here: not in a town or a hideout, and not where the area's own mods forbid portals.
    internal static bool PortalAllowed(Player conn) =>
        WaypointCatalog.Areas.Find(conn.Layout.Hash) is not { IsTown: true } && HideoutCatalog.Hideouts.All(h => h.AreaHash16 != conn.Layout.Hash)
        && !AreaMods.DisablesPortals(conn.Layout.Id);

    // A town portal (Portal Scroll, the Portal skill) beside (px, py): from a map to the hideout, anywhere else to the
    // character's last town. It replaces the one there was, and the character's town / hideout gets a portal back to it
    // (OpenPortals, SpawnBackPortal), which brings the player back to this cell of this instance.
    internal static async Task OpenTownPortal(Player conn, int px, int py, string why,
        bool replace = true)
    {
        ushort here = conn.Layout.Hash;
        var home = HomeHideout(conn);
        var mapOrigin = MapReturnOrigin(conn);
        bool toHideout = mapOrigin is not null || MapDevices.IsMapArea(here) && home is not null;
        ushort town = conn.LastTown != 0 ? conn.LastTown : AreaLayouts.LioneyesWatch;
        // One portal per player: the old one closes (0x8f with area 0) and goes.
        if (conn.ReturnPortal is { } old)
        {
            await Send(conn, InstanceMapDevice.Portals(0, [new(old.Id, 0)]), null);
            await global::Game.SendWorldRemoval(conn, old.Id, $"0x{InstanceProtocol.RemoveObjectId:x4} remove the old portal 0x{old.Id:x}");
            conn.ReturnPortal = null;
            conn.ReturnPortalTo = 0;
        }
        // Beside the player, so the click that opens it is not on the player (a reopened portal stands where it stood).
        var (x, y) = !replace || conn.Layout.Walkable is not { } walkable || !walkable(px + 3, py) ? (px, py) : (px + 3, py);
        uint id = conn.TakeObjectId();
        if (!await global::Game.SendWorldCreate(conn, id, InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(MapPortalObject.MetadataPath),
            new MapPortalObject(x, y, -1, MapPortalType, Open: 1).Blob()), $"0x02bb create the Portal Scroll's portal 0x{id:x} at ({x}, {y})")) return;
        await global::Game.SendWorldRecord(conn, id, InstanceProtocol.EnterWakeZone(id), null);
        conn.ReturnPortal = (id, x, y);
        conn.ReturnPortalTo = toHideout ? (ushort)0 : town;
        ushort leadsTo = toHideout ? mapOrigin?.Area ?? home!.AreaHash16 : town;
        string owner = CharacterName(conn);
        // Map return portals never create an extra entrance beside the device: re-entry spends its shared six portals, and
        // arrives at this portal (MapEntrySpot).
        var portal = new OpenPortal(here, conn.InstanceNumber, x, y, leadsTo);
        if (toHideout) { OpenPortals.TryRemove(owner, out _); MapTownPortals[owner] = portal; }
        else { MapTownPortals.TryRemove(owner, out _); OpenPortals[owner] = portal; }
        Console.WriteLine($"[gateway] {conn.Remote} {why}: town portal at ({x}, {y}) of 0x{here:x4} instance {conn.InstanceNumber} → 0x{leadsTo:x4}");
        // 0x8f only reaches a portal the client has finished creating (Game.ListPortal).
        global::Game.ListPortal(conn, id, leadsTo,
            $"the Portal Scroll's portal, to {(toHideout ? "the hideout" : "the last town")}", MapPortalListMs);
    }
    // C→S 0x00e7: an Atlas map node picked in the hideout map device (u16 WorldAreas HASH16, u8). The layout becomes
    // what the next generic map key opens.
    internal static async Task SelectAtlasMap(Player conn, (ushort Area, byte Mode) picked)
    {
        var layout = MapDevices.Atlas?.ByHash16(picked.Area);
        if (layout is not null && CharacterName(conn) is { } character)
        {
            MapDevices.Atlas!.Select(character, layout);
            CharacterSaves.SetAtlasLayout(character, layout.Id);
            Console.WriteLine($"[gateway] {conn.Remote} <= 0x00e7 Atlas node 0x{picked.Area:x4} selected: {layout.Name} ({layout.Id}), mode 0x{picked.Mode:x2}");
        }
        else Console.WriteLine($"[gateway] {conn.Remote} <= 0x00e7 Atlas node 0x{picked.Area:x4} (mode 0x{picked.Mode:x2}) is not a known layout");
    }

    // A hideout's placed doodads (HideoutCatalog.IsPlaced: the map device) from its decoration, with their HideoutDoodad data.
    internal static async Task SpawnHideoutDoodads(Player conn)
    {
        if (HideoutFurniture(conn) is not { } scene) return;
        foreach (var d in new[] { scene.Device })
        {
            if (HideoutCatalog.TypeOf(d.Type!) is not var (components, states) || !HideoutObject.CanSerialize(components))
            {
                Console.WriteLine($"[gateway] {conn.Remote} hideout doodad {d.Name} ({d.Type}): unknown type or unsupported components, not created");
                continue;
            }
            uint id = conn.TakeObjectId();
            // "active" 1: the map device stands open and idle (its .ot: per-player quest flag MapDeviceActive).
            var obj = new HideoutObject(d.X, d.Y, d.Rotation, components, new HideoutDoodad(d.Hash, (byte)d.Variation),
                [.. states.Select(s => s == "active" ? 1 : 0)]);
            await global::Game.SendWorldCreate(conn, id, InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(d.Type!), obj.Blob()),
                $"0x02bb create hideout doodad 0x{id:x} {d.Name} ({d.Type}) at ({d.X}, {d.Y}), components {string.Join(", ", components)}");
            await global::Game.SendWorldRecord(conn, id, InstanceProtocol.EnterWakeZone(id), $"0x01a9 hideout doodad 0x{id:x} enters wake zone");
            lock (conn.HideoutDoodads) conn.HideoutDoodads[id] = d;
            await RegisterStationaryCollision(conn, id, d.Type!, d.X, d.Y, d.Rotation, "map-device", d.Model);
        }
    }
}
