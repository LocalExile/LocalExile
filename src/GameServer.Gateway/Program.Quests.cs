using System.Text.Json;
using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

partial class Program
{
    // Sets or clears quest flags for the character (saved at once) and sends the player's flag set again.
    static async Task SetQuestFlags(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, IEnumerable<string> flags, bool on = true)
    {
        var changed = new List<string>();
        lock (conn.Monsters)
            foreach (string flag in flags)
                if (on ? conn.QuestFlags.Add(flag) : conn.QuestFlags.Remove(flag)) changed.Add(flag);
        if (changed.Count == 0) return;
        // Only the changed flags: a flag another connection of the character set meanwhile stays in the save.
        CharacterSaves.ChangeQuestFlags(CharacterName(conn), changed, on);
        foreach (string flag in changed) Console.WriteLine($"[gateway] {remote} {CharacterName(conn)}: quest flag {flag} {(on ? "set" : "cleared")}");
        if (!conn.PlayerSent) return;
        var all = PlayerQuestFlags(conn);
        await Send(remote, stream, ciphers, InstanceEntities.PlayerQuestFlagsUpdate(PlayerId, all), $"0x02ba player quest flags: {all.Count}");
        // The exits these flags open appear (or go again when a flag is cleared).
        foreach (var (id, transition) in conn.Layout.AreaTransitions)
        {
            if (TransitionFlag(conn, transition) is not { } flag || !changed.Contains(flag)) continue;
            if (on)
            {
                await Send(remote, stream, ciphers, InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(transition.Type), transition.Blob()),
                    $"0x02b9 create area transition 0x{id:x} {transition} (quest flag {flag})");
                await Send(remote, stream, ciphers, InstanceProtocol.EnterWakeZone(id), null);
            }
            else await Send(remote, stream, ciphers, InstanceProtocol.RemoveObject(id), $"0x02bb remove area transition 0x{id:x} (quest flag {flag} cleared)");
        }
        // The room objects these flags take away (a1q1: the force wall when Hillock dies).
        if (!on) return;
        List<uint> gone;
        lock (conn.Monsters)
        {
            gone = [.. conn.QuestRoomObjects.Where(o => changed.Contains(o.Value.Flag)).Select(o => o.Key)];
            foreach (uint id in gone) conn.QuestRoomObjects.Remove(id);
            conn.UpdateBarrier();
        }
        foreach (uint id in gone)
            await Send(remote, stream, ciphers, InstanceProtocol.RemoveObject(id), $"0x02bb remove room object 0x{id:x} (quest flag set)");
    }

    // The rooms' server objects a quest manages (Quests.RoomObjectRemovedBy), where the room fit places them, while the
    // character lacks the flag that takes them away: The Twilight Strand's force wall (Tutorial_Blocker_3) blocks the way to
    // the gate until Hillock is dead (12-quests.md §6). Ids from QuestRoomObjectFirstId.
    static async Task SpawnQuestRoomObjects(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
    {
        var notes = new List<string>();
        var objects = AreaPopulation.LoadObjects(conn.Layout.Id, conn.Seed, notes)
            .Select(o => (Object: o, Flag: Quests.RoomObjectRemovedBy(conn.Layout.Id, o.Type)))
            .Where(o => o.Flag is not null).ToList();
        if (objects.Count == 0) return;
        // The walls guard the exits their flag opens; the boss's side is the room's "mapboss" point (else the arrival).
        var boss = AreaPopulation.LoadPoints(conn.Layout.Id, conn.Seed, []).FirstOrDefault(p => p.Name == "mapboss");
        var from = conn.Layout.Near(boss is null ? (conn.Layout.SpawnX, conn.Layout.SpawnY) : (boss.X, boss.Y));
        uint id = QuestRoomObjectFirstId;
        foreach (var (o, flag) in objects)
        {
            if (conn.QuestFlags.Contains(flag!)) continue;
            var blocker = new BlockerObject(o.X, o.Y, o.Angle, o.Scale);
            await Send(remote, stream, ciphers, InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(o.Type), blocker.Blob()),
                $"0x02b9 create room object 0x{id:x} {o.Type} at ({o.X}, {o.Y}), angle {o.Angle:0.###}, scale {o.Scale:0.##}, until quest flag {flag}");
            await Send(remote, stream, ciphers, InstanceProtocol.EnterWakeZone(id), null);
            // The server's own wall: the walkable cells across the way (QuestBarriers).
            var guarded = conn.Layout.AreaTransitions.Where(t => TransitionFlag(conn, t.Transition) == flag)
                .Select(t => conn.Layout.Near((t.Transition.GridX, t.Transition.GridY))).ToList();
            var cells = conn.Layout.Walkable is { } walkable
                ? QuestBarriers.Cells(walkable, conn.Layout.GridW, conn.Layout.GridH, (o.X, o.Y), o.Angle, from, guarded)
                : [];
            Console.WriteLine($"[gateway] {remote} room object 0x{id:x}: {cells.Count} walkable cell(s) closed on the server between ({from.X}, {from.Y}) " +
                $"and {string.Join(", ", guarded)}" + (cells.Count == 0 ? "" : $", from {cells.Min()} to {cells.Max()}"));
            lock (conn.Monsters)
            {
                conn.QuestRoomObjects[id] = (flag!, cells);
                conn.UpdateBarrier();
            }
            id++;
        }
    }

    // The quest flag an area transition needs (Quests.FlagToEnter), or null. Such an exit is created only once the character
    // has the flag: The Twilight Strand's gate into Lioneye's Watch appears when Hillock dies (a1q1, 12-quests.md §6).
    static string? TransitionFlag(ClientState conn, AreaTransitionObject transition) =>
        AreaTransitionCatalog.IdOf(transition.DestinationHash16) is { } dest ? Quests.FlagToEnter(conn.Layout.Id, dest) : null;

    static bool TransitionShown(ClientState conn, AreaTransitionObject transition) =>
        TransitionFlag(conn, transition) is not { } flag || conn.QuestFlags.Contains(flag);
    static async Task SpawnOpeningTutorial(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
    {
        if (conn.Layout.Id != TutorialObjects.Strand || CharacterSaves.Load(CharacterName(conn))?.Tutorial is not { } tutorial) return;
        var objects = AreaPopulation.LoadObjects(conn.Layout.Id, conn.Seed, []);
        var exile = objects.FirstOrDefault(o => o.Type == TutorialObjects.Exile);
        conn.TutorialZombiePlacement = objects.FirstOrDefault(o => o.Type == TutorialObjects.Spawner);
        if (exile is not null && Npcs.Value.Find(TutorialObjects.Exile) is { Supported: true } npc)
            await SpawnNpc(remote, stream, ciphers, conn, npc with { TransitionState = (byte)(tutorial.Talked ? 2 : 1) }, exile.X, exile.Y, exile.Angle);
        if (conn.TutorialZombiePlacement is { } spawn)
        {
            if (tutorial.ZombieKilled)
                await DropTutorialGem(remote, stream, ciphers, conn, false, spawn.X, spawn.Y);
            else if (tutorial.Talked)
                await SpawnTutorialZombie(remote, stream, ciphers, conn);
            else
            {
                conn.TutorialSceneId = conn.NextObjectId++;
                await Send(remote, stream, ciphers, InstanceEntities.CreateObject(conn.TutorialSceneId, InstanceEntities.ObjectTypeHash(TutorialObjects.Spawner),
                    TutorialObjects.SceneBlob(spawn.X,spawn.Y,spawn.Angle)), "0x02b9 opening bite zombie scene");
                await Send(remote, stream, ciphers, InstanceProtocol.EnterWakeZone(conn.TutorialSceneId), null);
                await Send(remote, stream, ciphers, TutorialObjects.BiteAnimation(conn.TutorialSceneId,0), "0x01b2 opening zombie paused at bite start");
            }
        }
        // The recorder writes tile placements while the client generates the area. Wait briefly for this optional capture.
        _ = TrySpawnTutorialChest(remote, stream, ciphers, conn);
    }

    static async Task TrySpawnTutorialChest(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
    {
        try
        {
            for (int retry=0; retry<20 && !conn.Closed; retry++)
            {
                var named=AreaPopulation.LoadPoints(conn.Layout.Id,conn.Seed,[]).FirstOrDefault(p=>p.Name==TutorialObjects.Chest);
                if ((TutorialObjects.ChestPlacement(conn.Layout.Id,conn.Seed) ?? (named is null ? null : TutorialObjects.ChestAt(named))) is { } chest)
                {
                    conn.TutorialChest = chest; conn.TutorialChestId = conn.NextObjectId++;
                    bool opened = CharacterSaves.Load(CharacterName(conn))?.Tutorial?.ChestOpened == true;
                    await Send(remote,stream,ciphers,InstanceEntities.CreateObject(conn.TutorialChestId,InstanceEntities.ObjectTypeHash(chest.Type),
                        TutorialObjects.ChestBlob(chest,opened)), $"0x02b9 tutorial support chest at ({chest.X},{chest.Y}), opened={opened}");
                    await Send(remote,stream,ciphers,InstanceProtocol.EnterWakeZone(conn.TutorialChestId),null);
                    if (opened) await DropTutorialGem(remote,stream,ciphers,conn,true,chest.X,chest.Y);
                    return;
                }
                await Task.Delay(1000);
            }
            if (!conn.Closed) Console.WriteLine($"[gateway] {remote} tutorial chest has no captured placement for {conn.Layout.Id} seed {conn.Seed}; capture with -Walkmaps");
        }
        catch (Exception ex) when (ex is IOException or JsonException or ObjectDisposedException)
        { if (!conn.Closed) Console.WriteLine($"[gateway] tutorial chest: {ex.Message}"); }
    }

    static async Task TalkToWoundedExile(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,uint id,SpawnedNpc npc)
    {
        if (CharacterSaves.Load(CharacterName(conn))?.Tutorial is not { Talked: false } || conn.TutorialSceneRunning) return;
        bool armed = ItemsOf(conn).Items(PlayerInventories.Weapon1).Count > 0;
        conn.InNpcTalk = true;
        await Send(remote,stream,ciphers,InstanceNpcs.Talk(id,armed ? (byte)2 : (byte)1),"0x0084 dying exile: class weapon check");
        if (!armed) return;
        conn.TutorialSceneRunning = true;
        CharacterSaves.ChangeTutorial(CharacterName(conn),t=>t with { Talked=true });
        await SetQuestFlags(remote,stream,ciphers,conn,["TalkedToWoundedExile"]);
        _ = RunOpeningScene(remote,stream,ciphers,conn,id,npc);
    }

    static async Task RunOpeningScene(string remote,NetworkStream stream,ConnectionCiphers ciphers,ClientState conn,uint id,SpawnedNpc npc)
    {
        try
        {
            double start=conn.Clock.Elapsed.TotalSeconds;
            // NPCTalk's actual script: triggerer.TalkToWoundedExile(8.2,22.8). Use the paused instance clock.
            async Task Until(double elapsed) { while (!conn.Closed && conn.Clock.Elapsed.TotalSeconds < start+elapsed) await Task.Delay(50); }
            await Until(8.2); if(conn.Closed) return;
            int component=npc.Npc.Components!.ToList().IndexOf("Transitionable");
            if(component>=0) await Send(remote,stream,ciphers,InstanceEntities.TransitionableUpdate(id,(byte)component,2),"0x02ba wounded exile bitten");
            if(conn.TutorialSceneId!=0) await Send(remote,stream,ciphers,TutorialObjects.BiteAnimation(conn.TutorialSceneId,1),"0x01b2 opening zombie bites exile");
            await Until(22.8); if(conn.Closed) return;
            if(conn.TutorialSceneId!=0) await Send(remote,stream,ciphers,InstanceProtocol.RemoveObject(conn.TutorialSceneId),null);
            if(conn.TalkingTo==id) { conn.InNpcTalk=false;conn.TalkingTo=0;await Send(remote,stream,ciphers,InstanceNpcs.Talk(0),null); }
            await SpawnTutorialZombie(remote,stream,ciphers,conn);
        }
        catch(Exception ex) when(ex is IOException or ObjectDisposedException)
        { if(!conn.Closed) Console.WriteLine($"[gateway] opening scene: {ex.Message}"); }
        finally { conn.TutorialSceneRunning=false; }
    }

    static async Task SpawnTutorialZombie(string remote,NetworkStream stream,ConnectionCiphers ciphers,ClientState conn)
    {
        if(Environment.GetEnvironmentVariable("Game_MONSTERS")=="0" || conn.TutorialZombiePlacement is not { } spawn) return;
        lock(conn.Monsters) if(conn.Monsters.Values.Any(m=>m.Variety?.Id==TutorialObjects.Zombie)) return;
        if(Monsters.Find(TutorialObjects.Zombie) is { Supported:true } zombie)
            await SpawnMonster(remote,stream,ciphers,conn,zombie,1,MonsterRarity.Normal,spawn.X,spawn.Y);
    }

    static async Task DropTutorialGem(string remote,NetworkStream stream,ConnectionCiphers ciphers,ClientState conn,bool support,int x,int y)
    {
        if(CharacterSaves.Load(CharacterName(conn))?.Tutorial is not { } tutorial || (support ? tutorial.SupportClaimed : tutorial.SkillClaimed)) return;
        var item=CharacterSaves.FromSaved(support?tutorial.SupportGem:tutorial.SkillGem,Items);
        var ground=new WorldItemObject(x,y,item);
        if(conn.Instance.PlaceTutorialItem(TutorialObjects.DropKey(CharacterName(conn),tutorial,support),ground) is not uint id) return;
        await Send(remote,stream,ciphers,InstanceEntities.CreateObject(id,InstanceEntities.ObjectTypeHash(WorldItemObject.MetadataPath),ground.Blob()),
            $"0x02b9 tutorial {(support?"support":"skill")} gem {Items.GemName(item)} at ({x},{y})");
        await Send(remote,stream,ciphers,InstanceProtocol.EnterWakeZone(id),null);
    }

    static async Task OpenTutorialChest(string remote,NetworkStream stream,ConnectionCiphers ciphers,ClientState conn,InstanceTransfer.UseSkillOnObject use,TutorialObjects.Placement chest)
    {
        if(CharacterSaves.Load(CharacterName(conn))?.Tutorial is not { ChestOpened:false }) return;
        int action=Interlocked.Increment(ref conn.ActionCount);
        double now=conn.Clock.Elapsed.TotalSeconds;
        var (here,stop,arrive)=conn.Walk.WalkUpTo(chest.X,chest.Y,1,now);
        await Send(remote,stream,ciphers,InstanceProtocol.ActionStarted(PlayerId,here.X,here.Y,chest.X,chest.Y,use.SkillId,use.Sequence,use.Flags,targetObjectId:use.TargetId),null);
        if(arrive>now) await Task.Delay(TimeSpan.FromSeconds(arrive-now));
        if(conn.ActionCount!=action || conn.Closed) return;
        if(!GameServer.Core.World.PlayerWalk.InWalkUpRange(stop,(chest.X,chest.Y),1))
        {
            await Send(remote,stream,ciphers,InstanceProtocol.ActionEnded(PlayerId),null);
            return;
        }
        CharacterSaves.ChangeTutorial(CharacterName(conn),t=>t with { ChestOpened=true });
        await Send(remote,stream,ciphers,InstanceProtocol.RemoveObject(use.TargetId),null);
        await Send(remote,stream,ciphers,InstanceEntities.CreateObject(use.TargetId,InstanceEntities.ObjectTypeHash(chest.Type),TutorialObjects.ChestBlob(chest,true)),"0x02b9 opened support gem chest");
        await Send(remote,stream,ciphers,InstanceProtocol.EnterWakeZone(use.TargetId),null);
        await DropTutorialGem(remote,stream,ciphers,conn,true,chest.X,chest.Y);
        await Send(remote,stream,ciphers,InstanceProtocol.ActionEnded(PlayerId),null);
    }

}
