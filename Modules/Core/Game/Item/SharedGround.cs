using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

// One ground per instance (InstanceGround): an item one player drops, or a monster drops for them, lies there for every
// client in the instance, and goes for all of them when someone picks it up. The client that put it down was sent it
// where that happened; the others get it here, within a sync (100 ms). Quest items and the start weapon stay the
// player's own (InstanceGround.Add shared: false).
public static partial class Game
{
    // Evaluate at the cipher gate: a kill's delayed output may outlive the item's auto-pickup.
    // Creation and wake must stay together, before any competing pickup's removal can be sent.
    public static byte[]? GroundItemPresentation(ClientState conn, uint id, bool justDropped,
        WorldItemObject? expected = null, bool includeWake = true)
    {
        lock (conn.Monsters)
        {
            if (!CurrentGameWork || conn.Closed || conn.LeavingArea || !GameEvents.AllowsLoot(conn)) return null;
            if (conn.Instance.Ground.Get(id) is not { } item || expected is not null && !ReferenceEquals(item, expected)) return null;
            var (by, shared) = conn.Instance.Ground.Origin(id);
            if (!shared && !ReferenceEquals(by, conn)) return null;
            conn.SeenGround.Add(id);
            var create = InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(WorldItemObject.MetadataPath), item.Blob(justDropped: justDropped));
            return includeWake ? [.. create, .. InstanceProtocol.EnterWakeZone(id)] : create;
        }
    }

    public static Task SendGroundItem(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        uint id, WorldItemObject item, string? log, bool justDropped = false) =>
        SendWorldCreate(conn, id, () => GroundItemPresentation(conn, id, justDropped, item, includeWake: false),
            log, wake: true, transport: (remote, stream, ciphers));

    public static Task SyncSharedGround(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn) =>
        SyncGround(remote, stream, ciphers, conn, entering: false);

    static async Task SyncGround(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, bool entering)
    {
        if (!GameEvents.AllowsLoot(conn)) return;
        var ground = conn.Instance.Ground;
        await SendWorldBatch(conn, [], admission =>
        {
            if (!CurrentGameWork || conn.Closed || conn.LeavingArea || (!entering && !conn.PlayerSent)) return null;
            if (entering)
            {
                ground.Observe(conn);
                if (conn.Character is not null)
                    ground.Reassign(by => by is ClientState earlier
                        && string.Equals(earlier.Character, conn.Character, StringComparison.OrdinalIgnoreCase), conn);
            }
            var all = ground.All();
            var now = new HashSet<uint>(all.Select(e => e.Id));
            var packets = new WorldRecordBuilder(conn, admission);
            // Picked up by someone else (this client's own pickups have removed theirs already; a second remove of an
            // object it no longer has is ignored).
            foreach (uint gone in conn.SeenGround.Where(id => !now.Contains(id)).ToList())
            {
                conn.SeenGround.Remove(gone);
                packets.Remove(gone);
            }
            foreach (var (id, item) in all)
            {
                if (conn.SeenGround.Contains(id)) continue;
                var (by, shared) = ground.Origin(id);
                if (!shared && !ReferenceEquals(by, conn)
                    && (QuestFlagsOf(conn, item.Item) is null || !QuestDropVisibleTo(conn, id))) continue;
                conn.SeenGround.Add(id);
                if (!entering && ReferenceEquals(by, conn)) continue; // shown where it was put down
                packets.Create(id, InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(WorldItemObject.MetadataPath),
                    item.Blob(justDropped: !entering && ground.IsNewFor(conn, id))));
                packets.Presentation(id, InstanceProtocol.EnterWakeZone(id));
            }
            return packets.Build();
        }, transport: (remote, stream, ciphers));
    }
}
