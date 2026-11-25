using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

static partial class PortalsRules
{
    internal static IEnumerable<Player> DeviceVisitors(Player player) =>
        Program.InGame.Keys.Append(player).Distinct().Where(c => !c.Closed && !c.LeavingArea
            && (c.InstanceNumber == player.InstanceNumber && c.Layout.Hash == player.Layout.Hash
                || c.MapPortalRun?.Owner == CharacterName(player)));

    internal static async Task RetireMapPortal(MapDevice.OpenMap run, int index)
    {
        foreach (var visitor in Program.InGame.Keys.Where(c => !c.Closed && c.MapPortalRun?.Run == run.Run))
        {
            uint[] ids;
            lock (visitor.MapPortals)
            {
                ids = [.. visitor.MapPortals.Where(p => p.Value.Index == index).Select(p => p.Key)];
                foreach (uint id in ids) visitor.MapPortals.Remove(id);
            }
            try
            {
                foreach (uint id in ids)
                {
                    await SendMapPortals(visitor, MapDeviceHere(visitor)?.Id ?? 0, run.Area.Hash16, closed: id);
                    await global::Game.SendWorldRemoval(visitor, id, $"map portal {index} used");
                }
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException) { } // A departing observer cannot stop entry.
        }
        // A device that opens one portal at a time stands the next one, at the same place, once the last is taken.
        if (MapDevice.OpenOf(run.Owner) is not { PortalsLeft: > 0 } next || next.Run != run.Run) return;
        foreach (var visitor in Program.InGame.Keys.Where(c => !c.Closed && !c.LeavingArea && c.MapPortalRun?.Run == run.Run
            && MapDevice.AtEntrance(c.Layout.Hash, c.InstanceNumber)?.Run == run.Run))
            if (OneAtATime(visitor) is int delay)
            {
                // Each visitor's portals are their own view, as on entry: without it the creates go to the whole area.
                var previousView = Program.ViewOf.Value;
                Program.ViewOf.Value = visitor;
                try { await SpawnMapPortals(visitor, next, firstAfterMs: Math.Max(delay, MapPortalGapMs)); }
                finally { Program.ViewOf.Value = previousView; }
            }
    }

    // The hideout device here opens one portal at a time: the delay before the next one, else null.
    static int? OneAtATime(Player player)
    {
        if (MapDeviceHere(player) is not { Campaign: false } device) return null;
        lock (player.HideoutDoodads)
            return player.HideoutDoodads.GetValueOrDefault(device.Id) is { } d ? global::Game.HideoutNextPortalMs(d) : null;
    }

    // The open map's portals that are left, at their places on a ring around the hideout's map device, one after another
    // (MapPortalGapMs apart). 0x8f only reaches portal objects the client has already created, and a create sent in the same
    // burst is not done yet (live 2026-10-20), so each portal is listed MapPortalListMs after its create; on entering the
    // area the objects also wait until the world is active (firstAfterMs). A newer Activate or the portals' removal stops
    // the sequence.
    // Standing portals (the player enters an area where they are open already) come up together and settled: the client
    // plays a portal's opening (townportalopen) whenever 0x8f lists it for the first time or changed, so their entries are
    // listed once before the creates, while the objects do not exist yet, and every later list leaves them idle
    // (21-hideouts.md §5).
    internal static Task SpawnMapPortals(Player player, MapDevice.OpenMap open, int firstAfterMs = 0, bool standing = false)
    {
        if (MapDeviceHere(player) is not { } device) return Task.CompletedTask;
        uint deviceId = device.Id;
        var centre = (device.X, device.Y);
        if (device.Campaign && new QuestObjectTypes.AreaPlacements(player.Layout.Id, player.Seed, [])
            .Of(MapDevice.CampaignDeviceType + "_Main").FirstOrDefault() is { } orrery)
            centre = (orrery.X, orrery.Y);
        player.MapPortalRun = open;
        int generation = Interlocked.Increment(ref player.MapPortalGeneration);
        bool Current() => !player.Closed && !player.LeavingArea && player.MapPortalGeneration == generation
            && MapDevice.OpenOf(open.Owner)?.Run == open.Run;
        bool Available(int index) => Current() && MapDevice.OpenOf(open.Owner)?.Left.Contains(index) == true;
        uint type = InstanceEntities.ObjectTypeHash(MapPortalObject.MetadataPath);
        HideoutCatalog.Doodad? hideoutDevice = null;
        if (!device.Campaign) lock (player.HideoutDoodads) hideoutDevice = player.HideoutDoodads.GetValueOrDefault(deviceId);
        int most = hideoutDevice is not null && global::Game.HideoutNextPortalMs(hideoutDevice) is not null ? 1 : int.MaxValue;
        var facing = new Dictionary<int, float>();
        (int X, int Y) Place(int index)
        {
            (int X, int Y) cell;
            if (hideoutDevice is not null)
            {
                var (x, y, rotation) = global::Game.HideoutPortalPlace(hideoutDevice, index);
                if (rotation is float r) lock (facing) facing[index] = r;
                cell = (x, y);
            }
            else cell = MapDevice.PortalCell(centre.X, centre.Y, index);
            return player.Layout.Walkable is { } walk && !walk(cell.X, cell.Y) ? player.Layout.Near(cell) : cell;
        }
        float PortalAngle(int x, int y, int index)
        {
            lock (facing)
                return (facing.TryGetValue(index, out float scripted) ? scripted : MapDevice.PortalRotation(centre.X, centre.Y, x, y, index))
                    + player.PortalRotationRadians;
        }
        Task Create(uint id, int x, int y, int index, string log) =>
            global::Game.SendWorldCreate(player, id, () => InstanceEntities.CreateObject(id, type,
                new MapPortalObject(x, y, index, MapPortalType, Open: 1,
                    Rotation: PortalAngle(x, y, index)).Blob()), log, wake: true, awaitBirth: false);
        _ = AreaFrames.Start(async () =>
        {
            try
            {
                if (firstAfterMs > 0) await AreaDelay(player, firstAfterMs / 1000.0);
                if (standing)
                {
                    if (!Current()) return;
                    var placed = new List<(uint Id, int X, int Y, int Index)>();
                    lock (player.MapPortals)
                        foreach (int index in open.Left.Where(Available).Take(most))
                        {
                            var (px, py) = Place(index);
                            uint pid = player.TakeObjectId();
                            player.MapPortals[pid] = (px, py, index);
                            placed.Add((pid, px, py, index));
                        }
                    if (placed.Count == 0) return;
                    await SendMapPortals(player, deviceId, open.Area.Hash16);
                    foreach (var (pid, px, py, index) in placed)
                    {
                        lock (player.MapPortals) if (!player.MapPortals.ContainsKey(pid)) continue;   // taken meanwhile
                        await Create(pid, px, py, index,
                            $"0x02bb create map portal 0x{pid:x} (place {index}, standing) to {open.Area.Name} at ({px}, {py})");
                        bool kept;
                        lock (player.MapPortals) kept = player.MapPortals.ContainsKey(pid);
                        if (!kept) await global::Game.SendWorldRemoval(player, pid, null);
                    }
                    await AreaDelay(player, MapPortalListMs / 1000.0);
                    if (!Current()) return;
                    await SendMapPortals(player, deviceId, open.Area.Hash16);
                    foreach (var (pid, px, py, index) in placed)
                    {
                        lock (player.MapPortals) if (!player.MapPortals.ContainsKey(pid)) continue;
                        await global::Game.SendWorldRecord(player, pid,
                            InstanceHideoutEditing.Rotation(pid, PortalAngle(px, py, index)), null);
                    }
                    await AreaDelay(player, MapPortalRelistMs / 1000.0);
                    if (Current()) await SendMapPortals(player, deviceId, open.Area.Hash16);
                    return;
                }
                int n = 0, made = 0;
                foreach (int index in open.Left)
                {
                    if (!Current()) return;
                    if (made >= most) break;
                    if (n++ > 0) await AreaDelay(player, MapPortalGapMs / 1000.0);
                    if (!Current()) return;
                    if (!Available(index)) continue;
                    uint id = player.TakeObjectId();
                    var (x, y) = Place(index);
                    await Create(id, x, y, index,
                        $"0x02bb create map portal 0x{id:x} (place {index}, {n}/{open.PortalsLeft}) to {open.Area.Name} at ({x}, {y})");
                    bool keep;
                    lock (player.MapPortals)
                    {
                        keep = Available(index);
                        if (keep) { player.MapPortals[id] = (x, y, index); made++; }
                    }
                    if (!keep)
                    {
                        await global::Game.SendWorldRemoval(player, id, null);
                        continue;
                    }
                    await AreaDelay(player, MapPortalListMs / 1000.0);
                    if (!Current()) return;
                    await SendMapPortals(player, deviceId, open.Area.Hash16);
                    bool stillOpen;
                    lock (player.MapPortals) stillOpen = player.MapPortals.ContainsKey(id);
                    if (stillOpen)
                        await global::Game.SendWorldRecord(player, id,
                            InstanceHideoutEditing.Rotation(id, PortalAngle(x, y, index)), null);
                }
                // Every portal but the last was listed again with the next one; the last gets its second listing here
                // (Game.ListPortal says why one listing can be lost).
                if (n == 0) return;
                await AreaDelay(player, MapPortalRelistMs / 1000.0);
                if (Current()) await SendMapPortals(player, deviceId, open.Area.Hash16);
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException) { } // The visitor left during the sequence.
        });
        return Task.CompletedTask;
    }

    // A run that ends late names itself: a refused transfer's Done is fire-and-forget, and by the time it runs the
    // device may have been activated again. Only a connection still showing that run's portals loses them, so the
    // newer run's six portals and its placement sequence stay (review 2026-11-18: a late failure of run A after B).
    internal static async Task RemoveMapPortals(Player player, MapDevice.OpenMap? run = null)
    {
        List<uint> ids;
        if (run is { } ended)
        {
            lock (player.MapPortals)
            {
                if (player.MapPortalRun?.Run != ended.Run) return;   // a newer run's portals are not this run's to remove
                Interlocked.Increment(ref player.MapPortalGeneration);   // stops a sequence still placing them
                ids = [.. player.MapPortals.Keys];
                player.MapPortals.Clear();
            }
        }
        else
        {
            Interlocked.Increment(ref player.MapPortalGeneration);   // stops a sequence still placing them
            lock (player.MapPortals) { ids = [.. player.MapPortals.Keys]; player.MapPortals.Clear(); }
        }
        uint deviceId = MapDeviceHere(player)?.Id ?? 0;
        if (ids.Count > 0 && deviceId != 0)
            await Send(player, InstanceMapDevice.Portals(deviceId, [.. ids.Select(id => new InstanceMapDevice.Portal(id, 0))]),
                $"0x008f map device 0x{deviceId:x}: {ids.Count} portal(s) closed");
        foreach (uint id in ids) await global::Game.SendWorldRemoval(player, id, $"0x{InstanceProtocol.RemoveObjectId:x4} remove map portal 0x{id:x}");
    }

    // A hideout's instance is gone six minutes after its last player leaves (InstanceRegistry.Lifetime), and the device
    // and the portals standing at it go with it. The character comes back into a new instance of the same area: the run
    // follows them there, so the portals left come up at the new device instead of the run becoming unreachable (live
    // 2026-11-18: a disconnect inside the Simulacrum after the hideout had stood empty for six minutes came back to a
    // device with no portals, while the map's own instance was still alive).
    internal static async Task FollowDeviceToThisInstance(Player player)
    {
        if (MapDevice.OpenOf(CharacterName(player)) is not { PortalsLeft: > 0 } open
            || (open.Entrance ?? open.ReturnTo) is not { } origin
            || origin.Area != player.Layout.Hash || origin.Instance == 0 || origin.Instance == player.InstanceNumber) return;
        var response = await InstanceControl.CallAsync(new ControlRequest { Op = "list", Ticket = player.Ticket, Area = origin.Area });
        if (!AreaInstances.Gone(response, origin.Instance)) return;
        if (MapDevice.Rebind(CharacterName(player), origin.Area, player.InstanceNumber) is not { } moved) return;
        Console.WriteLine($"[gateway] {player.Connection.Remote} map run of {moved.Area.Id} follows the device into instance {player.InstanceNumber} " +
            $"(instance {origin.Instance} of {moved.Area.Name} is gone)");
    }
}
