using static Quests;
using static Act1Areas;

/// <summary>WorldAreas Ids of Act 1's quest areas.</summary>
static class Act1Areas
{
    public const string Strand = "1_1_1", LioneyesWatch = "1_1_town", Coast = "1_1_2", TidalIsland = "1_1_2a", MudFlats = "1_1_3",
        FetidPool = "1_1_3a", FloodedDepths = "1_1_4_0", SubmergedPassage = "1_1_4_1", UpperPrison = "1_1_7_2", PrisonersGate = "1_1_8",
        ShipGraveyard = "1_1_9", ShipGraveyardCave = "1_1_9a", CavernOfAnger = "1_1_11_2", SouthernForest = "1_2_1", WesternForest = "1_2_9";
}

/// <summary>MonsterVarieties Ids of Act 1's quest monsters (rows in brackets).</summary>
static class Act1Monsters
{
    public const string Hillock = "Metadata/Monsters/ZombieBoss/ZombieBossHillockNormal";        // 1166
    public const string Hailrake = "Metadata/Monsters/Cannibal/CannibalMaleBossHailrake";         // 2887
    public const string Kadavrus = "Metadata/Monsters/Necromancer/NecromancerBossFetidPool";      // 430 Kadavrus the Defiler
    public const string BoneRhoa = "Metadata/Monsters/Rhoas/RhoaSkeleton";                       // 469, The Fetid Pool's packs
    public const string Dweller = "Metadata/Monsters/ShieldCrabs/ShieldCrabBossSlowAura";        // 541
    public const string Brutus = "Metadata/Monsters/Brute/BossBrute";                            // 150 Brutus, Lord Incarcerator
    public const string Fairgraves = "Metadata/Monsters/Skeletons/SkeletonBossFairgraves";       // 550
    public const string Merveil = "Metadata/Monsters/seawitchillusion/BossMerveil";              // 162 Merveil, the Siren
    public const string MerveilTwisted = "Metadata/Monsters/Seawitch/BossMerveil2";              // 164 Merveil, the Twisted
}

/// <summary>BaseItemTypes Ids of Act 1's quest items dropped by rules (the glyphs are GlyphQuest's).</summary>
static class Act1Items
{
    public const string MedicineChest = MercyMission.MedicineChest;                               // 1534
    public const string Allflame = "Metadata/Items/QuestItems/AllFlameLantern1";                 // 1535
}

/// <summary>
/// Act 1's quests as the engine's tables (Agents/docs/game/campaign-quests.md, Agents/docs/protocol/12-quests.md §5–§10).
/// What a table does not say comes from the data: the dialogue flags (NPCTalk QuestFlag4/5 and scripts), the quest items'
/// flags (.it Quest components), the rewards (QuestRewardOffers, QuestStaticRewards) and the quest log (QuestStates).
/// The game's order (Quest.dat rows): a1q1, a1q5, a1q4, a1q8, a1q7, a1q2, a1q6, a1q3, a1q9 (finished in Act 2).
/// </summary>
sealed class Act1 : Act
{
    public override int Number => 1;

    public override IReadOnlyList<Quest> Table =>
        [
            // a1q1 "Enemy at the Gate": Hillock before Lioneye's Watch (12-quests.md §6). Coming near him sets SeenHillock
            // ("Kill Hillock"); his death levels the character to 2 (QuestStates: "… and levelled up"), takes down the force
            // wall (town_entrance_exclude.arm's Tutorial_Blocker_3) and opens the gate. Tarkleigh's "Hillock Reward" is offer 0.
            new Quest("a1q1")
            {
                On.Seen(Strand, Act1Monsters.Hillock, "a1q1-SeenHillock"),
                On.Killed(Strand, Act1Monsters.Hillock, "a1q1-KilledHillock"),
                On.LevelUp(Strand, Act1Monsters.Hillock, 2),
                On.Exit(Strand, LioneyesWatch, "a1q1-KilledHillock"),
                On.Wall(Strand, "Metadata/Terrain/Act1/Area1/Objects/Tutorial_Blocker_3", "a1q1-KilledHillock"),
            },
            // a1q5 "Mercy Mission" (§7): Hailrake drops the Medicine Chest; its .it holds a1q5-HaveMedicineChest while carried
            // and goes with a1q5-NessaCongratulated, the flag of Nessa's "The Medicine Chest", which takes it (hand-in).
            new Quest("a1q5")
            {
                On.Drops(TidalIsland, Act1Monsters.Hailrake, Act1Items.MedicineChest),
            },
            // a1q4 "Breaking Some Eggs" (§1–4): the Rhoa Nests and the Glyph Wall are GlyphQuest's; PlacedGlyphs opens the
            // Submerged Passage. Tarkleigh's two rewards are offers 3 and 4.
            new Quest("a1q4")
            {
                On.Exit(GlyphQuest.Area, GlyphQuest.Passage, GlyphQuest.PlacedGlyphs),
            },
            // a1q8 "A Dirty Job" (§8): Tarkleigh's "The Fetid Pool" gives it; Kadavrus and every monster of The Fetid Pool must
            // die ("Wipe out the remaining monsters"). The Bone Rhoas are its undead rhoas. Tarkleigh's "Necromancer Reward"
            // (offer 5) is the Book of Regrets: using it sets a1q8-UsedRewardBook, two refund points (QuestStaticRewards).
            new Quest("a1q8")
            {
                On.Seen(FetidPool, Act1Monsters.BoneRhoa, "a1q8-SeenUndeadRhoa"),
                On.Seen(FetidPool, Act1Monsters.Kadavrus, "a1q8-SeenNecromancer"),
                On.Killed(FetidPool, Act1Monsters.Kadavrus, "a1q8-KilledNecromancer"),
                On.Cleared(FetidPool, "a1q8-KilledNecromancer", "a1q8-Cleared"),
            },
            // a1q7 "The Dweller of the Deep" (§8): the crab in The Flooded Depths. Tarkleigh's "Dweller Reward" (offer 6) is a
            // Book of Skill: a1q7-UsedRewardBook, one passive point.
            new Quest("a1q7")
            {
                On.Seen(FloodedDepths, Act1Monsters.Dweller, "a1q7-SeenDweller"),
                On.Killed(FloodedDepths, Act1Monsters.Dweller, "a1q7-KilledDweller"),
            },
            // a1q2 "The Caged Brute" (§9): Brutus in the Warden's section of The Upper Prison (mapboss2); his death opens the
            // way to Prisoner's Gate (its EndQuestFlags too). Piety reads in her room on the way (PietyPrison, SeenPietyPrison,
            // in Act1Complete). Tarkleigh's "Brutus Reward" is offer 7, Nessa's "Prison Reward" offer 8 (magic armour).
            new Quest("a1q2")
            {
                On.Near(UpperPrison, "Metadata/NPC/Act1/PietyPrison", "SeenPietyPrison"),
                On.Seen(UpperPrison, Act1Monsters.Brutus, "a1q2-SeenBrutus"),
                On.Killed(UpperPrison, Act1Monsters.Brutus, "a1q2-KilledBrutus"),
                On.Exit(UpperPrison, PrisonersGate, "a1q2-KilledBrutus"),
            },
            // a1q6 "The Marooned Mariner" (§10): Fairgraves stands on his ship in The Ship Graveyard (intact_ship.arm) and wants
            // the Allflame, which the wounded slave girl (a quest chest in The Ship Graveyard Cave's treasure room) gives.
            // "Thank You" hands it in (a1q6-DeliveredAllFlame, the Allflame's remove_flag) and its script turns him into the
            // boss (SetSessionQuestFlag a1q6-FairgravesAggro); his death sets KilledFairgraves, and the NPC is gone with it
            // (his .ot unblock_quest_flag). Bestel's "Fairgraves Reward" (offer 9) is a Book of Skill.
            new Quest("a1q6")
            {
                On.Npc(ShipGraveyard, "Metadata/NPC/Act1/Fairgraves", "a1q6-KilledFairgraves"),
                On.TurnsInto(ShipGraveyard, "Metadata/NPC/Act1/Fairgraves", Act1Monsters.Fairgraves),
                On.Killed(ShipGraveyard, Act1Monsters.Fairgraves, "a1q6-KilledFairgraves"),
                On.Chest(ShipGraveyardCave, "Metadata/Chests/QuestChests/Fairgraves/AllFlameSlaveGirl", Act1Items.Allflame),
            },
            // a1q3 "The Siren's Cadence" (§9): Merveil, the Siren (no drops, a second form) becomes Merveil, the Twisted, whose
            // death sets KilledSeaWitch: the six MerveilExitBlockers before the exit stop blocking (object_size 5,
            // unblock_quest_flag) and the way into Act 2 opens. Nessa's "Merveil Reward" is offer 10.
            new Quest("a1q3")
            {
                On.Seen(CavernOfAnger, Act1Monsters.Merveil, "a1q3-SeenMerveil"),
                On.SecondForm(CavernOfAnger, Act1Monsters.Merveil, Act1Monsters.MerveilTwisted),
                On.Killed(CavernOfAnger, Act1Monsters.MerveilTwisted, "a1q3-KilledSeaWitch"),
                On.Exit(CavernOfAnger, SouthernForest, "a1q3-KilledSeaWitch"),
                On.Blocker(CavernOfAnger, "Metadata/QuestObjects/MerveilExitBlocker", 4, "a1q3-KilledSeaWitch"),
            },
            // a1q9 "The Way Forward" (§9): Tarkleigh's "Reaching the Forest" gives it; at Prisoner's Gate Piety has blocked the
            // road to The Western Forest with spikes (blocked_passage.arm: PietyBlockage, the SpikeBlockageBlockers with
            // unblock_quest_flag a1q9-RoadOpened). Seeing it sets SeenBlockage and RoadBlocked (both in Act1Complete); the road
            // opens in Act 2 (the Thaumetic Emblem), so the quest ends there.
            new Quest("a1q9")
            {
                On.Near(PrisonersGate, "Metadata/NPC/Act1/PietyBlockage", "a1q9-SeenBlockage", "a1q9-RoadBlocked"),
                On.Exit(PrisonersGate, WesternForest, "a1q9-RoadOpened"),
                On.Blocker(PrisonersGate, "Metadata/QuestObjects/Spikes/SpikeBlockageBlocker", 4, "a1q9-RoadOpened"),
            },
        ];
}
