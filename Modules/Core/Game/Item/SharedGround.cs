using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

// One ground per instance (InstanceGround): an item one player drops, or a monster drops for them, lies there for every
// client in the instance, and goes for all of them when someone picks it up. The client that put it down was sent it
// where that happened; the others get it here, within a sync (100 ms). Quest items and the start weapon stay the
// player's own (InstanceGround.Add shared: false).
public static partial class Game
{
    public static async Task SyncSharedGround(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
    {
        if (!GameEvents.AllowsLoot(conn)) return;
        var ground = conn.Instance.Ground;
        await SendWorldBatch(conn, [], admission =>
        {
            if (!CurrentGameWork || conn.Closed || conn.LeavingArea || !conn.PlayerSent) return null;
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
                if (ReferenceEquals(by, conn)) continue; // shown where it was put down
                packets.Create(id, InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(WorldItemObject.MetadataPath),
                    item.Blob(justDropped: ground.IsNewFor(conn, id))));
                packets.Presentation(id, InstanceProtocol.EnterWakeZone(id));
            }
            return packets.Build();
        }, transport: (remote, stream, ciphers));
    }
}
