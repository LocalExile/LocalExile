using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

static partial class PortalsRules
{
    // The open map's portals that are left, at their places on a ring around the hideout's map device, one after another
    // (MapPortalGapMs apart). 0x8f only reaches portal objects the client has already created, and a create sent in the same
    // burst is not done yet (live 2026-10-20), so each portal is listed MapPortalListMs after its create; on entering the
    // area the objects also wait until the world is active (firstAfterMs). A newer Activate or the portals' removal stops
    // the sequence.
    internal static Task SpawnMapPortals(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, MapDevice.OpenMap open, int firstAfterMs = 0)
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

    internal static async Task RemoveMapPortals(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
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
}
