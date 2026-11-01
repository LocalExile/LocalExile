using static Quests;
using static Act2Areas;
using static Act3Areas;

/// <summary>WorldAreas Ids of Act 3's quest areas (and Act 4's first, The Aqueduct, where the tower top's exit leads).</summary>
static class Act3Areas
{
    public const string SarnEncampment = "1_3_town", Slums = "1_3_2", Crematorium = "1_3_3_1", Marketplace = "1_3_5", Battlefront = "1_3_7",
        SolarisTemple2 = "1_3_8_2", Docks = "1_3_9", Sewers = "1_3_10_1", EbonyBarracks = "1_3_13", LunarisTemple2 = "1_3_14_2",
        ImperialGardens = "1_3_15", Library = "1_3_17_1", Archives = "1_3_17_2", SceptreOfGod = "1_3_18_1", UpperSceptreOfGod = "1_3_18_2",
        Aqueduct = "1_4_1";
}

/// <summary>MonsterVarieties Ids of Act 3's quest monsters (rows in brackets).</summary>
static class Act3Monsters
{
    public const string GuardCaptain = "Metadata/Monsters/Axis/GuardLieutenantClarissa";    // 1416, Clarissa's guards
    public const string PietyEscape = "Metadata/Monsters/Axis/PietyEscape";                 // 1424, at Tolman's chair
    public const string Gravicius = "Metadata/Monsters/Axis/AxisGeneral";                   // 1402 General Gravicius
    public const string Piety = "Metadata/Monsters/Axis/PietyBoss";                         // 1421, The Lunaris Temple
    public const string Dominus = "Metadata/Monsters/Pope/Pope";                            // 1678 Dominus, High Templar (mapboss2)
    public const string DominusAscendant = "Metadata/Monsters/Dominusdemon/Dominusdemon";   // 1688, his second form
}

/// <summary>BaseItemTypes Ids of Act 3's quest items (rows in brackets).</summary>
static class Act3Items
{
    public const string TolmansBracelet = "Metadata/Items/QuestItems/TolmanBracelet";        // 1546
    public const string RibbonSpool = "Metadata/Items/QuestItems/RibbonSpool";               // 1547
    public const string ThaumeticSulphite = "Metadata/Items/QuestItems/SulphiteFlask";       // 1545
    public const string TowerKey = "Metadata/Items/QuestItems/TowerKey";                     // 1548
    public const string Decanter = "Metadata/Items/QuestItems/Fairgraves/Decanter";          // 1558 Decanter Spiritus
    public const string Plum = "Metadata/Items/QuestItems/Fairgraves/Fruit";                 // 1559 Chitus' Plum
    public static readonly string[] Busts =                                                  // 1551–1553
        ["Metadata/Items/QuestItems/Busts/Bust1", "Metadata/Items/QuestItems/Busts/Bust2", "Metadata/Items/QuestItems/Busts/Bust3"];
    public static readonly string[] GoldenPages =                                            // 1554–1557
        ["Metadata/Items/QuestItems/GoldenPages/Page1", "Metadata/Items/QuestItems/GoldenPages/Page2",
         "Metadata/Items/QuestItems/GoldenPages/Page3", "Metadata/Items/QuestItems/GoldenPages/Page4"];
}

/// <summary>Act 3's quest objects, chests and out-of-town NPCs.</summary>
static class Act3Objects
{
    public const string ClarissaSlums = "Metadata/NPC/Act3/ClarissaSlums", TolmanChair = "Metadata/Chests/QuestChests/Tolman/TolmanChair";
    public const string SewerGrating = "Metadata/QuestObjects/Sewers/SewersLockedDoor";
    public static readonly string[] Stashes =
        ["Metadata/Chests/QuestChests/Victario/Stash1", "Metadata/Chests/QuestChests/Victario/Stash2", "Metadata/Chests/QuestChests/Victario/Stash3"];
    public const string Dialla = "Metadata/NPC/Act3/Dialla", RibbonSpoolChest = "Metadata/Chests/QuestChests/Dialla/RibbonSpoolChest";
    public const string SulphiteChest = "Metadata/Chests/QuestChests/Dialla/SulphiteChest";
    public const string BioWall = "Metadata/QuestObjects/Sewers/BioWall", BioWallBlocker = "Metadata/QuestObjects/Sewers/BioWallBlocker";
    public const string BridgeBarrier = "Metadata/QuestObjects/BridgeBarrier";
    public const string Siosa = "Metadata/NPC/Act3/Siosa";
    public const string LooseCandle = "Metadata/QuestObjects/Library/HiddenDoorTrigger", HiddenDoor = "Metadata/QuestObjects/Library/HiddenDoor";
    /// <summary>The Library's hidden door is open in this instance (no QuestFlags row: the game opens it per instance).</summary>
    public static readonly string HiddenDoorOpen = InstanceFlag("LibraryHiddenDoor");
    public static readonly string[] BookStands =
        ["Metadata/Chests/QuestChests/Siosa/GoldenBookStand1", "Metadata/Chests/QuestChests/Siosa/GoldenBookStand2",
         "Metadata/Chests/QuestChests/Siosa/GoldenBookStand3", "Metadata/Chests/QuestChests/Siosa/GoldenBookStand4"];
    public const string Fairgraves = "Metadata/NPC/Act3/Fairgraves";
    public const string DecanterChest = "Metadata/Chests/QuestChests/Fairgraves/DecanterChest", PlumTree = "Metadata/Chests/QuestChests/Fairgraves/FruitTree";
    public const string EpicDoorLock = "Metadata/QuestObjects/EpicDoor/EpicDoorLock", EpicDoor = "Metadata/QuestObjects/EpicDoor/EpicDoor",
        EpicDoorBlocker = "Metadata/QuestObjects/EpicDoor/EpicDoorBlocker";
    public const string DiallaTower = "Metadata/NPC/Act3/DiallaTower", DeathPortalLocator = "Metadata/Monsters/Pope/DeathPortalLocator",
        TowerExit = "Metadata/QuestObjects/DominusTowerExit";
    public const string Hargan = "Metadata/NPC/Act3/Hargan";
}

/// <summary>
/// Act 3's quests as the engine's tables (Agents/docs/game/campaign/act3.md, 12-quests.md §12). The order the campaign
/// plays them (scripts/dev/campaign/order.py): a2q8a3 (arrival), a3q1 with a3q2 (Clarissa, Tolman's bracelet, the Sewer
/// Keys), a3q11, a3q4, a3q3, a3q5, a3q13, a3q8, a3q9, a3q12, a3q10.
/// </summary>
sealed class Act3 : Act
{
    public override int Number => 3;

    public override IReadOnlyList<Quest> Table =>
        [
            // a3q1 "Lost in Love": Clarissa kneels among her guards in The City of Sarn (clarissa.arm; the Slums' hostage
            // rooms are the alternatives): ClarissaSlums, Transitionable 1 (not targetable) until the Guard Captain (mapboss)
            // dies, then 2; her "Tolman" sets a3q1-RescuedGirl. In The Crematorium Piety escapes from Tolman's chair
            // (TolmanChair, a chest with an NPC component, 3 states): it holds Tolman's Bracelet (grant a3q2-HaveTolmanItem,
            // extra a3q2-SeenTolmanItem), which ClarissaTown's "Tolman" takes. Maramoa's "Clarissa Reward" is offer 18 (gems).
            new Quest("a3q1")
            {
                On.Near(CityOfSarn, Act3Objects.ClarissaSlums, "a3q1-SeenGirl"),
                On.Npc(CityOfSarn, Act3Objects.ClarissaSlums, goneWith: "a3q1-RescuedGirl", freedBy: Act3Monsters.GuardCaptain),
                On.Near(Slums, Act3Objects.ClarissaSlums, "a3q1-SeenGirl"),
                On.Npc(Slums, Act3Objects.ClarissaSlums, goneWith: "a3q1-RescuedGirl", freedBy: Act3Monsters.GuardCaptain),
                On.Seen(Crematorium, Act3Monsters.PietyEscape, "a3q2-SeenPiety"),
                On.Chest(Crematorium, Act3Objects.TolmanChair, Act3Items.TolmansBracelet),
            },
            // a3q2 "Lost in Love" (the Sewer Keys): ClarissaTown's "Take Sewer Keys" is offer 19 (grant a3q11-HaveSewerKeys,
            // remove a3q11-UsedSewerKeys). The sewer gratings of The Slums and The Marketplace (SewersLockedDoor, tile record
            // slum_sewer_entrance_v02_01_sewergrating2) used with them set a3q11-UsedSewerKeys: the grating opens and the
            // sewer entrance appears; seven areas' EndQuestFlags have the flag.
            new Quest("a3q2")
            {
                On.Near(Slums, Act3Objects.SewerGrating, "a3q5-SeenSewerGrating"),
                On.Object(Slums, Act3Objects.SewerGrating, "a3q11-UsedSewerKeys"),
                On.UsedWith(Slums, Act3Objects.SewerGrating, ["a3q11-HaveSewerKeys"], "a3q11-UsedSewerKeys"),
                On.Exit(Slums, Sewers, "a3q11-UsedSewerKeys"),
                On.Object(Marketplace, Act3Objects.SewerGrating, "a3q11-UsedSewerKeys"),
                On.UsedWith(Marketplace, Act3Objects.SewerGrating, ["a3q11-HaveSewerKeys"], "a3q11-UsedSewerKeys"),
                On.Exit(Marketplace, Sewers, "a3q11-UsedSewerKeys"),
            },
            // a3q11 "Victario's Secrets": Victario's three stashes in The Sewers (Chests.dat rows without an .ot,
            // hideout_grass_1–3.arm) hold the Platinum Busts; no NPCTalk row sets their remove_flags (a3q11-DeliveredBust1–3),
            // so Hargan's "Victario's Holdfasts" hands in each bust carried. His reward (offer 20) is a Book of Skill: one
            // passive and two refund points.
            new Quest("a3q11")
            {
                On.Chest(Sewers, Act3Objects.Stashes[0], Act3Items.Busts[0]),
                On.Chest(Sewers, Act3Objects.Stashes[1], Act3Items.Busts[1]),
                On.Chest(Sewers, Act3Objects.Stashes[2], Act3Items.Busts[2]),
                On.HandIn(Act3Objects.Hargan, "Victario's Holdfasts", Act3Items.Busts[0]),
                On.HandIn(Act3Objects.Hargan, "Victario's Holdfasts", Act3Items.Busts[1]),
                On.HandIn(Act3Objects.Hargan, "Victario's Holdfasts", Act3Items.Busts[2]),
            },
            // a3q3 "The Gemling Queen": Lady Dialla in The Solaris Temple Level 2 (queenroom_1.arm), the hub of a3q3–a3q5.
            new Quest("a3q3")
            {
                On.Npc(SolarisTemple2, Act3Objects.Dialla, goneWith: null),
            },
            // a3q4 "The Ribbon Spool": the Blackguard Chest on The Battlefront's bridge (bridge_1.arm). Dialla's reward (offer
            // 21) is magic and rare amulets; with none built, its taken flag is set without an item.
            new Quest("a3q4")
            {
                On.Chest(Battlefront, Act3Objects.RibbonSpoolChest, Act3Items.RibbonSpool),
            },
            // a3q5 "Fiery Dust": the Undying blockage in The Sewers (BioWall, biowall_waypoint_1.arm) seen; the Thaumetic
            // Sulphite from the Supply Container in The Docks; Dialla's Infernal Talc (offer 22, grant a3q5-HaveTalc, remove
            // a3q5-UsedTalc) burns the blockage: a3q5-UsedTalc lifts the BioWallBlockers and opens the west of Sarn (six
            // areas' EndQuestFlags).
            new Quest("a3q5")
            {
                On.Near(Sewers, Act3Objects.BioWall, "a3q5-SeenSphincter"),
                On.Chest(Docks, Act3Objects.SulphiteChest, Act3Items.ThaumeticSulphite),
                On.Object(Sewers, Act3Objects.BioWall, "a3q5-UsedTalc"),
                On.UsedWith(Sewers, Act3Objects.BioWall, ["a3q5-HaveTalc"], "a3q5-UsedTalc"),
                On.Blocker(Sewers, Act3Objects.BioWallBlocker, 4, "a3q5-UsedTalc"),
                On.Exit(Sewers, EbonyBarracks, "a3q5-UsedTalc"),
            },
            // a3q8 "Sever the Right Hand": the barred bridge on The Battlefront (BridgeBarrier, a tile object) seen; General
            // Gravicius (MV 1402, mapboss of Area13/Rooms/Unique/boss.arm) seen and killed. Maramoa's reward is offer 23 (gems).
            new Quest("a3q8")
            {
                On.Near(Battlefront, Act3Objects.BridgeBarrier, "a3q8-SeenBarrier"),
                On.Seen(EbonyBarracks, Act3Monsters.Gravicius, "a3q8-SeenGeneral"),
                On.Killed(EbonyBarracks, Act3Monsters.Gravicius, "a3q8-KilledGeneral"),
            },
            // a3q12 "A Fixture of Fate": Siosa in The Library (siosa_1.arm); the loose candle (HiddenDoorTrigger, tile record
            // library_entrance_v02_01_candletrigger) opens the hidden door (HiddenDoor, a TriggerableBlockage) for the
            // instance and the transition into The Archives appears (its room point is "…disabled": the game enables it by
            // script); the four golden book stands of The Archives (Chests.dat rows without an .ot, book_1_1–book_4_1.arm)
            // hold the pages (remove a3q12-DeliveredPages: Siosa's "Four Golden Pages").
            new Quest("a3q12")
            {
                On.Npc(Library, Act3Objects.Siosa, goneWith: null),
                On.Object(Library, Act3Objects.LooseCandle, Act3Objects.HiddenDoorOpen),
                On.Used(Library, Act3Objects.LooseCandle, Act3Objects.HiddenDoorOpen),
                On.Object(Library, Act3Objects.HiddenDoor, Act3Objects.HiddenDoorOpen),
                On.Exit(Library, Archives, Act3Objects.HiddenDoorOpen),
                On.Chest(Archives, Act3Objects.BookStands[0], Act3Items.GoldenPages[0]),
                On.Chest(Archives, Act3Objects.BookStands[1], Act3Items.GoldenPages[1]),
                On.Chest(Archives, Act3Objects.BookStands[2], Act3Items.GoldenPages[2]),
                On.Chest(Archives, Act3Objects.BookStands[3], Act3Items.GoldenPages[3]),
            },
            // a3q13 "A Swig of Hope": Captain Fairgraves stands in The Docks only after his death in Act 1 (his .ot's
            // serialisation_start_flag a1q6-KilledFairgraves); the Decanter Spiritus in The Marketplace's Ornate Chest
            // (decanter_1.arm), Chitus' Plum on the plum tree of The Imperial Gardens (tile record fruittree_fruittree). His
            // reward (offer 25) is magic and rare rings.
            new Quest("a3q13")
            {
                On.Npc(Docks, Act3Objects.Fairgraves, goneWith: null, shownBy: "a1q6-KilledFairgraves"),
                // Row 496 (both items carried) takes both in that click, as online; the data's rows 497 and 498 would take
                // them one click each.
                On.HandIn(Act3Objects.Fairgraves, "A Swig of Hope", Act3Items.Plum, row: 496),
                On.HandIn(Act3Objects.Fairgraves, "A Swig of Hope", Act3Items.Decanter, row: 496),
                On.Chest(Marketplace, Act3Objects.DecanterChest, Act3Items.Decanter),
                On.Chest(ImperialGardens, Act3Objects.PlumTree, Act3Items.Plum),
            },
            // a3q9 "Piety's Pets": Piety (MV 1421, mapboss1 of pietyfight_2_1.arm) seen and killed; her death sets
            // a3q9-KilledPiety and a3q10-QuestReceived (no setter in the data; QuestStates 241) and drops the Tower Key.
            // Grigor's reward (offer 26) is a Book of Skill: one passive point.
            new Quest("a3q9")
            {
                On.Seen(LunarisTemple2, Act3Monsters.Piety, "a3q9-SeenPiety"),
                On.Killed(LunarisTemple2, Act3Monsters.Piety, "a3q9-KilledPiety", "a3q10-QuestReceived"),
                On.Drops(LunarisTemple2, Act3Monsters.Piety, Act3Items.TowerKey),
            },
            // a3q10 "The Sceptre of God": the Epic Door's lock in The Imperial Gardens (EpicDoorLock, tile record
            // Act3_EpicDoor_v02_01_door) used with the Tower Key sets a3q9-UsedTowerKey: the door opens, its EpicDoorBlockers
            // lift and the tower entrance appears. Atop The Upper Sceptre of God Dominus (MV 1678, mapboss2 of city_tower.arm)
            // dies into his second form (MV 1688), whose death sets a3q10-KilledDominus; Lady Dialla (DiallaTower) appears
            // where he stood (DeathPortalLocator) and the exit to The Aqueduct opens (Act 4's EndQuestFlags). That exit is
            // disabled in the layout (city_tower.arm's "entrance2disabled"): the gate below makes AreaTransitionCatalog create
            // it (Gated), shown once the flag is set.
            new Quest("a3q10")
            {
                On.Object(ImperialGardens, Act3Objects.EpicDoorLock, "a3q9-UsedTowerKey"),
                On.UsedWith(ImperialGardens, Act3Objects.EpicDoorLock, ["a3q9-HaveTowerKey"], "a3q9-UsedTowerKey"),
                On.Object(ImperialGardens, Act3Objects.EpicDoor, "a3q9-UsedTowerKey"),
                On.Blocker(ImperialGardens, Act3Objects.EpicDoorBlocker, 4, "a3q9-UsedTowerKey"),
                On.Exit(ImperialGardens, SceptreOfGod, "a3q9-UsedTowerKey"),
                On.Seen(UpperSceptreOfGod, Act3Monsters.Dominus, "a3q10-DominusFightStarted", "a3q10-SeenDominus"),
                On.SecondForm(UpperSceptreOfGod, Act3Monsters.Dominus, Act3Monsters.DominusAscendant),
                On.Killed(UpperSceptreOfGod, Act3Monsters.DominusAscendant, "a3q10-KilledDominus", "a3q10-SeenDialla"),
                On.Npc(UpperSceptreOfGod, Act3Objects.DiallaTower, goneWith: null, shownBy: "a3q10-KilledDominus", atObject: Act3Objects.DeathPortalLocator),
                On.Object(UpperSceptreOfGod, Act3Objects.TowerExit, "a3q10-KilledDominus"),
                On.Exit(UpperSceptreOfGod, Aqueduct, "a3q10-KilledDominus"),
            },
        ];
}
