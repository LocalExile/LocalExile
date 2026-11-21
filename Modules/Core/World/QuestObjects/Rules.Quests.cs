using System.Text.Json;
using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.Diagnostics;

static partial class QuestobjectsRules
{
    internal static async Task TrySpawnTutorialChest(Player player)
    {
        if (!LeagueFor(player).Rules.TutorialGifts) return;
        try
        {
            for (int retry=0; retry<20 && !player.Closed; retry++)
            {
                var named=AreaPopulation.LoadPoints(player.Layout.Id,player.Seed,[]).FirstOrDefault(p=>p.Name==TutorialObjects.Chest);
                if ((TutorialObjects.ChestPlacement(player.Layout.Id,player.Seed) ?? (named is null ? null : TutorialObjects.ChestAt(named))) is { } chest)
                {
                    player.TutorialChest = chest; player.TutorialChestId = player.TakeObjectId();
                    bool opened = CharacterSaves.Load(CharacterName(player))?.Tutorial?.ChestOpened == true;
                    await global::Game.SendWorldCreate(player, player.TutorialChestId, InstanceEntities.CreateObject(player.TutorialChestId,InstanceEntities.ObjectTypeHash(chest.Type),
                        TutorialObjects.ChestBlob(chest,opened)), $"0x02bb tutorial support chest at ({chest.X},{chest.Y}), opened={opened}");
                    await global::Game.SendWorldRecord(player, player.TutorialChestId, InstanceProtocol.EnterWakeZone(player.TutorialChestId),null);
                    await RegisterStationaryCollision(player,player.TutorialChestId,chest.Type,chest.X,chest.Y,0);
                    if (opened) await DropTutorialGem(player,true,chest.X,chest.Y);
                    return;
                }
                await Task.Delay(1000);
            }
            if (!player.Closed) Console.WriteLine($"[gateway] {player.Connection.Remote} tutorial chest has no captured placement for {player.Layout.Id} seed {player.Seed}; capture with -Walkmaps");
        }
        catch (Exception ex) when (ex is IOException or JsonException or ObjectDisposedException)
        { if (!player.Closed) Console.WriteLine($"[gateway] tutorial chest: {ex.Message}"); }
    }

    internal static async Task RunOpeningScene(Player player,uint id,SpawnedNpc npc,double biteAt,Task skipped)
    {
        try
        {
            double start=AreaTime(player);
            // Finish the selected voice line, then play the whole bite before handing over to the combat monster.
            // Use the paused instance clock for both waits.
            while (!player.Closed && !skipped.IsCompleted && AreaTime(player) < start+biteAt)
                await Task.Delay(50);
            await WhilePaused(player);
            if(player.Closed) return;
            double biteStarted = AreaTime(player);
            int component=npc.Npc.Components!.ToList().IndexOf("Transitionable");
            if(component>=0) await global::Game.SendWorldRecord(player, id, InstanceEntities.TransitionableUpdate(id,(byte)component,2),"0x02bc wounded exile bitten");
            if(player.TutorialSceneId!=0) await Send(player,TutorialObjects.BiteAnimation(player.TutorialSceneId,1),"0x01b2 opening zombie bites exile");
            while (!player.Closed && AreaTime(player) < biteStarted+TutorialObjects.BiteSeconds)
                await Task.Delay(50);
            if(player.Closed) return;
            if(player.TutorialSceneId!=0) await global::Game.SendWorldRemoval(player, player.TutorialSceneId,null);
            if(player.TalkingTo==id) { player.InNpcTalk=false;player.TalkingTo=0;await Send(player,InstanceNpcs.Talk(0),null); }
            await SpawnTutorialZombie(player);
        }
        catch(Exception ex) when(ex is IOException or ObjectDisposedException)
        { if(!player.Closed) Console.WriteLine($"[gateway] opening scene: {ex.Message}"); }
        finally { player.TutorialSceneRunning=false; player.TutorialDialogueSkipped=null; }
    }

    internal static async Task SpawnTutorialZombie(Player player)
    {
        if(Environment.GetEnvironmentVariable("Game_MONSTERS")=="0" || player.TutorialZombiePlacement is not { } spawn) return;
        if(player.Area.Monsters.Values.Any(m=>m.Variety?.Id==TutorialObjects.Zombie)) return;
        if(Monsters.Find(TutorialObjects.Zombie) is { Supported:true } zombie)
            await SpawnMonster(player,zombie,1,MonsterRarity.Normal,spawn.X,spawn.Y);
    }

    internal static GlyphQuest.Layout? LoadGlyphLayout(Player player)
    {
        try { return GlyphQuest.Load(player.Layout.Id, player.Seed); }
        catch (Exception ex) when (ex is IOException or JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            Console.WriteLine($"[gateway] {player.Connection.Remote} a1q4 capture: {ServerLog.Describe(ex)}");
            return null;
        }
    }

    internal static async Task CreateGlyphObjects(Player player, GlyphQuest.Layout layout)
    {
        try
        {
            player.Glyphs = layout;
            bool placed = ClientHasQuestFlag(player, GlyphQuest.PlacedGlyphs);
            if (!placed) SetPassageBlockers(player, true);
            else
                // Glyphs left on this instance's ground are not needed any more (their .it remove_flag).
                foreach (var (gid, _) in player.Instance.Ground.All().Where(e => GlyphIndex(e.Item.Item) is not null))
                    if (player.Instance.Ground.Take(gid) is not null)
                        await global::Game.SendWorldRemoval(player, gid, $"0x02bd remove glyph 0x{gid:x}: the glyphs are placed");
            for (int i = 0; i < layout.Nests.Count && i < 0x10; i++)
            {
                var nest = layout.Nests[i];
                bool opened = player.Instance.ChestOpened(nest.At.X, nest.At.Y);
                uint id = (opened ? OpenedNestFirstId : GlyphObjectFirstId) + (uint)i;
                lock (player.GlyphNests) player.GlyphNests[id] = nest;
                player.SetBlocker(id, nest.At.X, nest.At.Y, GlyphQuest.NestSize);
                await global::Game.SendWorldCreate(player, id, () => InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(nest.Type), GlyphQuest.NestBlob(nest.At, opened)),
                    $"0x02bb Rhoa Nest 0x{id:x} {nest.Type} at ({nest.At.X}, {nest.At.Y}) with glyph {nest.Glyph + 1}, opened={opened}", wake: true, awaitBirth: false);
            }
            if (layout.Wall is { } wall)
            {
                await global::Game.SendWorldCreate(player, GlyphWallObjectId, () => InstanceEntities.CreateObject(GlyphWallObjectId, InstanceEntities.ObjectTypeHash(GlyphQuest.Wall), GlyphQuest.WallBlob(wall, placed)),
                    $"0x02bb Glyph Wall 0x{GlyphWallObjectId:x} at ({wall.X}, {wall.Y}), {(placed ? "glyphs placed" : "waiting for the glyphs")}", wake: true, awaitBirth: false);
                player.GlyphWallId = GlyphWallObjectId;
            }
            if (layout.Water is { } water)
            {
                await global::Game.SendWorldCreate(player, GlyphWaterObjectId, () => InstanceEntities.CreateObject(GlyphWaterObjectId, InstanceEntities.ObjectTypeHash(GlyphQuest.Water), GlyphQuest.WaterBlob(water.X, water.Y, water.Rotation, placed)),
                    $"0x02bb waterfall 0x{GlyphWaterObjectId:x} at ({water.X}, {water.Y}), transitioned={placed}", wake: true, awaitBirth: false);
                player.GlyphWaterId = GlyphWaterObjectId;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or ObjectDisposedException or KeyNotFoundException or InvalidOperationException)
        { if (!player.Closed) Console.WriteLine($"[gateway] a1q4 objects: {ServerLog.Describe(ex)}"); }
    }

    // A nest's glyph on the instance's ground, unless the character no longer needs it (QuestItems.Drops: the glyphs are
    // placed, or a copy is carried or on this instance's ground): a glyph lost another way drops again.
    internal static Task DropGlyph(Player player, GlyphQuest.Nest nest) =>
        DropQuestItem(player, GlyphQuest.Glyphs[nest.Glyph], (nest.At.X, nest.At.Y), $"the Rhoa Nest at ({nest.At.X}, {nest.At.Y})");

    internal static async Task PlaceGlyphs(Player player)
    {
        var items = ItemsOf(player);
        List<(byte Row, InventoryItem Item)>? removed;
        if (HardcoreProgressBlocked(player)) return;
        removed = GlyphQuest.Place(CharacterName(player), items, GlyphIndex);
        if (removed is null)
        {
            Console.WriteLine($"[gateway] {player.Connection.Remote} Glyph Wall: needs all three glyphs");
            return;
        }
        foreach (var row in removed.GroupBy(r => r.Row))
            await Send(player, InstanceItems.InventoryChange(PlayerInventories.InventoryId(row.Key), items.NextRevision(row.Key), [.. row.Select(r => r.Item.Id)], []),
                $"0x00bf inventory {PlayerInventories.InventoryId(row.Key)}: glyphs {string.Join(", ", row.Select(r => r.Item.Id))} placed in the Glyph Wall");
        await SetQuestFlags(player, [GlyphQuest.PlacedGlyphs]);
        await SetQuestFlags(player, GlyphQuest.HaveGlyph, on: false);
    }



    internal static async Task DropTutorialGem(Player player,bool support,int x,int y)
    {
        foreach (var output in CommitTutorialGem(player,support,x,y)) await SendWorldReceipt(player, output);
    }

    internal static List<PlayerMinions.Output> CommitTutorialGem(Player player,bool support,int x,int y)
    {
        if (!LeagueFor(player).Rules.TutorialGifts && support) return [];
        if(CharacterSaves.Load(CharacterName(player))?.Tutorial is not { } tutorial || (support ? tutorial.SupportClaimed : tutorial.SkillClaimed)) return [];
        var item=CharacterSaves.FromSaved(support?tutorial.SupportGem:tutorial.SkillGem,Items);
        var ground=new WorldItemObject(x,y,item);
        if(player.Instance.PlaceTutorialItem(TutorialObjects.DropKey(CharacterName(player),tutorial,support),ground,player) is not uint id) return [];
        return [new(InstanceEntities.CreateObject(id,InstanceEntities.ObjectTypeHash(WorldItemObject.MetadataPath),ground.Blob(justDropped: true)),
            $"0x02bb tutorial {(support?"support":"skill")} gem {Items.GemName(item)} at ({x},{y})", WorldId: id, CreatesObject: true), new(InstanceProtocol.EnterWakeZone(id),null, WorldId: id)];
    }
}
