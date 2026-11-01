using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

static partial class CommandsRules
{
    internal static void SendMapPortalsLater(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, uint deviceId, ushort area, int delayMs) =>
        _ = Task.Delay(delayMs).ContinueWith(async _ => { if (!conn.Closed) await SendMapPortals(remote, stream, ciphers, conn, deviceId, area); });

    // Removes and recreates the map portals that stand, at the same places and with the current rotation, then lists them
    // again: the client builds a portal's visual from its object, so a rotated portal effect can be tried without reopening
    // the device. Returns how many were rebuilt.
    internal static async Task<int> RebuildMapPortals(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
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

    // Created closed (hidden), a map device portal is drawn and targetable once it is opened (Transitionable 0 → 1). Sent in
    // the same burst as its creation, the update hung the client (live 2026-10-20), so it goes once the portal is built.
    internal static void OpenMapPortalLater(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, uint id, int delayMs) =>
        _ = Task.Delay(delayMs).ContinueWith(async _ =>
        {
            bool still;
            lock (conn.MapPortals) still = conn.MapPortals.ContainsKey(id);
            if (still && !conn.Closed) await Send(remote, stream, ciphers, MapPortalObject.OpenUpdate(id), $"0x02ba map portal 0x{id:x} opens");
        });
}
