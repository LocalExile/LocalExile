using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

public static partial class Game
{
    public static (uint Id, int X, int Y, bool Campaign)? MapDeviceHere(ClientState conn)
    {
        if (conn.Layout.Id == MapDevice.CampaignDeviceArea)
            lock (conn.QuestObjects)
                foreach (var (id, obj) in conn.QuestObjects)
                    if (obj.Rule.ObjectType == MapDevice.CampaignDeviceType)
                        return (id, obj.At.X, obj.At.Y, true);
        lock (conn.HideoutDoodads)
            foreach (var (id, device) in conn.HideoutDoodads)
                return (id, device.X, device.Y, false);
        return null;
    }
    // Lists the portals already standing again, so a portal cosmetic takes effect without reopening the map device: 0x008f
    // carries the variation of every entry and the client re-applies the visual. False when nothing stands.
    public static async Task<bool> ResendMapPortals(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
    {
        uint deviceId = MapDeviceHere(conn)?.Id ?? 0;
        lock (conn.MapPortals)
            if (conn.MapPortals.Count == 0) return false;
        await SendMapPortals(remote, stream, ciphers, conn, deviceId,
            MapDevice.OpenOf(CharacterName(conn))?.Area.Hash16 ?? conn.Layout.Hash);
        return true;
    }

    // S→C 0x8f: the device's portals and where they lead (21-hideouts.md §5). Without it the client never draws them; a
    // portal listed with area 0 (one just taken) closes.
    public static async Task SendMapPortals(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, uint deviceId, ushort area, uint closed = 0)
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
}
