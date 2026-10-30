using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

partial class Program
{
    // A click on the map device (C→S 0x24): walk there, then S→C 0x7b kind 5 opens the map device window (21-hideouts.md §4).
    // Its slots and the activation are not served yet.
    static async Task UseMapDevice(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        InstanceTransfer.UseSkillOnObject use, HideoutCatalog.Doodad device, double requestedAt)
    {
        if (!TryBeginObjectWalk(conn, requestedAt, device.X, device.Y, StashReach, out var objectWalk, centreRange: true)) return;
        var (action, now, here, stop, arrive) = objectWalk;
        Console.WriteLine($"[gateway] {remote} <= 0x0024 {device.Name} ({use}) from ({here.X}, {here.Y}): reached at ({stop.X}, {stop.Y}) in {arrive - now:0.00} s");
        if (arrive > now) await Task.Delay(TimeSpan.FromSeconds(arrive - now));
        if (!PlayerActionCurrent(conn, action)) return;
        await Send(remote, stream, ciphers, InstanceWaypoints.OpenUi(InstanceWaypoints.HideoutMapDeviceUi, use.TargetId),
            "0x007b open the map device window", still: () => PlayerActionCurrent(conn, action));
    }

    // C→S 0x00e7: an Atlas map node picked in the hideout map device (u16 WorldAreas HASH16, u8). The layout becomes
    // what the next generic map key opens.
    static async Task SelectAtlasMap(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, (ushort Area, byte Mode) picked)
    {
        var layout = MapDevices.Value.Atlas?.ByHash16(picked.Area);
        if (layout is not null && CharacterName(conn) is { } character)
        {
            MapDevices.Value.Atlas!.Select(character, layout);
            Console.WriteLine($"[gateway] {remote} <= 0x00e7 Atlas node 0x{picked.Area:x4} selected: {layout.Name} ({layout.Id}), mode 0x{picked.Mode:x2}");
        }
        else Console.WriteLine($"[gateway] {remote} <= 0x00e7 Atlas node 0x{picked.Area:x4} (mode 0x{picked.Mode:x2}) is not a known layout");
    }

    // C→S 0x00d8, the map device window's Activate (21-hideouts.md §5): every slot must hold a map or a map fragment, and
    // exactly one map. Then the slots are consumed, the map's area opens (the first portal taken makes its instance) and its
    // portals appear around the device, replacing any open before. Otherwise nothing changes and the chat says why.
    static async Task ActivateMapDevice(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, (ushort Target, byte Index) activate)
    {
        HideoutCatalog.Doodad? device;
        lock (conn.HideoutDoodads) device = conn.HideoutDoodads.Values.FirstOrDefault();
        var items = ItemsOf(conn);
        var slots = items.Items(PlayerInventories.MapDevice);
        Console.WriteLine($"[gateway] {remote} <= 0x00d8 activate the map device (target 0x{activate.Target:x4}, index {activate.Index}): " +
            $"{string.Join(", ", slots.Select(i => Items.ByHash(i.Item.TypeHash)?.Name ?? $"0x{i.Item.TypeHash:x8}").DefaultIfEmpty("empty"))}");
        Task Tell(string text) => Send(remote, stream, ciphers, InstanceChat.ChatLine(text), $"0x0017 chat line \"{text}\"");
        if (device is null) { await Tell("There is no Map Device here."); return; }
        var area = MapDevices.Value.Check([.. slots.Select(i => Items.ByHash(i.Item.TypeHash) is { } t
            ? new MapDevice.Slot(i.Item.TypeHash, t.Class, t.Name) : new MapDevice.Slot(i.Item.TypeHash, "", $"Item 0x{i.Item.TypeHash:x8}"))], CharacterName(conn), out string why);
        if (area is null)
        {
            Console.WriteLine($"[gateway] {remote} map device not activated: {why}");
            await Tell(why);
            return;
        }
        // Every slot's item is consumed in one transaction: all of them or none.
        var consumed = Saves.Atomically(() => slots.Where(i => items.Remove(PlayerInventories.MapDevice, i.Id) is not null).Select(i => i.Id).ToList());
        await Send(remote, stream, ciphers, InstanceItems.InventoryChange(PlayerInventories.InventoryId(PlayerInventories.MapDevice),
            items.NextRevision(PlayerInventories.MapDevice), consumed, []),
            $"0x00bf inventory {PlayerInventories.InventoryId(PlayerInventories.MapDevice)}: the map device consumed {consumed.Count} item(s)");
        await RemoveMapPortals(remote, stream, ciphers, conn);
        var open = MapDevice.Activate(CharacterName(conn), area);
        Console.WriteLine($"[gateway] {remote} map device opened {area.Name} ({area.Id}, 0x{area.Hash16:x4}, tier {area.Tier}): {open.PortalsLeft} portals");
        await SpawnMapPortals(remote, stream, ciphers, conn, open);
    }

    // The open map's portals that are left, at their places on a ring around the hideout's map device, one after another
    // (MapPortalGapMs apart). 0x8f only reaches portal objects the client has already created, and a create sent in the same
    // burst is not done yet (live 2026-10-20), so each portal is listed MapPortalListMs after its create; on entering the
    // area the objects also wait until the world is active (firstAfterMs). A newer Activate or the portals' removal stops
    // the sequence.
    static Task SpawnMapPortals(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, MapDevice.OpenMap open, int firstAfterMs = 0)
    {
        HideoutCatalog.Doodad? device;
        uint deviceId;
        lock (conn.HideoutDoodads) (deviceId, device) = conn.HideoutDoodads.Select(kv => (kv.Key, (HideoutCatalog.Doodad?)kv.Value)).FirstOrDefault();
        if (device is null) return Task.CompletedTask;
        int generation = Interlocked.Increment(ref conn.MapPortalGeneration);
        bool Current() => !conn.Closed && conn.MapPortalGeneration == generation;
        uint type = InstanceEntities.ObjectTypeHash(MapPortalObject.MetadataPath);
        _ = Task.Run(async () =>
        {
            if (firstAfterMs > 0) await Task.Delay(firstAfterMs);
            int n = 0;
            foreach (int index in open.Left)
            {
                if (!Current()) return;
                if (n++ > 0) await Task.Delay(MapPortalGapMs);
                if (!Current()) return;
                uint id = conn.NextObjectId++;
                var (x, y) = MapDevice.PortalCell(device.X, device.Y, index);
                await Send(remote, stream, ciphers, InstanceEntities.CreateObject(id, type,
                        new MapPortalObject(x, y, index, MapPortalType, Open: 1, Rotation: conn.PortalRotationRadians).Blob()),
                    $"0x02b9 create map portal 0x{id:x} (place {index}, {n}/{open.PortalsLeft}) to {open.Area.Name} at ({x}, {y})");
                await Send(remote, stream, ciphers, InstanceProtocol.EnterWakeZone(id), null);
                lock (conn.MapPortals) conn.MapPortals[id] = (x, y, index);
                await Task.Delay(MapPortalListMs);
                if (!Current()) return;
                await SendMapPortals(remote, stream, ciphers, conn, deviceId, open.Area.Hash16);
            }
        });
        return Task.CompletedTask;
    }

    static void SendMapPortalsLater(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, uint deviceId, ushort area, int delayMs) =>
        _ = Task.Delay(delayMs).ContinueWith(async _ => { if (!conn.Closed) await SendMapPortals(remote, stream, ciphers, conn, deviceId, area); });

    // Removes and recreates the map portals that stand, at the same places and with the current rotation, then lists them
    // again: the client builds a portal's visual from its object, so a rotated portal effect can be tried without reopening
    // the device. Returns how many were rebuilt.
    static async Task<int> RebuildMapPortals(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
    {
        uint deviceId;
        lock (conn.HideoutDoodads) deviceId = conn.HideoutDoodads.Keys.FirstOrDefault();
        List<(uint Id, int X, int Y, int Index)> old;
        lock (conn.MapPortals)
        {
            old = [.. conn.MapPortals.Select(kv => (kv.Key, kv.Value.X, kv.Value.Y, kv.Value.Index))];
            conn.MapPortals.Clear();
        }
        if (old.Count == 0) return 0;
        uint type = InstanceEntities.ObjectTypeHash(MapPortalObject.MetadataPath);
        foreach (var (id, _, _, _) in old)
            await Send(remote, stream, ciphers, InstanceProtocol.RemoveObject(id), $"0x02bb remove map portal 0x{id:x} (rebuild)");
        // Each portal is listed a moment after its create: 0x008f in the same burst as the 0x2b9 reaches none of them (the
        // client has not built the object yet), which is why they then stay invisible.
        ushort area = MapDevice.OpenOf(CharacterName(conn))?.Area.Hash16 ?? conn.Layout.Hash;
        foreach (var (_, x, y, index) in old)
        {
            uint id = conn.NextObjectId++;
            await Send(remote, stream, ciphers, InstanceEntities.CreateObject(id, type,
                    new MapPortalObject(x, y, index, MapPortalType, Open: 1, Rotation: conn.PortalRotationRadians).Blob()),
                $"0x02b9 create map portal 0x{id:x} (place {index}) at ({x}, {y}) rotation {conn.PortalRotationDegrees:0.#}°");
            await Send(remote, stream, ciphers, InstanceProtocol.EnterWakeZone(id), null);
            lock (conn.MapPortals) conn.MapPortals[id] = (x, y, index);
            await Task.Delay(MapPortalListMs);
            await SendMapPortals(remote, stream, ciphers, conn, deviceId, area);
        }
        return old.Count;
    }

    // Lists the portals already standing again, so a portal cosmetic takes effect without reopening the map device: 0x008f
    // carries the variation of every entry and the client re-applies the visual. False when nothing stands.
    static async Task<bool> ResendMapPortals(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
    {
        uint deviceId;
        lock (conn.HideoutDoodads) deviceId = conn.HideoutDoodads.Keys.FirstOrDefault();
        lock (conn.MapPortals)
            if (conn.MapPortals.Count == 0) return false;
        await SendMapPortals(remote, stream, ciphers, conn, deviceId,
            MapDevice.OpenOf(CharacterName(conn))?.Area.Hash16 ?? conn.Layout.Hash);
        return true;
    }

    // S→C 0x8f: the device's portals and where they lead (21-hideouts.md §5). Without it the client never draws them; a
    // portal listed with area 0 (one just taken) closes.
    static async Task SendMapPortals(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, uint deviceId, ushort area, uint closed = 0)
    {
        byte style = conn.MapPortalStyle;
        List<uint> ids;
        lock (conn.MapPortals) ids = [.. conn.MapPortals.Keys.Order()];
        string owner = CharacterName(conn);
        var list = (closed == 0 ? [] : new List<InstanceMapDevice.Portal> { new(closed, 0) })
            .Concat(ids.Select(id => new InstanceMapDevice.Portal(id, area, owner, style, conn.CosmeticPortalVariation))).ToList();
        await Send(remote, stream, ciphers, InstanceMapDevice.Portals(deviceId, list),
            $"0x008f map device 0x{deviceId:x} portals: {ids.Count} open to 0x{area:x4}{(closed == 0 ? "" : $", 0x{closed:x} closed")}");
    }

    // Created closed (hidden), a map device portal is drawn and targetable once it is opened (Transitionable 0 → 1). Sent in
    // the same burst as its creation, the update hung the client (live 2026-10-20), so it goes once the portal is built.
    static void OpenMapPortalLater(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, uint id, int delayMs) =>
        _ = Task.Delay(delayMs).ContinueWith(async _ =>
        {
            bool still;
            lock (conn.MapPortals) still = conn.MapPortals.ContainsKey(id);
            if (still && !conn.Closed) await Send(remote, stream, ciphers, MapPortalObject.OpenUpdate(id), $"0x02ba map portal 0x{id:x} opens");
        });

    static async Task RemoveMapPortals(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
    {
        List<uint> ids;
        Interlocked.Increment(ref conn.MapPortalGeneration);   // stops a sequence still placing them
        lock (conn.MapPortals) { ids = [.. conn.MapPortals.Keys]; conn.MapPortals.Clear(); }
        uint deviceId;
        lock (conn.HideoutDoodads) deviceId = conn.HideoutDoodads.Keys.FirstOrDefault();
        if (ids.Count > 0 && deviceId != 0)
            await Send(remote, stream, ciphers, InstanceMapDevice.Portals(deviceId, [.. ids.Select(id => new InstanceMapDevice.Portal(id, 0))]),
                $"0x008f map device 0x{deviceId:x}: {ids.Count} portal(s) closed");
        foreach (uint id in ids) await Send(remote, stream, ciphers, InstanceProtocol.RemoveObject(id), $"0x{InstanceProtocol.RemoveObjectId:x4} remove map portal 0x{id:x}");
    }

    // A click on a map portal (C→S 0x24): walk there, then the portal is used up and the player goes into the open map, a new
    // instance for the first portal and that same instance for the others.
    static async Task UseMapPortal(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        InstanceTransfer.UseSkillOnObject use, (int X, int Y, int Index) at, double requestedAt)
    {
        if (!TryBeginObjectWalk(conn, requestedAt, at.X, at.Y, AreaTransitionObjectSize, out var objectWalk)) return;
        var (action, now, here, stop, arrive) = objectWalk;
        Console.WriteLine($"[gateway] {remote} <= 0x0024 map portal 0x{use.TargetId:x} from ({here.X}, {here.Y}): reached at ({stop.X}, {stop.Y}) in {arrive - now:0.00} s");
        if (arrive > now) await Task.Delay(TimeSpan.FromSeconds(arrive - now));
        if (!PlayerActionCurrent(conn, action) || conn.PendingTransfer is not null) return;
        lock (conn.MapPortals) if (!conn.MapPortals.Remove(use.TargetId)) return;
        string character = CharacterName(conn);
        uint deviceId;
        lock (conn.HideoutDoodads) deviceId = conn.HideoutDoodads.Keys.FirstOrDefault();
        // The portal at this place is used up: it is not there when the player comes back (the others are).
        if (MapDevice.TakePortal(character, at.Index) is not var (map, left))
        {
            Console.WriteLine($"[gateway] {remote} map portal 0x{use.TargetId:x}: no open map");
            await Send(remote, stream, ciphers, InstanceProtocol.RemoveObject(use.TargetId), $"0x{InstanceProtocol.RemoveObjectId:x4} remove map portal 0x{use.TargetId:x}");
            return;
        }
        if (deviceId != 0) await SendMapPortals(remote, stream, ciphers, conn, deviceId, map.Area.Hash16, closed: use.TargetId);
        await Send(remote, stream, ciphers, InstanceProtocol.RemoveObject(use.TargetId), $"0x{InstanceProtocol.RemoveObjectId:x4} remove map portal 0x{use.TargetId:x} (used)");
        Console.WriteLine($"[gateway] {remote} map portal at place {at.Index} taken: {left} left, into {(map.Instance == 0 ? "a new instance" : $"instance {map.Instance}")} of {map.Area.Name}");
        await ChangeArea(remote, stream, ciphers, conn, new PendingTransfer(map.Area.Hash16, map.Instance, map.Instance == 0)
        {
            Done = r =>
            {
                if (r.Ok) MapDevice.Entered(character, map.Area, r.Instance);
                else MapDevice.Close(character);
            },
        }, $"map portal to {map.Area.Name}");
    }

    // In a map's area (one a map item opens): a portal back to the hideout next to the arrival point, listed in 0x8f without
    // a device once the world is active (21-hideouts.md §5). It is not used up.
    static async Task SpawnMapReturnPortal(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
    {
        if (!MapDevices.Value.IsMapArea(conn.Layout.Hash) || HomeHideout(conn) is not { } hideout) return;
        var (x, y) = conn.Layout.Near((conn.Layout.SpawnX, conn.Layout.SpawnY));
        uint id = conn.NextObjectId++;
        await Send(remote, stream, ciphers, InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(MapPortalObject.MetadataPath),
            new MapPortalObject(x, y, -1, MapPortalType, Open: 1, Rotation: conn.PortalRotationRadians).Blob()),
            $"0x02b9 create the portal back to the {hideout.Name} 0x{id:x} at ({x}, {y})");
        await Send(remote, stream, ciphers, InstanceProtocol.EnterWakeZone(id), null);
        conn.ReturnPortal = (id, x, y);
        conn.ReturnPortalTo = 0;
        string owner = CharacterName(conn);
        _ = Task.Delay(MapPortalEntryMs).ContinueWith(async _ =>
        {
            if (!conn.Closed)
                await Send(remote, stream, ciphers, InstanceMapDevice.Portals(0,
                    [new(id, hideout.AreaHash16, owner, Variation: conn.CosmeticPortalVariation)]),
                    $"0x008f the portal 0x{id:x} leads to the {hideout.Name} (0x{hideout.AreaHash16:x4})");
        });
    }

    // The character's open town portal (OpenPortals) on entering an area: in the area it stands in (the same instance), the
    // portal again at its cell; in the town or hideout it leads to, a portal back beside the arrival, which brings the player
    // to that cell (SpotArrivals).
    static async Task SpawnTownPortals(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
    {
        if (!OpenPortals.TryGetValue(CharacterName(conn), out var open)) return;
        if (open.Area == conn.Layout.Hash && open.Instance == conn.InstanceNumber)
        {
            await OpenTownPortal(remote, stream, ciphers, conn, open.X, open.Y, "town portal again", replace: false);
            return;
        }
        if (open.LeadsTo != conn.Layout.Hash) return;
        var (x, y) = conn.Layout.Near((conn.Layout.SpawnX + 4, conn.Layout.SpawnY));
        uint id = conn.NextObjectId++;
        await Send(remote, stream, ciphers, InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(MapPortalObject.MetadataPath),
            new MapPortalObject(x, y, -1, MapPortalType, Open: 1).Blob()), $"0x02b9 create the portal back 0x{id:x} at ({x}, {y}) to 0x{open.Area:x4}");
        await Send(remote, stream, ciphers, InstanceProtocol.EnterWakeZone(id), null);
        conn.BackPortal = (id, x, y);
        string owner = CharacterName(conn);
        _ = Task.Delay(MapPortalListMs).ContinueWith(async _ =>
        {
            if (!conn.Closed && conn.BackPortal?.Id == id)
                await Send(remote, stream, ciphers, InstanceMapDevice.Portals(0, [new(id, open.Area, owner)]),
                    $"0x008f the portal back 0x{id:x} leads to 0x{open.Area:x4}");
        });
    }

    // A click on the portal back (C→S 0x24, the map's own or a Portal Scroll's): walk there, then to the hideout next to its
    // waypoint, or to the town the scroll's portal leads to (ReturnPortalTo), at its spawn.
    static async Task UseMapReturnPortal(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        InstanceTransfer.UseSkillOnObject use, (int X, int Y) at, double requestedAt)
    {
        if (!TryBeginObjectWalk(conn, requestedAt, at.X, at.Y, AreaTransitionObjectSize, out var objectWalk)) return;
        var (action, now, here, stop, arrive) = objectWalk;
        Console.WriteLine($"[gateway] {remote} <= 0x0024 portal back 0x{use.TargetId:x} from ({here.X}, {here.Y}): reached at ({stop.X}, {stop.Y}) in {arrive - now:0.00} s");
        if (arrive > now) await Task.Delay(TimeSpan.FromSeconds(arrive - now));
        if (!PlayerActionCurrent(conn, action)) return;
        if (conn.ReturnPortalTo is ushort town and not 0)
            await ChangeArea(remote, stream, ciphers, conn, new PendingTransfer(town, 0, false, AtSpawn: true), "portal to town");
        else
            await WarpToHideout(remote, stream, ciphers, conn, "map portal back");
    }

    // A Portal Scroll used (C→S 0x58) outside towns and hideouts: one scroll is spent and a portal opens where the player
    // stands, replacing the one there was (the scroll's earlier one, or a map's own portal back). From a map it leads to the
    // hideout (the player came through its map device), anywhere else to the character's last town. In a town or a hideout
    // nothing happens and nothing is spent.
    static async Task UsePortalScroll(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, byte row, InventoryItem item,
        ItemCatalog.BaseType type)
    {
        if (!PortalAllowed(conn))
        {
            Console.WriteLine($"[gateway] {remote} <= 0x0058 use {type.Name}: in a town or hideout, or {conn.Layout.Id} forbids portals: nothing happens");
            return;
        }
        var items = ItemsOf(conn);
        if (items.ConsumeOne(row, item.Id) is not { } taken) return;
        uint inventoryId = PlayerInventories.InventoryId(row);
        await Send(remote, stream, ciphers, InstanceItems.InventoryChange(inventoryId, items.NextRevision(row), [item.Id], taken.Left is { } left ? [left] : []),
            $"0x00bf inventory {inventoryId}: {type.Name} used" + (taken.Left is { } now ? $", {now.Item.StackSize} left" : ", stack empty"));
        var (px, py) = conn.Walk.Position(conn.Clock.Elapsed.TotalSeconds);
        await OpenTownPortal(remote, stream, ciphers, conn, px, py, type.Name);
    }

    // Whether a town portal can open here: not in a town or a hideout, and not where the area's own mods forbid portals.
    static bool PortalAllowed(ClientState conn) =>
        WaypointCatalog.Areas.Find(conn.Layout.Hash) is not { IsTown: true } && HideoutCatalog.Hideouts.All(h => h.AreaHash16 != conn.Layout.Hash)
        && !AreaMods.Value.DisablesPortals(conn.Layout.Id);

    // A town portal (Portal Scroll, the Portal skill) beside (px, py): from a map to the hideout, anywhere else to the
    // character's last town. It replaces the one there was, and the character's town / hideout gets a portal back to it
    // (OpenPortals, SpawnBackPortal), which brings the player back to this cell of this instance.
    static async Task OpenTownPortal(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, int px, int py, string why,
        bool replace = true)
    {
        ushort here = conn.Layout.Hash;
        var home = HomeHideout(conn);
        bool toHideout = MapDevices.Value.IsMapArea(here) && home is not null;
        ushort town = conn.LastTown != 0 ? conn.LastTown : AreaLayouts.LioneyesWatch;
        // One portal per player: the old one closes (0x8f with area 0) and goes.
        if (conn.ReturnPortal is { } old)
        {
            await Send(remote, stream, ciphers, InstanceMapDevice.Portals(0, [new(old.Id, 0)]), null);
            await Send(remote, stream, ciphers, InstanceProtocol.RemoveObject(old.Id), $"0x{InstanceProtocol.RemoveObjectId:x4} remove the old portal 0x{old.Id:x}");
        }
        // Beside the player, so the click that opens it is not on the player (a reopened portal stands where it stood).
        var (x, y) = !replace || conn.Layout.Walkable is not { } walkable || !walkable(px + 3, py) ? (px, py) : (px + 3, py);
        uint id = conn.NextObjectId++;
        await Send(remote, stream, ciphers, InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(MapPortalObject.MetadataPath),
            new MapPortalObject(x, y, -1, MapPortalType, Open: 1).Blob()), $"0x02b9 create the Portal Scroll's portal 0x{id:x} at ({x}, {y})");
        await Send(remote, stream, ciphers, InstanceProtocol.EnterWakeZone(id), null);
        conn.ReturnPortal = (id, x, y);
        conn.ReturnPortalTo = toHideout ? (ushort)0 : town;
        ushort leadsTo = toHideout ? home!.AreaHash16 : town;
        string owner = CharacterName(conn);
        OpenPortals[owner] = new OpenPortal(here, conn.InstanceNumber, x, y, leadsTo);
        Console.WriteLine($"[gateway] {remote} {why}: town portal at ({x}, {y}) of 0x{here:x4} instance {conn.InstanceNumber} → 0x{leadsTo:x4}");
        // 0x8f only reaches a portal the client has finished creating (MapPortalListMs, as the device's portals).
        _ = Task.Delay(MapPortalListMs).ContinueWith(async _ =>
        {
            if (!conn.Closed && conn.ReturnPortal?.Id == id)
                await Send(remote, stream, ciphers, InstanceMapDevice.Portals(0, [new(id, leadsTo, owner)]),
                    $"0x008f the Portal Scroll's portal 0x{id:x} leads to 0x{leadsTo:x4} ({(toHideout ? "the hideout" : "the last town")})");
        });
    }
}
