using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Modding;
using PlayerWalk = GameServer.Core.World.PlayerWalk;
using static Program;

/// <summary>The hideout's Heist Locker, as a built-in module (Program.Mods.cs). The hideout's scene places it while this module is loaded.</summary>
sealed class HeistFeature : IModule
{
    public const string Id = "heist";

    public void Load(IModuleContext mod) => mod.OnObjectUsed(async use =>
    {
        var conn = ConnOf(use.Player);
        if (conn.HeistLockerObject == 0 || use.ObjectId != conn.HeistLockerObject) return false;
        var (remote, stream, ciphers) = conn.Wire;
        await OpenHeistLocker(remote, stream, ciphers, conn);
        return true;
    });

    internal static async Task SpawnHeistLocker(string remote, NetworkStream stream, ConnectionCiphers ciphers,
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
        await RegisterStationaryCollision(remote, stream, ciphers, conn, id, HideoutCatalog.HeistLockerType,
            locker.X, locker.Y, locker.Rotation, "heist-locker", locker.Model);
    }

    static async Task OpenHeistLocker(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
    {
        int action = Interlocked.Increment(ref conn.ActionCount);
        double now = conn.Clock.Elapsed.TotalSeconds;
        var (_, stop, arrive) = conn.Walk.WalkUpTo(conn.HeistLockerAt.X, conn.HeistLockerAt.Y, conn.StationaryInteractionSize(conn.HeistLockerObject), now);
        if (arrive > now) await Task.Delay(TimeSpan.FromSeconds(arrive - now));
        if (conn.Closed || conn.ActionCount != action || !PlayerWalk.InWalkUpRange(stop, conn.HeistLockerAt, conn.StationaryInteractionSize(conn.HeistLockerObject))) return;
        conn.BenchOpen = false;
        conn.HarvestOpen = false;
        await RefreshBenchBalance(remote, stream, ciphers, conn, clear: true);
        await Send(remote, stream, ciphers, InstanceWaypoints.OpenUi(InstanceWaypoints.HeistLockerUi),
            "0x007b open native Heist Locker (HeistStashOpen)");
    }
}
