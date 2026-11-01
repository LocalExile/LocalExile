using GameServer.Core.Protocol.Instance;
using GameServer.Data.Terrain.Generation;
using GameServer.Modules.M_questobjects;
using static Act1Areas;
using static Act2Areas;
using static Act3Areas;
using static Act4Areas;
using static Act5Areas;
using static Act6Areas;
using static Quests;

namespace GameServer.Gateway.Tests;

/// <summary>
/// The quest engine (Agents/docs/game/campaign-quests.md, 12-quests.md §5): its generic parts read from the client's data,
/// and the campaign oracle, which plays an act's sequence of game events through the engine (<see cref="QuestSimulator"/>:
/// the act tables the QuestObjects module registers, NPCTalk, the quest items' flags, the exits and the client's own area
/// gates). After the act the character's flags must contain CharacterStartQuestState's preset for it and every quest of the
/// preset must reach its QuestStates "complete" row. A later act adds its sequence after <see cref="Act1"/> and its preset.
/// </summary>
public sealed class QuestEngineTests
{
    static readonly ItemCatalog Items = Program.Items;
    static QuestItemFlags Flags(string item) => Items.Find(item)!.Quest!;
    static Func<string, bool> With(params string[] flags) => flags.ToHashSet().Contains;

    const string Tarkleigh = "Metadata/NPC/Act1/Tarkleigh", Nessa = "Metadata/NPC/Act1/Nessa", Bestel = "Metadata/NPC/Act1/Bestel",
        Fairgraves = "Metadata/NPC/Act1/Fairgraves";
    const string BookA1q6 = "Metadata/Items/QuestItems/SkillBooks/Book-a1q6", BookA1q7 = "Metadata/Items/QuestItems/SkillBooks/Book-a1q7",
        BookA1q8 = "Metadata/Items/QuestItems/SkillBooks/Book-a1q8";
    const string BookA1q9 = "Metadata/Items/QuestItems/SkillBooks/Book-a1q9", BookA2q5 = "Metadata/Items/QuestItems/SkillBooks/Book-a2q5";
    const string Eramir = "Metadata/NPC/Act2/Eramir", Yeena = "Metadata/NPC/Act2/Yeena", Greust = "Metadata/NPC/Act2/Greust",
        Silk = "Metadata/NPC/Act2/Silk", Helena = "Metadata/NPC/Act2/HelenaTown";
    const string BookA3q11 = "Metadata/Items/QuestItems/SkillBooks/Book-a3q11v2", BookA3q9 = "Metadata/Items/QuestItems/SkillBooks/Book-a3q9";
    const string Maramoa = "Metadata/NPC/Act3/Maramoa", Clarissa = "Metadata/NPC/Act3/ClarissaTown", Grigor = "Metadata/NPC/Act3/Grigor",
        Hargan = "Metadata/NPC/Act3/Hargan", Dialla = "Metadata/NPC/Act3/Dialla", Siosa = "Metadata/NPC/Act3/Siosa",
        FairgravesDocks = "Metadata/NPC/Act3/Fairgraves", DiallaTower = "Metadata/NPC/Act3/DiallaTower";
    const string Oyun = "Metadata/NPC/Act4/Oyun", Tasuni = "Metadata/NPC/Act4/Tasuni", BookA4q6 = "Metadata/Items/QuestItems/Act4/Book-a4q6";
    const string Lani = "Metadata/NPC/Act5/Lani", Vilenta = "Metadata/NPC/Act5/Vilenta", UtulaTown = "Metadata/NPC/Act5/Utula",
        BookA5q3 = "Metadata/Items/QuestItems/Act5/Book-a5q3", BookA5q7 = "Metadata/Items/QuestItems/Act5/Book-a5q7";
    const string Tarkleigh6 = "Metadata/NPC/Act6/Tarkleigh", Bestel6 = "Metadata/NPC/Act6/Bestel", Lilly6 = "Metadata/NPC/Act6/Lilly",
        SinTown6 = "Metadata/NPC/Act6/SinTown";
    const string BookA6q3 = "Metadata/Items/QuestItems/Act6/Book-a6q3", BookA6q4 = "Metadata/Items/QuestItems/Act6/Book-a6q4",
        BookA6q6 = "Metadata/Items/QuestItems/Act6/Book-a6q6", BookA6q7 = "Metadata/Items/QuestItems/Act6/Book-a6q7";
    const string Eramir7 = "Metadata/NPC/Act7/Eramir", Helena7 = "Metadata/NPC/Act7/Helena", Yeena7 = "Metadata/NPC/Act7/YeenaTown",
        Sin7 = "Metadata/NPC/Act7/SinTown", Weylam7 = "Metadata/NPC/Act7/Weylam";
    const string Hargan8 = "Metadata/NPC/Act8/Hargan", Maramoa8 = "Metadata/NPC/Act8/Maramoa", Clarissa8 = "Metadata/NPC/Act8/ClarissaTown",
        Sin8 = "Metadata/NPC/Act8/SinTown";
    const string PetarusVanja9 = "Metadata/NPC/Act9/PetarusVanja", Irasha9 = "Metadata/NPC/Act9/Irasha", Sin9 = "Metadata/NPC/Act9/SinTown";
    const string Lani10 = "Metadata/NPC/Act10/Lani", Weylam10 = "Metadata/NPC/Act10/Weylam", Lilly10 = "Metadata/NPC/Act10/Lilly";
    static string Book(int act, string quest) => $"Metadata/Items/QuestItems/Act{act}/Book-{quest}";

    /// <summary>The engine's data-driven parts: quest items by their .it Quest component (flags, drops, the carried sync,
    /// hand-ins, pickups), QuestStaticRewards (passive, refund and ascendancy points, stats; refunds credited once in the
    /// save, session flags never saved), NPCTalk scripts, reward offers without a taken flag, the generic quest object blob,
    /// the act registry and its league scoping, the world pieces the rules use (quest portals replacing an exit without a
    /// model, later forms' boss points, an exit's own destination in its act only), and every rule of every registered act
    /// naming real data.</summary>
    [Fact]
    public void The_engine_reads_quest_items_rewards_dialogue_and_objects_from_the_data()
    {
        using var registered = Register("QuestObjects", QuestObjectsModule.Acts());

        // Quest items: the .it Quest component's grant, extra, remove and use flags.
        Assert.Equal(new QuestItemFlags("a1q5-HaveMedicineChest", null, "a1q5-NessaCongratulated", null), Flags(Act1Items.MedicineChest));
        Assert.Equal(new QuestItemFlags("a1q6-HaveAllFlame", null, "a1q6-DeliveredAllFlame", null), Flags(Act1Items.Allflame));
        Assert.Equal(new QuestItemFlags("a1q4-HaveGlyph2", "a1q4-KnowsAboutGlyphs", "a1q4-PlacedGlyphs", null), Flags(GlyphQuest.Glyphs[1]));
        Assert.Equal(new QuestItemFlags("a1q8-HaveRewardBook", null, "a1q8-UsedRewardBook", "a1q8-UsedRewardBook"), Flags(BookA1q8));
        // "minus" stands for "-" only where the literal name is no flag (Act 2's Thaumetic Emblem).
        Assert.Equal("a1q9-RoadBlocked", Flags("Metadata/Items/QuestItems/SpikeSealKey").Extra);
        Assert.Null(Items.Find("Metadata/Items/Currency/CurrencyIdentification")!.Quest);

        // A quest item drops until one is carried or on the ground, or its remove_flag is set (the stash does not count).
        var chest = Flags(Act1Items.MedicineChest);
        Assert.True(QuestItems.Drops(chest, With(), carried: false, onGround: false));
        Assert.False(QuestItems.Drops(chest, With(), carried: true, onGround: false));
        Assert.False(QuestItems.Drops(chest, With(), carried: false, onGround: true));
        Assert.False(QuestItems.Drops(chest, With("a1q5-NessaCongratulated"), carried: false, onGround: false));

        // Grant flags follow the items carried, the extra flag stays, done items go with their grant flag.
        var types = Items.QuestItemTypes.Select(t => t.Quest!).ToList();
        var (set, clear, remove) = QuestItems.Sync(types, new HashSet<QuestItemFlags> { Flags(GlyphQuest.Glyphs[0]) }, With());
        Assert.Equal(["a1q4-HaveGlyph1", "a1q4-KnowsAboutGlyphs"], set.Order());
        Assert.Empty(clear);
        Assert.Empty(remove);
        (set, clear, _) = QuestItems.Sync(types, new HashSet<QuestItemFlags>(), With("a1q4-HaveGlyph1", "a1q4-KnowsAboutGlyphs"));
        Assert.Equal(["a1q4-HaveGlyph1"], clear);
        Assert.Empty(set);
        (_, clear, remove) = QuestItems.Sync(types, new HashSet<QuestItemFlags> { chest }, With("a1q5-NessaCongratulated", "a1q5-HaveMedicineChest"));
        Assert.Equal([chest], remove);
        Assert.Equal(["a1q5-HaveMedicineChest"], clear);

        // A row that sets an item's remove_flag hands it in; pickups set the grant and extra flags until the item is done.
        Assert.Equal(chest, QuestItems.HandedIn("a1q5-NessaCongratulated", types, With()));
        Assert.Equal(Flags(Act1Items.Allflame), QuestItems.HandedIn("a1q6-DeliveredAllFlame", types, With()));
        Assert.Null(QuestItems.HandedIn("a1q6-DeliveredAllFlame", types, With("a1q6-DeliveredAllFlame")));
        Assert.Null(QuestItems.HandedIn("a1q5-ReceivedQuest", types, With()));
        Assert.Equal(["a1q4-HaveGlyph3", "a1q4-KnowsAboutGlyphs"], QuestItems.PickupFlags(Flags(GlyphQuest.Glyphs[2]), With()));
        Assert.Empty(QuestItems.PickupFlags(Flags(GlyphQuest.Glyphs[2]), With("a1q4-PlacedGlyphs")));

        // QuestStaticRewards: the books' points, the bandits' stats, the Labyrinth's ascendancy points.
        var rewards = QuestStaticRewards.Default.Value;
        Assert.Equal(3, rewards.PassivePoints(["a1q6-UsedRewardBook", "a1q7-UsedRewardBook", "a1q9-UsedRewardBook", "a1q8-UsedRewardBook"]));
        var (points, credited) = rewards.NewRefunds(["a1q8-UsedRewardBook", "a1q7-UsedRewardBook"], []);
        Assert.Equal(2, points);
        Assert.Equal(["a1q8-UsedRewardBook"], credited);
        Assert.Equal(0, rewards.NewRefunds(["a1q8-UsedRewardBook"], ["a1q8-UsedRewardBook"]).Points);
        Assert.Equal(new Dictionary<string, int> { ["base_movement_velocity_+%"] = 8 }, rewards.Stats(["a2q1-QuestRewardTaken"]));
        Assert.Empty(rewards.Stats([EndGameAvailable]));   // every character's flag, Kitava's penalty row: no stats
        Assert.Equal(2, rewards.AscendancyPoints(["LabyrinthComplete1"]));

        // In the save: refund points credited once, session flags never saved.
        string path = Path.Combine(Path.GetTempPath(), $"localexile-quests-{Guid.NewGuid():N}.db");
        Saves.Open(path);
        try
        {
            CharacterSaves.ChangeQuestFlags("QuestEngine", ["a1q8-UsedRewardBook", "a1q6-FairgravesAggro"], true);
            var save = CharacterSaves.Load("QuestEngine")!;
            Assert.Equal(2, save.RefundPoints ?? 0);
            Assert.DoesNotContain("a1q6-FairgravesAggro", save.QuestFlags!);
            CharacterSaves.ChangeQuestFlags("QuestEngine", ["a1q8-UsedRewardBook"], false);
            CharacterSaves.ChangeQuestFlags("QuestEngine", ["a1q8-UsedRewardBook"], true);
            Assert.Equal(2, CharacterSaves.Load("QuestEngine")!.RefundPoints ?? 0);
        }
        finally
        {
            Saves.Db.Dispose();
            foreach (string suffix in new[] { "", "-wal", "-shm" }) File.Delete(path + suffix);
        }

        // NPCTalk scripts: Fairgraves' rows set, clear, transition him into the boss; an event's flag is not a row's.
        var npcs = NpcCatalog.Load();
        var rows = npcs.TalksOf(npcs.Find(Fairgraves)!).ToList();
        var marooned = rows.Single(t => t.Option == "Marooned");
        Assert.Contains("a1q6-ReceivedQuest", marooned.ScriptSets);
        Assert.Equal(["a1q6-DeliveredAllFlame"], marooned.ScriptClears);
        var thanks = rows.First(t => t.Option == "Thank You");
        Assert.Contains("a1q6-FairgravesAggro", thanks.ScriptSets);
        Assert.Equal([Act1Monsters.Fairgraves], thanks.TransitionsVarieties);
        Assert.True(QuestStates.Default.Value.IsSession("a1q6-FairgravesAggro"));
        Assert.Equal("a1q6-DeliveredAllFlame", TalkStartFlag(thanks));
        Assert.Null(TalkStartFlag(rows.Single(t => t.Option == "Thank You" && t.StartFlag == "a1q6-KilledFairgraves")));
        Assert.Equal(["a2q7-QuestRewardTaken", "a2b-DeliveredAmulets"],
            npcs.TalksOf(npcs.Find("Metadata/NPC/Act2/Eramir")!).First(t => t.Row == 95).ScriptSets);
        // Several quest items to their NPC in one click: Fairgraves' "A Swig of Hope" row 496 (both carried) takes the plum
        // and the decanter, his quest row 493 nothing; Hargan's "Victario's Holdfasts" every bust carried.
        Assert.Equal([Act3Items.Plum, Act3Items.Decanter], HandsIn(Act3Objects.Fairgraves, "A Swig of Hope", 496));
        Assert.Empty(HandsIn(Act3Objects.Fairgraves, "A Swig of Hope", 493));
        Assert.Equal(Act3Items.Busts, HandsIn(Act3Objects.Hargan, "Victario's Holdfasts", 355));
        // Piety's "Malachai's Organs" (rows 624-627, one text) takes every organ carried.
        Assert.Equal([Act4Items.Lungs, Act4Items.Heart, Act4Items.Entrails], HandsIn(Act4Npcs.PietyHarvest, "Malachai's Organs", 624));

        // A book offer has no taken flag: its book's flags tell it taken.
        var offers = QuestRewardCatalog.Load();
        var dweller = offers.Find(6)!;
        Assert.Null(dweller.TakenFlag);
        var book = Items.Row(dweller.Choices.Single().ItemRow)!;
        Assert.Equal(BookA1q7, book.Id);
        Assert.False(QuestRewardCatalog.Taken(null, book.Quest, With("a1q7-Congratulated")));
        Assert.True(QuestRewardCatalog.Taken(null, book.Quest, With("a1q7-HaveRewardBook")));
        Assert.True(QuestRewardCatalog.Taken(null, book.Quest, With("a1q7-UsedRewardBook")));
        Assert.Equal("a1q2-NessaRewardTaken", offers.Find(8)!.TakenFlag);

        // The generic quest object blob writes the two objects traced live (a1q4's nest and wall); a chest without an .ot
        // is its Chests.dat row.
        var at = new GlyphQuest.Placement(GlyphQuest.NestTypes[0], "Metadata/Terrain/Beach/x.ao", 100, 200, 1.5f, -12.5f, 0);
        var nest = QuestObjectTypes.Find(GlyphQuest.NestTypes[0])!;
        Assert.Equal(GlyphQuest.NestBlob(at, true), new QuestObject(at.X, at.Y, nest.Components, at.Rotation, at.Z, at.Model, Opened: true).Blob());
        var wall = QuestObjectTypes.Find(GlyphQuest.Wall)!;
        Assert.Equal(GlyphQuest.WallBlob(at with { Type = GlyphQuest.Wall }, true),
            new QuestObject(at.X, at.Y, wall.Components, at.Rotation, at.Z, at.Model, TransitionState: 2).Blob());
        Assert.Equal((2, 7), (wall.States, wall.Size));
        var altar = QuestObjectTypes.Find("Metadata/Chests/QuestChests/ChurchAltar")!;
        Assert.Equal(QuestObjectTypes.Find("Metadata/Chests/QuestChests/QuestChest")!.Components, altar.Components);

        // The registry: the module's acts, in order, until the registration goes; they run only for a league that lists
        // the module that gave them.
        Assert.Equal([1, 2, 3, 4, 5, 6, 7, 8, 9, 10], QuestObjectsModule.Acts().Select(a => a.Number));
        Assert.Equal(["a1q1", "a1q5", "a1q4", "a1q8", "a1q7", "a1q2", "a1q6", "a1q3", "a1q9"], All.Take(9).Select(q => q.Id));
        Assert.Equal(["a2q11", "a2q10", "a2q6", "a2q4", "a2q5", "a2q9", "a2q7", "a2q8"], All.Where(q => q.Id.StartsWith("a2")).Select(q => q.Id));
        Assert.Equal(["a3q1", "a3q2", "a3q11", "a3q3", "a3q4", "a3q5", "a3q8", "a3q12", "a3q13", "a3q9", "a3q10"],
            All.Where(q => q.Id.StartsWith("a3")).Select(q => q.Id));
        Assert.Equal(["a4q1", "a4q2", "a4q3", "a4q4", "a4q5", "a4q6", "a5q1a", "a5q1b", "a5q2", "a5q3", "a5q4", "a5q5", "a5q6", "a5q7",
            "a6q1", "a6q2", "a6q3", "a6q4", "a6q5", "a6q6", "a6q7"], All.Where(q => q.Id is ['a', '4' or '5' or '6', ..]).Select(q => q.Id));
        Assert.Equal(["a7q1", "a7q2", "a7q3", "a7q4", "a7q5", "a7q6", "a7q7", "a7q8", "a7q9", "a8q1", "a8q2", "a8q3", "a8q4", "a8q5", "a8q6", "a8q7",
            "a9q1", "a9q2", "a9q3", "a9q4", "a9q5", "a10q1", "a10q2", "a10q3", "a10q4", "a10q5", "a10q6"],
            All.SkipWhile(q => q.Id != "a7q1").Select(q => q.Id));
        // Kitava in The Feeding Trough never dies (set_cannot_die): the hit that would kill him ends the fight at a threshold
        // and earns EndGameAvailable; no kill flag.
        Assert.Equal([EndGameAvailable], ThresholdOf(Act10Areas.FeedingTrough, Act10Monsters.Kitava)!.Flags);
        Assert.Empty(OnMonsterKilled(Act10Areas.FeedingTrough, Act10Monsters.Kitava));
        int own = All.Count;
        Assert.Same(Everything, For(new HashSet<string> { "Game", "QuestObjects" }));
        Assert.True(For(new HashSet<string> { "Game" }).IsEmpty);
        Assert.Empty(For(new HashSet<string> { "Game" }).OnMonsterKilled(Strand, Act1Monsters.Hillock));
        Assert.Equal("QuestObjects", Everything.SourceOf(Everything.ExitOf(Strand, LioneyesWatch)!));
        using (Register("Other", [new Act1()]))
        {
            // Each source on its own: a league runs only the tables of the modules it lists.
            Assert.True(For(new HashSet<string> { "Other" }).Includes("Other") && !For(new HashSet<string> { "Other" }).Includes("QuestObjects"));
            Assert.Equal(own + 9, All.Count);
        }
        using (Register("QuestObjects", []))
        {
            Assert.Empty(All);
            Assert.True(For(new HashSet<string> { "QuestObjects" }).IsEmpty);
        }
        Assert.Empty(All);
        registered.Dispose();
        using var again = Register("QuestObjects", QuestObjectsModule.Acts());

        // World pieces the rules use. A disabled entrance (a room point "entranceNdisabled" the game enables by script) is
        // created only when a quest gates it, marked as the quest's (it stands only where those tables run); one no quest
        // gates stays an arrival point. A quest portal marks the layout's exit without a model to its destination (not a
        // modelled one, a gate or a quest's own exit) as replaced: it stands only where the tables do not run. A later
        // form stands at its numbered boss point.
        const ushort strand = 0x1111, town = 0x2222, coast = 0x3333;
        AreaTransitionCatalog.AtlasConnection[] connections = [new(1, LioneyesWatch, town), new(2, Coast, coast)];
        PlacedExit Disabled(int number, int x) => new(new("Metadata/MiscellaneousObjects/AreaTransition", $"exit{number}.ao", x, 100, 0, float.NaN),
            ExitKind.Entrance, number, Enabled: false, Label: null, Target: null, Room: "room.arm", Why: "test");
        var gated = Assert.Single(AreaTransitionCatalog.FromExits(Strand, strand, connections, [Disabled(1, 100), Disabled(2, 300)], []));
        Assert.Equal((town, 100, 100, "QuestObjects", false), (gated.DestinationHash16, gated.GridX, gated.GridY, gated.QuestSource, gated.QuestReplaced));
        var exits = new List<AreaTransitionObject>
        {
            new(10, 10, 0x1234), new(20, 20, 0x1234, "Metadata/x.ao"), new(30, 30, 0x1234) { SourceType = "Metadata/QuestObjects/Portal" },
            new(40, 40, 0x5678),
        };
        Assert.Equal(1, AreaTransitionCatalog.ReplacePointExits(exits, 0x1234, "QuestObjects"));
        Assert.Equal([true, false, false, false], exits.Select(t => t.QuestReplaced && t.QuestSource == "QuestObjects"));
        var trinity = new PackCatalog.AreaPacks("2_9_8", 60, [], new Dictionary<string, PackCatalog.Pack>(), [7, 8]) { Absent = new HashSet<int> { 1 } };
        Assert.Equal(new Dictionary<int, (int X, int Y)> { [8] = (50, 60) },
            AreaPopulation.LaterFormPoints(trinity, [new("mapboss1", 10, 10), new("mapboss2", 50, 60)], (_, _) => true));
        // A quest portal beside its object stands on floor reached from the object: 12 cells along its turned +X, else the
        // next direction round it that has floor (a wall 6 cells east of the object at (50, 50)).
        var floor = new byte[100 * 100];
        for (int cy = 0; cy < 100; cy++)
            for (int cx = 0; cx < 100; cx++) floor[cy * 100 + cx] = (byte)(cx < 56 ? 2 : 0);
        Assert.Equal((50, 62), AreaTransitionCatalog.PortalCell(floor, 100, 100, 50, 50, MathF.PI / 2));
        var (wx, wy) = AreaTransitionCatalog.PortalCell(floor, 100, 100, 50, 50, 0);
        Assert.True(wx < 55 && floor[wy * 100 + wx] == 2 && Math.Sqrt((wx - 50) * (wx - 50) + (wy - 50) * (wy - 50)) >= 8);

        // The Pantheon (campaign/pantheon.md): PantheonPanelLayout's 4 major and 8 minor gods (rows 4, 5, 7 and 12 are
        // disabled placeholders), each unlocked by its boss kill; a god's power, its upgrades only with their soul after
        // An End to Hunger; C→S 0x1ee framed (live: unframed, the god was never chosen) as the row + 1, which the Player
        // component's update (flag 0x0080) and creation blob (two bytes after WashedUp: major, minor) carry back.
        var pantheon = PantheonCatalog.Default.Value;
        Assert.Equal((16, 4, 8), (pantheon.Gods.Count, pantheon.Gods.Count(g => g.Major && !g.Disabled), pantheon.Gods.Count(g => !g.Major && !g.Disabled)));
        Assert.All(new[] { 4, 5, 7, 12 }, row => Assert.True(pantheon.Gods[row].Disabled));
        var brine = pantheon.ById("TheBrineKing")!;
        Assert.Equal((0, true, "a6q1-KilledBoss", (byte)1), (brine.Row, brine.Major, brine.UnlockFlag, brine.Wire));
        var tukohama = pantheon.ById("Tukohama")!;
        Assert.Equal((11, false, "a6q3-KilledBoss", (byte)12), (tukohama.Row, tukohama.Major, tukohama.UnlockFlag, pantheon.Wire("Tukohama")));
        Assert.Same(tukohama, pantheon.ByWire(12));
        Assert.Null(pantheon.ByWire(0));
        Assert.Equal(0, pantheon.Wire(null));
        Assert.False(PantheonCatalog.Unlocked(brine, With()));
        Assert.True(PantheonCatalog.Unlocked(brine, With("a6q1-KilledBoss")));
        Assert.False(PantheonCatalog.Unlocked(pantheon.Gods[4], _ => true));
        var statKeys = new StatCatalog();
        Assert.Equal([(statKeys.Key("cannot_be_stunned_if_have_been_stunned_or_blocked_stunning_hit_in_past_2_seconds")!.Value, 1)],
            PantheonCatalog.Stats(brine, With(), endgame: false).ToList());
        var stun = (statKeys.Key("base_stun_recovery_+%")!.Value, 30);
        Assert.Equal("BrineKingUpgrade1", brine.Upgrades[0].Flag);
        // What the module's stat sources ask (ChosenStats): a character logs in with EndGameAvailable given, not earned
        // (Quests.EveryCharacter, ClientState.UnearnedQuestFlags; until earned not among its flags, Quests.EndgameOnly), so
        // its soul's upgrade waits for An End to Hunger.
        var chosen = new ClientState(new AreaLayout(0x1234, "1_1_3", 200, 200, 70, 70, null, []), 1, 1) { PantheonMajor = "TheBrineKing" };
        chosen.QuestFlags = [.. Quests.EveryCharacter.Except(Quests.EndgameOnly), "a6q1-KilledBoss", "BrineKingUpgrade1"];
        chosen.UnearnedQuestFlags = [.. Quests.EveryCharacter];
        Assert.True(!chosen.HasQuestFlag(EndGameAvailable) && !chosen.EarnedQuestFlag(EndGameAvailable));
        Assert.Equal(PantheonCatalog.Stats(brine, With(), endgame: false), pantheon.ChosenStats(chosen, chosen.HasQuestFlag));
        Assert.DoesNotContain(stun, pantheon.ChosenStats(chosen, chosen.HasQuestFlag));
        // Kitava's death in The Feeding Trough sets it (Game.CommitQuestFlags: the flag saved, no longer unearned).
        chosen.QuestFlags.Add(EndGameAvailable);
        chosen.UnearnedQuestFlags.Remove(EndGameAvailable);
        Assert.Contains(stun, pantheon.ChosenStats(chosen, chosen.HasQuestFlag));
        chosen.QuestFlags.Remove("a6q1-KilledBoss");          // a god whose unlock flag is gone grants nothing
        Assert.Empty(pantheon.ChosenStats(chosen, chosen.HasQuestFlag));
        Assert.Equal(3, InstanceProtocol.KnownClientLength(InstancePantheon.ChooseGodId));
        Assert.Equal([0x01, 0xee, 12], InstancePantheon.ChooseGod(12));
        Assert.Equal(12, InstancePantheon.ReadChooseGod(InstancePantheon.ChooseGod(12)));
        Assert.Equal([0x80, 0x00, 1, 12], InstanceEntities.PlayerPantheonUpdate(1, 1, 12)[^4..]);
        var noGods = new PlayerObject("Exile", 3, 10, 10, 50, 40).Blob();
        var gods = (new PlayerObject("Exile", 3, 10, 10, 50, 40) { PantheonMajor = 1, PantheonMinor = 12 }).Blob();
        var differ = Enumerable.Range(0, noGods.Length).Where(i => noGods[i] != gods[i]).ToList();
        Assert.Equal((noGods.Length, 2), (gods.Length, differ.Count));
        Assert.Equal((differ[0] + 1, (byte)1, (byte)12), (differ[1], gods[differ[0]], gods[differ[1]]));

        // Every rule names real flags, monsters, items and NPCs, and the objects the engine creates are ones it can write.
        var flags = ParseRows(File.ReadAllText("data/tables/QuestFlags.json"));
        var monsters = new MonsterCatalog();
        foreach (var rule in All.SelectMany(q => q.Rules))
        {
            IEnumerable<string> named = rule switch
            {
                KillRule r => r.Flags, SeenRule r => r.Flags, NearRule r => r.Flags.Concat(r.Needs ?? []), NearExitRule r => r.Flags,
                EnteredRule r => r.Flags, UsedRule r => r.Flags.Concat(r.Needs ?? []), ClearedRule r => r.Flags.Append(r.After ?? r.Flags[0]),
                ExitRule r => [r.Flag], NpcRule r => new[] { r.GoneWith, r.TransitionedBy, r.ShownBy }.OfType<string>(),
                BlockerRule r => [r.UnblockedBy], WallRule r => [r.RemovedBy], ObjectRule r => r.TransitionedBy is { } t ? [t] : [],
                ChestRule r => r.OpensWith is { } o ? [o] : [], ExistsRule r => new[] { r.From, r.Until }.OfType<string>(),
                AppearsRule r => r.GoneWith is { } gone ? [gone] : [], ThresholdRule r => r.Flags, KilledAllRule r => r.Flags,
                TalkRowRule r => r.Flags, NearSpawnRule r => r.Until is { } u ? [u] : [], _ => [],
            };
            Assert.All(named.Where(f => !IsInstanceFlag(f)), f => Assert.True(flags.ContainsKey(f), $"{rule}: no flag {f}"));
            string? variety = rule switch
            {
                KillRule r => r.Variety, SeenRule r => r.Variety, DropRule r => r.Variety, LevelRule r => r.Variety, TurnsIntoRule r => r.Variety,
                SecondFormRule r => r.Next, FormsRule r => r.Next, SpawnRule r => r.Variety, AppearsRule r => r.Variety,
                ThresholdRule r => r.Variety, NearSpawnRule r => r.Variety, _ => null,
            };
            if (variety is not null) Assert.True(monsters.Find(variety)?.Id == variety, $"{rule}: no monster {variety}");
            string? item = rule switch { DropRule r => r.Item, ChestRule r => r.Item, HandInRule r => r.Item, _ => null };
            if (item is not null) Assert.True(Items.Find(item)?.Id == item, $"{rule}: no item {item}");
            string? npc = rule switch { NpcRule r => r.NpcType, AppearsRule r => r.NpcType, TurnsIntoRule r => r.NpcType, _ => null };
            if (npc is not null) Assert.True(npcs.Find(npc) is { Supported: true } n && n.Id == npc, $"{rule}: no supported NPC {npc}");
            string? created = rule switch { ObjectRule r => r.ObjectType, ChestRule r => r.ChestType, SpawnRule r => r.ObjectType, _ => null };
            if (created is not null)
                Assert.True(QuestObjectTypes.Find(created) is { } info && QuestObject.CanSerialize(info.Components),
                    $"{rule}: {created} is not a quest object the engine can create ({string.Join(", ", QuestObjectTypes.Find(created)?.Components ?? [])})");
        }

        // Act 10's burnt Oriath has Act 5's tiles: an exit's own destination (ExistsRule.To: Act 5's InnocenceToDestroyedCourts
        // to Act 5's Torched Courts) holds in its act only, so The Desecrated Chambers' way out leads to Act 10's.
        Assert.True(AreaTransitionCatalog.SameAct(ChamberOfInnocence, TorchedCourts));
        Assert.False(AreaTransitionCatalog.SameAct("2_10_4", TorchedCourts));
        if (ServerLevels.For("2_10_4", 1) is not null) // needs the game files (Client/Content.ggpk)
        {
            var chambers = AreaTransitionCatalog.For(AreaTransitionCatalog.HashOf("2_10_4")!.Value, "2_10_4", 1, []);
            var burnt = Assert.Single(chambers, e => e.Transition.SourceType == Act5Objects.InnocenceToDestroyedCourts);
            Assert.Equal("2_10_3", AreaTransitionCatalog.IdOf(burnt.Transition.DestinationHash16));
        }
    }

    /// <summary>Act 1 in the game's order: a1q1, a1q5, a1q4, a1q8, a1q7, a1q2 (with a1q9's blockage on the way), a1q6,
    /// a1q3 and the step into Act 2.</summary>
    internal static void Act1(QuestSimulator s)
    {
        // a1q1 Enemy at the Gate
        s.Enter(Strand).See(Act1Monsters.Hillock).Kill(Act1Monsters.Hillock).Enter(LioneyesWatch)
            .Talk(Tarkleigh, "Introduction").Reward(Tarkleigh, "Hillock Reward");
        // a1q5 Mercy Mission
        s.Talk(Nessa, "Lioneye's Watch").Talk(Bestel, "The Medicine Chest")
            .Enter(Coast).Enter(TidalIsland).Kill(Act1Monsters.Hailrake).PickUp(Act1Items.MedicineChest)
            .Enter(LioneyesWatch, travel: true).Talk(Nessa, "The Medicine Chest")
            .Reward(Nessa, "Medicine Chest Reward 2").Reward(Nessa, "Medicine Chest Reward");
        // a1q4 Breaking Some Eggs (the Rhoa Nests give the glyphs: GlyphQuest's objects)
        s.Enter(Coast, travel: true).Enter(MudFlats).Talk(Tarkleigh, "Rhoas");
        foreach (string glyph in GlyphQuest.Glyphs) s.PickUp(glyph, given: true);
        s.Use(GlyphQuest.Wall).Enter(SubmergedPassage).Enter(LioneyesWatch, travel: true)
            .Reward(Tarkleigh, "Glyph Reward 2").Reward(Tarkleigh, "Glyph Reward");
        // a1q8 A Dirty Job
        s.Talk(Tarkleigh, "The Fetid Pool").Enter(MudFlats, travel: true).Enter(FetidPool)
            .See(Act1Monsters.BoneRhoa).See(Act1Monsters.Kadavrus).Kill(Act1Monsters.Kadavrus).Clear()
            .Enter(LioneyesWatch, travel: true).Talk(Tarkleigh, "The Necromancer").Reward(Tarkleigh, "Necromancer Reward").UseItem(BookA1q8);
        // a1q7 The Dweller of the Deep
        s.Talk(Tarkleigh, "Dweller of the Deep").Enter(SubmergedPassage, travel: true).Enter(FloodedDepths)
            .See(Act1Monsters.Dweller).Kill(Act1Monsters.Dweller)
            .Enter(LioneyesWatch, travel: true).Talk(Tarkleigh, "Dweller of the Deep").Reward(Tarkleigh, "Dweller Reward").UseItem(BookA1q7);
        // a1q2 The Caged Brute (The Ledge, The Climb, The Lower Prison on the way)
        s.Enter(SubmergedPassage, travel: true).Enter("1_1_5").Enter("1_1_6").Enter("1_1_7_1")
            .Enter(LioneyesWatch, travel: true).Talk(Tarkleigh, "Brutus").Talk(Nessa, "Brutus").Reward(Nessa, "Prison Reward")
            .Talk(Tarkleigh, "Reaching the Forest")
            .Enter("1_1_7_1", travel: true).Enter(UpperPrison).Near("Metadata/NPC/Act1/PietyPrison")
            .See(Act1Monsters.Brutus).Kill(Act1Monsters.Brutus).Enter(PrisonersGate)
            // a1q9 The Way Forward: Piety's blockage on the road to The Western Forest
            .Near("Metadata/NPC/Act1/PietyBlockage")
            .Enter(LioneyesWatch, travel: true).Talk(Tarkleigh, "Brutus").Reward(Tarkleigh, "Brutus Reward");
        // a1q6 The Marooned Mariner
        s.Enter(PrisonersGate, travel: true).Enter(ShipGraveyard).Talk(Fairgraves, "Introduction").Talk(Fairgraves, "Marooned")
            .Enter(ShipGraveyardCave).OpenChest("Metadata/Chests/QuestChests/Fairgraves/AllFlameSlaveGirl").PickUp(Act1Items.Allflame)
            .Enter(ShipGraveyard).Talk(Fairgraves, "Thank You").Kill(Act1Monsters.Fairgraves)
            .Enter(LioneyesWatch, travel: true).Talk(Bestel, "Captain Fairgraves").Reward(Bestel, "Fairgraves Reward").UseItem(BookA1q6);
        // a1q3 The Siren's Cadence, and Act 2
        s.Talk(Nessa, "Merveil").Enter(ShipGraveyard, travel: true).Enter("1_1_11_1")
            .Enter(LioneyesWatch, travel: true).Talk(Nessa, "Merveil's Lair").Reward(Nessa, "Merveil Reward")
            .Enter("1_1_11_1", travel: true).Enter(CavernOfAnger)
            .See(Act1Monsters.Merveil).Kill(Act1Monsters.Merveil).Kill(Act1Monsters.MerveilTwisted).Enter(SouthernForest);
    }

    /// <summary>Act 2 as the driver plays it (order.py): a2q11 (a1q9's road and Bestel's book), a2q10, a2q6, a2q4, a2q5,
    /// a2q9, a2q7 killing all three bandits, a2q8 and the step into Act 3. Areas off the main road are reached by waypoint
    /// (travel), as the driver does; the client's own gates still apply.</summary>
    internal static void Act2(QuestSimulator s)
    {
        // a2q11 The Way Forward: Arteri's emblem opens the pass; Bestel's book
        s.Enter(ForestEncampment, travel: true).Talk(Silk, "Blocked Passage")
            .Enter(WesternForest, travel: true).See(Act2Monsters.Arteri).Kill(Act2Monsters.Arteri).PickUp(Act2Items.ThaumeticEmblem)
            .Use(Act2Objects.ThaumeticSeal).Enter(PrisonersGate)
            .Enter(LioneyesWatch, travel: true).Talk(Bestel, "Inspirational Poem").Reward(Bestel, "Road Reward").UseItem(BookA1q9);
        // a2q10 The Great White Beast
        s.Enter(ForestEncampment, travel: true).Talk(Yeena, "The Great White Beast")
            .Enter(OldFields, travel: true).Enter(Den).See(Act2Monsters.GreatWhiteBeast).Kill(Act2Monsters.GreatWhiteBeast)
            .Enter(ForestEncampment, travel: true).Talk(Yeena, "The Great White Beast").Reward(Yeena, "Great White Beast Reward");
        // a2q6 Intruders in Black: Helena below the ledge, Fidelitas, the Strange Device
        s.Enter("1_2_6_1", travel: true).Enter(ChamberOfSins2).Near(Act2Objects.HelenaSins).Near(Act2Objects.PietySins)
            .Kill(Act2Monsters.Fidelitas).Talk(Act2Objects.HelenaSins, "Rescued").OpenChest(Act2Objects.StrangeDevice).PickUp(Act2Items.BalefulGem)
            .Enter(ForestEncampment, travel: true).Talk(Helena, "Rescued").Talk(Greust, "Helena").Reward(Greust, "Blackguard Reward");
        // a2q4 Sharp and Cruel
        s.Talk(Helena, "Maligaro's Spike").Talk(Silk, "Maligaro's Spike")
            .Enter(WeaversChambers, travel: true).See(Act2Monsters.Weaver).Kill(Act2Monsters.Weaver).PickUp(Act2Items.MaligarosSpike)
            .Enter(ForestEncampment, travel: true).Talk(Silk, "Maligaro's Spike").Reward(Silk, "Maligaro's Spike Reward");
        // a2q5 Through Sacred Ground
        s.Talk(Yeena, "The Fellshrine Hand").Enter(CryptLevel2, travel: true).OpenChest(Act2Objects.GoldenHandAltar).PickUp(Act2Items.GoldenHand)
            .Enter(ForestEncampment, travel: true).Talk(Yeena, "The Fellshrine Hand").Reward(Yeena, "Fellshrine Reward").UseItem(BookA2q5);
        // a2q9 The Root of the Problem
        s.Talk(Helena, "Lorrata").Enter(Wetlands, travel: true).Near(Act2Objects.PoisonTree).Use(Act2Objects.PoisonTree).Enter(VaalRuins);
        // a2q7 Deal with the Bandits: kill all three, Eramir's point and The Apex
        s.Enter(ForestEncampment, travel: true).Talk(Eramir, "The Bandit Scourge")
            .Enter(BrokenBridge, travel: true).Kill(Act2Monsters.Kraityn).PickUp(Act2Items.KraitynsAmulet)
            .Enter(WesternForest, travel: true).Kill(Act2Monsters.Alira).PickUp(Act2Items.AlirasAmulet)
            .Enter(Wetlands, travel: true).Kill(Act2Monsters.Oak).PickUp(Act2Items.OaksAmulet)
            .Enter(ForestEncampment, travel: true).Talk(Eramir, "Bandit Lords").Talk(Eramir, "Bandit Amulets").Talk(Eramir, "Bandit Amulets")
            .Reward(Eramir, "Take the Apex");
        // a2q8 Shadow of the Vaal: the Ancient Seal, the Apex door, the Dark Altar, the Vaal Oversoul, Act 3
        s.Enter(VaalRuins, travel: true).Use(Act2Objects.AncientSeal).Enter(NorthernForest)
            .Enter(ForestEncampment, travel: true).Talk(Yeena, "The Darkness").Talk(Eramir, "Yeena's Vision")
            .Enter(Caverns, travel: true).Near(Act2Objects.ApexDoor).Enter(AncientPyramid).Near(Act2Objects.DarkAltar).Use(Act2Objects.DarkAltar)
            .See(Act2Monsters.VaalOversoul).Kill(Act2Monsters.VaalOversoul).Enter(CityOfSarn).Enter(SarnEncampment);
    }

    /// <summary>Act 3 as the driver plays it (order.py): a2q8a3 (arrived in Act 2), a3q1 with a3q2 (Clarissa, the bracelet,
    /// the Sewer Keys), a3q11, a3q4 with a3q3, a3q5, a3q13, a3q8, a3q9, a3q12, a3q10 and the step into Act 4.</summary>
    internal static void Act3(QuestSimulator s)
    {
        // a3q1 Lost in Love: Clarissa, Tolman's bracelet, Maramoa's reward; a3q2: the Sewer Keys open the sewers
        s.Enter(SarnEncampment, travel: true).Talk(Maramoa, "Clarissa")
            .Enter(CityOfSarn, travel: true).Near(Act3Objects.ClarissaSlums).Kill(Act3Monsters.GuardCaptain).Talk(Act3Objects.ClarissaSlums, "Tolman")
            .Enter(Slums, travel: true).Enter(Crematorium).See(Act3Monsters.PietyEscape).OpenChest(Act3Objects.TolmanChair).PickUp(Act3Items.TolmansBracelet)
            .Enter(SarnEncampment, travel: true).Talk(Clarissa, "Tolman").Talk(Maramoa, "Clarissa").Reward(Maramoa, "Clarissa Reward")
            .Talk(Clarissa, "Tolman").Reward(Clarissa, "Take Sewer Keys")
            .Enter(Slums, travel: true).Near(Act3Objects.SewerGrating).Use(Act3Objects.SewerGrating).Enter(Sewers);
        // a3q11 Victario's Secrets
        s.Enter(SarnEncampment, travel: true).Talk(Hargan, "Introduction").Talk(Hargan, "Victario").Enter(Sewers, travel: true);
        for (int i = 0; i < 3; i++) s.OpenChest(Act3Objects.Stashes[i]).PickUp(Act3Items.Busts[i]);
        s.Enter(SarnEncampment, travel: true).Talk(Hargan, "Victario's Holdfasts").Reward(Hargan, "Platinum Bust Reward").UseItem(BookA3q11);
        // a3q3 The Gemling Queen and a3q4 The Ribbon Spool
        s.Talk(Grigor, "The Gemling Queen").Enter(Battlefront, travel: true).OpenChest(Act3Objects.RibbonSpoolChest).PickUp(Act3Items.RibbonSpool)
            .Enter(SolarisTemple2, travel: true).Talk(Dialla, "Introduction").Talk(Dialla, "The Ribbon Spool").Reward(Dialla, "Ribbon Spool Reward");
        // a3q5 Fiery Dust
        s.Talk(Dialla, "The Sewers").Talk(Dialla, "Thaumetic Sulphite")
            .Enter(Docks, travel: true).OpenChest(Act3Objects.SulphiteChest).PickUp(Act3Items.ThaumeticSulphite)
            .Enter(SolarisTemple2, travel: true).Talk(Dialla, "Thaumetic Sulphite").Talk(Dialla, "Infernal Talc").Reward(Dialla, "Take Infernal Talc")
            .Enter(Sewers, travel: true).Near(Act3Objects.BioWall).Use(Act3Objects.BioWall).Enter(EbonyBarracks);
        // a3q13 A Swig of Hope (Fairgraves died in Act 1): row 496 takes the plum and the decanter in one click
        s.Enter(Marketplace, travel: true).OpenChest(Act3Objects.DecanterChest).PickUp(Act3Items.Decanter)
            .Enter(ImperialGardens, travel: true).OpenChest(Act3Objects.PlumTree).PickUp(Act3Items.Plum)
            .Enter(Docks, travel: true).Talk(FairgravesDocks, "Introduction");
        // The quest row, then the hand-in: an option that sets nothing new fails the test instead of looping.
        for (int talks = 0; !s.Has("a3q13-DeliveredItems"); talks++)
        {
            Assert.True(talks < 3, "Fairgraves' \"A Swig of Hope\" does not deliver the plum and the decanter");
            s.Talk(FairgravesDocks, "A Swig of Hope");
        }
        s.Reward(FairgravesDocks, "Swig of Hope Reward");
        // a3q8 Sever the Right Hand
        s.Enter(SarnEncampment, travel: true).Talk(Maramoa, "General Gravicius").Enter(Battlefront, travel: true).Near(Act3Objects.BridgeBarrier)
            .Enter(EbonyBarracks, travel: true).See(Act3Monsters.Gravicius).Kill(Act3Monsters.Gravicius)
            .Enter(SarnEncampment, travel: true).Talk(Maramoa, "General Gravicius").Reward(Maramoa, "Gravicius Reward");
        // a3q9 Piety's Pets
        s.Talk(Grigor, "Piety").Enter(LunarisTemple2, travel: true).See(Act3Monsters.Piety).Kill(Act3Monsters.Piety).PickUp(Act3Items.TowerKey)
            .Enter(SarnEncampment, travel: true).Talk(Grigor, "Piety").Reward(Grigor, "Piety Reward").UseItem(BookA3q9);
        // a3q12 A Fixture of Fate: the loose candle opens the hidden door for the instance only
        s.Enter(Library, travel: true).Talk(Siosa, "Introduction").Talk(Siosa, "Four Golden Pages")
            .Use(Act3Objects.LooseCandle).Enter(Archives);
        for (int i = 0; i < 4; i++) s.OpenChest(Act3Objects.BookStands[i]).PickUp(Act3Items.GoldenPages[i]);
        s.Enter(Library).Talk(Siosa, "Four Golden Pages").Talk(Siosa, "Four Golden Pages").Reward(Siosa, "Golden Pages Reward");
        // a3q10 The Sceptre of God, and Act 4
        s.Enter(ImperialGardens, travel: true).Use(Act3Objects.EpicDoorLock).Enter(SceptreOfGod).Enter(UpperSceptreOfGod)
            .See(Act3Monsters.Dominus).Kill(Act3Monsters.Dominus).Kill(Act3Monsters.DominusAscendant)
            .Talk(DiallaTower, "Dominus").Talk(DiallaTower, "Highgate").Enter(Aqueduct).Enter("1_4_town");
    }

    /// <summary>Act 4 as the driver plays it (order.py): a4q2 (with a4q1's first talks), a4q6, a4q3, a4q4, a4q5 (with a4q1's
    /// Rapture and Piety), a4q1 (Malachai, the Rapture reward) and a5q1a, the Resonator into Act 5.</summary>
    internal static void Act4(QuestSimulator s)
    {
        // a4q1's start, a4q2 Breaking the Seal (DiallaAqueduct's "Highgate", NPCTalk 509, is optional: only the log uses it)
        s.Enter(Aqueduct, travel: true).Enter(Highgate).Talk(Act4Npcs.DiallaTown, "Oyun")
            .Talk(Oyun, "Introduction").Talk(Oyun, "The Red Banner")
            .Enter(DriedLake, travel: true).See(Act4Monsters.Voll).Kill(Act4Monsters.Voll).PickUp(Act4Items.RedBanner)
            .Enter(Highgate, travel: true).Use(Act4Objects.Seal).Talk(Oyun, "The Red Banner").Reward(Oyun, "Red Banner Reward");
        // a4q6 An Indomitable Spirit
        s.Talk(Tasuni, "Deshret").Enter(Mines1).Enter(Mines2).Near(Act4Objects.DeshretSpirit).Use(Act4Objects.DeshretSpirit)
            .Enter(Highgate, travel: true).Talk(Tasuni, "Deshret").Reward(Tasuni, "Deshret Reward").UseItem(BookA4q6);
        // a4q3 The King of Fury (Dialla sends the player to the Rapture Device first)
        s.Talk(Act4Npcs.DiallaTown, "Malachai").Enter(CrystalVeins, travel: true).Near(Act4Npcs.DiallaRapture)
            .Talk(Act4Npcs.DiallaRapture, "The Rapture", 0)
            .Enter(KaomsStronghold, travel: true).See(Act4Monsters.Kaom).Kill(Act4Monsters.Kaom).PickUp(Act4Items.KaomGem)
            .Enter(CrystalVeins, travel: true).Talk(Act4Npcs.DiallaRapture, "The Eye of Fury");
        // a4q4 The King of Desire
        s.Enter(GrandArena, travel: true).See(Act4Monsters.Daresso).Kill(Act4Monsters.Daresso).PickUp(Act4Items.DaressoGem)
            .Enter(CrystalVeins, travel: true).Talk(Act4Npcs.DiallaRapture, "The Eye of Desire");
        // a4q5 Corpus Malachus: the Rapture Device, Piety in the Belly, the organs in The Harvest
        s.Talk(Act4Npcs.DiallaRapture, "The Rapture", 8).Talk(Act4Npcs.DiallaRapture, "The Rapture", 9)
            .Enter(Belly1).Enter(Belly2).See(Act4Monsters.PietyBeast).Kill(Act4Monsters.PietyBeast)
            .Talk(Act4Npcs.PietyBelly, "Nightmare").Talk(Act4Npcs.PietyBelly, "Malachai")
            .Enter(Harvest).Kill(Act4Monsters.Doedre).PickUp(Act4Items.Lungs).Kill(Act4Monsters.Maligaro).PickUp(Act4Items.Heart)
            .Kill(Act4Monsters.Shavronne).PickUp(Act4Items.Entrails)
            // Rows 624-627 share their text: one click hands in all three organs (1-3), then the Black Core (4).
            .Talk(Act4Npcs.PietyHarvest, "Malachai's Organs", 1).Talk(Act4Npcs.PietyHarvest, "Malachai's Organs", 4);
        // a4q1 The Eternal Nightmare: Malachai, then Highgate's withered Dialla
        s.See(Act4Monsters.Malachai).Kill(Act4Monsters.Malachai).Kill(Act4Monsters.Malachai2)
            .Enter(Highgate, travel: true).Talk(Tasuni, "Malachai", 23).Talk(Act4Npcs.DiallaTownWithered, "Malachai", 4)
            .Talk(Act4Npcs.DiallaTownWithered, "The Rapture Device").Reward(Act4Npcs.DiallaTownWithered, "Rapture Reward");
        // a5q1a Return to Oriath: the Resonator on The Ascent
        s.Enter(Ascent).Near(Act4Objects.ResonatorLever).Use(Act4Objects.ResonatorLever).Enter(SlavePens);
    }

    /// <summary>Act 5 as the driver plays it: a5q1b, a5q3 and a5q2 (both in The Control Blocks), a5q4, a5q5, a5q7, a5q6
    /// (Kitava's threshold) and Lilly's voyage to Act 6. (Act5Areas' ControlBlocks, Reliquary, Ossuary and CathedralRooftop
    /// are named in full: Act 10 has areas of the same names.)</summary>
    internal static void Act5(QuestSimulator s)
    {
        // a5q1b Return to Oriath
        s.See(Act5Monsters.Krow).Kill(Act5Monsters.Krow).Enter(OverseersTower).Talk(Lani, "Overseer Krow").Reward(Lani, "Overseer Reward");
        // a5q3 In Service to Science and a5q2 The Key to Freedom
        s.Talk(Vilenta, "Miasmeter").Talk(UtulaTown, "Introduction").Talk(UtulaTown, "Justicar Casticus")
            .Enter(Act5Areas.ControlBlocks, travel: true).OpenChest(Act5Objects.SupplyChest).PickUp(Act5Items.Miasmeter)
            .See(Act5Monsters.Casticus).Kill(Act5Monsters.Casticus).PickUp(Act5Items.EyesOfZeal)
            .Enter(OverseersTower, travel: true).Talk(Vilenta, "Miasmeter").Reward(Vilenta, "Miasmeter Reward")
            .UseItem(BookA5q3).Talk(Lani, "Justicar Casticus").Reward(Lani, "Casticus Reward");
        // a5q4 Death to Purity: the Templar Courts door, Avarius into Innocence, Sin
        s.Enter(OriathSquare, travel: true).Use(Act5Objects.TemplarCourtsDoor).Enter(TemplarCourts).Enter(ChamberOfInnocence)
            .See(Act5Monsters.Avarius).Kill(Act5Monsters.Avarius).See(Act5Monsters.Innocence).Kill(Act5Monsters.Innocence)
            .Talk(Act5Npcs.SinInnocence, "Kitava")
            .Enter(OverseersTower, travel: true).Talk(Lani, "High Templar Avarius").Reward(Lani, "Avarius Reward");
        // a5q5 The King's Feast: Bannon, Utula in The Ruined Square
        s.Enter(ChamberOfInnocence, travel: true).Talk(Act5Npcs.BannonInnocence, "Rescue")
            .Enter(OverseersTower, travel: true).Talk(Act5Npcs.BannonTown, "Introduction").Talk(Act5Npcs.BannonTown, "Utula")
            .Enter(RuinedSquare, travel: true).See(Act5Monsters.Utula).Kill(Act5Monsters.Utula)
            .Enter(OverseersTower, travel: true).Talk(Act5Npcs.BannonTown, "Utula").Reward(Act5Npcs.BannonTown, "Utula Reward");
        // a5q7 Kitava's Torments: Lani takes the three in one click
        s.Talk(Lani, "Kitava's Torments").Enter(Act5Areas.Reliquary, travel: true)
            .OpenChest(Act5Objects.RelicCases[0]).PickUp(Act5Items.TukohamasTooth)
            .OpenChest(Act5Objects.RelicCases[1]).PickUp(Act5Items.HinekorasHair)
            .OpenChest(Act5Objects.RelicCases[2]).PickUp(Act5Items.ValakosJaw)
            .Enter(OverseersTower, travel: true).Talk(Lani, "Kitava's Torments").Reward(Lani, "Torments Reward").UseItem(BookA5q7);
        // a5q6 The Ravenous God: the Sign of Purity, Kitava's threshold (he never dies), Lilly sails to Lioneye's Watch
        s.Enter(TorchedCourts, travel: true).Enter(RuinedSquare, travel: true).Enter(Act5Areas.Ossuary)
            .OpenChest(Act5Objects.PurityBox).PickUp(Act5Items.SignOfPurity)
            .Enter(Act5Areas.CathedralRooftop, travel: true).Use(Act5Objects.ArenaSocket)
            .See(Act5Monsters.Kitava).Kill(Act5Monsters.Kitava)
            .Talk(Act5Npcs.SinKitava, "Kitava").Talk(Act5Npcs.Lilly, "Sail to Wraeclast").Enter(LioneyesWatchAct6, travel: true);
    }

    /// <summary>Act 6 as the driver plays it: a6q4, a6q5, a6q3 (with a6q1's first Nessa), a6q2, a6q7 (the second Nessa),
    /// a6q6, a6q1 (the third, the Beacon, the Brine King) and Weylam's voyage to Act 7.</summary>
    internal static void Act6(QuestSimulator s)
    {
        s.Talk(Tarkleigh6, "Introduction").Talk(Tarkleigh6, "Nessa").Talk(SinTown6, "Shavronne of Umbra");
        // a6q4 Fallen from Grace
        s.Talk(Lilly6, "Introduction").Talk(Lilly6, "The Twilight Strand").Enter(TwilightStrandAct6).Clear()
            .Enter(LioneyesWatchAct6, travel: true).Talk(Lilly6, "The Twilight Strand").Reward(Lilly6, "Twilight Strand Reward")
            .UseItem(BookA6q4);
        // a6q5 Bestel's Epic
        s.Talk(Bestel6, "Bestel's Epic").Enter(CoastAct6, travel: true).Enter(TidalIslandAct6)
            .OpenChest(Act6Objects.BestelChest).PickUp(Act6Items.BestelsManuscript)
            .Enter(LioneyesWatchAct6, travel: true).Talk(Bestel6, "Bestel's Epic").Reward(Bestel6, "Bestel's Epic Reward");
        // a6q3 The Father of War (the first Nessa)
        s.Enter(MudFlatsAct6, travel: true).Near(Act6Npcs.NessaMudFlats).Near(Act6Objects.MudFlatsKaruiDoor)
            .Enter(LioneyesWatchAct6, travel: true).Talk(Tarkleigh6, "The Karui Fortress")
            .Enter(MudFlatsAct6, travel: true).Kill(Act6Monsters.DishonouredQueen).PickUp(Act6Items.EyeOfConquest)
            .Use(Act6Objects.MudFlatsKaruiDoor).Enter(KaruiFortress).Near(Act6Objects.FortressKaruiDoor)
            .See(Act6Monsters.Tukohama).Kill(Act6Monsters.Tukohama).Talk(Act6Npcs.SinTukohama, "Tukohama")
            .Enter(LioneyesWatchAct6, travel: true).Talk(Tarkleigh6, "Tukohama").Reward(Tarkleigh6, "Tukohama Reward").UseItem(BookA6q3);
        // a6q2 Essence of Umbra
        s.Enter(Ridge, travel: true).Enter(LowerPrisonAct6).Enter(ShavronnesTower).See(Act6Monsters.Shavronne).Kill(Act6Monsters.Brutus)
            .Kill(Act6Monsters.Shavronne).Talk(Act6Npcs.SinShavronne, "Shavronne")
            .Enter(LioneyesWatchAct6, travel: true).Talk(Tarkleigh6, "Shavronne of Umbra").Reward(Tarkleigh6, "Shavronne Reward");
        // a6q7 The Cloven One (the second Nessa)
        s.Talk(Bestel6, "Abberath").Enter(PrisonersGateAct6, travel: true).Near(Act6Npcs.NessaPrisonersGate)
            .See(Act6Monsters.Abberath).Kill(Act6Monsters.Abberath).Kill(Act6Monsters.Abberath2).Talk(Act6Npcs.SinAbberath, "Abberath")
            .Enter(LioneyesWatchAct6, travel: true).Talk(Bestel6, "Abberath").Reward(Bestel6, "Abberath Reward").UseItem(BookA6q7);
        // a6q6 The Puppet Mistress
        s.Talk(Tarkleigh6, "The Puppet Mistress").Enter(WetlandsAct6, travel: true).See(Act6Monsters.Ryslatha).Kill(Act6Monsters.Ryslatha)
            .Talk(Act6Npcs.SinRyslatha, "Ryslatha")
            .Enter(LioneyesWatchAct6, travel: true).Talk(Tarkleigh6, "The Puppet Mistress").Reward(Tarkleigh6, "Puppet Mistress Reward")
            .UseItem(BookA6q6);
        // a6q1 The Brine King: the third Nessa, the Black Flag, the Beacon, the Reef, Weylam to Act 7
        s.Enter(CavernOfAngerAct6, travel: true).Near(Act6Npcs.NessaMerveilsCaverns)
            .OpenChest(Act6Objects.BlackFlagChest).PickUp(Act6Items.BlackFlag)
            .Enter(Beacon, travel: true).Near(Act6Objects.BeaconCamera).Use(Act6Objects.BeaconLever).Use(Act6Objects.BeaconStatue)
            .Talk(Act6Npcs.WeylamBeacon, "Introduction").Talk(Act6Npcs.WeylamBeacon, "Sail to the Brine King's Reef")
            .Enter(BrineKingsReef, travel: true).See(Act6Monsters.BrineKing).Kill(Act6Monsters.BrineKing)
            .Talk(Act6Npcs.SinBrineKing, "The Brine King").Talk(Act6Npcs.WeylamReef2, "Sail to the Bridge Encampment")
            .Enter(BridgeEncampment, travel: true);
    }

    /// <summary>A character at the start of Act 7 as /quest_preset E3StartOfAct7 makes it (CampaignState.PresetFlags), for
    /// the checks of a later act that need no played Acts 1–6: in The Bridge Encampment with the preset's quest item
    /// (Lilly's Book of Regrets: a6q4-HaveRewardBook without its use flag), which it uses.</summary>
    internal static QuestSimulator FromAct7()
    {
        var s = new QuestSimulator();
        s.Set(CampaignState.PresetFlags("E3StartOfAct7", out _)!);
        s.Carried.Add(Book(6, "a6q4"));
        s.Enter(Act7Areas.BridgeEncampment, travel: true).UseItem(Book(6, "a6q4"));
        return s;
    }

    /// <summary>Act 7 as the driver plays it (order.py): a7q5, a7q2, a7q3, a7q1, a7q8, a7q9, a7q7, a7q6, a7q4 and the step
    /// into Act 8.</summary>
    internal static void Act7(QuestSimulator s)
    {
        // a7q5 The Silver Locket
        s.Talk(Weylam7, "The Broken Bridge").Talk(Weylam7, "The Silver Locket")
            .Enter(Act7Areas.BrokenBridge, travel: true).OpenChest(Act7Objects.LocketChest).PickUp(Act7Items.SilverLocket)
            .Enter(Act7Areas.BridgeEncampment, travel: true).Talk(Weylam7, "The Silver Locket").Reward(Weylam7, "Silver Locket Reward");
        // a7q2 Essence of the Artist (with a7q3's start: Silk appears with the map), through the Reverie Device's portal
        s.Talk(Sin7, "Inquisitor Maligaro").Talk(Helena7, "Maligaro")
            .Enter(Act7Areas.Crypt, travel: true).OpenChest(Act7Objects.MaligaroMapChest).PickUp(Act7Items.MaligaroMap)
            .Enter(Act7Areas.SinsLevel1, travel: true).Talk(Act7Objects.SilkSins, "Black Venom").Use(Act7Objects.Orrery)
            .Enter(Act7Areas.MaligarosSanctum).See(Act7Monsters.Maligaro)
            .Kill(Act7Monsters.BlackDeath).PickUp(Act7Items.BlackVenom).Kill(Act7Monsters.Maligaro).Talk(Act7Objects.SinMaligaro, "Maligaro")
            .Enter(Act7Areas.BridgeEncampment, travel: true).Talk(Helena7, "Maligaro").Reward(Helena7, "Maligaro Reward");
        // a7q3 Web of Secrets: the venom for the key, the barrier, the passage into The Den
        s.Enter(Act7Areas.SinsLevel1, travel: true).Talk(Act7Objects.SilkSins, "Black Venom").Reward(Act7Objects.SilkSins, "Black Death Reward")
            .Near(Act7Objects.SinsBarrier).Enter(Act7Areas.SinsLevel2).Use(Act7Objects.PassageCover).Enter(Act7Areas.Den);
        // a7q1 The Master of a Million Faces (all three bandits died in Act 2: Greust is Ralakesh's avatar)
        s.Enter(Act7Areas.BridgeEncampment, travel: true).Talk(Eramir7, "Greust")
            .Enter(Act7Areas.AshenFields, travel: true).See(Act7Monsters.Greust).Kill(Act7Monsters.Greust).Talk(Act7Objects.SinRalakesh, "Ralakesh")
            .Enter(Act7Areas.BridgeEncampment, travel: true).Talk(Eramir7, "Ralakesh").Reward(Eramir7, "Ralakesh Reward").UseItem(Book(7, "a7q1"));
        // a7q8 In Memory of Greust
        s.Talk(Helena7, "Greust's Necklace").Reward(Helena7, "Take Greust's Necklace")
            .Enter(Act7Areas.NorthernForest, travel: true).Near(Act7Objects.GreustShrine).Use(Act7Objects.GreustShrine)
            .Enter(Act7Areas.BridgeEncampment, travel: true).Talk(Helena7, "Greust's Necklace").Reward(Helena7, "Greust's Necklace Reward");
        // a7q9 Queen of Despair
        s.Talk(Sin7, "Gruthkul").Enter(Act7Areas.DreadThicket, travel: true).See(Act7Monsters.Gruthkul).Kill(Act7Monsters.Gruthkul)
            .Talk(Act7Objects.SinGruthkul, "Gruthkul")
            .Enter(Act7Areas.BridgeEncampment, travel: true).Talk(Eramir7, "Gruthkul").Reward(Eramir7, "Gruthkul Reward").UseItem(Book(7, "a7q9"));
        // a7q7 Lighting the Way: Yeena at the webbed temple (seen: a7q4-SeenBlockage) asks for the seven scattered Fireflies
        // and takes them in one click
        s.Enter(Act7Areas.VaalCity, travel: true).Near(Act7Objects.TempleWeb).Talk(Act7Objects.YeenaVaalCity, "Fireflies")
            .Enter(Act7Areas.DreadThicket, travel: true);
        for (int n = 0; n < 7; n++) s.OpenChest(Act7Objects.FireflyChests[n]).PickUp(Act7Items.Fireflies[n]);
        s.Enter(Act7Areas.VaalCity, travel: true).Talk(Act7Objects.YeenaVaalCity, "Fireflies");
        // a7q6 Kishara's Star
        s.Enter(Act7Areas.BridgeEncampment, travel: true).Talk(Weylam7, "Kishara's Star")
            .Enter(Act7Areas.Causeway, travel: true).OpenChest(Act7Objects.KisharaStarChest).PickUp(Act7Items.KisharaStar)
            .Enter(Act7Areas.BridgeEncampment, travel: true).Talk(Weylam7, "Kishara's Star").Reward(Weylam7, "Kishara's Star Reward")
            .UseItem(Book(7, "a7q6"));
        // a7q4 The Mother of Spiders, and Act 8 over Arakaali's bridge
        s.Talk(Yeena7, "Silk").Enter(Act7Areas.VaalCity, travel: true).Enter(Act7Areas.TempleOfDecay1).Enter(Act7Areas.TempleOfDecay2)
            .See(Act7Monsters.Arakaali).Kill(Act7Monsters.Arakaali).Talk(Act7Objects.SinArakaali, "Arakaali")
            .Enter(Act7Areas.SarnRamparts);
    }

    /// <summary>Act 8 as the driver plays it (order.py): a8q1, a8q5, a8q4, a8q6, a8q7, a8q3 with a8q2 and the step into
    /// Act 9.</summary>
    internal static void Act8(QuestSimulator s)
    {
        // a8q1 Essence of the Hag
        s.Enter(Act8Areas.SarnEncampment, travel: true).Talk(Sin8, "Doedre Darktongue").Talk(Hargan8, "Doedre Darktongue")
            .Enter(Act8Areas.DoedresCesspool, travel: true).See(Act8Monsters.Doedre).Kill(Act8Monsters.Doedre).Talk(Act8Objects.SinDoedre, "Doedre")
            .Enter(Act8Areas.SarnEncampment, travel: true).Talk(Hargan8, "Doedre Darktongue").Reward(Hargan8, "Doedre Reward");
        // a8q5 The Wings of Vastiri: Titucius' tomb opens once he is dead
        s.Talk(Hargan8, "The Wings of Vastiri").Enter(Act8Areas.BathHouse, travel: true).See(Act8Monsters.Titucius).Kill(Act8Monsters.Titucius)
            .OpenChest(Act8Objects.TituciusChest).PickUp(Act8Items.WingsOfVastiri)
            .Enter(Act8Areas.SarnEncampment, travel: true).Talk(Hargan8, "The Wings of Vastiri").Reward(Hargan8, "Wings of Vastiri Reward");
        // a8q4 Reflection of Terror
        s.Talk(Sin8, "Reflection of Terror").Enter(Act8Areas.HighGardens, travel: true).See(Act8Monsters.Yugul).Kill(Act8Monsters.Yugul)
            .Talk(Act8Objects.SinYugul, "Yugul")
            .Enter(Act8Areas.SarnEncampment, travel: true).Talk(Hargan8, "Yugul").Reward(Hargan8, "Yugul Reward").UseItem(Book(8, "a8q4"));
        // a8q6 Love is Dead
        s.Talk(Clarissa8, "Reviving Tolman").Enter(Act8Areas.Quay, travel: true).OpenChest(Act8Objects.AnkhChest).PickUp(Act8Items.AnkhOfEternity)
            .Talk(Act8Objects.ClarissaQuay, "The Ankh of Eternity").Talk(Act8Objects.ClarissaQuay, "Tolman")
            .Enter(Act8Areas.SarnEncampment, travel: true).Talk(Clarissa8, "Tolman").Reward(Clarissa8, "Tolman Reward").UseItem(Book(8, "a8q6"));
        // a8q7 The Gemling Legion: done once none of the seven is left
        s.Talk(Maramoa8, "The Gemling Legion").Enter(Act8Areas.GrainGate, travel: true).See(Act8Monsters.GemlingLegion[3]);
        foreach (string legionnaire in Act8Monsters.GemlingLegion) s.Kill(legionnaire);
        s.Enter(Act8Areas.SarnEncampment, travel: true).Talk(Maramoa8, "The Gemling Legion").Reward(Maramoa8, "Gemling Legion Reward")
            .UseItem(Book(8, "a8q7"));
        // a8q3 Solar Eclipse with a8q2 Lunar Eclipse: both orbs at the fight starter, Solaris and Lunaris, and Act 9
        s.Talk(Maramoa8, "Solaris and Lunaris").Talk(Maramoa8, "The Moon Orb").Talk(Maramoa8, "The Sun Orb")
            .Enter(Act8Areas.LunarisTemple2, travel: true).See(Act8Monsters.Dusk).Kill(Act8Monsters.Dusk).PickUp(Act8Items.LunarisOrb)
            .Enter(Act8Areas.SolarisTemple2, travel: true).See(Act8Monsters.Dawn).Kill(Act8Monsters.Dawn).PickUp(Act8Items.SolarisOrb)
            .Enter(Act8Areas.HarbourBridge, travel: true).Use(Act8Objects.GoddessFightStarter)
            .Kill(Act8Monsters.Lunaris).Kill(Act8Monsters.Solaris).Talk(Act8Objects.SinSolarisLunaris, "Solaris and Lunaris")
            .Enter(Act8Areas.BloodAqueduct);
    }

    /// <summary>Act 9 as the driver plays it (order.py): a9q3, a9q5, a9q4, a9q2, a9q1 (Lilly sails to Act 10).</summary>
    internal static void Act9(QuestSimulator s)
    {
        // a9q3 The Storm Blade
        s.Enter(Act9Areas.Highgate, travel: true).Talk(PetarusVanja9, "The Storm Blade")
            .Enter(Act9Areas.VastiriDesert, travel: true).Near(Act9Objects.MummyEventChest)
            .Kill(Act9Monsters.SandWarrior).Kill(Act9Monsters.SandGoliath).PickUp(Act9Items.StormBlade)
            .Enter(Act9Areas.Highgate, travel: true).Talk(PetarusVanja9, "The Storm Blade").Reward(PetarusVanja9, "Storm Blade Reward");
        // a9q5 Queen of the Sands: the Bottled Storm carried to the barrier, both Shakaris
        s.Talk(Sin9, "Shakari").Talk(PetarusVanja9, "Bottled Storm").Reward(PetarusVanja9, "Take Bottled Storm")
            .Enter(Act9Areas.VastiriDesert, travel: true).Near(Act9Objects.SandBarrierSeen).Near(Act9Objects.SandBarrier).Enter(Act9Areas.Oasis)
            .See(Act9Monsters.Shakari).Kill(Act9Monsters.Shakari).Kill(Act9Monsters.Shakari2).Talk(Act9Objects.SinShakari, "Shakari")
            .Enter(Act9Areas.Highgate, travel: true).Talk(Irasha9, "Shakari").Reward(Irasha9, "Shakari Reward").UseItem(Book(9, "a9q5"));
        // a9q4 Fastis Fortuna
        s.Talk(PetarusVanja9, "The Maraketh Calendar").Enter(Act9Areas.Foothills, travel: true).See(Act9Monsters.Boulderback)
            .Kill(Act9Monsters.Boulderback).PickUp(Act9Items.Calendar)
            .Enter(Act9Areas.Highgate, travel: true).Talk(PetarusVanja9, "The Maraketh Calendar").Reward(PetarusVanja9, "Maraketh Calendar Reward")
            .UseItem(Book(9, "a9q4"));
        // a9q2 The Ruler of Highgate: the feather to Irasha
        s.Talk(Irasha9, "Oyun").Enter(Act9Areas.Quarry, travel: true).See(Act9Monsters.Garukhan).Kill(Act9Monsters.Garukhan)
            .Talk(Act9Objects.SinGarukhan, "Garukhan").PickUp(Act9Items.SekhemaFeather)
            .Enter(Act9Areas.Highgate, travel: true).Talk(Irasha9, "Feather of Sekhema").Reward(Irasha9, "Feather Reward").UseItem(Book(9, "a9q2"));
        // a9q1 Recurring Nightmare: both ingredients in one click, the membrane, the three spirits, the Trinity, Act 10
        s.Talk(Sin9, "The Dark Ember")
            .Enter(Act9Areas.Refinery, travel: true).Kill(Act9Monsters.Adus).OpenChest(Act9Objects.TheurgicPrecipitate).PickUp(Act9Items.TrarthanPowder)
            .Enter(Act9Areas.BoilingLake, travel: true).Kill(Act9Monsters.Basilisk).PickUp(Act9Items.BasiliskAcid)
            .Enter(Act9Areas.Highgate, travel: true).Talk(Sin9, "_take_ingredients_").Talk(Sin9, "The Dark Ember")
            .Enter(Act9Areas.Quarry, travel: true).Talk(Act9Objects.SinQuarry, "The Beast").Enter(Act9Areas.BellyOfTheBeast).Enter(Act9Areas.RottingCore)
            .Talk(Act9Objects.SinCore, "The Dark Ember")
            .Kill(Act9Monsters.DoedreSoul).Kill(Act9Monsters.MaligaroSoul).Kill(Act9Monsters.ShavronneSoul)
            .Talk(Act9Objects.SinCore, "United Trio").Kill(Act9Monsters.DepravedTrinity)
            .Talk(Act9Objects.Lilly, "Oriath").Talk(Act9Objects.Lilly, "Sail to Oriath").Enter(Act10Areas.OriathDocks, travel: true);
    }

    // The NPC's option index of an NPCTalk row, for rows that share their text (Lani's "Kitava": 1408 gossip, 1415 the
    // reward's start); the driver picks them by row.
    static readonly Lazy<NpcCatalog> Npcs = new(() => NpcCatalog.Load());
    static int IndexOf(string npc, int row) => Npcs.Value.TalksOf(Npcs.Value.Find(npc)!).First(t => t.Row == row).Index;

    /// <summary>Act 10 as the driver plays it (order.py): a10q1, a10q6, a10q4, a10q5, a10q2, a10q3 and A11q0 (Lilly sails to
    /// Karui Shores; a10q3a11, a10q3's reward in the Epilogue, is complete with the reward taken in Oriath Docks).</summary>
    internal static void Act10(QuestSimulator s)
    {
        // a10q1 Safe Passage: Plaguewing comes as the player reaches Bannon
        s.Talk(Lani10, "Bannon").Enter(Act10Areas.CathedralRooftop, travel: true).Near(Act10Objects.BannonRooftop)
            .See(Act10Monsters.Plaguewing).Kill(Act10Monsters.Plaguewing).Talk(Act10Objects.BannonRooftop, "Thank You")
            .Enter(Act10Areas.OriathDocks, travel: true).Talk(Act10Objects.BannonTown, "Welcome Back").Talk(Lani10, "Bannon").Reward(Lani10, "Bannon Reward");
        // a10q6 Vilenta's Vengeance
        s.Talk(Lani10, "Vilenta").Enter(Act10Areas.ControlBlocks, travel: true).See(Act10Monsters.Vilenta).Kill(Act10Monsters.Vilenta)
            .Enter(Act10Areas.OriathDocks, travel: true).Talk(Lani10, "Vilenta").Reward(Lani10, "Vilenta Reward").UseItem(Book(10, "a10q6"));
        // a10q4 No Love for Old Ghosts
        s.Talk(Weylam10, "Elixir of Allure").Enter(Act10Areas.Ossuary, travel: true).OpenChest(Act10Objects.ElixirChest).PickUp(Act10Items.Elixir)
            .Enter(Act10Areas.OriathDocks, travel: true).Talk(Weylam10, "Elixir of Allure").Reward(Weylam10, "Elixir of Allure Reward")
            .UseItem(Book(10, "a10q4"));
        // a10q5 Map to Tsoatha
        s.Talk(Lilly10, "Map to Tsoatha").Enter(Act10Areas.Reliquary, travel: true).OpenChest(Act10Objects.TeardropChest).PickUp(Act10Items.Teardrop)
            .Enter(Act10Areas.OriathDocks, travel: true).Talk(Lilly10, "Map to Tsoatha").Reward(Lilly10, "Tsoatha Reward");
        // a10q2 Death and Rebirth: Bannon takes the staff and becomes Innocence (the town's later stage)
        s.Talk(Act10Objects.BannonTown, "The Staff of Avarius").Enter(Act10Areas.DesecratedChambers, travel: true)
            .See(Act10Monsters.Avarius).Kill(Act10Monsters.Avarius).PickUp(Act10Items.AvariusStaff)
            .Enter(Act10Areas.OriathDocks, travel: true).Talk(Act10Objects.BannonTown, "The Staff of Avarius")
            .Talk(Act10Objects.BannonTown, "The Staff of Avarius").Talk(Lani10, "Innocence").Reward(Lani10, "Innocence Reward");
        // a10q3 An End to Hunger: the horns, Kitava's scripted end (EndGameAvailable earned), Sin's portal home, Lani's book
        s.Talk(Act10Objects.InnocenceTown, "Kitava").Enter(Act10Areas.RavagedSquare, travel: true).Near(Act10Objects.HornBlockage)
            .Talk(Act10Objects.InnocenceSquare, "Kitava").Enter(Act10Areas.Canals).Enter(Act10Areas.FeedingTrough)
            .See(Act10Monsters.Kitava).Talk(Act10Objects.SinTrough, "Kitava").Kill(Act10Monsters.Kitava)
            .Enter(Act10Areas.OriathDocks).Talk(Lani10, "Kitava", IndexOf(Lani10, 1415)).Reward(Lani10, "Kitava Reward").UseItem(Book(10, "a10q3"));
        // A11q0 Toward the Future: Lilly sails to Karui Shores, the Epilogue
        s.Talk(Lilly10, "Set Sail from Oriath").Enter(Act10Areas.KaruiShores, travel: true);
    }

    /// <summary>Plays an act's sequence, failing the test with the step the game would refuse.</summary>
    static void Play(QuestSimulator s, Action<QuestSimulator> act)
    {
        try { act(s); }
        catch (QuestSimulator.StepFailed e) { Assert.Fail($"{e.Message}\nafter: {string.Join(" ", s.Log.TakeLast(12))}"); }
    }

    /// <summary>The CharacterStartQuestState preset's flags are all set (but <paramref name="later"/>, a later act's step a
    /// preset also holds) and every quest of it with a log reached its "complete" row.</summary>
    static void PresetHolds(QuestSimulator s, string presetId, params string[] later)
    {
        var states = QuestStates.Default.Value;
        var preset = states.FindPreset(presetId)!;
        Assert.Equal([], preset.Flags.Where(f => !s.Has(f) && !later.Contains(f)).ToArray());
        // A quest without QuestStates rows (a3q2, the Sewer Keys: shown through a3q1's log) has no "complete" row to reach.
        Assert.Empty(preset.Quests.Where(q => states.Of(q).Count > 0 && !states.Complete(q, s.Has))
            .Select(q => $"{presetId} {q}: {states.Current(q, s.Has)?.Text ?? "not started"}"));
    }

    /// <summary>The campaign oracle, Acts 1 to 10 and the Epilogue in one character's order: each act's events give its
    /// CharacterStartQuestState preset (Act1Complete, Act2CompleteEramir, Act3Complete, Act4Complete, E3EndOfAct8's and
    /// EpilogueAtlasDemo's act flags; Acts 5, 6, 9 and 10 have none of their own) and every quest's "complete" row, the
    /// books' points and the way into the next act; the exits, objects and NPCs a quest gates stay shut, unused or absent
    /// until their flags.</summary>
    [Fact]
    public void The_campaign_oracle_plays_the_acts_through_the_engine_to_their_presets()
    {
        using var registered = Register("QuestObjects", QuestObjectsModule.Acts());
        var s = new QuestSimulator();
        try { Act1(s); }
        catch (QuestSimulator.StepFailed e) { Assert.Fail($"{e.Message}\nafter: {string.Join(" ", s.Log.TakeLast(12))}"); }
        var states = QuestStates.Default.Value;
        var preset = states.FindPreset("Act1Complete")!;
        Assert.Equal([], preset.Flags.Where(f => !s.Has(f)).ToArray());
        Assert.Empty(preset.Quests.Where(q => states.Of(q).Count > 0 && !states.Complete(q, s.Has))
            .Select(q => $"{q}: {states.Current(q, s.Has)?.Text ?? "not started"}"));
        // Books of Skill from a1q6 and a1q7 (a1q9's comes in Act 2), the Book of Regrets' two refund points; the quest items
        // all handed in or used.
        Assert.Equal((2, 2), (s.PassivePoints, s.RefundPoints));
        Assert.Equal(SouthernForest, s.Area);
        Assert.Empty(s.Carried);
        // a1q9 is under way: Piety's blockage seen, the road still blocked (it opens in Act 2).
        Assert.True(s.Has("a1q9-QuestReceived") && s.Has("a1q9-SeenBlockage") && s.Has("a1q9-RoadBlocked"));
        Assert.False(s.Has("a1q9-RoadOpened") || states.Complete("a1q9", s.Has));
        // Hillock's kill levels the character to 2; Fairgraves and Merveil's second form come with their fights.
        Assert.Equal(2, LevelAfterKill(Strand, Act1Monsters.Hillock));
        Assert.True(QuestSpawned(ShipGraveyard, Act1Monsters.Fairgraves) && QuestSpawned(CavernOfAnger, Act1Monsters.MerveilTwisted));
        Assert.False(QuestSpawned(CavernOfAnger, Act1Monsters.Merveil));

        // Brutus must die before Prisoner's Gate, Merveil in her second form before The Southern Forest; the road to The
        // Western Forest waits for Act 2.
        var brutus = new QuestSimulator();
        brutus.Set(["a1q1-KilledHillock", GlyphQuest.PlacedGlyphs]).Enter("1_1_7_1", travel: true).Enter(UpperPrison);
        Assert.Throws<QuestSimulator.StepFailed>(() => brutus.Enter(PrisonersGate));
        brutus.Kill(Act1Monsters.Brutus).Enter(PrisonersGate);
        Assert.Throws<QuestSimulator.StepFailed>(() => brutus.Enter(WesternForest));
        var merveil = new QuestSimulator();
        merveil.Set(["a1q1-KilledHillock", GlyphQuest.PlacedGlyphs, "a1q2-KilledBrutus"]).Enter(CavernOfAnger, travel: true).Kill(Act1Monsters.Merveil);
        Assert.Throws<QuestSimulator.StepFailed>(() => merveil.Enter(SouthernForest));
        merveil.Kill(Act1Monsters.MerveilTwisted).Enter(SouthernForest);

        // Act 2 after Act 1, killing all three bandits: Act2CompleteEramir (it also has Act 3's first step, Clarissa rescued
        // in The City of Sarn); a1q9's and a2q5's books and Eramir's point, a2q5's refund points; The Apex went on the altar.
        Play(s, Act2);
        PresetHolds(s, "Act2CompleteEramir", "a3q1-RescuedGirl");
        Assert.Equal((5, 4), (s.PassivePoints, s.RefundPoints));
        Assert.Equal(SarnEncampment, s.Area);
        Assert.Empty(s.Carried);
        Assert.True(s.Has("FinishedVaalOversoulQuest") && s.Has("a1q9-RoadOpened"));
        // Act 3 after Act 2: Act3Complete (the Act 2 preset's Clarissa too); a3q11's and a3q9's books, a3q11's refund points.
        Play(s, Act3);
        PresetHolds(s, "Act2CompleteEramir");
        PresetHolds(s, "Act3Complete");
        Assert.Equal((7, 6), (s.PassivePoints, s.RefundPoints));
        Assert.Equal("1_4_town", s.Area);
        Assert.Empty(s.Carried);
        Assert.True(QuestSpawned(AncientPyramid, Act2Monsters.VaalOversoul) && QuestSpawned(UpperSceptreOfGod, Act3Monsters.DominusAscendant));

        // The Thaumetic Seal and the tree need their items, the Strange Device Helena's rescue.
        var seal = new QuestSimulator();
        seal.Set(["a1q3-KilledSeaWitch", "a1q2-KilledBrutus"]).Enter(WesternForest, travel: true);
        Assert.Throws<QuestSimulator.StepFailed>(() => seal.Use(Act2Objects.ThaumeticSeal));
        Assert.Throws<QuestSimulator.StepFailed>(() => seal.Enter(PrisonersGate));
        seal.Kill(Act2Monsters.Arteri).PickUp(Act2Items.ThaumeticEmblem).Use(Act2Objects.ThaumeticSeal).Enter(PrisonersGate);
        Assert.DoesNotContain(Act2Items.ThaumeticEmblem, seal.Carried);
        seal.Enter(Wetlands, travel: true);
        Assert.Throws<QuestSimulator.StepFailed>(() => seal.Use(Act2Objects.PoisonTree));
        Assert.Throws<QuestSimulator.StepFailed>(() => seal.Enter(VaalRuins));
        seal.Enter(ChamberOfSins2, travel: true).Kill(Act2Monsters.Fidelitas);
        Assert.Throws<QuestSimulator.StepFailed>(() => seal.OpenChest(Act2Objects.StrangeDevice));

        // The Archives open with the loose candle for the instance only (nothing saved); the tower top's exit to The
        // Aqueduct (disabled in the layout, created because a3q10 gates it) waits for Dominus' second form.
        string[] sarn = ["a1q3-KilledSeaWitch", "a1q2-KilledBrutus", "FinishedVaalOversoulQuest", "a3q11-UsedSewerKeys", "a3q5-UsedTalc"];
        var library = new QuestSimulator();
        library.Set(sarn).Enter(Library, travel: true);
        Assert.Throws<QuestSimulator.StepFailed>(() => library.Enter(Archives));
        library.Use(Act3Objects.LooseCandle);
        Assert.DoesNotContain(Act3Objects.HiddenDoorOpen, library.Flags);
        library.Enter(Archives);
        Assert.Equal("a3q10-KilledDominus", FlagToEnter(UpperSceptreOfGod, Aqueduct));
        var tower = new QuestSimulator();
        tower.Set([.. sarn, "a3q9-UsedTowerKey"]).Enter(UpperSceptreOfGod, travel: true).Kill(Act3Monsters.Dominus);
        Assert.Throws<QuestSimulator.StepFailed>(() => tower.Enter(Aqueduct));
        tower.Kill(Act3Monsters.DominusAscendant).Enter(Aqueduct);

        // One "A Swig of Hope" after the quest row (row 496, both carried) delivers the plum and the decanter, as online.
        var swig = new QuestSimulator();
        swig.Set([.. sarn, "a1q6-KilledFairgraves"]).Enter(Docks, travel: true)
            .Talk(FairgravesDocks, "Introduction").Talk(FairgravesDocks, "A Swig of Hope");
        swig.Carried.AddRange([Act3Items.Plum, Act3Items.Decanter]);
        swig.Set(["a3q13-HavePlum", "a3q13-HaveDecanter"]).Talk(FairgravesDocks, "A Swig of Hope");
        Assert.True(swig.Has("a3q13-PlumDelivered") && swig.Has("a3q13-DecanterDelivered"));
        Assert.Empty(swig.Carried);

        // Act 4 after Act 3: Act4Complete; a4q6's book (a passive and two refund points). a5q1a has no "complete" row (every
        // state needs Visited-5_1 missing: it leaves the log in The Slave Pens).
        Play(s, Act4);
        PresetHolds(s, "Act4Complete");
        Assert.Equal((8, 8), (s.PassivePoints, s.RefundPoints));
        Assert.Equal(SlavePens, s.Area);
        Assert.Empty(s.Carried);
        Assert.True(s.Has("a5q1-UsedResonator") && s.Has("Visited-5_1"));
        Assert.Null(states.Current("a5q1a", s.Has));
        // Act 5: every quest complete, a5q3's and a5q7's books; Kitava never dies, his threshold sets a5q6-CompletedBoss
        // (QuestStaticRewards 38: the −30% Game.ResistancePenalty mirrors).
        Play(s, Act5);
        string[] act5 = ["a5q1b", "a5q2", "a5q3", "a5q4", "a5q5", "a5q6", "a5q7"];
        Assert.Empty(act5.Where(q => !states.Complete(q, s.Has)).Select(q => $"{q}: {states.Current(q, s.Has)?.Text ?? "not started"}"));
        Assert.Equal((10, 10), (s.PassivePoints, s.RefundPoints));
        Assert.True(s.Has("a5q6-CompletedBoss"));
        Assert.Equal(50, ThresholdOf(Act5Areas.CathedralRooftop, Act5Monsters.Kitava)?.LifePct);
        Assert.True(QuestSpawned(Act5Areas.CathedralRooftop, Act5Monsters.Kitava) && QuestSpawned(ChamberOfInnocence, Act5Monsters.Innocence));
        Assert.Equal([("base_resist_all_elements_%", -30), ("base_chaos_damage_resistance_%", -30)],
            QuestStaticRewards.Default.Value.Find("a5q6-CompletedBoss")!.Stats);
        Assert.Contains("a5q6-CompletedBoss", Game.ResistancePenaltyAct5);
        // Lilly waits on the dock only from a5q6-CompletedBoss (lilly.ot serialisation_start_flag).
        Assert.False(NpcPresent(Act5Areas.CathedralRooftop, Act5Npcs.Lilly, With("a5q6-DeliveredItem")));
        Assert.True(NpcPresent(Act5Areas.CathedralRooftop, Act5Npcs.Lilly, With("a5q6-CompletedBoss")));
        Assert.Equal(LioneyesWatchAct6, s.Area);
        Assert.Empty(s.Carried);
        // Act 6: every quest complete, the books of a6q3, a6q4, a6q6 and a6q7, and Weylam's voyage to Act 7's town.
        Play(s, Act6);
        string[] act6 = ["a6q1", "a6q2", "a6q3", "a6q4", "a6q5", "a6q6", "a6q7"];
        Assert.Empty(act6.Where(q => !states.Complete(q, s.Has)).Select(q => $"{q}: {states.Current(q, s.Has)?.Text ?? "not started"}"));
        Assert.Equal((13, 12), (s.PassivePoints, s.RefundPoints));
        Assert.Equal(BridgeEncampment, s.Area);
        Assert.Empty(s.Carried);
        // The Pantheon gods Act 6 unlocks: The Brine King, Tukohama, Abberath and Ryslatha (Sin's rows let them be chosen
        // where they fell: the session flag CanChangePantheon).
        Assert.Equal(["TheBrineKing", "Abberath", "Tukohama", "Ryslatha"],
            PantheonCatalog.Default.Value.Gods.Where(g => PantheonCatalog.Unlocked(g, s.Has)).Select(g => g.Id));
        Assert.True(QuestStates.Default.Value.IsSession("CanChangePantheon"));
        // Acts 7–10 and the Epilogue after Act 6. After Act 8 the flags of acts 7 and 8 in E3EndOfAct8 (the preset is
        // cumulative and lists every earlier act's item-held flags, which a played character no longer has).
        Play(s, Act7);
        Play(s, Act8);
        Assert.Equal([], PresetFlagsOf("E3EndOfAct8", "a7q|a8q").Where(f => !s.Has(f)).ToArray());
        Assert.Equal(Act8Areas.BloodAqueduct, s.Area);
        // Kitava's Act 10 penalty: EndGameAvailable, which every character is given for the Atlas (sent in a hideout
        // and the maps only, Quests.EndgameOnly), is not earned before him (Act 5's −30% only), so the Epilogue stays
        // shut in Oriath Docks: no "Set Sail from Oriath" (1433), no Kitava reward from Lani (1415/1416), no way into
        // Karui Shores. His scripted end earns it (another −30%) and the Act 10 steps below take all three.
        Play(s, Act9);
        Assert.Contains(EndGameAvailable, s.Unearned);
        Assert.False(s.Has(EndGameAvailable));
        Assert.Equal(-30, Penalty(s));
        Assert.Throws<QuestSimulator.StepFailed>(() => s.Talk(Lilly10, "Set Sail from Oriath"));
        Assert.Throws<QuestSimulator.StepFailed>(() => s.Talk(Lani10, "Kitava", IndexOf(Lani10, 1415)));
        Assert.Throws<QuestSimulator.StepFailed>(() => s.Reward(Lani10, "Kitava Reward"));
        Assert.Throws<QuestSimulator.StepFailed>(() => s.Enter(Act10Areas.KaruiShores, travel: true));
        Play(s, Act10);
        Assert.DoesNotContain(EndGameAvailable, s.Unearned);
        Assert.Equal(-60, Penalty(s));
        // The Epilogue's EpilogueAtlasDemo: acts 7–10's quest flags and EndGameAvailable; every quest of acts 7–10, A11q0
        // and a10q3a11 at its "complete" row; the campaign's 24 quest passive points and 20 refund points; Karui Shores.
        Assert.Equal([], PresetFlagsOf("EpilogueAtlasDemo", "a7q|a8q|a9q|a10q|A11|EndGameAvailable").Where(f => !s.Has(f)).ToArray());
        string[] lateQuests = [.. Enumerable.Range(7, 4).SelectMany(a => states.QuestsOfAct(a)).Where(q => System.Text.RegularExpressions.Regex.IsMatch(q, @"^a\d+q")),
            "a10q3a11", "A11q0"];
        Assert.Equal(29, lateQuests.Distinct().Count());
        Assert.Empty(lateQuests.Where(q => !states.Complete(q, s.Has)).Select(q => $"{q}: {states.Current(q, s.Has)?.Text ?? "not started"}"));
        Assert.Equal((24, 20), (s.PassivePoints, s.RefundPoints));
        Assert.Equal(Act10Areas.KaruiShores, s.Area);
        Assert.Empty(s.Carried);
        Assert.True(QuestSpawned(Act9Areas.RottingCore, Act9Monsters.DepravedTrinity) && QuestSpawned(Act9Areas.Oasis, Act9Monsters.Shakari2)
            && QuestSpawned(Act8Areas.HarbourBridge, Act8Monsters.Solaris) && QuestSpawned(Act10Areas.CathedralRooftop, Act10Monsters.Plaguewing));

        // Highgate's Dialla: DiallaTown until the Rapture Device works, DiallaTownWithered after (both town room lines).
        Assert.True(NpcPresent(Highgate, Act4Npcs.DiallaTown, With()) && !NpcPresent(Highgate, Act4Npcs.DiallaTownWithered, With()));
        Assert.True(!NpcPresent(Highgate, Act4Npcs.DiallaTown, With("a4q1-RaptureActivated"))
            && NpcPresent(Highgate, Act4Npcs.DiallaTownWithered, With("a4q1-RaptureActivated")));
        // Oriath burns with Innocence: the Control Blocks' and the Chamber's exits lead to their own version of the area.
        Assert.True(Exists(Act5Objects.ControlBlocksToSquare, With()) && !Exists(Act5Objects.ControlBlocksToDestroyedSquare, With()));
        Assert.True(!Exists(Act5Objects.ControlBlocksToSquare, With("a5q4-KilledBoss")) && Exists(Act5Objects.ControlBlocksToDestroyedSquare, With("a5q4-KilledBoss")));
        Assert.Equal(TorchedCourts, ExitTo(Act5Objects.InnocenceToDestroyedCourts));

        // From /quest_preset Act3Complete (CampaignState.PresetFlags: the preset's quests completed): the Mines wait for the
        // banner on Deshret's Seal, The Harvest for Piety's talk; one "Malachai's Organs" click takes
        // all three organs.
        var act3 = new QuestSimulator();
        act3.Set(CampaignState.PresetFlags("Act3Complete", out _)!).Enter(Highgate, travel: true);
        Assert.Throws<QuestSimulator.StepFailed>(() => act3.Enter(Mines1));
        Assert.Throws<QuestSimulator.StepFailed>(() => act3.Use(Act4Objects.Seal));
        act3.Set(["a4q1-RaptureActivated", "a4q1-DiallaOnTransformed"]).Enter(Belly2, travel: true);
        Assert.Throws<QuestSimulator.StepFailed>(() => act3.Enter(Harvest));
        act3.Set(["a4q5-QuestReceived"]).Enter(Harvest).Kill(Act4Monsters.Doedre).PickUp(Act4Items.Lungs).Kill(Act4Monsters.Maligaro)
            .PickUp(Act4Items.Heart).Kill(Act4Monsters.Shavronne).PickUp(Act4Items.Entrails).Talk(Act4Npcs.PietyHarvest, "Malachai's Organs", 1);
        Assert.All(new[] { "a4q5-DeliveredOrgan1", "a4q5-DeliveredOrgan2", "a4q5-DeliveredOrgan3" }, f => Assert.True(act3.Has(f), f));
        Assert.Empty(act3.Carried);
        // The Oriath Portal on The Ascent waits for the Resonator's lever.
        var ascent = new QuestSimulator();
        ascent.Set(CampaignState.PresetFlags("Act4Complete", out _)!).Set(["a5q1-UsedResonator"], false).Enter(Ascent, travel: true);
        Assert.Throws<QuestSimulator.StepFailed>(() => ascent.Enter(SlavePens));
        ascent.Near(Act4Objects.ResonatorLever).Use(Act4Objects.ResonatorLever).Enter(SlavePens);
        // Without the Sign of Purity the rooftop stays shut (EndQuestFlags a5q6-SeenItem); the socket takes it.
        var oriath = new QuestSimulator();
        oriath.Set([.. CampaignState.PresetFlags("Act4Complete", out _)!, "a5q1-KilledBoss", "a5q2-KilledBoss", "a5q4-KilledBoss"])
            .Enter(RuinedSquare, travel: true).Enter(Act5Areas.Ossuary);
        Assert.Throws<QuestSimulator.StepFailed>(() => oriath.Enter(Act5Areas.CathedralRooftop, travel: true));
        oriath.OpenChest(Act5Objects.PurityBox).PickUp(Act5Items.SignOfPurity).Enter(Act5Areas.CathedralRooftop, travel: true)
            .Use(Act5Objects.ArenaSocket);
        Assert.True(oriath.Has("a5q6-DeliveredItem") && !oriath.Has("a5q6-HaveItem") && !oriath.Has("a5q6-CompletedBoss"));
        oriath.Kill(Act5Monsters.Kitava);
        Assert.True(oriath.Has("a5q6-CompletedBoss"));

        // Act 7: the barrier before Level 2 lifts only with the Obsidian Key; Arakaali's bridge into Act 8 needs her death.
        var sins = FromAct7();
        sins.Enter(Act7Areas.SinsLevel1, travel: true);
        Assert.Throws<QuestSimulator.StepFailed>(() => sins.Near(Act7Objects.SinsBarrier));
        Assert.Throws<QuestSimulator.StepFailed>(() => sins.Enter(Act7Areas.SinsLevel2));
        sins.Set(["a7q1-KilledBoss", "a7q7-DeliveredItems"]).Enter(Act7Areas.TempleOfDecay2, travel: true);
        Assert.Throws<QuestSimulator.StepFailed>(() => sins.Enter(Act7Areas.SarnRamparts));
        // Act 8: the goddesses' fight needs both orbs; the way into Act 9 both goddesses dead.
        sins.Set(["a8q1-KilledBoss"]).Enter(Act8Areas.HarbourBridge, travel: true);
        Assert.Throws<QuestSimulator.StepFailed>(() => sins.Use(Act8Objects.GoddessFightStarter));
        sins.Set(["a8q2-HaveItem", "a8q3-HaveItem"]).Use(Act8Objects.GoddessFightStarter).Kill(Act8Monsters.Lunaris);
        Assert.Throws<QuestSimulator.StepFailed>(() => sins.Enter(Act8Areas.BloodAqueduct));
        sins.Kill(Act8Monsters.Solaris).Enter(Act8Areas.BloodAqueduct);
        // One click hands in every item a row takes: Sin's "_take_ingredients_" (1336) the powder and the acid, Yeena's
        // "Fireflies" (1042) all seven.
        var hands = FromAct7();
        hands.Set(["a8q23-KilledBosses"]).Enter(Act9Areas.Highgate, travel: true).Talk(Sin9, "The Dark Ember");
        hands.Carried.AddRange([Act9Items.TrarthanPowder, Act9Items.BasiliskAcid]);
        hands.Set(["a9q1-HaveItem1", "a9q1-HaveItem2"]).Talk(Sin9, "_take_ingredients_");
        Assert.True(hands.Has("a9q1-DeliveredItem1") && hands.Has("a9q1-DeliveredItem2"));
        Assert.Empty(hands.Carried);
        hands.Set(["a7q1-KilledBoss"]).Enter(Act7Areas.VaalCity, travel: true);
        hands.Carried.AddRange(Act7Items.Fireflies);
        hands.Set([.. Enumerable.Range(1, 7).Select(n => $"a7q7-HaveItem{n}")]).Talk(Act7Objects.YeenaVaalCity, "Fireflies");
        Assert.True(hands.Has("a7q7-DeliveredItems"));
        Assert.Empty(hands.Carried);
        // Act 10: Bannon stands in Oriath Docks until he becomes Innocence, then InnocenceTown in his place; the horn gate
        // into The Canals and Sin's portal home wait for Innocence and Kitava.
        Assert.True(NpcPresent(Act10Areas.OriathDocks, Act10Objects.BannonTown, With()) && !NpcPresent(Act10Areas.OriathDocks, Act10Objects.InnocenceTown, With()));
        Assert.True(!NpcPresent(Act10Areas.OriathDocks, Act10Objects.BannonTown, With("a10q2-BannonTransformed"))
            && NpcPresent(Act10Areas.OriathDocks, Act10Objects.InnocenceTown, With("a10q2-BannonTransformed")));
        Assert.Equal("a10q3-OpenedGate", FlagToEnter(Act10Areas.RavagedSquare, Act10Areas.Canals));
        Assert.Equal(EndGameAvailable, FlagToEnter(Act10Areas.FeedingTrough, Act10Areas.OriathDocks));
        hands.Set(["a9q1-KilledBoss", "a10q3-OpenedGate"]).Enter(Act10Areas.FeedingTrough, travel: true);
        Assert.Throws<QuestSimulator.StepFailed>(() => hands.Enter(Act10Areas.OriathDocks));
        hands.Kill(Act10Monsters.Kitava).Enter(Act10Areas.OriathDocks);
    }

    /// <summary>The preset's flags of these quest prefixes (a regex alternation; a whole flag name matches itself), but
    /// those the driver's sequence leaves out: The Crossroads' BanditBlock (the area cannot be built at seed 1), Helena's
    /// map clue (log text), Sin's Arakaali talk in town (no reward), Yeena's delivery clue in town.</summary>
    static IEnumerable<string> PresetFlagsOf(string presetId, string prefixes) =>
        QuestStates.Default.Value.FindPreset(presetId)!.Flags.Where(f => System.Text.RegularExpressions.Regex.IsMatch(f, $"^({prefixes})")
            && f is not ("a7q1-SeenBlockage" or "a7q2-MapClue2" or "a7q4-QuestRewardTaken" or "a7q7-DeliveryClue"));

    /// <summary>Kitava's resistance penalty for the simulated character, as the Game module counts it
    /// (Game.ResistancePenalty: Act 5's flags, and Act 10's once earned).</summary>
    static int Penalty(QuestSimulator s)
    {
        var conn = new ClientState(new AreaLayout(0x1234, "2_10_town", 200, 200, 70, 70, null, []), 1, 1)
        {
            QuestFlags = [.. s.Flags], UnearnedQuestFlags = [.. s.Unearned],
        };
        return Game.ResistancePenalty(conn);
    }
}
