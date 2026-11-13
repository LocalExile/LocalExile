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
    // Lists the portals already standing again, so a portal cosmetic takes effect without reopening the map device or
    // using another Portal Scroll: 0x008f carries the variation of every entry and the client re-applies the visual.
    // False when nothing stands.
    public static async Task<bool> ResendMapPortals(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
    {
        bool single = await SendSinglePortals(remote, stream, ciphers, conn, "listed again");
        uint deviceId = MapDeviceHere(conn)?.Id ?? 0;
        lock (conn.MapPortals)
            if (conn.MapPortals.Count == 0) return single;
        await SendMapPortals(remote, stream, ciphers, conn, deviceId,
            MapDevice.OpenOf(CharacterName(conn))?.Area.Hash16 ?? conn.Layout.Hash);
        return true;
    }

    // The portals that stand without a map device (a Portal Scroll's, a map's own way back, the portal back to an open
    // town portal): object id → the area it leads to, for the connection that made them.
    static readonly RuleTable<ClientState, System.Collections.Concurrent.ConcurrentDictionary<uint, ushort>> SinglePortals = new();

    /// <summary>
    /// Lists a portal that stands without a map device, <paramref name="afterMs"/> after its create and once more
    /// MapPortalRelistMs later. 0x8f only reaches a portal object the client has built. The client reads its socket once
    /// per update and handles 0x8f at once, while the create waits for its world frame, so a list that arrives in the
    /// same read as the create is lost and the portal is never drawn (seen live 2026-11-12, a background window in the
    /// lockstep mode: 0x8f handled 3 ms before the object was built, sent 100 ms after it). An entry listed again
    /// unchanged leaves the portal as it is.
    /// </summary>
    public static void ListPortal(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, uint id, ushort area,
        string what, int afterMs)
    {
        SinglePortals.GetOrCreateValue(conn)[id] = area;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(afterMs);
                if (!await SendSinglePortals(remote, stream, ciphers, conn, what, id)) return;
                await Task.Delay(MapPortalRelistMs);
                await SendSinglePortals(remote, stream, ciphers, conn, what + ", listed again", id);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
            {
                // The connection closed while sending.
            }
        });
    }

    /// <summary>
    /// The effect one portal is drawn with. The Portal slot has two choices that are no cosmetic
    /// (MicrotransactionSlotAdditionalDefaultOptions; C→S 0x1f9 with record 0 and the option): 1 "Use one random
    /// variation for every portal" and 2 "Use random variations for all portals". The pick is kept, per area visit for
    /// 1 and per portal for 2, so a portal listed again keeps its effect. Otherwise the worn effect.
    /// </summary>
    public static ushort PortalVariation(ClientState conn, uint portal)
    {
        byte mode = CharacterSaves.Cosmetics(CharacterName(conn)).GetValueOrDefault(InstanceMicrotransactions.PortalSlot) is
            { Record: 0, Option: 1 or 2 } choice ? choice.Option : (byte)0;
        if (mode == 0 || Cosmetics.Portals.Count == 0) return conn.CosmeticPortalVariation;
        int seed = mode == 1 ? HashCode.Combine(conn.Seed, conn.Ticket) : HashCode.Combine(conn.Seed, conn.Ticket, portal);
        return (ushort)Cosmetics.Portals[(int)((uint)seed % (uint)Cosmetics.Portals.Count)].Index;
    }

    // One 0x8f without a device for every portal of SinglePortals that still stands, each with the worn portal effect.
    // With an id, only while that portal stands. False when nothing was sent.
    static async Task<bool> SendSinglePortals(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, string why, uint only = 0)
    {
        if (conn.Closed || !SinglePortals.TryGetValue(conn, out var portals)) return false;
        bool Stands(uint id) => conn.ReturnPortal?.Id == id || conn.BackPortal?.Id == id;
        foreach (uint gone in portals.Keys.Where(id => !Stands(id)).ToList()) portals.TryRemove(gone, out _);
        if (portals.IsEmpty || (only != 0 && !portals.ContainsKey(only))) return false;
        string owner = CharacterName(conn);
        var list = portals.OrderBy(p => p.Key)
            .Select(p => new InstanceMapDevice.Portal(p.Key, p.Value, owner, Variation: PortalVariation(conn, p.Key))).ToList();
        await Send(remote, stream, ciphers, InstanceMapDevice.Portals(0, list),
            $"0x008f {string.Join(", ", list.Select(p => $"portal 0x{p.ObjectId:x} leads to 0x{p.Area:x4}"))} (variation {string.Join(", ", list.Select(p => p.Variation))}; {why})");
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
            .Concat(ids.Select(id => new InstanceMapDevice.Portal(id, area, owner, style, PortalVariation(conn, id)))).ToList();
        await Send(remote, stream, ciphers, InstanceMapDevice.Portals(deviceId, list),
            $"0x008f map device 0x{deviceId:x} portals: {ids.Count} open to 0x{area:x4}{(closed == 0 ? "" : $", 0x{closed:x} closed")}");
    }
}
