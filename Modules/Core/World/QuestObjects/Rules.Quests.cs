using System.Text.Json;
using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.Diagnostics;

static partial class QuestobjectsRules
{
    internal static async Task TrySpawnTutorialChest(ClientState conn)
    {
        if (!LeagueFor(conn).Rules.TutorialGifts) return;
        try
        {
            for (int retry=0; retry<20 && !conn.Closed; retry++)
            {
                var named=AreaPopulation.LoadPoints(conn.Layout.Id,conn.Seed,[]).FirstOrDefault(p=>p.Name==TutorialObjects.Chest);
                if ((TutorialObjects.ChestPlacement(conn.Layout.Id,conn.Seed) ?? (named is null ? null : TutorialObjects.ChestAt(named))) is { } chest)
                {
                    conn.TutorialChest = chest; conn.TutorialChestId = conn.TakeObjectId();
                    bool opened = CharacterSaves.Load(CharacterName(conn))?.Tutorial?.ChestOpened == true;
                    await global::Game.SendWorldCreate(conn, conn.TutorialChestId, InstanceEntities.CreateObject(conn.TutorialChestId,InstanceEntities.ObjectTypeHash(chest.Type),
                        TutorialObjects.ChestBlob(chest,opened)), $"0x02bb tutorial support chest at ({chest.X},{chest.Y}), opened={opened}");
                    await global::Game.SendWorldRecord(conn, conn.TutorialChestId, InstanceProtocol.EnterWakeZone(conn.TutorialChestId),null);
                    await RegisterStationaryCollision(conn,conn.TutorialChestId,chest.Type,chest.X,chest.Y,0);
                    if (opened) await DropTutorialGem(conn,true,chest.X,chest.Y);
                    return;
                }
                await Task.Delay(1000);
            }
            if (!conn.Closed) Console.WriteLine($"[gateway] {conn.Remote} tutorial chest has no captured placement for {conn.Layout.Id} seed {conn.Seed}; capture with -Walkmaps");
        }
        catch (Exception ex) when (ex is IOException or JsonException or ObjectDisposedException)
        { if (!conn.Closed) Console.WriteLine($"[gateway] tutorial chest: {ex.Message}"); }
    }

    internal static async Task RunOpeningScene(ClientState conn,uint id,SpawnedNpc npc,double biteAt,Task skipped)
    {
        try
        {
            double start=conn.Clock.Elapsed.TotalSeconds;
            // Finish the selected voice line, then play the whole bite before handing over to the combat monster.
            // Use the paused instance clock for both waits.
            while (!conn.Closed && !skipped.IsCompleted && conn.Clock.Elapsed.TotalSeconds < start+biteAt)
                await Task.Delay(50);
            await WhilePaused(conn);
            if(conn.Closed) return;
            double biteStarted = conn.Clock.Elapsed.TotalSeconds;
            int component=npc.Npc.Components!.ToList().IndexOf("Transitionable");
            if(component>=0) await global::Game.SendWorldRecord(conn, id, InstanceEntities.TransitionableUpdate(id,(byte)component,2),"0x02bc wounded exile bitten");
            if(conn.TutorialSceneId!=0) await Send(conn,TutorialObjects.BiteAnimation(conn.TutorialSceneId,1),"0x01b2 opening zombie bites exile");
            while (!conn.Closed && conn.Clock.Elapsed.TotalSeconds < biteStarted+TutorialObjects.BiteSeconds)
                await Task.Delay(50);
            if(conn.Closed) return;
            if(conn.TutorialSceneId!=0) await global::Game.SendWorldRemoval(conn, conn.TutorialSceneId,null);
            if(conn.TalkingTo==id) { conn.InNpcTalk=false;conn.TalkingTo=0;await Send(conn,InstanceNpcs.Talk(0),null); }
            await SpawnTutorialZombie(conn);
        }
        catch(Exception ex) when(ex is IOException or ObjectDisposedException)
        { if(!conn.Closed) Console.WriteLine($"[gateway] opening scene: {ex.Message}"); }
        finally { conn.TutorialSceneRunning=false; conn.TutorialDialogueSkipped=null; }
    }

    internal static async Task SpawnTutorialZombie(ClientState conn)
    {
        if(Environment.GetEnvironmentVariable("Game_MONSTERS")=="0" || conn.TutorialZombiePlacement is not { } spawn) return;
        lock(conn.Monsters) if(conn.Monsters.Values.Any(m=>m.Variety?.Id==TutorialObjects.Zombie)) return;
        if(Monsters.Find(TutorialObjects.Zombie) is { Supported:true } zombie)
            await SpawnMonster(conn,zombie,1,MonsterRarity.Normal,spawn.X,spawn.Y);
    }

    internal static GlyphQuest.Layout? LoadGlyphLayout(ClientState conn)
    {
        try { return GlyphQuest.Load(conn.Layout.Id, conn.Seed); }
        catch (Exception ex) when (ex is IOException or JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            Console.WriteLine($"[gateway] {conn.Remote} a1q4 capture: {ServerLog.Describe(ex)}");
            return null;
        }
    }

    internal static async Task CreateGlyphObjects(ClientState conn, GlyphQuest.Layout layout)
    {
        try
        {
            conn.Glyphs = layout;
            bool placed = ClientHasQuestFlag(conn, GlyphQuest.PlacedGlyphs);
            if (!placed) SetPassageBlockers(conn, true);
            else
                // Glyphs left on this instance's ground are not needed any more (their .it remove_flag).
                foreach (var (gid, _) in conn.Instance.Ground.All().Where(e => GlyphIndex(e.Item.Item) is not null))
                    if (conn.Instance.Ground.Take(gid) is not null)
                        await global::Game.SendWorldRemoval(conn, gid, $"0x02bd remove glyph 0x{gid:x}: the glyphs are placed");
            for (int i = 0; i < layout.Nests.Count && i < 0x10; i++)
            {
                var nest = layout.Nests[i];
                bool opened = conn.Instance.ChestOpened(nest.At.X, nest.At.Y);
                uint id = (opened ? OpenedNestFirstId : GlyphObjectFirstId) + (uint)i;
                lock (conn.GlyphNests) conn.GlyphNests[id] = nest;
                conn.SetBlocker(id, nest.At.X, nest.At.Y, GlyphQuest.NestSize);
                await global::Game.SendWorldCreate(conn, id, InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(nest.Type), GlyphQuest.NestBlob(nest.At, opened)),
                    $"0x02bb Rhoa Nest 0x{id:x} {nest.Type} at ({nest.At.X}, {nest.At.Y}) with glyph {nest.Glyph + 1}, opened={opened}");
                await global::Game.SendWorldRecord(conn, id, InstanceProtocol.EnterWakeZone(id), null);
            }
            if (layout.Wall is { } wall)
            {
                await global::Game.SendWorldCreate(conn, GlyphWallObjectId, InstanceEntities.CreateObject(GlyphWallObjectId, InstanceEntities.ObjectTypeHash(GlyphQuest.Wall), GlyphQuest.WallBlob(wall, placed)),
                    $"0x02bb Glyph Wall 0x{GlyphWallObjectId:x} at ({wall.X}, {wall.Y}), {(placed ? "glyphs placed" : "waiting for the glyphs")}");
                await global::Game.SendWorldRecord(conn, GlyphWallObjectId, InstanceProtocol.EnterWakeZone(GlyphWallObjectId), null);
                conn.GlyphWallId = GlyphWallObjectId;
            }
            if (layout.Water is { } water)
            {
                await global::Game.SendWorldCreate(conn, GlyphWaterObjectId, InstanceEntities.CreateObject(GlyphWaterObjectId, InstanceEntities.ObjectTypeHash(GlyphQuest.Water), GlyphQuest.WaterBlob(water.X, water.Y, water.Rotation, placed)),
                    $"0x02bb waterfall 0x{GlyphWaterObjectId:x} at ({water.X}, {water.Y}), transitioned={placed}");
                await global::Game.SendWorldRecord(conn, GlyphWaterObjectId, InstanceProtocol.EnterWakeZone(GlyphWaterObjectId), null);
                conn.GlyphWaterId = GlyphWaterObjectId;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or ObjectDisposedException or KeyNotFoundException or InvalidOperationException)
        { if (!conn.Closed) Console.WriteLine($"[gateway] a1q4 objects: {ServerLog.Describe(ex)}"); }
    }

    // A nest's glyph on the instance's ground, unless the character no longer needs it (QuestItems.Drops: the glyphs are
    // placed, or a copy is carried or on this instance's ground): a glyph lost another way drops again.
    internal static Task DropGlyph(ClientState conn, GlyphQuest.Nest nest) =>
        DropQuestItem(conn, GlyphQuest.Glyphs[nest.Glyph], (nest.At.X, nest.At.Y), $"the Rhoa Nest at ({nest.At.X}, {nest.At.Y})");

    internal static async Task PlaceGlyphs(ClientState conn)
    {
        var items = ItemsOf(conn);
        List<(byte Row, InventoryItem Item)>? removed;
        lock (conn.Monsters)
        {
            if (HardcoreProgressBlocked(conn)) return;
            removed = GlyphQuest.Place(CharacterName(conn), items, GlyphIndex);
        }
        if (removed is null)
        {
            Console.WriteLine($"[gateway] {conn.Remote} Glyph Wall: needs all three glyphs");
            return;
        }
        foreach (var row in removed.GroupBy(r => r.Row))
            await Send(conn, InstanceItems.InventoryChange(PlayerInventories.InventoryId(row.Key), items.NextRevision(row.Key), [.. row.Select(r => r.Item.Id)], []),
                $"0x00bf inventory {PlayerInventories.InventoryId(row.Key)}: glyphs {string.Join(", ", row.Select(r => r.Item.Id))} placed in the Glyph Wall");
        await SetQuestFlags(conn, [GlyphQuest.PlacedGlyphs]);
        await SetQuestFlags(conn, GlyphQuest.HaveGlyph, on: false);
    }



    internal static async Task DropTutorialGem(ClientState conn,bool support,int x,int y)
    {
        foreach (var output in CommitTutorialGem(conn,support,x,y)) await SendWorldReceipt(conn, output);
    }

    internal static List<PlayerMinions.Output> CommitTutorialGem(ClientState conn,bool support,int x,int y)
    {
        if (!LeagueFor(conn).Rules.TutorialGifts && support) return [];
        if(CharacterSaves.Load(CharacterName(conn))?.Tutorial is not { } tutorial || (support ? tutorial.SupportClaimed : tutorial.SkillClaimed)) return [];
        var item=CharacterSaves.FromSaved(support?tutorial.SupportGem:tutorial.SkillGem,Items);
        var ground=new WorldItemObject(x,y,item);
        if(conn.Instance.PlaceTutorialItem(TutorialObjects.DropKey(CharacterName(conn),tutorial,support),ground,conn) is not uint id) return [];
        return [new(InstanceEntities.CreateObject(id,InstanceEntities.ObjectTypeHash(WorldItemObject.MetadataPath),ground.Blob(justDropped: true)),
            $"0x02bb tutorial {(support?"support":"skill")} gem {Items.GemName(item)} at ({x},{y})", WorldId: id, CreatesObject: true), new(InstanceProtocol.EnterWakeZone(id),null, WorldId: id)];
    }
}
