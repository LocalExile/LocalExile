using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;
using static Program;

static class HideoutEditingFeature
{
    /// <summary>Creates the Monster Statue decorations saved for this account's hideout.</summary>
    internal static async Task SpawnMonsterStatues(Player player)
    {
        if (HideoutCatalog.ByArea(player.Layout.Hash) is null) return;
        foreach (var s in global::Game.AccountMonsterStatues(player))
        {
            uint id = player.TakeObjectId();
            await global::Game.SendWorldCreate(player, id, InstanceEntities.CreateObject(id,
                InstanceEntities.ObjectTypeHash(InstanceMonsterStatue.TypePath),
                InstanceMonsterStatue.Blob(s.X, s.Y, s.MonsterHash16, s.Scale, s.Variation, s.Rotation)),
                $"0x02bb saved monster statue at ({s.X}, {s.Y})");
            await global::Game.SendWorldRecord(player, id, InstanceProtocol.EnterWakeZone(id),
                $"0x01a9 saved monster statue 0x{id:x} enters wake zone");
            await global::Game.SendWorldRecord(player, id, InstanceProtocol.AnimationUpdate(id, 0, 0, 1),
                $"0x1b2 saved monster statue 0x{id:x} idle");
        }
    }

    /// <summary>C→S 0x0209: the Monster Statue Creator used on a monster. Replaces it with the stone statue and saves it.</summary>
    internal static async Task CreateMonsterStatue(Player player, (uint Action, uint Target) create)
    {
        if (create.Action != InstanceMonsterStatue.CreatorAction)
        {
            Console.WriteLine($"[gateway] {player.Connection.Remote} <= 0x0209 action 0x{create.Action:x8} is not the Monster Statue Creator");
            return;
        }
        if (!OwnHideout(player) || HomeHideout(player)?.AreaHash16 != player.Layout.Hash)
        {
            Console.WriteLine($"[gateway] {player.Connection.Remote} refused 0x0209 in {player.Layout.Id}: not the character's hideout");
            return;
        }
        SpawnedMonster? monster;
        monster = player.Area.Monsters.GetValueOrDefault(create.Target);
        if (monster?.Variety is not { } variety)
        {
            Console.WriteLine($"[gateway] {player.Connection.Remote} refused 0x0209 target 0x{create.Target:x}: not a known monster");
            return;
        }
        var (x, y) = monster.Walk.Position(AreaTime(player));
        uint id = player.TakeObjectId();
        await global::Game.SendWorldCreate(player, id, InstanceEntities.CreateObject(id,
            InstanceEntities.ObjectTypeHash(InstanceMonsterStatue.TypePath),
            InstanceMonsterStatue.Blob(x, y, variety.Hash16)), $"0x02bb monster statue of {variety} at ({x}, {y})");
        await global::Game.SendWorldRecord(player, id, InstanceProtocol.EnterWakeZone(id),
            $"0x01a9 monster statue 0x{id:x} enters wake zone");
        await global::Game.SendWorldRecord(player, id, InstanceProtocol.AnimationUpdate(id, 0, 0, 1),
            $"0x1b2 monster statue 0x{id:x} idle");
        await global::Game.SendWorldRemoval(player, create.Target,
            $"0x02bd remove the monster 0x{create.Target:x} turned into a statue");
        player.Area.Monsters.Remove(create.Target);
        if (CharacterName(player) is { } character)
            global::Game.AddAccountMonsterStatue(player,
                new CharacterSaves.SavedMonsterStatue(x, y, 0, variety.Hash16));
    }

    internal static async Task MoveHideoutObject(Player player, InstanceHideoutEditing.Move move)
    {
        if (!player.Connection.PlayerSent || player.PendingTransfer is not null) return;
        if (!OwnHideout(player) || HomeHideout(player)?.AreaHash16 != player.Layout.Hash)
        {
            Console.WriteLine($"[gateway] {player.Connection.Remote} refused hideout move 0x{move.ObjectId:x}: {player.Layout.Id} is not the character's selected hideout");
            return;
        }
        if (!player.HideoutEditor.TryMove(move, HideoutEditing.Cells(player.Layout),
            (key, p) => global::Game.SetAccountHideoutPlacement(player, player.Layout.Id, key, p), out var entry))
        {
            Console.WriteLine($"[gateway] {player.Connection.Remote} refused hideout move 0x{move.ObjectId:x} to ({move.X},{move.Y})");
            return;
        }
        var p = entry!.Position;
        if (entry.BlockerSize >= 0) player.SetBlocker(move.ObjectId, p.X, p.Y, entry.BlockerSize, entry.CollisionRadius);
        if (move.ObjectId == player.StashObject) player.StashAt = (p.X, p.Y);
        if (move.ObjectId == player.GuildStashObject) player.GuildStashAt = (p.X, p.Y);
        if (move.ObjectId == player.CraftingBenchObject) player.CraftingBenchAt = (p.X, p.Y);
        lock (player.Npcs)
            if (player.Npcs.TryGetValue(move.ObjectId, out var npc))
                player.Npcs[move.ObjectId] = npc with { X = p.X, Y = p.Y };
        lock (player.HideoutDoodads)
            if (player.HideoutDoodads.TryGetValue(move.ObjectId, out var doodad))
                player.HideoutDoodads[move.ObjectId] = doodad with { X = p.X, Y = p.Y, Rotation = p.Rotation };
        if (move.ObjectId == WaypointCatalog.ObjectId && player.Layout.Waypoint is { } waypoint)
            player.MoveWaypoint(waypoint with { GridX = p.X, GridY = p.Y, Rotation = p.Rotation });
        player.Furniture = null;
        await global::Game.SendWorldRecord(player, move.ObjectId, InstanceProtocol.Teleport(move.ObjectId, p.X, p.Y, 0x7fffffff),
            $"hideout move {entry.Key} 0x{move.ObjectId:x} to ({p.X},{p.Y}), saved");
        await Send(player, InstanceHideoutEditing.Rotation(move.ObjectId, p.Rotation),
            $"hideout rotation {p.Rotation:0.###}");
        await Send(player, InstanceHideoutEditing.RebuildCollision(),
            "rebuild hideout model collision from current placements");
        await PushOutOfCollision(player);
    }
}
