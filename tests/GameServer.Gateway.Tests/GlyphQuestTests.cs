using System.Text.Json;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

[Collection(TestSaves.FailWriteCollection)]
public class GlyphQuestTests : IDisposable
{
    const uint Glyph1 = 3228085585, Glyph2 = 1721197556, Glyph3 = 278505504, Other = 99;
    readonly string folder = TestSaves.Catalog(new TestSaves.Type(Glyph1, GlyphQuest.Glyphs[0]), new TestSaves.Type(Glyph2, GlyphQuest.Glyphs[1]),
        new TestSaves.Type(Glyph3, GlyphQuest.Glyphs[2], Height: 2), new TestSaves.Type(Other, "Metadata/Items/Other"));
    readonly string character = "Mud" + Guid.NewGuid().ToString("N")[..12];
    public GlyphQuestTests() => TestSaves.Ensure();
    public void Dispose() { Saves.FailWrite = null; Directory.Delete(folder, true); }

    PlayerInventories Inventory() => PlayerInventories.For(character, new ItemCatalog(folder));
    static int? GlyphOf(Item i) => i.TypeHash switch { Glyph1 => 0, Glyph2 => 1, Glyph3 => 2, _ => null };
    static Item Make(uint hash) => new(hash, ["Base"], 1);
    HashSet<string> Flags() => [.. CharacterSaves.Load(character)?.QuestFlags ?? []];

    // The Mud Flats seed 1 as the recorder captured it (data/walkability/1_1_3_seed1.objects.json).
    void WriteCapture(string area = "1_1_3", int seed = 1) => File.WriteAllText(Path.Combine(folder, $"{area}_seed{seed}.objects.json"), JsonSerializer.Serialize(new
    {
        area, seed,
        objects = new object[]
        {
            new { objectType = GlyphQuest.Wall, model = "Metadata/Terrain/Beach/Transitions/BeachToWatercave_v2/Beach_to_watercave_v2_stone_switch.ao",
                x = 829, y = 375, rotation = 4.71238911f, z = -165.61325f, code = 3 },
            new { objectType = GlyphQuest.NestTypes[0], model = "Metadata/Terrain/Beach/Features/beach_rhoanest_v01_01_chest.ao", x = 793, y = 563, rotation = 0f, z = -57.5458f, code = 0 },
            new { objectType = GlyphQuest.NestTypes[0], model = "Metadata/Terrain/Beach/Features/beach_rhoanest_v01_01_chest.ao", x = 425, y = 609, rotation = 4.712389f, z = -57.5458f, code = 3 },
            new { objectType = GlyphQuest.NestTypes[0], model = "Metadata/Terrain/Beach/Features/beach_rhoanest_v01_01_chest.ao", x = 678, y = 770, rotation = 1.570796f, z = -57.5458f, code = 1 },
            new { objectType = "Metadata/Chests/Other", model = "Metadata/Other.ao", x = 1, y = 1, rotation = 0f, z = 0f, code = 0 },
        },
    }));

    [Fact]
    public void CapturedNestsGetOneGlyphEachTheSameEveryTime()
    {
        WriteCapture();
        var layout = GlyphQuest.Load("1_1_3", 1, folder)!;
        Assert.Equal(3, layout.Nests.Count);
        Assert.Equal(new[] { 0, 1, 2 }, layout.Nests.Select(n => n.Glyph).Order());
        Assert.All(layout.Nests, n => Assert.Equal(GlyphQuest.NestTypes[n.Glyph], n.Type));
        Assert.Equal(layout.Nests.Select(n => (n.At.X, n.At.Y, n.Glyph)), GlyphQuest.Load("1_1_3", 1, folder)!.Nests.Select(n => (n.At.X, n.At.Y, n.Glyph)));
        Assert.Equal(-57.5458f, layout.Nests[0].At.Z!.Value, 3);
        Assert.Equal((829, 375, 3), (layout.Wall!.X, layout.Wall.Y, layout.Wall.Code));
    }

    [Fact]
    public void CaptureOfAnotherAreaOrSeedIsNotUsed()
    {
        WriteCapture("1_1_3", 2);
        Assert.Null(GlyphQuest.Load("1_1_3", 1, folder));
        WriteCapture("1_1_2", 1);
        Assert.Null(GlyphQuest.Load("1_1_2", 1, folder));
    }

    [Fact]
    public void CaveRoomObjectsArePlacedThroughTheWallsRecord()
    {
        WriteCapture();
        var layout = GlyphQuest.Load("1_1_3", 1, folder)!;
        // The captured client doodads of to_cave.arm's SunkenPassageBlockers (dead_seagull_v01_03.ao), seed 1.
        Assert.Equal(19, layout.Blockers.Count);
        Assert.Contains((819, 304), layout.Blockers);
        Assert.Contains((849, 346), layout.Blockers);
        Assert.Contains((873, 358), layout.Blockers);
        // The waterfall beside the cave's own waterfall doodad at (899, 343); the room's angle 0 turned by code 3.
        Assert.Equal((906, 342), (layout.Water!.Value.X, layout.Water.Value.Y));
        Assert.Equal(3 * MathF.PI / 2, layout.Water.Value.Rotation, 4);
        // Every code is a turn or flip of the offset from the wall.
        var wall = layout.Wall!;
        Assert.Equal((wall.X + 33, wall.Y + 77), GlyphQuest.FromRoom(wall with { Code = 0 }, (71, 147)));
        Assert.Equal((wall.X + 77, wall.Y + 33), GlyphQuest.FromRoom(wall with { Code = 7 }, (71, 147)));
    }

    [Fact]
    public void NestAndWallBlobsCarryHeightOpenedAndTransitionState()
    {
        var nest = new GlyphQuest.Placement(GlyphQuest.NestTypes[1], "Metadata/A.ao", 10, 20, 0, -57.5f, 0);
        byte[] closed = GlyphQuest.NestBlob(nest, false), opened = GlyphQuest.NestBlob(nest, true);
        // flags, base data, x, y, rotation, u16, flags0 0x04, flags1 0x02, height steps and z, two visibility bytes.
        Assert.Equal(0x04, closed[16]);
        Assert.Equal((int)MathF.Round(-57.5f / 7.8125f), BitConverter.ToInt32(closed, 18));
        int chest = 2 + 16 + 8 + 2 + 1 + 4 + 2 * "Metadata/A.ao".Length;
        Assert.Equal(0, closed[chest]);
        Assert.Equal(1, opened[chest]);
        // Chest 3, Stats 10, ObjectMagicProperties 4, InteractionAction, MinimapIcon, tasks.
        Assert.Equal(chest + 3 + 10 + 4 + 3, closed.Length);

        var wall = nest with { Type = GlyphQuest.Wall };
        byte[] waiting = GlyphQuest.WallBlob(wall, false), done = GlyphQuest.WallBlob(wall, true);
        Assert.Equal(chest + 4, waiting.Length);   // Transitionable, MinimapIcon, InteractionAction, tasks
        Assert.Equal(GlyphQuest.Untransitioned, waiting[chest]);
        Assert.Equal(GlyphQuest.Transitioned, done[chest]);
        byte[] water = GlyphQuest.WaterBlob(1, 2, 0, true);
        Assert.Equal(0, water[16]);   // terrain height
        Assert.Equal(GlyphQuest.Transitioned, water[^2]);
    }

    [Fact]
    public void TheSubmergedPassageIsClosedUntilTheGlyphsArePlaced()
    {
        Assert.Equal(GlyphQuest.PlacedGlyphs, Quests.FlagToEnter("1_1_3", "1_1_4_1"));
        Assert.Null(Quests.FlagToEnter("1_1_3", "1_1_3a"));
        Assert.Equal(1, GlyphQuest.GlyphOf("Metadata/Items/QuestItems/Glyphs/Glyph2"));
        Assert.Null(GlyphQuest.GlyphOf("Metadata/Items/Other"));
    }

    [Fact]
    public void PickingUpAGlyphSavesItsFlagsWithTheItem()
    {
        var inventory = Inventory();
        GlyphQuest.Claim(character, 2, () => inventory.Add(0, 0, 0, Make(Glyph3)));
        Assert.Superset(new HashSet<string> { GlyphQuest.KnowsAboutGlyphs, "a1q4-HaveGlyph3" }, Flags());
        Assert.DoesNotContain("a1q4-HaveGlyph1", Flags());
        Assert.Single(inventory.Items(0));
    }

    [Fact]
    public void TheWallTakesAllThreeGlyphsOrNothing()
    {
        var inventory = Inventory();
        inventory.Add(0, 0, 0, Make(Glyph1));
        inventory.Add(0, 1, 0, Make(Other));
        inventory.Add(0, 2, 0, Make(Glyph3));
        CharacterSaves.ChangeQuestFlags(character, ["a1q4-HaveGlyph1", "a1q4-HaveGlyph3"], true);
        Assert.Null(GlyphQuest.Place(character, inventory, GlyphOf));
        Assert.Equal(3, inventory.Items(0).Count);
        Assert.DoesNotContain(GlyphQuest.PlacedGlyphs, Flags());

        inventory.Add(PlayerInventories.Cursor, 0, 0, Make(Glyph2));
        var removed = GlyphQuest.Place(character, inventory, GlyphOf)!;
        Assert.Equal(3, removed.Count);
        Assert.Contains(removed, r => r.Row == PlayerInventories.Cursor);
        Assert.Equal(new[] { Other }, inventory.Items(0).Select(i => i.Item.TypeHash));
        Assert.Contains(GlyphQuest.PlacedGlyphs, Flags());
        Assert.DoesNotContain(Flags(), f => f.StartsWith("a1q4-HaveGlyph"));
        // As saved: a reload has the same items.
        PlayerInventories.Forget(character);
        Assert.Equal(new[] { Other }, Inventory().Items(0).Select(i => i.Item.TypeHash));
    }

    [Fact]
    public void AFailedPlacementKeepsTheGlyphsAndTheFlags()
    {
        var inventory = Inventory();
        inventory.Add(0, 0, 0, Make(Glyph1));
        inventory.Add(0, 1, 0, Make(Glyph2));
        inventory.Add(0, 2, 0, Make(Glyph3));
        CharacterSaves.ChangeQuestFlags(character, [.. GlyphQuest.HaveGlyph, GlyphQuest.KnowsAboutGlyphs], true);
        int writes = 0;
        // Three item removals, then the flag write fails.
        Saves.FailWrite = key => key == character && ++writes == 4;
        Assert.ThrowsAny<Exception>(() => GlyphQuest.Place(character, inventory, GlyphOf));
        Assert.Equal(4, writes);
        Saves.FailWrite = null;
        PlayerInventories.Forget(character);
        Assert.Equal(3, Inventory().Items(0).Count);
        Assert.DoesNotContain(GlyphQuest.PlacedGlyphs, Flags());
        Assert.Superset(new HashSet<string>(GlyphQuest.HaveGlyph), Flags());
    }

    [Fact]
    public void ASecondQuickUseOfTheWallFindsNothingAndDoesNotThrow()
    {
        var inventory = Inventory();
        inventory.Add(0, 0, 0, Make(Glyph1));
        inventory.Add(0, 1, 0, Make(Glyph2));
        inventory.Add(0, 2, 0, Make(Glyph3));
        Assert.NotNull(GlyphQuest.Place(character, inventory, GlyphOf));
        Assert.Null(GlyphQuest.Place(character, inventory, GlyphOf));
    }

    [Fact]
    public void AFailedPickupKeepsNeitherTheGlyphNorItsFlags()
    {
        var inventory = Inventory();
        int writes = 0;
        // The item's write, then the flags' write fails.
        Saves.FailWrite = key => key == character && ++writes == 2;
        Assert.ThrowsAny<Exception>(() => GlyphQuest.Claim(character, 0, () => inventory.Add(0, 0, 0, Make(Glyph1))));
        Saves.FailWrite = null;
        PlayerInventories.Forget(character);
        Assert.Empty(Inventory().Items(0));
        Assert.DoesNotContain(GlyphQuest.KnowsAboutGlyphs, Flags());
        Assert.DoesNotContain("a1q4-HaveGlyph1", Flags());
    }

    [Fact]
    public void AGlyphIsNotPickedUpOnceTheGlyphsArePlaced()
    {
        var inventory = Inventory();
        CharacterSaves.ChangeQuestFlags(character, [GlyphQuest.PlacedGlyphs], true);
        Assert.Null(GlyphQuest.Claim(character, 1, () => inventory.Add(0, 0, 0, Make(Glyph2))));
        Assert.Empty(inventory.Items(0));
        Assert.DoesNotContain("a1q4-HaveGlyph2", Flags());
        Assert.DoesNotContain(GlyphQuest.KnowsAboutGlyphs, Flags());
    }

    [Fact]
    public void ASecondCopyAddsNoFlagWrites()
    {
        var inventory = Inventory();
        GlyphQuest.Claim(character, 0, () => inventory.Add(0, 0, 0, Make(Glyph1)));
        int writes = 0;
        Saves.FailWrite = key => { if (key == character) writes++; return false; };
        Assert.NotNull(GlyphQuest.Claim(character, 0, () => inventory.Add(0, 1, 0, Make(Glyph1))));
        Saves.FailWrite = null;
        Assert.Equal(1, writes);   // only the item
        Assert.Equal(2, inventory.Items(0).Count);
    }

    [Fact]
    public void ANestDropsItsGlyphOnlyWhileNoCopyIsWithinReach()
    {
        var inventory = Inventory();
        var ground = new InstanceGround();
        int stashed = 0;
        int Stash(uint hash) => hash == Glyph2 ? stashed : 0;
        Assert.True(GlyphQuest.NestDrops(Glyph2, false, inventory, Stash, ground));
        Assert.False(GlyphQuest.NestDrops(Glyph2, true, inventory, Stash, ground));
        // Carried anywhere, the cursor included.
        var held = inventory.Add(PlayerInventories.Cursor, 0, 0, Make(Glyph2));
        Assert.False(GlyphQuest.NestDrops(Glyph2, false, inventory, Stash, ground));
        Assert.True(GlyphQuest.NestDrops(Glyph1, false, inventory, Stash, ground));
        inventory.Remove(PlayerInventories.Cursor, held.Id);
        // In the stash.
        stashed = 1;
        Assert.False(GlyphQuest.NestDrops(Glyph2, false, inventory, Stash, ground));
        stashed = 0;
        // On this instance's ground; once it is gone (picked up and lost, or dropped in another instance) it drops again.
        uint lying = ground.Add(new WorldItemObject(1, 2, Make(Glyph2)));
        Assert.False(GlyphQuest.NestDrops(Glyph2, false, inventory, Stash, ground));
        ground.Take(lying);
        Assert.True(GlyphQuest.NestDrops(Glyph2, false, inventory, Stash, ground));
    }

    [Fact]
    public void TheHaveGlyphFlagsFollowTheCarriedGlyphs()
    {
        var (set, clear) = GlyphQuest.SyncFlags(new HashSet<string> { "a1q4-HaveGlyph1", "a1q4-HaveGlyph2" }, false, new HashSet<int> { 1, 2 });
        Assert.Equal(new[] { "a1q4-HaveGlyph3", GlyphQuest.KnowsAboutGlyphs }, set);
        Assert.Equal(new[] { "a1q4-HaveGlyph1" }, clear);
        (set, clear) = GlyphQuest.SyncFlags(new HashSet<string> { GlyphQuest.KnowsAboutGlyphs, "a1q4-HaveGlyph3" }, false, new HashSet<int> { 2 });
        Assert.Empty(set);
        Assert.Empty(clear);
        // Placed: none held, whatever is carried.
        (set, clear) = GlyphQuest.SyncFlags(new HashSet<string> { GlyphQuest.PlacedGlyphs, "a1q4-HaveGlyph2" }, true, new HashSet<int> { 0 });
        Assert.Empty(set);
        Assert.Equal(new[] { "a1q4-HaveGlyph2" }, clear);
    }

    [Fact]
    public void TheClientsFlagsIncludeTheAccessFlagsAndOnlyRealChangesCount()
    {
        var rows = new Dictionary<string, int> { ["a1q4-PlacedGlyphs"] = 24, ["a1q1-KilledHillock"] = 9 };
        var own = new HashSet<string>();
        Assert.False(Quests.ClientHas("a1q4-PlacedGlyphs", own, [9, 30], rows));
        Assert.True(Quests.ClientHas("a1q4-PlacedGlyphs", own, [24], rows));   // the Submerged Passage's waypoint
        own.Add("a1q4-PlacedGlyphs");
        Assert.True(Quests.ClientHas("a1q4-PlacedGlyphs", own, [], rows));
        Assert.False(Quests.ClientHas("unknown", own, [24], rows));

        // Set for real while the waypoint already gave it: nothing to create again.
        var before = new Dictionary<string, bool> { ["a1q4-PlacedGlyphs"] = true, ["a1q1-KilledHillock"] = false };
        var after = new Dictionary<string, bool> { ["a1q4-PlacedGlyphs"] = true, ["a1q1-KilledHillock"] = true };
        Assert.Equal(new[] { "a1q1-KilledHillock" }, Quests.EffectiveChanges(["a1q4-PlacedGlyphs", "a1q1-KilledHillock"], f => before[f], f => after[f]));
        // Cleared (dev) while the waypoint still gives it: nothing to remove.
        Assert.Empty(Quests.EffectiveChanges(["a1q4-PlacedGlyphs"], _ => true, _ => true));
    }

    [Fact]
    public async Task QuestFlagReadsBesideWritesSeeWholeSets()
    {
        // SetQuestFlagsHeld changes conn.QuestFlags under the Monsters lock while the player tick (SyncGlyphs,
        // ClientHasQuestFlag, PlayerQuestFlags) reads it: through QuestFlagsSnapshot/HasQuestFlag the reads never throw
        // "Collection was modified" and never see half a change (each write swaps one flag for another).
        var conn = new ClientState(new AreaLayout(1, "test", 200, 200, 10, 10, null, []), 1, 1);
        lock (conn.Monsters) for (int i = 0; i < 50; i++) conn.QuestFlags.Add($"f{i}");
        var until = DateTime.UtcNow.AddMilliseconds(400);
        int reads = 0, torn = 0;
        var writer = Task.Run(() =>
        {
            for (int i = 0; DateTime.UtcNow < until; i++)
                lock (conn.Monsters)
                {
                    conn.QuestFlags.Remove($"f{i % 100}");
                    conn.QuestFlags.Add($"f{(i + 50) % 100}");
                }
        });
        var readers = Enumerable.Range(0, 3).Select(_ => Task.Run(() =>
        {
            while (DateTime.UtcNow < until)
            {
                var snapshot = conn.QuestFlagsSnapshot();
                if (snapshot.Count(f => f.StartsWith('f')) != 50) Interlocked.Increment(ref torn);
                conn.HasQuestFlag("f7");
                Interlocked.Increment(ref reads);
            }
        })).ToArray();
        await Task.WhenAll([writer, .. readers]);
        Assert.True(reads > 0);
        Assert.Equal(0, torn);
    }

    [Fact]
    public async Task TheQuestGateLetsOneChangeThroughAtATime()
    {
        var conn = new ClientState(new AreaLayout(1, "test", 200, 200, 10, 10, null, []), 1, 1);
        int inside = 0, most = 0;
        async Task Change()
        {
            await conn.QuestGate.WaitAsync();
            try
            {
                int now = Interlocked.Increment(ref inside);
                most = Math.Max(most, now);
                await Task.Delay(5);
                Interlocked.Decrement(ref inside);
            }
            finally { conn.QuestGate.Release(); }
        }
        await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(Change)));
        Assert.Equal(1, most);
    }

    [Fact]
    public void AQuestChestOpensOncePerInstance()
    {
        var instance = new InstanceState();
        Assert.False(instance.ChestOpened(5, 6));
        Assert.True(instance.OpenChest(5, 6));
        Assert.False(instance.OpenChest(5, 6));
        Assert.True(instance.ChestOpened(5, 6));
        Assert.False(new InstanceState().ChestOpened(5, 6));
    }
}
