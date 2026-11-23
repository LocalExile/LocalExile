using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

public static partial class Game
{
    public static bool OwnHideout(Player player) => HideoutCatalog.ByArea(player.Layout.Hash) is not null
        && (player.InstanceOwner is null || string.Equals(player.InstanceOwner, CharacterName(player), StringComparison.OrdinalIgnoreCase)
            || player.Character.Account is not null && string.Equals(HideoutAccount(player, true), player.Character.Account, StringComparison.OrdinalIgnoreCase));

    public static IReadOnlyDictionary<string, HideoutPlacement>? SavedHideoutPlacements(Player player) =>
        SharedHideout(player, instanceOwner: true) is { } shared
            ? shared.Placements.GetValueOrDefault(player.Layout.Id)
            : CharacterSaves.Load(player.InstanceOwner ?? CharacterName(player))?.HideoutPlacements?.GetValueOrDefault(player.Layout.Id);

    public static void PrepareHideoutWaypoint(Player player)
    {
        if (HideoutCatalog.ByArea(player.Layout.Hash) is null || player.Layout.Waypoint is not { } waypoint) return;
        if (SavedHideoutPlacements(player)?.GetValueOrDefault("waypoint") is { } p)
            player.MoveWaypoint(waypoint = waypoint with { GridX = p.X, GridY = p.Y, Rotation = p.Rotation });
        // A hideout always starts on its saved waypoint, including logins and portal returns.
        player.Use(player.Layout with { SpawnX = waypoint.GridX, SpawnY = waypoint.GridY });
        player.HideoutEditor.Register(WaypointCatalog.ObjectId, "waypoint", waypoint.GridX, waypoint.GridY,
            waypoint.Rotation, -1);
    }
}
