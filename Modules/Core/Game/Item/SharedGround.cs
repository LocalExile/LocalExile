using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

// One ground per instance (InstanceGround): an item one player drops, or a monster drops for them, lies there for every
// client in the instance (its create is the area's record), and goes for all of them when someone picks it up. A client
// entering the area is given what lies there. Quest items and the start weapon can be picked up only by their player
// (InstanceGround.Add shared: false).
public static partial class Game
{
    // Evaluate at the cipher gate: a kill's delayed output may outlive the item's auto-pickup.
    // Creation and wake must stay together, before any competing pickup's removal can be sent.
    public static byte[]? GroundItemPresentation(Player player, uint id, bool justDropped,
        WorldItemObject? expected = null, bool includeWake = true)
    {
        if (!CurrentGameWork || player.Closed || player.LeavingArea || !GameEvents.AllowsLoot(player)) return null;
        if (player.Instance.Ground.Get(id) is not { } item || expected is not null && !ReferenceEquals(item, expected)) return null;
        var (by, shared) = player.Instance.Ground.Origin(id);
        if (!shared && !ReferenceEquals(by, player)) return null;
        var create = InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(WorldItemObject.MetadataPath), item.Blob(justDropped: justDropped));
        return includeWake ? [.. create, .. InstanceProtocol.EnterWakeZone(id)] : create;
    }

    // An item only its player may pick up (a quest item, the start weapon) is theirs alone to see.
    public static Task SendGroundItem(Player player,
        uint id, WorldItemObject item, string? log, bool justDropped = false)
    {
        if (InAreaFrame(player) && player.Instance.Ground.Origin(id) is (var by, false))
        {
            if (by is Player owner && GroundItemPresentation(owner, id, justDropped, item, includeWake: false) is { } create)
                ShowPrivateItem(owner, id, create, log);
            return Task.CompletedTask;
        }
        return SendWorldCreate(player, id, () => GroundItemPresentation(player, id, justDropped, item, includeWake: false),
            log, wake: true);
    }

    static void ShowPrivateItem(Player owner, uint id, byte[] create, string? log)
    {
        owner.Area.Delta.Add(id, create, log, AreaDelta.Kind.Create, only: owner);
        owner.Area.Delta.Add(id, InstanceProtocol.EnterWakeZone(id), null, only: owner);
    }

    // What lies on the instance's ground when a player enters: all of it for everyone when the player's entry generated the
    // area (a kept instance's items); a later player is given the area's items as the others have them (AreaObjectRecords),
    // and here only their own private ones.
    static void SyncGround(Player player, bool generated)
    {
        if (!GameEvents.AllowsLoot(player) || !CurrentGameWork || player.Closed || player.LeavingArea) return;
        var ground = player.Instance.Ground;
        ground.Observe(player);
        if (player.Character.Name is not null)
            ground.Reassign(by => by is Player earlier
                && string.Equals(earlier.Character.Name, player.Character.Name, StringComparison.OrdinalIgnoreCase), player);
        foreach (var (id, item) in ground.All())
        {
            var (by, shared) = ground.Origin(id);
            var create = InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(WorldItemObject.MetadataPath), item.Blob(justDropped: false));
            if (!shared) { if (ReferenceEquals(by, player)) ShowPrivateItem(player, id, create, null); }
            else if (generated)
            {
                player.Area.Delta.Add(id, create, null, AreaDelta.Kind.Create);
                player.Area.Delta.Add(id, InstanceProtocol.EnterWakeZone(id));
            }
        }
    }
}
