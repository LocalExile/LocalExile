using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;
using static Program;

static class HideoutEditingFeature
{
    const string DecorationKeyPrefix = "deco:";

    static bool CanEditHere(ClientState conn) =>
        conn.PlayerSent && conn.PendingTransfer is null && OwnHideout(conn)
        && HomeHideout(conn)?.AreaHash16 == conn.Layout.Hash;

    static async Task<uint?> SpawnDecorationObject(string remote, NetworkStream stream, ConnectionCiphers ciphers,
        ClientState conn, global::Game.SavedHideoutDecoration saved, string label)
    {
        if (HideoutCatalog.DoodadByHash(saved.Hash) is not var (type, models) || string.IsNullOrEmpty(type)) return null;
        if (HideoutCatalog.TypeOf(type) is not var (components, states) || !HideoutObject.CanSerialize(components)) return null;
        string? model = models is { Length: > 0 } ? models[Math.Clamp(saved.Variation, 0, models.Length - 1)] : null;
        uint id = conn.TakeObjectId();
        byte flags = (byte)(saved.Mirrored ? 1 : 0);
        var obj = new HideoutObject(saved.X, saved.Y, saved.Rotation, components,
            new HideoutDoodad(saved.Hash, saved.Variation, flags),
            [.. states.Select(_ => 0)], model);
        await global::Game.SendWorldCreate(remote, stream, ciphers, conn, id, InstanceEntities.CreateObject(id,
            InstanceEntities.ObjectTypeHash(type), obj.Blob()),
            $"0x02bb {label} 0x{id:x} ({type}) at ({saved.X}, {saved.Y})");
        await global::Game.SendWorldRecord(remote, stream, ciphers, conn, id, InstanceProtocol.EnterWakeZone(id),
            $"0x01a9 {label} 0x{id:x} enters wake zone");
        var doodad = new HideoutCatalog.Doodad(saved.Key, saved.Hash, type, model, saved.X, saved.Y, saved.Rotation, saved.Variation)
        {
            CollisionRadius = HideoutCatalog.CollisionRadius(model),
        };
        lock (conn.HideoutDoodads) conn.HideoutDoodads[id] = doodad;
        await RegisterStationaryCollision(remote, stream, ciphers, conn, id, type, saved.X, saved.Y, saved.Rotation, saved.Key, model);
        return id;
    }

    /// <summary>Spawns saved custom hideout decorations (placed via C→S 0x59/0x5a) when entering a hideout.</summary>
    internal static async Task SpawnDecorations(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
    {
        if (HideoutCatalog.ByArea(conn.Layout.Hash) is null) return;
        foreach (var saved in global::Game.AccountHideoutDecorations(conn))
            await SpawnDecorationObject(remote, stream, ciphers, conn, saved, "saved hideout decoration");
    }

    /// <summary>C→S 0x59 / 0x5a: places a hideout decoration at the requested grid cell, saves it and updates collision.</summary>
    internal static async Task PlaceDecoration(string remote, NetworkStream stream, ConnectionCiphers ciphers,
        ClientState conn, InstanceHideoutEditing.Place place)
    {
        if (!CanEditHere(conn) || !float.IsFinite(place.Rotation)) return;
        if (HideoutEditing.Cells(conn.Layout)?.Invoke(place.X, place.Y) == false)
        {
            Console.WriteLine($"[gateway] {remote} refused hideout place 0x{place.Hash:x8} at ({place.X},{place.Y}): unwalkable cell");
            return;
        }
        if (HideoutCatalog.DoodadByHash(place.Hash) is not var (type, _) || string.IsNullOrEmpty(type)
            || HideoutCatalog.TypeOf(type) is not var (components, _) || !HideoutObject.CanSerialize(components))
        {
            Console.WriteLine($"[gateway] {remote} refused hideout place 0x{place.Hash:x8}: unknown or unsupported decoration type");
            return;
        }
        string key = DecorationKeyPrefix + Guid.NewGuid().ToString("N")[..12];
        var saved = new global::Game.SavedHideoutDecoration(key, place.Hash, place.X, place.Y, place.Rotation, place.Variation, place.Mirrored);
        global::Game.AddAccountHideoutDecoration(conn, conn.Layout.Id, saved);
        if (await SpawnDecorationObject(remote, stream, ciphers, conn, saved, "hideout decoration") is not uint id) return;
        await Send(remote, stream, ciphers, InstanceHideoutEditing.RebuildCollision(),
            $"rebuild hideout collision after placing {key} (0x{id:x})");
        await PushOutOfCollision(remote, stream, ciphers, conn);
    }

    /// <summary>C→S 0x5d: reclaims a placed custom decoration (core stations and NPCs cannot be reclaimed).</summary>
    internal static async Task ReclaimDecoration(string remote, NetworkStream stream, ConnectionCiphers ciphers,
        ClientState conn, uint objectId)
    {
        if (!CanEditHere(conn)) return;
        if (!conn.HideoutEditor.TryGet(objectId, out var entry) || !entry.Key.StartsWith(DecorationKeyPrefix, StringComparison.Ordinal))
        {
            Console.WriteLine($"[gateway] {remote} refused hideout reclaim 0x{objectId:x}: not a removable custom decoration");
            return;
        }
        conn.HideoutEditor.Unregister(objectId);
        conn.RemoveBlocker(objectId);
        lock (conn.HideoutDoodads) conn.HideoutDoodads.Remove(objectId);
        global::Game.RemoveAccountHideoutDecoration(conn, conn.Layout.Id, entry.Key);
        await global::Game.SendWorldRemoval(remote, stream, ciphers, conn, objectId,
            $"0x02bd reclaim hideout decoration {entry.Key} (0x{objectId:x})");
        await Send(remote, stream, ciphers, InstanceHideoutEditing.RebuildCollision(),
            "rebuild hideout collision after decoration reclaim");
    }

    /// <summary>C→S 0x5e: mode 0 reclaims all custom decorations; mode 1 resets placements and decorations to defaults.</summary>
    internal static async Task ResetHideout(string remote, NetworkStream stream, ConnectionCiphers ciphers,
        ClientState conn, byte mode)
    {
        if (!CanEditHere(conn)) return;
        bool resetPlacements = mode != 0;
        var custom = conn.HideoutEditor.All()
            .Where(o => o.Entry.Key.StartsWith(DecorationKeyPrefix, StringComparison.Ordinal))
            .ToList();
        foreach (var (id, entry) in custom)
        {
            conn.HideoutEditor.Unregister(id);
            conn.RemoveBlocker(id);
            lock (conn.HideoutDoodads) conn.HideoutDoodads.Remove(id);
            await global::Game.SendWorldRemoval(remote, stream, ciphers, conn, id,
                $"0x02bd reclaim hideout decoration {entry.Key} (0x{id:x})");
        }
        global::Game.ClearAccountHideoutDecorations(conn, conn.Layout.Id, resetPlacements);
        conn.Furniture = null;
        await Send(remote, stream, ciphers, InstanceHideoutEditing.RebuildCollision(),
            $"rebuild hideout collision after 0x5e mode {mode}");
        await conn.Commands!.Reply(resetPlacements
            ? $"Hideout reset to default layout ({custom.Count} decoration(s) reclaimed; re-enter hideout to restore default station positions)."
            : $"Reclaimed {custom.Count} hideout decoration(s).");
    }

    /// <summary>C→S 0x255: validates and persists the hideout music box selection (u16 Music.dat row; 0xffff = default).</summary>
    internal static Task SetMusic(string remote, ClientState conn, ushort musicRow)
    {
        if (!CanEditHere(conn)) return Task.CompletedTask;
        if (HideoutCatalog.HideoutMusicName(musicRow) is not { } track)
        {
            Console.WriteLine($"[gateway] {remote} refused 0x0255 hideout music row {musicRow}: not available in hideouts");
            return Task.CompletedTask;
        }
        global::Game.SetAccountHideoutMusic(conn, conn.Layout.Id, musicRow);
        Console.WriteLine($"[gateway] {remote} <= 0x0255 hideout music set to row {musicRow} ({track}), saved");
        return Task.CompletedTask;
    }

    /// <summary>Object click on a placed Item Display Case: walks up and opens S→C 0x7b kind 0x49.</summary>
    internal static async Task<bool> UseDisplayCase(string remote, NetworkStream stream, ConnectionCiphers ciphers,
        ClientState conn, InstanceTransfer.UseSkillOnObject use, double requestedAt)
    {
        HideoutCatalog.Doodad? doodad;
        lock (conn.HideoutDoodads) doodad = conn.HideoutDoodads.GetValueOrDefault(use.TargetId);
        if (doodad?.Type != HideoutCatalog.ItemDisplayCaseType) return false;
        if (!TryBeginObjectWalk(conn, requestedAt, doodad.X, doodad.Y, conn.StationaryInteractionSize(use.TargetId), out var walk))
            return true;
        var (action, now, _, stop, arrive) = walk;
        if (arrive > now) await Task.Delay(TimeSpan.FromSeconds(arrive - now));
        if (conn.Closed || !PlayerActionCurrent(conn, action)
            || !PlayerWalk.InWalkUpRange(stop, (doodad.X, doodad.Y), conn.StationaryInteractionSize(use.TargetId)))
            return true;
        await Send(remote, stream, ciphers, InstanceWaypoints.OpenUi(InstanceHideoutEditing.DisplayCaseUi, use.TargetId),
            $"0x007b open Item Display Case 0x{use.TargetId:x} (kind 0x{InstanceHideoutEditing.DisplayCaseUi:x2})",
            still: () => PlayerActionCurrent(conn, action));
        return true;
    }

    /// <summary>C→S 0x67: places or swaps the cursor item into an Item Display Case, persisting across hideout visits.</summary>
    internal static async Task PlaceDisplayCaseItem(string remote, NetworkStream stream, ConnectionCiphers ciphers,
        ClientState conn, InstanceHideoutEditing.DisplayCasePlace place)
    {
        if (!CanEditHere(conn)) return;
        HideoutCatalog.Doodad? doodad;
        lock (conn.HideoutDoodads) doodad = conn.HideoutDoodads.GetValueOrDefault(place.ObjectId);
        if (doodad?.Type != HideoutCatalog.ItemDisplayCaseType || !conn.HideoutEditor.TryGet(place.ObjectId, out var entry))
        {
            Console.WriteLine($"[gateway] {remote} <= 0x0067 display case 0x{place.ObjectId:x}: not a placed Item Display Case");
            return;
        }
        var items = ItemsOf(conn);
        if (PlayerInventories.Row(place.InventoryId) is not byte row || row != PlayerInventories.Cursor)
        {
            Console.WriteLine($"[gateway] {remote} <= 0x0067 display case 0x{place.ObjectId:x}: expected cursor inventory, got {place.InventoryId}");
            return;
        }
        InventoryItem? removed = null, restored = null;
        bool ok = Saves.Atomically(() =>
        {
            var existing = global::Game.AccountDisplayCaseItem(conn, conn.Layout.Id, entry.Key);
            removed = items.Remove(PlayerInventories.Cursor, place.ItemId);
            if (removed is null) return false;
            global::Game.SetAccountDisplayCaseItem(conn, conn.Layout.Id, entry.Key,
                CharacterSaves.ToSaved(removed.Item, ItemsFor(conn).Mods));
            if (existing is not null)
            {
                var prior = CharacterSaves.FromSaved(existing, ItemsFor(conn));
                restored = items.Put(PlayerInventories.Cursor, new InventoryItem(items.NewItemId(), 0, 0, prior));
            }
            return true;
        });
        if (!ok || removed is null)
        {
            Console.WriteLine($"[gateway] {remote} <= 0x0067 display case 0x{place.ObjectId:x}: item {place.ItemId} not on cursor");
            return;
        }
        await Send(remote, stream, ciphers, InstanceItems.InventoryChange(PlayerInventories.InventoryId(PlayerInventories.Cursor),
            items.NextRevision(PlayerInventories.Cursor), [removed.Id], restored is null ? [] : [restored]),
            $"0x00bf cursor: placed item {removed.Id} into Item Display Case 0x{place.ObjectId:x}");
    }

    /// <summary>C→S 0x68: toggles an Item Display Case; when turning off with a stored item, returns it to cursor or inventory.</summary>
    internal static async Task ToggleDisplayCase(string remote, NetworkStream stream, ConnectionCiphers ciphers,
        ClientState conn, InstanceHideoutEditing.DisplayCaseToggle toggle)
    {
        if (!CanEditHere(conn)) return;
        HideoutCatalog.Doodad? doodad;
        lock (conn.HideoutDoodads) doodad = conn.HideoutDoodads.GetValueOrDefault(toggle.ObjectId);
        if (doodad?.Type != HideoutCatalog.ItemDisplayCaseType || !conn.HideoutEditor.TryGet(toggle.ObjectId, out var entry))
        {
            Console.WriteLine($"[gateway] {remote} <= 0x0068 display case 0x{toggle.ObjectId:x}: not a placed Item Display Case");
            return;
        }
        if (toggle.Flag)
        {
            Console.WriteLine($"[gateway] {remote} <= 0x0068 display case 0x{toggle.ObjectId:x} toggle on");
            return;
        }
        var items = ItemsOf(conn);
        byte targetRow = PlayerInventories.Cursor;
        InventoryItem? returned = null;
        bool ok = Saves.Atomically(() =>
        {
            var existing = global::Game.AccountDisplayCaseItem(conn, conn.Layout.Id, entry.Key);
            if (existing is null) return false;
            var item = CharacterSaves.FromSaved(existing, ItemsFor(conn));
            if (items.Items(PlayerInventories.Cursor).Count == 0)
            {
                targetRow = PlayerInventories.Cursor;
                returned = items.Put(PlayerInventories.Cursor, new InventoryItem(items.NewItemId(), 0, 0, item));
            }
            else if (ShapeOf(item) is { } shape && items.FreeCell(PlayerInventories.MainInventory, shape.Width, shape.Height, ShapeOf) is { } cell)
            {
                targetRow = PlayerInventories.MainInventory;
                returned = items.Put(PlayerInventories.MainInventory, new InventoryItem(items.NewItemId(), cell.X, cell.Y, item));
            }
            else return false;
            global::Game.SetAccountDisplayCaseItem(conn, conn.Layout.Id, entry.Key, null);
            return true;
        });
        if (!ok || returned is null)
        {
            Console.WriteLine($"[gateway] {remote} <= 0x0068 display case 0x{toggle.ObjectId:x} reclaim: empty or no room");
            return;
        }
        await Send(remote, stream, ciphers, InstanceItems.InventoryChange(PlayerInventories.InventoryId(targetRow),
            items.NextRevision(targetRow), [], [returned]),
            $"0x00bf row {targetRow}: reclaimed item {returned.Id} from Item Display Case 0x{toggle.ObjectId:x}");
    }

    /// <summary>Creates the Monster Statue decorations saved for this account's hideout.</summary>
    internal static async Task SpawnMonsterStatues(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
    {
        if (HideoutCatalog.ByArea(conn.Layout.Hash) is null) return;
        foreach (var s in global::Game.AccountMonsterStatues(conn))
        {
            uint id = conn.TakeObjectId();
            await global::Game.SendWorldCreate(remote, stream, ciphers, conn, id, InstanceEntities.CreateObject(id,
                InstanceEntities.ObjectTypeHash(InstanceMonsterStatue.TypePath),
                InstanceMonsterStatue.Blob(s.X, s.Y, s.MonsterHash16, s.Scale, s.Variation, s.Rotation)),
                $"0x02bb saved monster statue at ({s.X}, {s.Y})");
            await global::Game.SendWorldRecord(remote, stream, ciphers, conn, id, InstanceProtocol.EnterWakeZone(id),
                $"0x01a9 saved monster statue 0x{id:x} enters wake zone");
            await global::Game.SendWorldRecord(remote, stream, ciphers, conn, id, InstanceProtocol.AnimationUpdate(id, 0, 0, 1),
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
        if (!OwnHideout(conn) || HomeHideout(conn)?.AreaHash16 != conn.Layout.Hash)
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
        uint id = conn.TakeObjectId();
        await global::Game.SendWorldCreate(remote, stream, ciphers, conn, id, InstanceEntities.CreateObject(id,
            InstanceEntities.ObjectTypeHash(InstanceMonsterStatue.TypePath),
            InstanceMonsterStatue.Blob(x, y, variety.Hash16)), $"0x02bb monster statue of {variety} at ({x}, {y})");
        await global::Game.SendWorldRecord(remote, stream, ciphers, conn, id, InstanceProtocol.EnterWakeZone(id),
            $"0x01a9 monster statue 0x{id:x} enters wake zone");
        await global::Game.SendWorldRecord(remote, stream, ciphers, conn, id, InstanceProtocol.AnimationUpdate(id, 0, 0, 1),
            $"0x1b2 monster statue 0x{id:x} idle");
        await global::Game.SendWorldRemoval(remote, stream, ciphers, conn, create.Target,
            $"0x02bd remove the monster 0x{create.Target:x} turned into a statue");
        lock (conn.Monsters) conn.Monsters.Remove(create.Target);
        if (CharacterName(conn) is { } character)
            global::Game.AddAccountMonsterStatue(conn,
                new CharacterSaves.SavedMonsterStatue(x, y, 0, variety.Hash16));
    }

    internal static async Task MoveHideoutObject(string remote, NetworkStream stream, ConnectionCiphers ciphers,
        ClientState conn, InstanceHideoutEditing.Move move)
    {
        if (!conn.PlayerSent || conn.PendingTransfer is not null) return;
        if (!OwnHideout(conn) || HomeHideout(conn)?.AreaHash16 != conn.Layout.Hash)
        {
            Console.WriteLine($"[gateway] {remote} refused hideout move 0x{move.ObjectId:x}: {conn.Layout.Id} is not the character's selected hideout");
            return;
        }
        if (!conn.HideoutEditor.TryMove(move, HideoutEditing.Cells(conn.Layout),
            (key, p) =>
            {
                if (key.StartsWith(DecorationKeyPrefix, StringComparison.Ordinal))
                    global::Game.UpdateAccountHideoutDecoration(conn, conn.Layout.Id, key, p, move.Variation, move.Mirrored);
                else
                    global::Game.SetAccountHideoutPlacement(conn, conn.Layout.Id, key, p);
            }, out var entry))
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
        await global::Game.SendWorldRecord(remote, stream, ciphers, conn, move.ObjectId, InstanceProtocol.Teleport(move.ObjectId, p.X, p.Y, 0x7fffffff),
            $"hideout move {entry.Key} 0x{move.ObjectId:x} to ({p.X},{p.Y}), saved");
        await Send(remote, stream, ciphers, InstanceHideoutEditing.Rotation(move.ObjectId, p.Rotation),
            $"hideout rotation {p.Rotation:0.###}");
        await Send(remote, stream, ciphers, InstanceHideoutEditing.RebuildCollision(),
            "rebuild hideout model collision from current placements");
        await PushOutOfCollision(remote, stream, ciphers, conn);
    }
}
