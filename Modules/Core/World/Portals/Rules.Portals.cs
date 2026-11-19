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
                    await SendMapPortals(visitor, MapDeviceHere(visitor)?.Id ?? 0, run.Area.Hash16, closed: id);
                    await global::Game.SendWorldRemoval(visitor, id, $"map portal {index} used");
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
    internal static Task SpawnMapPortals(ClientState conn, MapDevice.OpenMap open, int firstAfterMs = 0)
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
        _ = Task.Run(async () =>
        {
            try
            {
                if (firstAfterMs > 0) await Task.Delay(firstAfterMs);
                int n = 0;
                foreach (int index in open.Left)
                {
                    if (!Current()) return;
                    if (n++ > 0) await Task.Delay(MapPortalGapMs);
                    if (!Current()) return;
                    if (!Available(index)) continue;
                    uint id = conn.TakeObjectId();
                    var ring = MapDevice.PortalCell(centre.X, centre.Y, index);
                    var (x, y) = device.Campaign && conn.Layout.Walkable is { } walk && !walk(ring.X, ring.Y)
                        ? conn.Layout.Near(ring) : ring;
                    await global::Game.SendWorldCreate(conn, id, InstanceEntities.CreateObject(id, type,
                            new MapPortalObject(x, y, index, MapPortalType, Open: 1, Rotation: conn.PortalRotationRadians).Blob()),
                        $"0x02bb create map portal 0x{id:x} (place {index}, {n}/{open.PortalsLeft}) to {open.Area.Name} at ({x}, {y})");
                    await global::Game.SendWorldRecord(conn, id, InstanceProtocol.EnterWakeZone(id), null);
                    bool keep;
                    lock (conn.MapPortals)
                    {
                        keep = Available(index);
                        if (keep) conn.MapPortals[id] = (x, y, index);
                    }
                    if (!keep)
                    {
                        await global::Game.SendWorldRemoval(conn, id, null);
                        continue;
                    }
                    await Task.Delay(MapPortalListMs);
                    if (!Current()) return;
                    await SendMapPortals(conn, deviceId, open.Area.Hash16);
                }
                // Every portal but the last was listed again with the next one; the last gets its second listing here
                // (Game.ListPortal says why one listing can be lost).
                if (n == 0) return;
                await Task.Delay(MapPortalRelistMs);
                if (Current()) await SendMapPortals(conn, deviceId, open.Area.Hash16);
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException) { } // The visitor left during the sequence.
        });
        return Task.CompletedTask;
    }

    internal static async Task RemoveMapPortals(ClientState conn)
    {
        List<uint> ids;
        Interlocked.Increment(ref conn.MapPortalGeneration);   // stops a sequence still placing them
        lock (conn.MapPortals) { ids = [.. conn.MapPortals.Keys]; conn.MapPortals.Clear(); }
        uint deviceId = MapDeviceHere(conn)?.Id ?? 0;
        if (ids.Count > 0 && deviceId != 0)
            await Send(conn, InstanceMapDevice.Portals(deviceId, [.. ids.Select(id => new InstanceMapDevice.Portal(id, 0))]),
                $"0x008f map device 0x{deviceId:x}: {ids.Count} portal(s) closed");
        foreach (uint id in ids) await global::Game.SendWorldRemoval(conn, id, $"0x{InstanceProtocol.RemoveObjectId:x4} remove map portal 0x{id:x}");
    }
}
