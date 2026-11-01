using static Quests;
using static Act1Areas;
using static Act2Areas;

/// <summary>WorldAreas Ids of Act 2's quest areas (and Act 3's first, The City of Sarn, where the pyramid's exit leads).</summary>
static class Act2Areas
{
    public const string ForestEncampment = "1_2_town", OldFields = "1_2_2", Den = "1_2_2a", CryptLevel2 = "1_2_5_2", ChamberOfSins2 = "1_2_6_2",
        BrokenBridge = "1_2_4", WeaversChambers = "1_2_10", Wetlands = "1_2_12", VaalRuins = "1_2_11", NorthernForest = "1_2_8",
        Caverns = "1_2_14_2", AncientPyramid = "1_2_14_3", CityOfSarn = "1_3_1";
}

/// <summary>MonsterVarieties Ids of Act 2's quest monsters (rows in brackets).</summary>
static class Act2Monsters
{
    public const string Arteri = "Metadata/Monsters/Axis/Lieutenant/LieutenantLetter";            // 1419 Captain Arteri
    public const string GreatWhiteBeast = "Metadata/Monsters/Beasts/BeastBossWhite";                // 116
    public const string Fidelitas = "Metadata/Monsters/DemonFourLegged/BossFourLeggedDemon";        // 154 Fidelitas, the Mourning
    public const string Weaver = "Metadata/Monsters/Spiders/SpiderBossWeaver";                      // 979
    public const string Kraityn = "Metadata/Monsters/BanditLeaderDexInt/BanditLeaderKraityn";       // 37 Kraityn, Scarbearer
    public const string Alira = "Metadata/Monsters/BanditLeaderInt/BanditLeaderAlira";              // 33 Alira Darktongue
    public const string Oak = "Metadata/Monsters/BanditLeaderStrDex/BanditLeaderOak";               // 42 Oak, Skullbreaker
    public const string VaalOversoul = "Metadata/Monsters/IncaShadowBoss/BossIncaShadow";           // 158
}

/// <summary>BaseItemTypes Ids of Act 2's quest items dropped by rules (rows in brackets).</summary>
static class Act2Items
{
    public const string ThaumeticEmblem = "Metadata/Items/QuestItems/SpikeSealKey";                 // 1560
    public const string BalefulGem = "Metadata/Items/QuestItems/PoisonSkillGem";                    // 1543
    public const string MaligarosSpike = "Metadata/Items/QuestItems/PoisonSpear";                   // 1542
    public const string GoldenHand = "Metadata/Items/QuestItems/GoldenHand";                        // 1544
    public const string KraitynsAmulet = "Metadata/Items/QuestItems/BanditAmulets/DexAmulet";       // 1539
    public const string AlirasAmulet = "Metadata/Items/QuestItems/BanditAmulets/IntAmulet";         // 1538
    public const string OaksAmulet = "Metadata/Items/QuestItems/BanditAmulets/StrAmulet";           // 1540
    public const string Apex = "Metadata/Items/QuestItems/BanditAmulets/CombinedAmulet";            // 1541 The Apex (Eramir's offer 17)
}

/// <summary>Act 2's quest objects (rooms and tiles; QuestObjectTypes places them).</summary>
static class Act2Objects
{
    public const string ThaumeticSeal = "Metadata/QuestObjects/Spikes/SpikeBlockageButton";         // tile forest_mountainpass_v01_01.tdt
    public const string SpikeWall = "Metadata/QuestObjects/Spikes/SpikeBlockageForest", SpikeWall2 = "Metadata/QuestObjects/Spikes/SpikeBlockageForest2";
    public const string SpikeBlocker = "Metadata/QuestObjects/Spikes/SpikeBlockageBlocker";
    public const string HelenaSins = "Metadata/NPC/Act2/HelenaSins", PietySins = "Metadata/NPC/Act2/PietySins";
    public const string StrangeDevice = "Metadata/Chests/QuestChests/Helena/PoisonCart";            // Chests 188
    public const string GoldenHandAltar = "Metadata/Chests/QuestChests/ChurchAltar";                // Chests 189, tile church_relic_altar_v01_01.tdt
    public const string PoisonTree = "Metadata/QuestObjects/Inca/PoisonTree";                       // tile forest_caveentrance_inca_v01_02.tdt
    public const string TreeBlocker = "Metadata/QuestObjects/Inca/IncaTreeTransitionBlocker";
    public const string AncientSeal = "Metadata/QuestObjects/Inca/IncaDarknessRelease";             // tile dungeon_bossrelease_v01_01.tdt
    public const string SealBlocker = "Metadata/QuestObjects/Inca/IncaDarknessReleaseBlocker";
    public const string ApexDoor = "Metadata/QuestObjects/Inca/ApexDoor", ApexDoorBlocker = "Metadata/QuestObjects/Inca/ApexDoorBlocker";
    public const string DarkAltar = "Metadata/Monsters/IncaShadowBoss/IncaBossSpawner";             // tile inca_boss_emerge.tdt
    public const string PyramidDoor = "Metadata/QuestObjects/Inca/IncaPyramidDoor";
}

/// <summary>
/// Act 2's quests as the engine's tables (Agents/docs/game/campaign/act2.md, 12-quests.md §11). As in Act 1, what a table
/// does not say comes from the data: dialogue flags (NPCTalk), the quest items' flags (.it), the rewards and the log.
/// The order the campaign plays them (scripts/dev/campaign/order.py): a2q11 (a1q9's road), a2q10, a2q6, a2q4, a2q5, a2q9,
/// a2q7 (with a2q1–a2q3), a2q8.
/// </summary>
sealed class Act2 : Act
{
    public override int Number => 2;

    public override IReadOnlyList<Quest> Table =>
        [
            // a2q11 "The Way Forward" (a1q9's road, act2.md): Captain Arteri (MV 1419, WorldAreas Bosses[1] of The Western
            // Forest, mapboss2 of passageblock.arm) guards the blocked pass. Coming near him sets SeenCaptain ("Kill the
            // Blackguards"), his death SeenKey ("Take the Thaumetic Emblem") and drops the emblem (grant a2q11-HaveKey, extra
            // a1q9-RoadBlocked, remove a1q9-RoadOpened). The Thaumetic Seal (tile record forest_mountainpass_v01_01_spike_switch)
            // used with it sets a1q9-RoadOpened: the seal and the spike walls transition, the client's SpikeBlockageBlockers
            // lift and the way to Prisoner's Gate opens (both sides). Bestel's "Road Reward" (offer 11) is a Book of Skill.
            new Quest("a2q11")
            {
                On.Seen(WesternForest, Act2Monsters.Arteri, "a2q11-SeenCaptain"),
                On.Killed(WesternForest, Act2Monsters.Arteri, "a2q11-SeenKey"),
                On.Drops(WesternForest, Act2Monsters.Arteri, Act2Items.ThaumeticEmblem),
                On.Object(WesternForest, Act2Objects.ThaumeticSeal, "a1q9-RoadOpened"),
                On.UsedWith(WesternForest, Act2Objects.ThaumeticSeal, ["a2q11-HaveKey"], "a1q9-RoadOpened"),
                On.Object(WesternForest, Act2Objects.SpikeWall, "a1q9-RoadOpened"),
                On.Object(WesternForest, Act2Objects.SpikeWall2, "a1q9-RoadOpened"),
                On.Blocker(WesternForest, Act2Objects.SpikeBlocker, 4, "a1q9-RoadOpened"),
                On.Exit(WesternForest, PrisonersGate, "a1q9-RoadOpened"),
            },
            // a2q10 "The Great White Beast": Yeena's quest; the beast in The Den (mapboss of fields_cave_treasure_1a.arm). Her
            // reward is offer 14 (flasks and belts; the normal flasks are built).
            new Quest("a2q10")
            {
                On.Seen(Den, Act2Monsters.GreatWhiteBeast, "a2q10-SeenBeast"),
                On.Killed(Den, Act2Monsters.GreatWhiteBeast, "a2q10-KilledBeast"),
            },
            // a2q6 "Intruders in Black": in The Chamber of Sins Level 2 (Unique/boss_1a.arm) Helena hangs below the ledge
            // (HelenaSins, Transitionable 1: not targetable); Piety runs off. Fidelitas' death sets KilledDemon and Helena
            // climbs up (state 2). Her "Rescued" (NPCTalk 457) sets RescuedHelena, after which the Strange Device (PoisonCart,
            // Transitionable 1 → 2) can be opened: it drops the Baleful Gem (grant a2q6-HaveSkillGem, extra
            // a2q6-HelenaInTown). Greust's "Blackguard Reward" is offer 13 (gems).
            new Quest("a2q6")
            {
                On.Near(ChamberOfSins2, Act2Objects.HelenaSins, "a2q6-SeenHelena"),
                On.Near(ChamberOfSins2, Act2Objects.PietySins, "a2q6-SeenPiety"),
                On.Killed(ChamberOfSins2, Act2Monsters.Fidelitas, "a2q6-KilledDemon"),
                On.Npc(ChamberOfSins2, Act2Objects.HelenaSins, goneWith: "a2q6-HelenaInTown", transitionedBy: "a2q6-KilledDemon"),
                On.Chest(ChamberOfSins2, Act2Objects.StrangeDevice, Act2Items.BalefulGem, opensWith: "a2q6-RescuedHelena"),
            },
            // a2q4 "Sharp and Cruel": the Weaver (mapboss of SpiderGrove/Rooms/Unique/treasure.arm) drops Maligaro's Spike
            // (grant a2q4-HavePoisonSpear, remove a2q9-PoisonedTree). Silk's "Maligaro's Spike" does not take it (a2q9 needs
            // it); her reward is offer 15 (support gems).
            new Quest("a2q4")
            {
                On.Seen(WeaversChambers, Act2Monsters.Weaver, "a2q4-SeenWeaver"),
                On.Drops(WeaversChambers, Act2Monsters.Weaver, Act2Items.MaligarosSpike),
            },
            // a2q5 "Through Sacred Ground": the altar in The Crypt Level 2 (ChurchAltar, a Chests.dat row without an .ot, at
            // the tile record dungeon_church_relic_altar_v01_01_dungeon_church_relic_altar) holds the Golden Hand (remove
            // a2q5-DeliveredGoldenHand: Yeena's "The Fellshrine Hand" takes it). Her reward (offer 12) is a Book of Skill:
            // one passive and two refund points (QuestStaticRewards 7).
            new Quest("a2q5")
            {
                On.Chest(CryptLevel2, Act2Objects.GoldenHandAltar, Act2Items.GoldenHand),
            },
            // a2q9 "The Root of the Problem": the tree in The Wetlands (PoisonTree, tile record
            // forest_caveentrance_inca_v01_01_forest_cave_entrance) blocks the way into The Vaal Ruins. Seen: SeenTree. Used
            // with the spike and the gem: a2q9-PoisonedTree (both items' remove_flag: they go), the tree transitions, the
            // client's IncaTreeTransitionBlockers lift and the exit (IncaTreeTransition) appears; The Vaal Ruins'
            // EndQuestFlags too.
            new Quest("a2q9")
            {
                On.Near(Wetlands, Act2Objects.PoisonTree, "a2q9-SeenTree"),
                On.Object(Wetlands, Act2Objects.PoisonTree, "a2q9-PoisonedTree"),
                On.UsedWith(Wetlands, Act2Objects.PoisonTree, ["a2q4-HavePoisonSpear", "a2q6-HaveSkillGem"], "a2q9-PoisonedTree"),
                On.Blocker(Wetlands, Act2Objects.TreeBlocker, 4, "a2q9-PoisonedTree"),
                On.Exit(Wetlands, VaalRuins, "a2q9-PoisonedTree"),
            },
            // a2q7 "Deal with the Bandits" (and a2q1–a2q3, the help quests): the three lords (MV 37, 33, 42: Monster with NPC
            // and InteractionAction) drop their amulets when they die; picking one up sets its hold flag and its kill flag
            // (extra_flag), which the kill sets too (Eramir's rows read the kill flags). Eramir's "Bandit Amulets" (NPCTalk 95,
            // SetQuestFlag("a2q7-QuestRewardTaken", "a2b-DeliveredAmulets")) takes the three (their remove_flag): one passive
            // point (QuestStaticRewards 8); "Take the Apex" is offer 17, The Apex (a2b-SeenApexReward). The help path (the
            // bandit vote) is not implemented: the lords are hostile and the choice comes from the kills.
            new Quest("a2q7")
            {
                On.Killed(BrokenBridge, Act2Monsters.Kraityn, "a2b-KilledDexBandit"),
                On.Drops(BrokenBridge, Act2Monsters.Kraityn, Act2Items.KraitynsAmulet),
                On.Killed(WesternForest, Act2Monsters.Alira, "a2b-KilledIntBandit"),
                On.Drops(WesternForest, Act2Monsters.Alira, Act2Items.AlirasAmulet),
                On.Killed(Wetlands, Act2Monsters.Oak, "a2b-KilledStrBandit"),
                On.Drops(Wetlands, Act2Monsters.Oak, Act2Items.OaksAmulet),
            },
            // a2q8 "Shadow of the Vaal": the Ancient Seal in The Vaal Ruins (IncaDarknessRelease, tile record
            // dungeon_bossrelease_v01_01_ball_replacement) releases the darkness (a2q8-UnleashedDarkness: the client lifts the
            // IncaDarknessReleaseBlockers; The Northern Forest, The Dread Thicket, The Caverns and The Ancient Pyramid open).
            // The Apex door in The Caverns (sewer_connection.arm) opens with a2b-SeenApexReward. On top of The Ancient Pyramid
            // the Dark Altar (IncaBossSpawner, tile record inca_boss_emerge_incabossemerge_setpiece) takes The Apex
            // (a2q8-PlacedApex, its remove_flag) and the Vaal Oversoul (MV 158, no WorldAreas boss) emerges; its death sets
            // KilledShadowMonster and FinishedVaalOversoulQuest (no setter in the data; Act 3's areas need the latter) and
            // opens the exit to The City of Sarn.
            new Quest("a2q8")
            {
                On.Object(VaalRuins, Act2Objects.AncientSeal, "a2q8-UnleashedDarkness"),
                On.Used(VaalRuins, Act2Objects.AncientSeal, "a2q8-UnleashedDarkness"),
                On.Blocker(VaalRuins, Act2Objects.SealBlocker, 4, "a2q8-UnleashedDarkness"),
                On.Exit(VaalRuins, NorthernForest, "a2q8-UnleashedDarkness"),
                On.Near(Caverns, Act2Objects.ApexDoor, "a2q8-SeenApexDoor"),
                On.Object(Caverns, Act2Objects.ApexDoor, "a2b-SeenApexReward"),
                On.Blocker(Caverns, Act2Objects.ApexDoorBlocker, 4, "a2b-SeenApexReward"),
                On.Entered(AncientPyramid, "a2q8-SeenPyramid"),
                On.Near(AncientPyramid, Act2Objects.DarkAltar, "a2q8-SeenAltar"),
                On.Object(AncientPyramid, Act2Objects.DarkAltar, "a2q8-PlacedApex"),
                On.UsedWith(AncientPyramid, Act2Objects.DarkAltar, ["a2q8-HaveApex"], "a2q8-PlacedApex"),
                On.Spawns(AncientPyramid, Act2Objects.DarkAltar, Act2Monsters.VaalOversoul),
                On.Seen(AncientPyramid, Act2Monsters.VaalOversoul, "a2q8-SeenShadowMonster"),
                On.Killed(AncientPyramid, Act2Monsters.VaalOversoul, "a2q8-KilledShadowMonster", "FinishedVaalOversoulQuest"),
                On.Object(AncientPyramid, Act2Objects.PyramidDoor, "FinishedVaalOversoulQuest"),
                On.Exit(AncientPyramid, CityOfSarn, "FinishedVaalOversoulQuest"),
            },
        ];
}
