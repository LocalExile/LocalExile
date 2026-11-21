using System.Net.Sockets;
using System.Runtime.CompilerServices;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

static partial class QuestobjectsRules
{
    // The stashes of a Labyrinth room's connection by object id, with their cells.
    static readonly ConditionalWeakTable<Player, Dictionary<uint, (int X, int Y)>> LabyrinthStashes = new();

    // The account's stash where a Labyrinth room has one (the room before each of Izaro's arenas, the Ascendancy Chamber:
    // its Metadata/MiscellaneousObjects/Stash room object), as a town's.
    static async Task PlaceLabyrinthStashes(Player player,
        List<AreaPopulation.RoomObject> all)
    {
        var placed = new Dictionary<uint, (int X, int Y)>();
        foreach (var at in all.Where(o => o.Type == StashObject.MetadataPath))
        {
            uint id = player.TakeObjectId();
            byte[] create = InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(StashObject.MetadataPath),
                new StashObject(at.X, at.Y, at.Angle).Blob());
            await global::Game.SendWorldCreate(player, id, () => create, $"0x02bb create stash 0x{id:x} at ({at.X}, {at.Y})", wake: true);
            await RegisterStationaryCollision(player, id, StashObject.MetadataPath, at.X, at.Y, at.Angle);
            placed[id] = (at.X, at.Y);
            if (player.StashObject == 0) { (player.StashObject, player.StashAt) = (id, (at.X, at.Y)); global::Game.SetAreaStash(player, id, (at.X, at.Y)); }
        }
        if (placed.Count > 0) LabyrinthStashes.AddOrUpdate(player, placed);
    }

    // A click on one of them: it becomes the stash the game opens (Player keeps one). The click goes on to the game.
    internal static bool PickLabyrinthStash(Player player, uint id)
    {
        if (!LabyrinthStashes.TryGetValue(player, out var stashes) || !stashes.TryGetValue(id, out var at)) return false;
        (player.StashObject, player.StashAt) = (id, at);
        global::Game.SetAreaStash(player, id, at);
        return true;
    }
}
