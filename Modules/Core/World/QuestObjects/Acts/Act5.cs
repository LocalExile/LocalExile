using static Quests;
using static Act4Areas;
using static Act5Areas;

/// <summary>WorldAreas Ids of Act 5's quest areas (and Act 6's town, Lioneye's Watch, where Lilly sails).</summary>
static class Act5Areas
{
    public const string OverseersTower = "1_5_town", ControlBlocks = "1_5_2", OriathSquare = "1_5_3", TemplarCourts = "1_5_4",
        ChamberOfInnocence = "1_5_5", RuinedSquare = "1_5_3b", TorchedCourts = "1_5_4b", Ossuary = "1_5_6", Reliquary = "1_5_7",
        CathedralRooftop = "1_5_8", LioneyesWatchAct6 = "2_6_town";
}

/// <summary>MonsterVarieties Ids of Act 5's quest monsters (rows in brackets).</summary>
static class Act5Monsters
{
    public const string Krow = "Metadata/Monsters/TemplarSlaveDriver/TemplarSlaveDriverBoss";  // 5199 Overseer Krow
    public const string Casticus = "Metadata/Monsters/BlackguardInquisitor/Bloodwing";        // 5213 Justicar Casticus
    public const string Avarius = "Metadata/Monsters/AvariusCasticus/AvariusCasticus";       // 5466 High Templar Avarius
    public const string Innocence = "Metadata/Monsters/AvariusCasticus/AvariusCasticusDivine"; // 5475 Innocence, God-Emperor of Eternity
    public const string Utula = "Metadata/Monsters/BlacksmithGolem/BlacksmithGolem";          // 5770 Utula, Stone and Steel (mapboss1)
    public const string Kitava = "Metadata/Monsters/KitavaBoss/Kitava1";                      // 5747 Kitava, the Insatiable (force:mapboss)
}

/// <summary>BaseItemTypes Ids of Act 5's quest items dropped by rules (rows in brackets).</summary>
static class Act5Items
{
    public const string Miasmeter = "Metadata/Items/QuestItems/Act5/Miasmeter";        // 1568
    public const string EyesOfZeal = "Metadata/Items/QuestItems/Act5/TemplarCourtKey"; // 1569
    public const string SignOfPurity = "Metadata/Items/QuestItems/Act5/KitavaKey";     // 1570
    public const string TukohamasTooth = "Metadata/Items/QuestItems/Act5/Torment1";    // 1571
    public const string HinekorasHair = "Metadata/Items/QuestItems/Act5/Torment2";     // 1572
    public const string ValakosJaw = "Metadata/Items/QuestItems/Act5/Torment3";        // 1573
}

/// <summary>.ot types and Chests.dat Ids of Act 5's quest objects and chests the engine creates or a rule watches.</summary>
static class Act5Objects
{
    public const string SupplyChest = "Metadata/Chests/QuestChests/Vilenta/SupplyChest";                 // Chests 201
    public const string TemplarCourtsDoor = "Metadata/QuestObjects/Act5/TemplarCourtsDoor";
    public const string ArenaSocket = "Metadata/Terrain/Act5/Area8/Objects/ArenaSocket";                  // bossentry.arm
    public const string PurityBox = "Metadata/Chests/QuestChests/Ossuary/PurityBox";                     // the Tomb of the First Templar
    public const string CathedralRoofBlocker = "Metadata/QuestObjects/Act5/CathedralRoofBlocker",
        CathedralRoofFlame = "Metadata/QuestObjects/Act5/CathedralRoofFlame";
    public static readonly string[] RelicCases =                                                          // Chests 203–205
        ["Metadata/Chests/QuestChests/Reliquary/RelicCase1", "Metadata/Chests/QuestChests/Reliquary/RelicCase2",
         "Metadata/Chests/QuestChests/Reliquary/RelicCase3"];
    // Area transitions by their tile type (serialisation flags): the court gate, and the exits that swap when Oriath burns.
    public const string OriathSquareToCourts = "Metadata/QuestObjects/Act5/OriathSquareToCourts",
        ControlBlocksToSquare = "Metadata/QuestObjects/Act5/ControlBlocksToSquare",
        ControlBlocksToDestroyedSquare = "Metadata/QuestObjects/Act5/ControlBlocksToDestroyedSquare",
        InnocenceToCourts = "Metadata/QuestObjects/Act5/InnocenceToCourts",
        InnocenceToDestroyedCourts = "Metadata/QuestObjects/Act5/InnocenceToDestroyedCourts";
}

/// <summary>NPCs.dat / .ot Ids of Act 5's quest NPCs.</summary>
static class Act5Npcs
{
    public const string Utula = "Metadata/NPC/Act5/Utula", BannonTown = "Metadata/NPC/Act5/BannonTown",
        BannonInnocence = "Metadata/NPC/Act5/BannonInnocence", SinInnocence = "Metadata/NPC/Act5/SinInnocence",
        SinOssuary = "Metadata/NPC/Act5/SinOssuary", SinKitava = "Metadata/NPC/Act5/SinKitava", Lilly = "Metadata/NPC/Act5/Lilly";
}

/// <summary>
/// Act 5's quests as the engine's tables (Agents/docs/game/campaign/act5.md, 12-quests.md §14). Oriath: Overseer's Tower is
/// the town. a5q1b (Krow) opens it, a5q2 (Casticus, the Eyes of Zeal) Oriath Square, a5q4 (the Templar Courts door, Avarius
/// and Innocence) turns Oriath into its ruined version, a5q6 (the Sign of Purity, Kitava on the Cathedral Rooftop) ends the
/// act: Kitava cannot be beaten, at his threshold a5q6-CompletedBoss is set (QuestStaticRewards 38: −30% to all
/// resistances, Game.ResistancePenalty) and Lilly sails the player to Lioneye's Watch (NPCTalk 893 AreaTransitionTo
/// 2_6_town). Side quests: a5q3 (the Miasmeter), a5q5 (Utula), a5q7 (Kitava's Torments).
/// The game's order (Quest.dat rows): a5q1b (63), a5q2, a5q3, a5q4, a5q5, a5q6, a5q7 (69). The order the campaign plays
/// them (scripts/dev/campaign/order.py): a5q1b, a5q3, a5q2, a5q4, a5q5, a5q7, a5q6.
/// </summary>
sealed class Act5 : Act
{
    public override int Number => 5;

    public override IReadOnlyList<Quest> Table =>
        [
            // a5q1b "Return to Oriath": Overseer Krow (tower_01.arm mapboss) guards the way into Overseer's Tower and The
            // Control Blocks (both EndQuestFlags a5q1-KilledBoss). SeenTown has no setter in the data: seeing the tower's
            // exit. Lani's "Overseer Reward" is offer 34.
            new Quest("a5q1b")
            {
                On.NearExit(SlavePens, OverseersTower, "a5q1-SeenTown"),
                On.Seen(SlavePens, Act5Monsters.Krow, "a5q1-SeenBoss"),
                On.Killed(SlavePens, Act5Monsters.Krow, "a5q1-KilledBoss"),
                On.Exit(SlavePens, OverseersTower, "a5q1-KilledBoss"),
            },
            // a5q2 "The Key to Freedom": Justicar Casticus (boss_01.arm mapboss) drops the Eyes of Zeal (wiki drop_monsters =
            // Bloodwing; TemplarCourtKey.it: grant a5q2-HaveKey, remove a5q2-UsedKey); his death opens Oriath Square
            // (EndQuestFlags a5q2-KilledBoss). Lani's "Casticus Reward" is offer 35.
            new Quest("a5q2")
            {
                On.Seen(ControlBlocks, Act5Monsters.Casticus, "a5q2-SeenBoss"),
                On.Killed(ControlBlocks, Act5Monsters.Casticus, "a5q2-KilledBoss"),
                On.Drops(ControlBlocks, Act5Monsters.Casticus, Act5Items.EyesOfZeal),
                On.Exit(ControlBlocks, OriathSquare, "a5q2-KilledBoss"),
            },
            // a5q3 "In Service to Science": the Experimental Supplies (pietylab_01.arm, Chests 201) hold the Miasmeter
            // (remove_flag a5q3-DeliveredItem: Vilenta's 849 takes it). Vilenta's "Miasmeter Reward" is offer 36.
            new Quest("a5q3")
            {
                On.Chest(ControlBlocks, Act5Objects.SupplyChest, Act5Items.Miasmeter),
            },
            // a5q4 "Death to Purity": the Eyes of Zeal open the Templar Courts door (a tile object; its court gate
            // OriathSquareToCourts exists only from a5q2-UsedKey: serialisation_start_flag). QuestReceived has no setter in
            // the data (E3KitavaKey has it, ConsoleOriathSquare not yet): set with the door. High Templar Avarius (boss_02.arm
            // mapboss) becomes Innocence (set_monster_has_second_form, MV 5475 not a Bosses row), whose death sets KilledBoss:
            // Oriath burns (the exits swap by serialisation flags, each to its own version of the area) and Sin appears where
            // Innocence fell (no room places him; NPCTalk 882 → 883 → 884 gives a5q6). Lani's "Avarius Reward" is offer 37.
            new Quest("a5q4")
            {
                On.Object(OriathSquare, Act5Objects.TemplarCourtsDoor, "a5q2-UsedKey"),
                On.UsedWith(OriathSquare, Act5Objects.TemplarCourtsDoor, ["a5q2-HaveKey"], "a5q2-UsedKey", "a5q4-QuestReceived"),
                On.Exists(Act5Objects.OriathSquareToCourts, from: "a5q2-UsedKey"),
                On.Exit(OriathSquare, TemplarCourts, "a5q2-UsedKey"),
                On.Seen(ChamberOfInnocence, Act5Monsters.Avarius, "a5q4-SeenBoss"),
                On.SecondForm(ChamberOfInnocence, Act5Monsters.Avarius, Act5Monsters.Innocence),
                On.Seen(ChamberOfInnocence, Act5Monsters.Innocence, "a5q4-SeenInnocence"),
                On.Killed(ChamberOfInnocence, Act5Monsters.Innocence, "a5q4-KilledBoss"),
                On.Appears(ChamberOfInnocence, Act5Monsters.Innocence, Act5Npcs.SinInnocence, "a5q6-QuestReceived"),
                On.Exists(Act5Objects.ControlBlocksToSquare, until: "a5q4-KilledBoss", to: OriathSquare),
                On.Exists(Act5Objects.ControlBlocksToDestroyedSquare, from: "a5q4-KilledBoss", to: RuinedSquare),
                On.Exists(Act5Objects.InnocenceToCourts, until: "a5q4-KilledBoss", to: TemplarCourts),
                On.Exists(Act5Objects.InnocenceToDestroyedCourts, from: "a5q4-KilledBoss", to: TorchedCourts),
            },
            // a5q5 "The King's Feast": Bannon waits by the Chamber of Innocence's waypoint after Avarius (wiki; NPCTalk 854
            // "Rescue"), then stands in Overseer's Tower; Utula has left the town (Lani's and Vilenta's traitor gossip) and
            // fights in The Ruined Square (park.arm mapboss1). Bannon's "Utula Reward" is offer 38.
            new Quest("a5q5")
            {
                On.Npc(ChamberOfInnocence, Act5Npcs.BannonInnocence, null, shownBy: "a5q4-KilledBoss"),
                On.Npc(OverseersTower, Act5Npcs.BannonTown, null, shownBy: "BannonRescued"),
                On.Npc(OverseersTower, Act5Npcs.Utula, "a5q4-KilledBoss"),
                On.Seen(RuinedSquare, Act5Monsters.Utula, "a5q5-SeenBoss"),
                On.Killed(RuinedSquare, Act5Monsters.Utula, "a5q5-KilledBoss"),
            },
            // a5q6 "The Ravenous God": the Tomb of the First Templar in The Ossuary (a tile chest) holds the Sign of Purity
            // (KitavaKey.it: grant a5q6-HaveItem, extra a5q6-SeenItem, remove a5q6-DeliveredItem); SeenItem lifts the Ruined
            // Square's flame wall (18 CathedralRoofBlockers, object_size 4) and opens the rooftop (EndQuestFlags). The Sign in
            // its socket (bossentry.arm, placed from the server's own rooms) wakes Kitava there (the area does not place him:
            // Quests.QuestSpawned). Kitava never dies: at about half life he destroys the Sign (wiki), CompletedBoss is set
            // (QSR 38: −30%, Game.ResistancePenaltyAct5) and he leaves (the engine's threshold, On.Threshold); Sin and Lilly
            // (lilly.ot serialisation_start_flag a5q6-CompletedBoss) wait on sin_dock.arm, and Lilly's "Sail to Wraeclast"
            // (893) moves the player to Lioneye's Watch.
            new Quest("a5q6")
            {
                On.Chest(Ossuary, Act5Objects.PurityBox, Act5Items.SignOfPurity),
                On.Blocker(RuinedSquare, Act5Objects.CathedralRoofBlocker, 3, "a5q6-SeenItem"),
                On.Exists(Act5Objects.CathedralRoofFlame, until: "a5q6-SeenItem"),
                On.Npc(Ossuary, Act5Npcs.SinOssuary, null, shownBy: "a5q6-SeenItem"),
                On.Object(CathedralRooftop, Act5Objects.ArenaSocket, "a5q6-DeliveredItem"),
                On.UsedWith(CathedralRooftop, Act5Objects.ArenaSocket, ["a5q6-HaveItem"], "a5q6-DeliveredItem"),
                On.Spawns(CathedralRooftop, Act5Objects.ArenaSocket, Act5Monsters.Kitava),
                On.Seen(CathedralRooftop, Act5Monsters.Kitava, "a5q6-SeenBoss"),
                On.Threshold(CathedralRooftop, Act5Monsters.Kitava, 50, "a5q6-CompletedBoss"),
                On.Npc(CathedralRooftop, Act5Npcs.SinKitava, null, shownBy: "a5q6-CompletedBoss"),
                On.Exists(Act5Npcs.Lilly, from: "a5q6-CompletedBoss"),
            },
            // a5q7 "Kitava's Torments": the three Karui Relic Cases of The Reliquary (kitavachest_01-03.arm, Chests 203-205)
            // hold the Torments (remove_flag a5q7-DeliveredItems: Lani's 828 takes all three). Lani's "Torments Reward" (offer
            // 39) is a Book of Skill (QSR 13: +1 passive, +2 respec).
            new Quest("a5q7")
            {
                On.Chest(Reliquary, Act5Objects.RelicCases[0], Act5Items.TukohamasTooth),
                On.Chest(Reliquary, Act5Objects.RelicCases[1], Act5Items.HinekorasHair),
                On.Chest(Reliquary, Act5Objects.RelicCases[2], Act5Items.ValakosJaw),
            },
        ];
}
