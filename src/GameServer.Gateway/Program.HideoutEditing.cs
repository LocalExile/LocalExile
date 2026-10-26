using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

partial class Program
{
    static IReadOnlyDictionary<string, HideoutPlacement>? SavedHideoutPlacements(ClientState conn) =>
        CharacterSaves.Load(CharacterName(conn))?.HideoutPlacements?.GetValueOrDefault(conn.Layout.Id);

    static void PrepareHideoutWaypoint(ClientState conn)
    {
        if (HideoutCatalog.ByArea(conn.Layout.Hash) is null || conn.Layout.Waypoint is not { } waypoint) return;
        if (SavedHideoutPlacements(conn)?.GetValueOrDefault("waypoint") is { } p)
            conn.MoveWaypoint(waypoint = waypoint with { GridX = p.X, GridY = p.Y, Rotation = p.Rotation });
        // A hideout always starts on its saved waypoint, including logins and portal returns.
        conn.Use(conn.Layout with { SpawnX = waypoint.GridX, SpawnY = waypoint.GridY });
        conn.HideoutEditor.Register(WaypointCatalog.ObjectId, "waypoint", waypoint.GridX, waypoint.GridY,
            waypoint.Rotation, 0);
    }

    static async Task MoveHideoutObject(string remote, NetworkStream stream, ConnectionCiphers ciphers,
        ClientState conn, InstanceHideoutEditing.Move move)
    {
        if (!conn.PlayerSent || conn.PendingTransfer is not null || HomeHideout(conn)?.AreaHash16 != conn.Layout.Hash)
            return;
        if (!conn.HideoutEditor.TryMove(move, (x, y) => x < conn.Layout.GridW && y < conn.Layout.GridH
            && conn.Layout.Walkable?.Invoke(x, y) != false,
            (key, p) => CharacterSaves.SetHideoutPlacement(CharacterName(conn), conn.Layout.Id, key, p), out var entry))
        {
            Console.WriteLine($"[gateway] {remote} refused hideout move 0x{move.ObjectId:x} to ({move.X},{move.Y})");
            return;
        }
        var p = entry!.Position;
        if (entry.BlockerSize > 0) conn.SetBlocker(move.ObjectId, p.X, p.Y, entry.BlockerSize);
        if (move.ObjectId == conn.HeistLockerObject) conn.HeistLockerAt = (p.X, p.Y);
        if (move.ObjectId == conn.StashObject) conn.StashAt = (p.X, p.Y);
        if (move.ObjectId == conn.GuildStashObject) conn.GuildStashAt = (p.X, p.Y);
        if (move.ObjectId == conn.CraftingBenchObject) conn.CraftingBenchAt = (p.X, p.Y);
        if (move.ObjectId == conn.HorticraftingObject) conn.HorticraftingAt = (p.X, p.Y);
        lock (conn.Npcs)
            if (conn.Npcs.TryGetValue(move.ObjectId, out var npc))
                conn.Npcs[move.ObjectId] = npc with { X = p.X, Y = p.Y };
        lock (conn.HideoutDoodads)
            if (conn.HideoutDoodads.TryGetValue(move.ObjectId, out var doodad))
                conn.HideoutDoodads[move.ObjectId] = doodad with { X = p.X, Y = p.Y, Rotation = p.Rotation };
        if (move.ObjectId == WaypointCatalog.ObjectId && conn.Layout.Waypoint is { } waypoint)
            conn.MoveWaypoint(waypoint with { GridX = p.X, GridY = p.Y, Rotation = p.Rotation });
        conn.Furniture = null;
        // Preserve the object identity, dialogue, device state and any portals: update Positioned in place.
        await Send(remote, stream, ciphers, InstanceProtocol.Teleport(move.ObjectId, p.X, p.Y, 0x7fffffff),
            $"hideout move {entry.Key} 0x{move.ObjectId:x} to ({p.X},{p.Y}), saved");
        await Send(remote, stream, ciphers, InstanceHideoutEditing.Rotation(move.ObjectId, p.Rotation),
            $"hideout rotation {p.Rotation:0.###}");
    }
}
