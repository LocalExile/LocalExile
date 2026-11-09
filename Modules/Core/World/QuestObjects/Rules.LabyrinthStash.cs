using System.Net.Sockets;
using System.Runtime.CompilerServices;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

static partial class QuestobjectsRules
{
    // The stashes of a Labyrinth room's connection by object id, with their cells.
    static readonly ConditionalWeakTable<ClientState, Dictionary<uint, (int X, int Y)>> LabyrinthStashes = new();

    // The account's stash where a Labyrinth room has one (the room before each of Izaro's arenas, the Ascendancy Chamber:
    // its Metadata/MiscellaneousObjects/Stash room object), as a town's.
    static async Task PlaceLabyrinthStashes(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        List<AreaPopulation.RoomObject> all)
    {
        var placed = new Dictionary<uint, (int X, int Y)>();
        foreach (var at in all.Where(o => o.Type == StashObject.MetadataPath))
        {
            uint id = conn.NextObjectId++;
            await Send(remote, stream, ciphers, [.. InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(StashObject.MetadataPath),
                new StashObject(at.X, at.Y, at.Angle).Blob()), .. InstanceProtocol.EnterWakeZone(id)], $"0x02bb create stash 0x{id:x} at ({at.X}, {at.Y})");
            await RegisterStationaryCollision(remote, stream, ciphers, conn, id, StashObject.MetadataPath, at.X, at.Y, at.Angle);
            placed[id] = (at.X, at.Y);
            if (conn.StashObject == 0) (conn.StashObject, conn.StashAt) = (id, (at.X, at.Y));
        }
        if (placed.Count > 0) LabyrinthStashes.AddOrUpdate(conn, placed);
    }

    // A click on one of them: it becomes the stash the game opens (ClientState keeps one). The click goes on to the game.
    internal static bool PickLabyrinthStash(ClientState conn, uint id)
    {
        if (!LabyrinthStashes.TryGetValue(conn, out var stashes) || !stashes.TryGetValue(id, out var at)) return false;
        (conn.StashObject, conn.StashAt) = (id, at);
        return true;
    }
}
