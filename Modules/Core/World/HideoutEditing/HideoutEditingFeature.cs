using System.Runtime.CompilerServices;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;
using static Program;

static class HideoutEditingFeature
{
    const string DecorationKeyPrefix = "deco:";
    const int MaxUncapturedCoordinate = 2048;
    static readonly ConditionalWeakTable<GameArea, Dictionary<uint, HideoutCatalog.Doodad>> AreaDecorations = new();
    static readonly ConditionalWeakTable<GameArea, SemaphoreSlim> ImportGates = new();

    static Dictionary<uint, HideoutCatalog.Doodad> DecorationsOf(Player player) =>
        AreaDecorations.GetValue(player.Area, _ => []);

    static IEnumerable<Player> AreaPlayers(Player player) =>
        player.Frames.Players().Where(p => !p.Closed && !p.LeavingArea).Append(player).Distinct();

    static async Task BroadcastRebuildCollision(Player player, string reason)
    {
        byte[] packet = InstanceHideoutEditing.RebuildCollision();
        foreach (var member in AreaPlayers(player))
            await Send(member, packet, reason);
    }

    static bool PlacementInBounds(AreaLayout layout, int x, int y)
    {
        if (HideoutEditing.Cells(layout) is { } cells) return cells(x, y);
        int maxW = layout.GridW > 0 ? layout.GridW : MaxUncapturedCoordinate;
        int maxH = layout.GridH > 0 ? layout.GridH : MaxUncapturedCoordinate;
        return x >= 0 && y >= 0 && x < maxW && y < maxH;
    }

    static bool CanEditHere(Player player) =>
        player.Connection.PlayerSent && player.PendingTransfer is null && OwnHideout(player)
        && HomeHideout(player)?.AreaHash16 == player.Layout.Hash;

    static async Task<uint?> SpawnDecorationObject(Player player, global::Game.SavedHideoutDecoration saved, string label)
    {
        if (HideoutCatalog.DoodadByHash(saved.Hash) is not var (type, models) || string.IsNullOrEmpty(type)) return null;
        if (HideoutCatalog.TypeOf(type) is not var (components, states) || !HideoutObject.CanSerialize(components)) return null;
        string? model = models is { Length: > 0 } ? models[Math.Clamp(saved.Variation, 0, models.Length - 1)] : null;
        uint id = player.TakeObjectId();
        byte flags = (byte)(saved.Mirrored ? 1 : 0);
        var obj = new HideoutObject(saved.X, saved.Y, saved.Rotation, components,
            new HideoutDoodad(saved.Hash, saved.Variation, flags),
            [.. states.Select(_ => 0)], model);
        await global::Game.SendWorldCreate(player, id, InstanceEntities.CreateObject(id,
            InstanceEntities.ObjectTypeHash(type), obj.Blob()),
            $"0x02bb {label} 0x{id:x} ({type}) at ({saved.X}, {saved.Y})");
        await global::Game.SendWorldRecord(player, id, InstanceProtocol.EnterWakeZone(id),
            $"0x01a9 {label} 0x{id:x} enters wake zone");
        var doodad = new HideoutCatalog.Doodad(saved.Key, saved.Hash, type, model, saved.X, saved.Y, saved.Rotation, saved.Variation)
        {
            CollisionRadius = HideoutCatalog.CollisionRadius(model),
        };
        var areaDecorations = DecorationsOf(player);
        lock (areaDecorations) areaDecorations[id] = doodad;
        foreach (var member in AreaPlayers(player))
            lock (member.HideoutDoodads) member.HideoutDoodads[id] = doodad;
        await RegisterStationaryCollision(player, id, type, saved.X, saved.Y, saved.Rotation, saved.Key, model);
        return id;
    }

    /// <summary>Copies the area's spawned custom hideout decorations into a late-joining player's doodad lookup.</summary>
    internal static void SyncAreaDecorations(Player player)
    {
        if (!AreaDecorations.TryGetValue(player.Area, out var areaDecorations)) return;
        KeyValuePair<uint, HideoutCatalog.Doodad>[] snapshot;
        lock (areaDecorations) snapshot = [.. areaDecorations];
        lock (player.HideoutDoodads)
            foreach (var (id, doodad) in snapshot)
                if (player.Area.Objects.Has(id)) player.HideoutDoodads[id] = doodad;
    }

    /// <summary>Spawns saved custom hideout decorations (placed via C→S 0x59/0x5a) when entering a hideout.</summary>
    internal static async Task SpawnDecorations(Player player)
    {
        if (HideoutCatalog.ByArea(player.Layout.Hash) is null) return;
        foreach (var saved in global::Game.AccountHideoutDecorations(player))
            await SpawnDecorationObject(player, saved, "saved hideout decoration");
    }

    /// <summary>C→S 0x59: places a hideout decoration at the requested grid cell, saves it and updates collision.</summary>
    internal static async Task PlaceDecoration(Player player, InstanceHideoutEditing.Place place)
    {
        if (!CanEditHere(player) || player.Character.Account is null || !float.IsFinite(place.Rotation)) return;
        if (!PlacementInBounds(player.Layout, place.X, place.Y))
        {
            Console.WriteLine($"[gateway] {player.Connection.Remote} refused hideout place 0x{place.Hash:x8} at ({place.X},{place.Y}): out of bounds or unwalkable cell");
            return;
        }
        if (HideoutCatalog.DoodadByHash(place.Hash) is not var (type, _) || string.IsNullOrEmpty(type)
            || HideoutCatalog.TypeOf(type) is not var (components, _) || !HideoutObject.CanSerialize(components))
        {
            Console.WriteLine($"[gateway] {player.Connection.Remote} refused hideout place 0x{place.Hash:x8}: unknown or unsupported decoration type");
            return;
        }
        string key = DecorationKeyPrefix + Guid.NewGuid().ToString("N")[..12];
        var saved = new global::Game.SavedHideoutDecoration(key, place.Hash, place.X, place.Y, place.Rotation, place.Variation, place.Mirrored);
        if (!global::Game.AddAccountHideoutDecoration(player, player.Layout.Id, saved))
        {
            int limit = global::Game.MaxHideoutDecorations;
            Console.WriteLine($"[gateway] {player.Connection.Remote} refused hideout place 0x{place.Hash:x8}: hideout decoration limit ({limit}) reached");
            if (player.Commands is { } cmd)
                await cmd.Reply($"Hideout decoration limit ({limit}) reached.");
            return;
        }
        if (await SpawnDecorationObject(player, saved, "hideout decoration") is not uint id) return;
        await BroadcastRebuildCollision(player, $"rebuild hideout collision after placing {key} (0x{id:x})");
        await PushOutOfCollision(player);
    }

    /// <summary>C→S 0x5d: reclaims a placed custom decoration (core stations and NPCs cannot be reclaimed).</summary>
    internal static async Task ReclaimDecoration(Player player, uint objectId)
    {
        if (!CanEditHere(player)) return;
        if (!player.HideoutEditor.TryGet(objectId, out var entry) || !entry.Key.StartsWith(DecorationKeyPrefix, StringComparison.Ordinal))
        {
            Console.WriteLine($"[gateway] {player.Connection.Remote} refused hideout reclaim 0x{objectId:x}: not a removable custom decoration");
            return;
        }
        if (global::Game.DisplayCaseHasStoredItem(player, player.Layout.Id, entry.Key))
        {
            Console.WriteLine($"[gateway] {player.Connection.Remote} refused hideout reclaim 0x{objectId:x}: display case holds a stored item");
            if (player.Commands is { } cmd)
                await cmd.Reply("Cannot reclaim this Item Display Case while it holds a stored item. Remove the displayed item first.");
            return;
        }
        if (!global::Game.RemoveAccountHideoutDecoration(player, player.Layout.Id, entry.Key)) return;
        var areaDecorations = DecorationsOf(player);
        lock (areaDecorations) areaDecorations.Remove(objectId);
        foreach (var member in AreaPlayers(player))
        {
            member.HideoutEditor.Unregister(objectId);
            member.RemoveBlocker(objectId);
            lock (member.HideoutDoodads) member.HideoutDoodads.Remove(objectId);
        }
        await global::Game.SendWorldRemoval(player, objectId,
            $"0x02bd reclaim hideout decoration {entry.Key} (0x{objectId:x})");
        await BroadcastRebuildCollision(player, "rebuild hideout collision after decoration reclaim");
    }

    /// <summary>C→S 0x5e: mode 0 reclaims all custom decorations; mode 1 resets placements and decorations to defaults.</summary>
    internal static async Task ResetHideout(Player player, byte mode)
    {
        if (!CanEditHere(player)) return;
        if (global::Game.HideoutHasStoredDisplayCaseItems(player, player.Layout.Id))
        {
            Console.WriteLine($"[gateway] {player.Connection.Remote} refused hideout reset mode {mode}: an Item Display Case holds a stored item");
            if (player.Commands is { } cmd)
                await cmd.Reply("Cannot reset hideout while an Item Display Case holds a stored item. Remove all displayed items first.");
            return;
        }
        bool resetPlacements = mode != 0;
        if (!global::Game.ClearAccountHideoutDecorations(player, player.Layout.Id, resetPlacements)) return;
        var custom = player.HideoutEditor.All()
            .Where(o => o.Entry.Key.StartsWith(DecorationKeyPrefix, StringComparison.Ordinal))
            .ToList();
        var areaDecorations = DecorationsOf(player);
        foreach (var (id, entry) in custom)
        {
            lock (areaDecorations) areaDecorations.Remove(id);
            foreach (var member in AreaPlayers(player))
            {
                member.HideoutEditor.Unregister(id);
                member.RemoveBlocker(id);
                lock (member.HideoutDoodads) member.HideoutDoodads.Remove(id);
            }
            await global::Game.SendWorldRemoval(player, id,
                $"0x02bd reclaim hideout decoration {entry.Key} (0x{id:x})");
        }
        player.Furniture = null;
        await BroadcastRebuildCollision(player, $"rebuild hideout collision after 0x5e mode {mode}");
        if (player.Commands is { } commands)
            await commands.Reply(resetPlacements
                ? $"Hideout reset to default layout ({custom.Count} decoration(s) reclaimed; re-enter hideout to restore default station positions)."
                : $"Reclaimed {custom.Count} hideout decoration(s).");
    }

    /// <summary>C→S 0x255: validates and persists the hideout music box selection (u16 Music.dat row; 0xffff = default).</summary>
    internal static Task SetMusic(Player player, ushort musicRow)
    {
        if (!CanEditHere(player)) return Task.CompletedTask;
        if (HideoutCatalog.HideoutMusicName(musicRow) is not { } track)
        {
            Console.WriteLine($"[gateway] {player.Connection.Remote} refused 0x0255 hideout music row {musicRow}: not available in hideouts");
            return Task.CompletedTask;
        }
        global::Game.SetAccountHideoutMusic(player, player.Layout.Id, musicRow);
        Console.WriteLine($"[gateway] {player.Connection.Remote} <= 0x0255 hideout music set to row {musicRow} ({track}), saved");
        return Task.CompletedTask;
    }

    /// <summary>Object click on a placed Item Display Case: walks up and opens S→C 0x7b kind 0x49.</summary>
    internal static async Task<bool> UseDisplayCase(Player player, InstanceTransfer.UseSkillOnObject use, double requestedAt)
    {
        HideoutCatalog.Doodad? doodad;
        lock (player.HideoutDoodads) doodad = player.HideoutDoodads.GetValueOrDefault(use.TargetId);
        if (doodad?.Type != HideoutCatalog.ItemDisplayCaseType) return false;
        if (!TryBeginObjectWalk(player, requestedAt, doodad.X, doodad.Y, player.StationaryInteractionSize(use.TargetId), out var walk))
            return true;
        var (action, now, _, stop, arrive) = walk;
        if (arrive > now) await AreaDelay(player, arrive - now);
        if (player.Closed || !PlayerActionCurrent(player, action)
            || !PlayerWalk.InWalkUpRange(stop, (doodad.X, doodad.Y), player.StationaryInteractionSize(use.TargetId)))
            return true;
        await Send(player, InstanceWaypoints.OpenUi(InstanceHideoutEditing.DisplayCaseUi, use.TargetId),
            $"0x007b open Item Display Case 0x{use.TargetId:x} (kind 0x{InstanceHideoutEditing.DisplayCaseUi:x2})",
            still: () => PlayerActionCurrent(player, action));
        return true;
    }

    /// <summary>C→S 0x67: places or swaps the cursor item into an Item Display Case, persisting per league across hideout visits.</summary>
    internal static async Task PlaceDisplayCaseItem(Player player, InstanceHideoutEditing.DisplayCasePlace place)
    {
        if (!CanEditHere(player) || player.Character.Account is null) return;
        HideoutCatalog.Doodad? doodad;
        lock (player.HideoutDoodads) doodad = player.HideoutDoodads.GetValueOrDefault(place.ObjectId);
        if (doodad?.Type != HideoutCatalog.ItemDisplayCaseType || !player.HideoutEditor.TryGet(place.ObjectId, out var entry))
        {
            Console.WriteLine($"[gateway] {player.Connection.Remote} <= 0x0067 display case 0x{place.ObjectId:x}: not a placed Item Display Case");
            return;
        }
        var items = ItemsOf(player);
        if (PlayerInventories.Row(place.InventoryId) is not byte row || row != PlayerInventories.Cursor)
        {
            Console.WriteLine($"[gateway] {player.Connection.Remote} <= 0x0067 display case 0x{place.ObjectId:x}: expected cursor inventory, got {place.InventoryId}");
            return;
        }
        var catalog = ItemsFor(player);
        InventoryItem? removed = null, restored = null;
        bool ok = Saves.Atomically(() =>
        {
            var held = items.Items(PlayerInventories.Cursor).FirstOrDefault(i => i.Id == place.ItemId);
            if (held is null) return false;
            if (held.Item.Components.Contains("Quest") || catalog.Stash.IsQuestItem(held.Item.TypeHash)) return false;
            var existing = global::Game.AccountDisplayCaseItem(player, player.Layout.Id, entry.Key);
            removed = items.Remove(PlayerInventories.Cursor, place.ItemId);
            if (removed is null) return false;
            if (!global::Game.SetAccountDisplayCaseItem(player, player.Layout.Id, entry.Key,
                    CharacterSaves.ToSaved(removed.Item, catalog.Mods)))
                throw new InvalidOperationException("Failed to persist Item Display Case item");
            if (existing is not null)
            {
                var prior = CharacterSaves.FromSaved(existing, catalog);
                restored = items.Put(PlayerInventories.Cursor, new InventoryItem(items.NewItemId(), 0, 0, prior));
            }
            return true;
        });
        if (!ok || removed is null)
        {
            Console.WriteLine($"[gateway] {player.Connection.Remote} <= 0x0067 display case 0x{place.ObjectId:x}: item {place.ItemId} refused or not on cursor");
            return;
        }
        await Send(player, InstanceItems.InventoryChange(PlayerInventories.InventoryId(PlayerInventories.Cursor),
            items.NextRevision(PlayerInventories.Cursor), [removed.Id], restored is null ? [] : [restored]),
            $"0x00bf cursor: placed item {removed.Id} into Item Display Case 0x{place.ObjectId:x}");
    }

    /// <summary>C→S 0x68: toggles an Item Display Case; when turning off with a stored item, returns it to cursor or inventory.</summary>
    internal static async Task ToggleDisplayCase(Player player, InstanceHideoutEditing.DisplayCaseToggle toggle)
    {
        if (!CanEditHere(player) || player.Character.Account is null) return;
        HideoutCatalog.Doodad? doodad;
        lock (player.HideoutDoodads) doodad = player.HideoutDoodads.GetValueOrDefault(toggle.ObjectId);
        if (doodad?.Type != HideoutCatalog.ItemDisplayCaseType || !player.HideoutEditor.TryGet(toggle.ObjectId, out var entry))
        {
            Console.WriteLine($"[gateway] {player.Connection.Remote} <= 0x0068 display case 0x{toggle.ObjectId:x}: not a placed Item Display Case");
            return;
        }
        if (toggle.Flag)
        {
            Console.WriteLine($"[gateway] {player.Connection.Remote} <= 0x0068 display case 0x{toggle.ObjectId:x} toggle on");
            return;
        }
        var items = ItemsOf(player);
        byte targetRow = PlayerInventories.Cursor;
        InventoryItem? returned = null;
        bool ok = Saves.Atomically(() =>
        {
            var existing = global::Game.AccountDisplayCaseItem(player, player.Layout.Id, entry.Key);
            if (existing is null) return false;
            var item = CharacterSaves.FromSaved(existing, ItemsFor(player));
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
            if (!global::Game.SetAccountDisplayCaseItem(player, player.Layout.Id, entry.Key, null))
                throw new InvalidOperationException("Failed to clear Item Display Case item");
            return true;
        });
        if (!ok || returned is null)
        {
            Console.WriteLine($"[gateway] {player.Connection.Remote} <= 0x0068 display case 0x{toggle.ObjectId:x} reclaim: empty or no room");
            return;
        }
        await Send(player, InstanceItems.InventoryChange(PlayerInventories.InventoryId(targetRow),
            items.NextRevision(targetRow), [], [returned]),
            $"0x00bf row {targetRow}: reclaimed item {returned.Id} from Item Display Case 0x{toggle.ObjectId:x}");
    }

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
        if (!player.HideoutEditor.TryMove(move, (x, y) => PlacementInBounds(player.Layout, x, y),
            (key, p) =>
            {
                if (key.StartsWith(DecorationKeyPrefix, StringComparison.Ordinal))
                    global::Game.UpdateAccountHideoutDecoration(player, player.Layout.Id, key, p, move.Variation, move.Mirrored);
                else
                    global::Game.SetAccountHideoutPlacement(player, player.Layout.Id, key, p);
            }, out var entry))
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
        var areaDecorations = DecorationsOf(player);
        lock (areaDecorations)
            if (areaDecorations.TryGetValue(move.ObjectId, out var areaDoodad))
                areaDecorations[move.ObjectId] = areaDoodad with { X = p.X, Y = p.Y, Rotation = p.Rotation };
        foreach (var member in AreaPlayers(player))
            lock (member.HideoutDoodads)
                if (member.HideoutDoodads.TryGetValue(move.ObjectId, out var doodad))
                    member.HideoutDoodads[move.ObjectId] = doodad with { X = p.X, Y = p.Y, Rotation = p.Rotation };
        if (move.ObjectId == WaypointCatalog.ObjectId && player.Layout.Waypoint is { } waypoint)
            player.MoveWaypoint(waypoint with { GridX = p.X, GridY = p.Y, Rotation = p.Rotation });
        player.Furniture = null;
        await global::Game.SendWorldRecord(player, move.ObjectId, InstanceProtocol.Teleport(move.ObjectId, p.X, p.Y, 0x7fffffff),
            $"hideout move {entry.Key} 0x{move.ObjectId:x} to ({p.X},{p.Y}), saved");
        await Send(player, InstanceHideoutEditing.Rotation(move.ObjectId, p.Rotation),
            $"hideout rotation {p.Rotation:0.###}");
        await BroadcastRebuildCollision(player, "rebuild hideout model collision from current placements");
        await PushOutOfCollision(player);
    }

    /// <summary>C→S 0x0213: imports a hideout template layout, validating hideout match, bounds and serializable doodads,
    /// replacing saved custom decorations, moving matched functional stations and broadcasting world updates.</summary>
    internal static async Task ImportLayout(Player player, InstanceHideoutEditing.Layout layout)
    {
        if (player.Closed || player.LeavingArea || !CanEditHere(player) || player.Character.Account is null) return;
        var gate = ImportGates.GetValue(player.Area, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try
        {
            if (player.Closed || player.LeavingArea || !CanEditHere(player) || player.Character.Account is null) return;
            await ImportLayoutLocked(player, layout);
        }
        finally { gate.Release(); }
    }

    static async Task ImportLayoutLocked(Player player, InstanceHideoutEditing.Layout layout)
    {
        if (HideoutCatalog.ByArea(player.Layout.Hash) is not { } hideout || layout.HideoutHash != hideout.Hash16)
        {
            Console.WriteLine($"[gateway] {player.Connection.Remote} refused 0x0213 hideout import: template hash 0x{layout.HideoutHash:x4} does not match current hideout {player.Layout.Id}");
            if (player.Commands is { } wrongCmd)
                await wrongCmd.Reply("Cannot import hideout layout: template does not match the current hideout.");
            return;
        }
        if (global::Game.HideoutHasStoredDisplayCaseItems(player, player.Layout.Id))
        {
            Console.WriteLine($"[gateway] {player.Connection.Remote} refused 0x0213 hideout import: an Item Display Case holds a stored item");
            if (player.Commands is { } dcCmd)
                await dcCmd.Reply("Cannot import hideout layout while an Item Display Case holds a stored item. Remove all displayed items first.");
            return;
        }

        // Map known functional station hashes so imported templates reposition installed stations rather than duplicating them.
        var functionalByHash = new Dictionary<uint, string>();
        void MapFunctional(uint hash, string key) { if (hash != 0) functionalByHash.TryAdd(hash, key); }
        if (hideout.First(WaypointObject.MetadataPath) is { } wp) MapFunctional(wp.Hash, "waypoint");
        if (player.CurrentHideoutScene is { } scene)
        {
            MapFunctional(scene.Stash.Hash, "stash");
            if (hideout.First(StashObject.GuildMetadataPath) is { } gs) MapFunctional(gs.Hash, "guild-stash");
            MapFunctional(scene.Bench.Hash, "bench");
            MapFunctional(scene.Device.Hash, "map-device");
        }
        var objectIdByKey = new Dictionary<string, (uint Id, HideoutEditing.Entry Entry)>(StringComparer.Ordinal);
        foreach (var (id, entry) in player.HideoutEditor.All())
            if (!entry.Key.StartsWith(DecorationKeyPrefix, StringComparison.Ordinal))
                objectIdByKey.TryAdd(entry.Key, (id, entry));

        var importedPlacements = new Dictionary<string, HideoutPlacement>(StringComparer.Ordinal);
        var importedDecorations = new List<global::Game.SavedHideoutDecoration>();
        int skipped = 0;
        foreach (var d in layout.Doodads)
        {
            if (!PlacementInBounds(player.Layout, d.X, d.Y)) { skipped++; continue; }
            float rot = d.RotationRadians;
            if (!float.IsFinite(rot)) { skipped++; continue; }
            if (functionalByHash.TryGetValue(d.Hash, out string? key) && !importedPlacements.ContainsKey(key))
            {
                importedPlacements[key] = new HideoutPlacement(d.X, d.Y, rot);
                continue;
            }
            if (importedDecorations.Count >= global::Game.MaxHideoutDecorations) { skipped++; continue; }
            if (HideoutCatalog.DoodadByHash(d.Hash) is not var (type, _) || string.IsNullOrEmpty(type)
                || HideoutCatalog.TypeOf(type) is not var (components, _) || !HideoutObject.CanSerialize(components))
            {
                skipped++;
                continue;
            }
            string decoKey = DecorationKeyPrefix + Guid.NewGuid().ToString("N")[..12];
            importedDecorations.Add(new(decoKey, d.Hash, d.X, d.Y, rot, d.Variation, d.Mirrored));
        }
        ushort? musicRow = HideoutCatalog.HideoutMusicRowByHash16(layout.MusicHash);

        if (!global::Game.ReplaceAccountHideoutLayout(player, player.Layout.Id, importedPlacements, importedDecorations, musicRow))
            return;

        // Remove previously spawned custom decorations before spawning the imported layout.
        var existingCustom = player.HideoutEditor.All()
            .Where(o => o.Entry.Key.StartsWith(DecorationKeyPrefix, StringComparison.Ordinal))
            .ToList();
        var areaDecorations = DecorationsOf(player);
        foreach (var (oldId, oldEntry) in existingCustom)
        {
            lock (areaDecorations) areaDecorations.Remove(oldId);
            foreach (var member in AreaPlayers(player))
            {
                member.HideoutEditor.Unregister(oldId);
                member.RemoveBlocker(oldId);
                lock (member.HideoutDoodads) member.HideoutDoodads.Remove(oldId);
            }
            await global::Game.SendWorldRemoval(player, oldId,
                $"0x02bd remove replaced hideout decoration {oldEntry.Key} (0x{oldId:x})");
        }

        // Move matched functional objects in place and broadcast teleport + rotation.
        foreach (var (key, p) in importedPlacements)
        {
            if (!objectIdByKey.TryGetValue(key, out var target)) continue;
            var (objId, entry) = target;
            lock (areaDecorations)
                if (areaDecorations.TryGetValue(objId, out var areaDoodad))
                    areaDecorations[objId] = areaDoodad with { X = p.X, Y = p.Y, Rotation = p.Rotation };
            foreach (var member in AreaPlayers(player))
            {
                member.HideoutEditor.Register(objId, entry.Key, p.X, p.Y, p.Rotation, entry.BlockerSize, entry.CollisionRadius);
                if (entry.BlockerSize >= 0) member.SetBlocker(objId, p.X, p.Y, entry.BlockerSize, entry.CollisionRadius);
                if (objId == member.StashObject) member.StashAt = (p.X, p.Y);
                if (objId == member.GuildStashObject) member.GuildStashAt = (p.X, p.Y);
                if (objId == member.CraftingBenchObject) member.CraftingBenchAt = (p.X, p.Y);
                lock (member.HideoutDoodads)
                    if (member.HideoutDoodads.TryGetValue(objId, out var memberDoodad))
                        member.HideoutDoodads[objId] = memberDoodad with { X = p.X, Y = p.Y, Rotation = p.Rotation };
                if (objId == WaypointCatalog.ObjectId && member.Layout.Waypoint is { } waypoint)
                    member.MoveWaypoint(waypoint with { GridX = p.X, GridY = p.Y, Rotation = p.Rotation });
            }
            if (objId == player.StashObject) global::Game.SetAreaStash(player, objId, (p.X, p.Y));
            if (objId == player.GuildStashObject) global::Game.SetAreaGuildStash(player, objId, (p.X, p.Y));
            if (objId == player.CraftingBenchObject) global::Game.SetAreaBench(player, objId, (p.X, p.Y));
            await global::Game.SendWorldRecord(player, objId, InstanceProtocol.Teleport(objId, p.X, p.Y, InstanceProtocol.FollowTerrain),
                $"hideout import move {entry.Key} 0x{objId:x} to ({p.X},{p.Y})");
            await global::Game.SendWorldRecord(player, objId, InstanceHideoutEditing.Rotation(objId, p.Rotation),
                $"hideout import rotation {entry.Key} {p.Rotation:0.###}");
        }

        // Spawn newly imported custom decorations.
        int spawned = 0;
        foreach (var deco in importedDecorations)
            if (await SpawnDecorationObject(player, deco, "imported hideout decoration") is not null)
                spawned++;

        player.Furniture = null;
        await BroadcastRebuildCollision(player, $"rebuild hideout collision after 0x0213 import ({spawned} decoration(s))");
        await PushOutOfCollision(player);
        Console.WriteLine($"[gateway] {player.Connection.Remote} <= 0x0213 hideout import {hideout.Name}: {spawned} decoration(s), {importedPlacements.Count} station(s), {skipped} skipped");
        if (player.Commands is { } cmd)
            await cmd.Reply($"Imported hideout layout \"{hideout.Name}\" ({spawned} decoration(s), {importedPlacements.Count} station(s)" +
                (skipped > 0 ? $", {skipped} skipped" : "") + ").");
    }
}
