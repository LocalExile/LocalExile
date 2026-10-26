using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using PlayerWalk = GameServer.Core.World.PlayerWalk;

partial class Program
{
    static async Task SpawnHeistLocker(string remote, NetworkStream stream, ConnectionCiphers ciphers,
        ClientState conn, HideoutScene? scene)
    {
        if (scene?.Heist is not { } locker) return;
        if (HideoutCatalog.TypeOf(HideoutCatalog.HeistLockerType) is not { } type) return;
        var (components, _) = type;
        if (components.Count == 0 || !HideoutObject.CanSerialize(components)) return;
        uint id = conn.NextObjectId++;
        var obj = new HideoutObject(locker.X, locker.Y, locker.Rotation, components, new HideoutDoodad(0, 0));
        await Send(remote, stream, ciphers, InstanceEntities.CreateObject(id,
            InstanceEntities.ObjectTypeHash(HideoutCatalog.HeistLockerType), obj.Blob()),
            $"0x02b9 create Heist Locker 0x{id:x} at ({locker.X}, {locker.Y})");
        await Send(remote, stream, ciphers, InstanceProtocol.EnterWakeZone(id), null);
        conn.HeistLockerObject = id;
        conn.HeistLockerAt = (locker.X, locker.Y);
        conn.SetBlocker(id, locker.X, locker.Y, 4);
        conn.HideoutEditor.Register(id, "heist-locker", locker.X, locker.Y, locker.Rotation, 4);
    }

    static async Task OpenHeistLocker(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, double requestedAt)
    {
        if (!TryBeginObjectWalk(conn, requestedAt, conn.HeistLockerAt.X, conn.HeistLockerAt.Y, 4, out var walk)) return;
        var (action, now, _, stop, arrive) = walk;
        if (arrive > now) await Task.Delay(TimeSpan.FromSeconds(arrive - now));
        if (!PlayerActionCurrent(conn, action) || !PlayerWalk.InWalkUpRange(stop, conn.HeistLockerAt, 4)) return;
        conn.BenchOpen = false;
        await RefreshBenchBalance(remote, stream, ciphers, conn, clear: true);
        await Send(remote, stream, ciphers, InstanceWaypoints.OpenUi(InstanceWaypoints.HeistLockerUi),
            "0x007b open native Heist Locker (HeistStashOpen)", still: () => PlayerActionCurrent(conn, action));
    }
}
