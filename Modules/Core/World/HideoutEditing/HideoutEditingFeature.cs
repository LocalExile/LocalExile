using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;
using static Program;

static class HideoutEditingFeature
{
    /// <summary>Creates the Monster Statue decorations saved for this character's hideout.</summary>
    internal static async Task SpawnMonsterStatues(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
    {
        if (HomeHideout(conn)?.AreaHash16 != conn.Layout.Hash || CharacterName(conn) is not { } character) return;
        foreach (var s in CharacterSaves.MonsterStatues(character, conn.Layout.Id))
        {
            uint id = conn.NextObjectId++;
            await Send(remote, stream, ciphers, InstanceEntities.CreateObject(id,
                InstanceEntities.ObjectTypeHash(InstanceMonsterStatue.TypePath),
                InstanceMonsterStatue.Blob(s.X, s.Y, s.MonsterHash16, s.Scale, s.Variation, s.Rotation)),
                $"0x02b9 saved monster statue at ({s.X}, {s.Y})");
            await Send(remote, stream, ciphers, InstanceProtocol.EnterWakeZone(id),
                $"0x01a9 saved monster statue 0x{id:x} enters wake zone");
            await Send(remote, stream, ciphers, InstanceProtocol.AnimationUpdate(id, 0, 0, 1),
                $"0x1b2 saved monster statue 0x{id:x} idle");
        }
    }

    /// <summary>C→S 0x0209: the Monster Statue Creator used on a monster. Replaces it with the stone statue and saves it.</summary>
    internal static async Task CreateMonsterStatue(string remote, NetworkStream stream, ConnectionCiphers ciphers,
        ClientState conn, (uint Action, uint Target) create)
    {
        if (create.Action != InstanceMonsterStatue.CreatorAction)
        {
            Console.WriteLine($"[gateway] {remote} <= 0x0209 action 0x{create.Action:x8} is not the Monster Statue Creator");
            return;
        }
        if (HomeHideout(conn)?.AreaHash16 != conn.Layout.Hash)
        {
            Console.WriteLine($"[gateway] {remote} refused 0x0209 in {conn.Layout.Id}: not the character's hideout");
            return;
        }
        SpawnedMonster? monster;
        lock (conn.Monsters) monster = conn.Monsters.GetValueOrDefault(create.Target);
        if (monster?.Variety is not { } variety)
        {
            Console.WriteLine($"[gateway] {remote} refused 0x0209 target 0x{create.Target:x}: not a known monster");
            return;
        }
        var (x, y) = monster.Walk.Position(conn.Clock.Elapsed.TotalSeconds);
        uint id = conn.NextObjectId++;
        await Send(remote, stream, ciphers, InstanceEntities.CreateObject(id,
            InstanceEntities.ObjectTypeHash(InstanceMonsterStatue.TypePath),
            InstanceMonsterStatue.Blob(x, y, variety.Hash16)), $"0x02b9 monster statue of {variety} at ({x}, {y})");
        await Send(remote, stream, ciphers, InstanceProtocol.EnterWakeZone(id),
            $"0x01a9 monster statue 0x{id:x} enters wake zone");
        await Send(remote, stream, ciphers, InstanceProtocol.AnimationUpdate(id, 0, 0, 1),
            $"0x1b2 monster statue 0x{id:x} idle");
        await Send(remote, stream, ciphers, InstanceProtocol.RemoveObject(create.Target),
            $"0x02bb remove the monster 0x{create.Target:x} turned into a statue");
        lock (conn.Monsters) conn.Monsters.Remove(create.Target);
        if (CharacterName(conn) is { } character)
            CharacterSaves.AddMonsterStatue(character, conn.Layout.Id,
                new CharacterSaves.SavedMonsterStatue(x, y, 0, variety.Hash16));
    }

    internal static async Task MoveHideoutObject(string remote, NetworkStream stream, ConnectionCiphers ciphers,
        ClientState conn, InstanceHideoutEditing.Move move)
    {
        if (!conn.PlayerSent || conn.PendingTransfer is not null) return;
        if (HomeHideout(conn)?.AreaHash16 != conn.Layout.Hash)
        {
            Console.WriteLine($"[gateway] {remote} refused hideout move 0x{move.ObjectId:x}: {conn.Layout.Id} is not the character's selected hideout");
            return;
        }
        if (!conn.HideoutEditor.TryMove(move, HideoutEditing.Cells(conn.Layout),
            (key, p) => CharacterSaves.SetHideoutPlacement(CharacterName(conn), conn.Layout.Id, key, p), out var entry))
        {
            Console.WriteLine($"[gateway] {remote} refused hideout move 0x{move.ObjectId:x} to ({move.X},{move.Y})");
            return;
        }
        var p = entry!.Position;
        if (entry.BlockerSize >= 0) conn.SetBlocker(move.ObjectId, p.X, p.Y, entry.BlockerSize, entry.CollisionRadius);
        if (move.ObjectId == conn.StashObject) conn.StashAt = (p.X, p.Y);
        if (move.ObjectId == conn.GuildStashObject) conn.GuildStashAt = (p.X, p.Y);
        if (move.ObjectId == conn.CraftingBenchObject) conn.CraftingBenchAt = (p.X, p.Y);
        lock (conn.Npcs)
            if (conn.Npcs.TryGetValue(move.ObjectId, out var npc))
                conn.Npcs[move.ObjectId] = npc with { X = p.X, Y = p.Y };
        lock (conn.HideoutDoodads)
            if (conn.HideoutDoodads.TryGetValue(move.ObjectId, out var doodad))
                conn.HideoutDoodads[move.ObjectId] = doodad with { X = p.X, Y = p.Y, Rotation = p.Rotation };
        if (move.ObjectId == WaypointCatalog.ObjectId && conn.Layout.Waypoint is { } waypoint)
            conn.MoveWaypoint(waypoint with { GridX = p.X, GridY = p.Y, Rotation = p.Rotation });
        conn.Furniture = null;
        await Send(remote, stream, ciphers, InstanceProtocol.Teleport(move.ObjectId, p.X, p.Y, 0x7fffffff),
            $"hideout move {entry.Key} 0x{move.ObjectId:x} to ({p.X},{p.Y}), saved");
        await Send(remote, stream, ciphers, InstanceHideoutEditing.Rotation(move.ObjectId, p.Rotation),
            $"hideout rotation {p.Rotation:0.###}");
        await Send(remote, stream, ciphers, InstanceHideoutEditing.RebuildCollision(),
            "rebuild hideout model collision from current placements");
        await PushOutOfCollision(remote, stream, ciphers, conn);
    }
}
