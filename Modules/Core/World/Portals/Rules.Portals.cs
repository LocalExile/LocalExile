using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

static partial class PortalsRules
{
    internal static IEnumerable<ClientState> DeviceVisitors(ClientState conn) =>
        Program.InGame.Keys.Append(conn).Distinct().Where(c => !c.Closed && !c.LeavingArea
            && (c.InstanceNumber == conn.InstanceNumber && c.Layout.Hash == conn.Layout.Hash
                || c.MapPortalRun?.Owner == CharacterName(conn)));

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
                var (r, s, c) = visitor.Wire;
                foreach (uint id in ids)
                {
                    await SendMapPortals(r, s, c, visitor, MapDeviceHere(visitor)?.Id ?? 0, run.Area.Hash16, closed: id);
                    await global::Game.SendWorldRemoval(r, s, c, visitor, id, $"map portal {index} used");
                }
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException) { } // A departing observer cannot stop entry.
        }
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
    internal static Task SpawnMapPortals(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, MapDevice.OpenMap open,
        int firstAfterMs = 0, bool standing = false)
    {
        if (MapDeviceHere(conn) is not { } device) return Task.CompletedTask;
        uint deviceId = device.Id;
        var centre = (device.X, device.Y);
        if (device.Campaign && new QuestObjectTypes.AreaPlacements(conn.Layout.Id, conn.Seed, [])
            .Of(MapDevice.CampaignDeviceType + "_Main").FirstOrDefault() is { } orrery)
            centre = (orrery.X, orrery.Y);
        conn.MapPortalRun = open;
        int generation = Interlocked.Increment(ref conn.MapPortalGeneration);
        bool Current() => !conn.Closed && !conn.LeavingArea && conn.MapPortalGeneration == generation
            && MapDevice.OpenOf(open.Owner)?.Run == open.Run;
        bool Available(int index) => Current() && MapDevice.OpenOf(open.Owner)?.Left.Contains(index) == true;
        uint type = InstanceEntities.ObjectTypeHash(MapPortalObject.MetadataPath);
        (int X, int Y) Place(int index)
        {
            var ring = MapDevice.PortalCell(centre.X, centre.Y, index);
            return device.Campaign && conn.Layout.Walkable is { } walk && !walk(ring.X, ring.Y) ? conn.Layout.Near(ring) : ring;
        }
        Task Create(uint id, int x, int y, int index, string log) =>
            global::Game.SendWorldCreate(remote, stream, ciphers, conn, id, InstanceEntities.CreateObject(id, type,
                new MapPortalObject(x, y, index, MapPortalType, Open: 1, Rotation: conn.PortalRotationRadians).Blob()), log);
        _ = Task.Run(async () =>
        {
            try
            {
                if (firstAfterMs > 0) await Task.Delay(firstAfterMs);
                if (standing)
                {
                    if (!Current()) return;
                    var placed = new List<(uint Id, int X, int Y, int Index)>();
                    lock (conn.MapPortals)
                        foreach (int index in open.Left)
                            if (Available(index))
                            {
                                var (px, py) = Place(index);
                                uint pid = conn.TakeObjectId();
                                conn.MapPortals[pid] = (px, py, index);
                                placed.Add((pid, px, py, index));
                            }
                    if (placed.Count == 0) return;
                    await SendMapPortals(remote, stream, ciphers, conn, deviceId, open.Area.Hash16);
                    foreach (var (pid, px, py, index) in placed)
                    {
                        lock (conn.MapPortals) if (!conn.MapPortals.ContainsKey(pid)) continue;   // taken meanwhile
                        await Create(pid, px, py, index,
                            $"0x02bb create map portal 0x{pid:x} (place {index}, standing) to {open.Area.Name} at ({px}, {py})");
                        await global::Game.SendWorldRecord(remote, stream, ciphers, conn, pid, InstanceProtocol.EnterWakeZone(pid), null);
                        bool kept;
                        lock (conn.MapPortals) kept = conn.MapPortals.ContainsKey(pid);
                        if (!kept) await global::Game.SendWorldRemoval(remote, stream, ciphers, conn, pid, null);
                    }
                    await Task.Delay(MapPortalListMs);
                    if (!Current()) return;
                    await SendMapPortals(remote, stream, ciphers, conn, deviceId, open.Area.Hash16);
                    await Task.Delay(MapPortalRelistMs);
                    if (Current()) await SendMapPortals(remote, stream, ciphers, conn, deviceId, open.Area.Hash16);
                    return;
                }
                int n = 0;
                foreach (int index in open.Left)
                {
                    if (!Current()) return;
                    if (n++ > 0) await Task.Delay(MapPortalGapMs);
                    if (!Current()) return;
                    if (!Available(index)) continue;
                    uint id = conn.TakeObjectId();
                    var (x, y) = Place(index);
                    await Create(id, x, y, index,
                        $"0x02bb create map portal 0x{id:x} (place {index}, {n}/{open.PortalsLeft}) to {open.Area.Name} at ({x}, {y})");
                    await global::Game.SendWorldRecord(remote, stream, ciphers, conn, id, InstanceProtocol.EnterWakeZone(id), null);
                    bool keep;
                    lock (conn.MapPortals)
                    {
                        keep = Available(index);
                        if (keep) conn.MapPortals[id] = (x, y, index);
                    }
                    if (!keep)
                    {
                        await global::Game.SendWorldRemoval(remote, stream, ciphers, conn, id, null);
                        continue;
                    }
                    await Task.Delay(MapPortalListMs);
                    if (!Current()) return;
                    await SendMapPortals(remote, stream, ciphers, conn, deviceId, open.Area.Hash16);
                }
                // Every portal but the last was listed again with the next one; the last gets its second listing here
                // (Game.ListPortal says why one listing can be lost).
                if (n == 0) return;
                await Task.Delay(MapPortalRelistMs);
                if (Current()) await SendMapPortals(remote, stream, ciphers, conn, deviceId, open.Area.Hash16);
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException) { } // The visitor left during the sequence.
        });
        return Task.CompletedTask;
    }

    internal static async Task RemoveMapPortals(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
    {
        List<uint> ids;
        Interlocked.Increment(ref conn.MapPortalGeneration);   // stops a sequence still placing them
        lock (conn.MapPortals) { ids = [.. conn.MapPortals.Keys]; conn.MapPortals.Clear(); }
        uint deviceId = MapDeviceHere(conn)?.Id ?? 0;
        if (ids.Count > 0 && deviceId != 0)
            await Send(remote, stream, ciphers, InstanceMapDevice.Portals(deviceId, [.. ids.Select(id => new InstanceMapDevice.Portal(id, 0))]),
                $"0x008f map device 0x{deviceId:x}: {ids.Count} portal(s) closed");
        foreach (uint id in ids) await global::Game.SendWorldRemoval(remote, stream, ciphers, conn, id, $"0x{InstanceProtocol.RemoveObjectId:x4} remove map portal 0x{id:x}");
    }

    // A hideout's instance is gone six minutes after its last player leaves (InstanceRegistry.Lifetime), and the device
    // and the portals standing at it go with it. The character comes back into a new instance of the same area: the run
    // follows them there, so the portals left come up at the new device instead of the run becoming unreachable (live
    // 2026-11-18: a disconnect inside the Simulacrum after the hideout had stood empty for six minutes came back to a
    // device with no portals, while the map's own instance was still alive).
    internal static async Task FollowDeviceToThisInstance(string remote, ClientState conn)
    {
        if (MapDevice.OpenOf(CharacterName(conn)) is not { PortalsLeft: > 0 } open
            || (open.Entrance ?? open.ReturnTo) is not { } origin
            || origin.Area != conn.Layout.Hash || origin.Instance == 0 || origin.Instance == conn.InstanceNumber) return;
        var response = await InstanceControl.CallAsync(new ControlRequest { Op = "list", Ticket = conn.Ticket, Area = origin.Area });
        if (!AreaInstances.Gone(response, origin.Instance)) return;
        if (MapDevice.Rebind(CharacterName(conn), origin.Area, conn.InstanceNumber) is not { } moved) return;
        Console.WriteLine($"[gateway] {remote} map run of {moved.Area.Id} follows the device into instance {conn.InstanceNumber} " +
            $"(instance {origin.Instance} of {moved.Area.Name} is gone)");
    }
}
