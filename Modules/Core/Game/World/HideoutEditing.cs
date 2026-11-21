using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

public static partial class Game
{
    public static bool OwnHideout(ClientState conn) => HideoutCatalog.ByArea(conn.Layout.Hash) is not null
        && (conn.InstanceOwner is null || string.Equals(conn.InstanceOwner, CharacterName(conn), StringComparison.OrdinalIgnoreCase)
            || conn.Account is not null && string.Equals(HideoutAccount(conn, true), conn.Account, StringComparison.OrdinalIgnoreCase));

    public static IReadOnlyDictionary<string, HideoutPlacement>? SavedHideoutPlacements(ClientState conn) =>
        SharedHideout(conn, instanceOwner: true) is { } shared
            ? shared.Placements.GetValueOrDefault(conn.Layout.Id)
            : CharacterSaves.Load(conn.InstanceOwner ?? CharacterName(conn))?.HideoutPlacements?.GetValueOrDefault(conn.Layout.Id);

    public static void PrepareHideoutWaypoint(ClientState conn)
    {
        if (HideoutCatalog.ByArea(conn.Layout.Hash) is null || conn.Layout.Waypoint is not { } waypoint) return;
        if (SavedHideoutPlacements(conn)?.GetValueOrDefault("waypoint") is { } p)
            conn.MoveWaypoint(waypoint = waypoint with { GridX = p.X, GridY = p.Y, Rotation = p.Rotation });
        // A hideout always starts on its saved waypoint, including logins and portal returns.
        conn.Use(conn.Layout with { SpawnX = waypoint.GridX, SpawnY = waypoint.GridY });
        conn.HideoutEditor.Register(WaypointCatalog.ObjectId, "waypoint", waypoint.GridX, waypoint.GridY,
            waypoint.Rotation, -1);
    }

    /// <summary>Persists and confirms the current hideout layout for /save_hideout (C→S 0x16 command 50 or chat).</summary>
    public static async Task SaveHideoutLayout(ClientState conn)
    {
        if (!OwnHideout(conn))
        {
            await conn.Commands!.Reply("You must be in your own hideout to save its layout.");
            return;
        }
        var (name, placements, decorations, statues, music) = HideoutLayoutSummary(conn);
        string musicNote = music is ushort row && HideoutCatalog.HideoutMusicName(row) is { } track ? $", music: {track}" : "";
        Console.WriteLine($"[gateway] {conn.Wire.Remote} /save_hideout {name}: {placements} placement(s), {decorations} decoration(s), {statues} statue(s){musicNote}");
        await conn.Commands!.Reply($"Hideout \"{name}\" layout saved ({placements} placement(s), {decorations} decoration(s), {statues} statue(s){musicNote}).");
    }
}
