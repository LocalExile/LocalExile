using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;
using static Program;

static class QuestObjectsFeature
{
    // The rooms' server objects a quest manages (Quests.RoomObjectRemovedBy), where the room fit places them, while the
    // character lacks the flag that takes them away: The Twilight Strand's force wall (Tutorial_Blocker_3) blocks the way to
    // the gate until Hillock is dead (12-quests.md §6). Ids from QuestRoomObjectFirstId.
    internal static async Task SpawnQuestRoomObjects(Player player)
    {
        var notes = new List<string>();
        var objects = AreaPopulation.LoadObjects(player.Layout.Id, player.Seed, notes)
            .Select(o => (Object: o, Flag: QuestsOf(player).RoomObjectRemovedBy(player.Layout.Id, o.Type)))
            .Where(o => o.Flag is not null).ToList();
        if (objects.Count == 0) return;
        // The walls guard the exits their flag opens; the boss's side is the room's boss point ("mapboss", "mapboss2" …,
        // AreaPopulation.BossPoint; else the arrival).
        var boss = AreaPopulation.LoadPoints(player.Layout.Id, player.Seed, []).FirstOrDefault(p => AreaPopulation.BossPoint(p.Name) is not null);
        var from = player.Layout.Near(boss is null ? (player.Layout.SpawnX, player.Layout.SpawnY) : (boss.X, boss.Y));
        uint id = QuestRoomObjectFirstId;
        foreach (var (o, flag) in objects)
        {
            if (ClientHasQuestFlag(player, flag!)) continue;
            var blocker = new BlockerObject(o.X, o.Y, o.Angle, o.Scale);
            await global::Game.SendWorldCreate(player, id, () => InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(o.Type), blocker.Blob()),
                $"0x02bb create room object 0x{id:x} {o.Type} at ({o.X}, {o.Y}), angle {o.Angle:0.###}, scale {o.Scale:0.##}, until quest flag {flag}", wake: true, awaitBirth: false);
            // The server's own wall: the walkable cells across the way (QuestBarriers).
            var guarded = player.Layout.AreaTransitions.Where(t => TransitionFlag(player, t.Transition) == flag)
                .Select(t => player.Layout.Near((t.Transition.GridX, t.Transition.GridY))).ToList();
            var cells = player.Layout.Walkable is { } walkable
                ? QuestBarriers.Cells(walkable, player.Layout.GridW, player.Layout.GridH, (o.X, o.Y), o.Angle, from, guarded)
                : [];
            Console.WriteLine($"[gateway] {player.Connection.Remote} room object 0x{id:x}: {cells.Count} walkable cell(s) closed on the server between ({from.X}, {from.Y}) " +
                $"and {string.Join(", ", guarded)}" + (cells.Count == 0 ? "" : $", from {cells.Min()} to {cells.Max()}"));
            player.QuestRoomObjects[id] = (flag!, cells);
            player.UpdateBarrier();
            id++;
        }
    }

    internal static async Task SpawnOpeningTutorial(Player player)
    {
        if (player.Layout.Id != TutorialObjects.Strand || CharacterSaves.Load(CharacterName(player))?.Tutorial is not { } tutorial) return;
        var objects = AreaPopulation.LoadObjects(player.Layout.Id, player.Seed, []);
        var exile = objects.FirstOrDefault(o => o.Type == TutorialObjects.Exile);
        player.TutorialZombiePlacement = objects.FirstOrDefault(o => o.Type == TutorialObjects.Spawner);
        if (exile is not null && Npcs.Find(TutorialObjects.Exile) is { Supported: true } npc)
            await SpawnNpc(player, npc with { TransitionState = (byte)(tutorial.Talked ? 2 : 1) }, exile.X, exile.Y, exile.Angle);
        if (player.TutorialZombiePlacement is { } spawn)
        {
            if (tutorial.ZombieKilled)
                await DropTutorialGem(player, false, spawn.X, spawn.Y);
            else if (tutorial.Talked)
                await SpawnTutorialZombie(player);
            else
            {
                // Pre-register ZombieBite@1 so the handoff when the bite starts needs no extra round-trip.
                if (Monsters.Find(TutorialObjects.Zombie) is { Supported: true } zombie && !player.MonsterTypes.Contains((zombie.Row, 1)))
                    await Send(player, [], $"0x0020 monster type {zombie.Id}@1 (row {zombie.Row})", atGate: () =>
                        !player.Closed && !player.LeavingArea && player.MonsterTypes.Add((zombie.Row, 1))
                            ? InstanceEntities.MonsterTypes([((ushort)zombie.Row, 1)]) : null);
                player.TutorialSceneId = player.TakeObjectId();
                await global::Game.SendWorldCreate(player, player.TutorialSceneId, InstanceEntities.CreateObject(player.TutorialSceneId, InstanceEntities.ObjectTypeHash(TutorialObjects.Spawner),
                    TutorialObjects.SceneBlob(spawn.X,spawn.Y,spawn.Angle)), "0x02bb opening bite zombie scene");
                await global::Game.SendWorldRecord(player, player.TutorialSceneId, InstanceProtocol.EnterWakeZone(player.TutorialSceneId), null);
                await Send(player, TutorialObjects.BiteAnimation(player.TutorialSceneId,0), "0x01b2 opening zombie paused at bite start");
            }
        }
        // The recorder writes tile placements while the client generates the area. Wait briefly for this optional capture.
        _ = TrySpawnTutorialChest(player);
    }

    // a1q4 "Breaking Some Eggs" (GlyphQuest, 12-quests.md §2–4): The Mud Flats' three Rhoa Nests, the Glyph Wall and the
    // waterfall where the recorder captured them, and the client's invisible blockers on the server's walk estimate until
    // PlacedGlyphs. The recorder writes the capture while the client generates the area: wait briefly for it.
    internal static async Task SpawnGlyphQuest(Player player)
    {
        if (player.Layout.Id != GlyphQuest.Area) return;
        // Usually captured on an earlier visit: created at once, with the area. Else wait for the recorder in the background.
        if (LoadGlyphLayout(player) is { } now) { await CreateGlyphObjects(player, now); return; }
        _ = AreaFrames.Start(async () =>
        {
            for (int retry = 0; retry < 20 && !player.Closed; retry++)
            {
                await AreaDelay(player, 1);
                if (LoadGlyphLayout(player) is { } later) { await CreateGlyphObjects(player, later); return; }
            }
            if (!player.Closed) Console.WriteLine($"[gateway] {player.Connection.Remote} a1q4: no captured Rhoa Nests or Glyph Wall for {player.Layout.Id} seed {player.Seed}; capture with -Walkmaps");
        });
    }

    internal static async Task OpenTutorialChest(Player player,InstanceTransfer.UseSkillOnObject use,TutorialObjects.Placement chest,double requestedAt)
    {
        if (!LeagueFor(player).Rules.TutorialGifts) return;
        if(CharacterSaves.Load(CharacterName(player))?.Tutorial is not { ChestOpened:false }) return;
        if (!TryBeginObjectWalk(player, requestedAt, chest.X, chest.Y, TutorialObjects.ChestInteractionSize, out var objectWalk)) return;
        var (action, now, here, stop, arrive) = objectWalk;
        if (!use.Started)   // a lockstep client got its start from the frame pass
            await global::Game.SendWorldRecord(player, player.Id, InstanceProtocol.ActionStarted(player.Id,here.X,here.Y,chest.X,chest.Y,use.SkillId,use.Sequence,use.Flags,targetObjectId:use.TargetId),null,still:()=>PlayerActionCurrent(player,action));
        if(arrive>now) await AreaDelay(player, arrive-now);
        if(!PlayerActionCurrent(player,action) || player.Closed) return;
        if(GameServer.Core.World.PlayerWalk.InWalkUpRange(stop,(chest.X,chest.Y),TutorialObjects.ChestInteractionSize))
            await OpenTutorialChestCore(player,use.TargetId,chest);
        await global::Game.SendWorldRecord(player, player.Id, InstanceProtocol.ActionEnded(player.Id),null);
    }

    // The support gem chest opens (walked up to, or a warcry with open_nearby_chests_on_warcry): saved first, once.
    internal static async Task<bool> OpenTutorialChestCore(Player player,uint id,TutorialObjects.Placement chest)
    {
        if(!LeagueFor(player).Rules.TutorialGifts || CharacterSaves.Load(CharacterName(player))?.Tutorial is not { ChestOpened:false }) return false;
        // The walk-up and a warcry can both get here at once: only the first opens it.
        if(Interlocked.Exchange(ref player.TutorialChestOpening,1)==1) return false;
        CharacterSaves.ChangeTutorial(CharacterName(player),t=>t with { ChestOpened=true });
        await global::Game.SendWorldRemoval(player, id,null);
        await global::Game.SendWorldCreate(player, id, InstanceEntities.CreateObject(id,InstanceEntities.ObjectTypeHash(chest.Type),TutorialObjects.ChestBlob(chest,true)),"0x02bb opened support gem chest");
        await global::Game.SendWorldRecord(player, id, InstanceProtocol.EnterWakeZone(id),null);
        await DropTutorialGem(player,true,chest.X,chest.Y);
        return true;
    }

    // C→S 0x24 on a Rhoa Nest: walk up, open it (once per instance) and drop its glyph, unless the character has it already.
    internal static async Task OpenRhoaNest(Player player, InstanceTransfer.UseSkillOnObject use, GlyphQuest.Nest nest)
    {
        var at = nest.At;
        if (player.Instance.ChestOpened(at.X, at.Y)) return;
        int action = Interlocked.Increment(ref player.ActionCount);
        double now = AreaTime(player);
        var (here, stop, arrive) = player.Walk.WalkUpTo(at.X, at.Y, GlyphQuest.NestSize, now);
        if (!use.Started)   // a lockstep client got its start from the frame pass
            await global::Game.SendWorldRecord(player, player.Id, InstanceProtocol.ActionStarted(player.Id, here.X, here.Y, at.X, at.Y, use.SkillId, use.Sequence, use.Flags, targetObjectId: use.TargetId),
                $"0x01ab player opens Rhoa Nest 0x{use.TargetId:x} from ({here.X}, {here.Y}): reached at ({stop.X}, {stop.Y}) in {arrive - now:0.00} s");
        if (arrive > now) await AreaDelay(player, arrive - now);
        if (player.ActionCount != action || player.Closed) return;
        if (GameServer.Core.World.PlayerWalk.InWalkUpRange(stop, (at.X, at.Y), GlyphQuest.NestSize))
            await OpenRhoaNestCore(player, use.TargetId, nest);
        await global::Game.SendWorldRecord(player, player.Id, InstanceProtocol.ActionEnded(player.Id), null);
    }

    // A Rhoa Nest opens (walked up to, or a warcry with open_nearby_chests_on_warcry): once per instance.
    internal static async Task<bool> OpenRhoaNestCore(Player player,
        uint id, GlyphQuest.Nest nest)
    {
        var at = nest.At;
        int index = player.Glyphs is { } layout ? layout.Nests.ToList().IndexOf(nest) : -1;
        if (index < 0 || !player.Instance.OpenChest(at.X, at.Y)) return false;
        // Chest has no update reader: the opened nest replaces the closed one, under its own id (the same id again made
        // the client log "Replacing awake object", live 2026-10-25).
        uint opened = OpenedNestFirstId + (uint)index;
        lock (player.GlyphNests) { player.GlyphNests.Remove(id); player.GlyphNests[opened] = nest; }
        player.RemoveBlocker(id);
        player.SetBlocker(opened, at.X, at.Y, GlyphQuest.NestSize);
        await global::Game.SendWorldRemoval(player, id, null);
        await global::Game.SendWorldCreate(player, opened, () => InstanceEntities.CreateObject(opened, InstanceEntities.ObjectTypeHash(nest.Type), GlyphQuest.NestBlob(at, true)),
            $"0x02bb opened Rhoa Nest 0x{opened:x} (was 0x{id:x})", wake: true, awaitBirth: false);
        await DropGlyph(player, nest);
        return true;
    }

    // C→S 0x24 on the Glyph Wall: walk up; seeing it sets SeenGlyphWall; with all three glyphs it takes them and sets
    // PlacedGlyphs (SetQuestFlags then opens the passage).
    internal static async Task UseGlyphWall(Player player, InstanceTransfer.UseSkillOnObject use)
    {
        if (player.Glyphs?.Wall is not { } wall) return;
        int action = Interlocked.Increment(ref player.ActionCount);
        double now = AreaTime(player);
        var (here, stop, arrive) = player.Walk.WalkUpTo(wall.X, wall.Y, GlyphQuest.WallSize, now);
        if (!use.Started)   // a lockstep client got its start from the frame pass
            await global::Game.SendWorldRecord(player, player.Id, InstanceProtocol.ActionStarted(player.Id, here.X, here.Y, wall.X, wall.Y, use.SkillId, use.Sequence, use.Flags, targetObjectId: use.TargetId),
                $"0x01ab player uses the Glyph Wall from ({here.X}, {here.Y}): reached at ({stop.X}, {stop.Y}) in {arrive - now:0.00} s");
        if (arrive > now) await AreaDelay(player, arrive - now);
        if (player.ActionCount != action || player.Closed) return;
        if (GameServer.Core.World.PlayerWalk.InWalkUpRange(stop, (wall.X, wall.Y), GlyphQuest.WallSize))
        {
            await SetQuestFlags(player, [GlyphQuest.SeenGlyphWall]);
            if (!ClientHasQuestFlag(player, GlyphQuest.PlacedGlyphs))
                await PlaceGlyphs(player);
        }
        else Console.WriteLine($"[gateway] {player.Connection.Remote} Glyph Wall out of reach from ({stop.X}, {stop.Y})");
        await global::Game.SendWorldRecord(player, player.Id, InstanceProtocol.ActionEnded(player.Id), null);
    }


    internal static async Task TalkToWoundedExile(Player player,uint id,SpawnedNpc npc)
    {
        if (CharacterSaves.Load(CharacterName(player))?.Tutorial is not { } tutorial) return;
        if (tutorial.Talked || player.TutorialSceneRunning)
        {
            TutorialObjects.SkipDialogue(player);
            await Send(player,InstanceNpcs.Talk(0),"0x0084 dying exile: opening scene already started");
            return;
        }
        bool armed = ItemsOf(player).Items(PlayerInventories.Weapon1).Count > 0;
        var dialogue = TutorialObjects.Dialogue(armed, tutorial.WeaponPromptHeard);
        player.InNpcTalk = true;
        await Send(player,InstanceNpcs.Talk(id,dialogue.Option),"0x0084 dying exile: class weapon check");
        if (dialogue.BiteAt is not double biteAt)
        {
            CharacterSaves.ChangeTutorial(CharacterName(player),t=>t with { WeaponPromptHeard=true });
            return;
        }
        player.TutorialSceneRunning = true;
        var skipped = player.TutorialDialogueSkipped = new(TaskCreationOptions.RunContinuationsAsynchronously);
        CharacterSaves.ChangeTutorial(CharacterName(player),t=>t with { Talked=true });
        await SetQuestFlags(player,["TalkedToWoundedExile"]);
        _ = RunOpeningScene(player,id,npc,biteAt,skipped.Task);
    }

}
