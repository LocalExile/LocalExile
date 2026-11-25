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
        // Portal returns arrive at their recorded cell beside the map device (ArriveAt); logins and waypoint/command warps start at the waypoint.
        if (player.ArriveAt is null)
            player.Use(player.Layout with { SpawnX = waypoint.GridX, SpawnY = waypoint.GridY });
        player.HideoutEditor.Register(WaypointCatalog.ObjectId, "waypoint", waypoint.GridX, waypoint.GridY,
            waypoint.Rotation, -1);
    }

    /// <summary>Persists and exports the current hideout layout (S→C 0x0213) for /save_hideout (C→S 0x16 command 50 or chat).</summary>
    public static async Task SaveHideoutLayout(Player player)
    {
        if (!OwnHideout(player) || HideoutCatalog.ByArea(player.Layout.Hash) is not { } hideout)
        {
            await player.Commands!.Reply("You must be in your own hideout to save its layout.");
            return;
        }
        var (name, placements, decorations, statues, music) = HideoutLayoutSummary(player);
        var layout = BuildExportHideoutLayout(player, hideout, music);
        await Send(player, InstanceHideoutEditing.EncodeLayout(layout),
            $"0x0213 hideout layout export \"{name}\" ({layout.Doodads.Count} doodad(s), music 0x{layout.MusicHash:x4})");
        string musicNote = music is ushort row && HideoutCatalog.HideoutMusicName(row) is { } track ? $", music: {track}" : "";
        Console.WriteLine($"[gateway] {player.Connection.Remote} /save_hideout {name}: {placements} placement(s), {decorations} decoration(s), {statues} statue(s){musicNote}");
        await player.Commands!.Reply($"Hideout \"{name}\" layout saved ({placements} placement(s), {decorations} decoration(s), {statues} statue(s){musicNote}).");
    }

    static InstanceHideoutEditing.Layout BuildExportHideoutLayout(Player player, HideoutCatalog.Hideout hideout, ushort? musicRow)
    {
        var doodads = new List<InstanceHideoutEditing.LayoutDoodad>();
        var savedPositions = SavedHideoutPlacements(player);
        void AddFunctional(string key, HideoutCatalog.Doodad? template, int x, int y, float rotation)
        {
            if (template is null || template.Hash == 0) return;
            if (savedPositions?.TryGetValue(key, out var p) == true) (x, y, rotation) = (p.X, p.Y, p.Rotation);
            doodads.Add(new(template.Hash, (ushort)Math.Clamp(x, 0, ushort.MaxValue),
                (ushort)Math.Clamp(y, 0, ushort.MaxValue),
                InstanceHideoutEditing.EncodeRotation(rotation),
                InstanceHideoutEditing.PackFv((byte)Math.Clamp(template.Variation, 0, 0x7f), false)));
        }
        if (hideout.First(WaypointObject.MetadataPath) is { } wp)
            AddFunctional("waypoint", wp, player.Layout.Waypoint?.GridX ?? wp.X,
                player.Layout.Waypoint?.GridY ?? wp.Y, player.Layout.Waypoint?.Rotation ?? wp.Rotation);
        if (player.CurrentHideoutScene is { } scene)
        {
            var guildTemplate = hideout.First(StashObject.GuildMetadataPath);
            AddFunctional("stash", scene.Stash, scene.Stash.X, scene.Stash.Y, scene.Stash.Rotation);
            AddFunctional("guild-stash", guildTemplate, scene.Guild.X, scene.Guild.Y, guildTemplate?.Rotation ?? 0f);
            AddFunctional("bench", scene.Bench, scene.Bench.X, scene.Bench.Y, scene.Bench.Rotation);
            AddFunctional("map-device", scene.Device, scene.Device.X, scene.Device.Y, scene.Device.Rotation);
        }
        foreach (var deco in AccountHideoutDecorations(player))
        {
            doodads.Add(new(deco.Hash, (ushort)Math.Clamp(deco.X, 0, ushort.MaxValue),
                (ushort)Math.Clamp(deco.Y, 0, ushort.MaxValue),
                InstanceHideoutEditing.EncodeRotation(deco.Rotation),
                InstanceHideoutEditing.PackFv(deco.Variation, deco.Mirrored)));
        }
        ushort musicHash = musicRow is ushort row ? HideoutCatalog.HideoutMusicHash(row) : (ushort)0;
        return new(hideout.Hash16, musicHash, 0, doodads);
    }
}
