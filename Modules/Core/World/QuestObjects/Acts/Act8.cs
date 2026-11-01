using static Quests;
using static Act8Areas;

/// <summary>WorldAreas Ids of Act 8's quest areas (and Act 9's first, The Blood Aqueduct, where the Harbour Bridge leads).</summary>
static class Act8Areas
{
    public const string SarnEncampment = "2_8_town", DoedresCesspool = "2_8_2_2", HighGardens = "2_8_4", BathHouse = "2_8_5",
        LunarisTemple2 = "2_8_7_2", Quay = "2_8_8", GrainGate = "2_8_9", SolarisTemple2 = "2_8_12_2", HarbourBridge = "2_8_13",
        BloodAqueduct = "2_9_1";
}

/// <summary>MonsterVarieties Ids of Act 8's quest monsters (rows in brackets).</summary>
static class Act8Monsters
{
    public const string Doedre = "Metadata/Monsters/Doedre/DoedreSewers";                          // 5536 Doedre the Vile
    public const string Yugul = "Metadata/Monsters/Frog/FrogGod/SilverFrog";                        // 6061 Yugul, Reflection of Terror
    public const string Titucius = "Metadata/Monsters/Exiles/Titucius";                              // 6092 Hector Titucius, Eternal Servant
    public const string Dusk = "Metadata/Monsters/GuardianOfLunaris/GuardianOfLunaris";             // 5657 Dusk, Harbinger of Lunaris
    public const string Dawn = "Metadata/Monsters/GuardianOfSolaris/GuardianOfSolaris";             // 5681 Dawn, Harbinger of Solaris
    public const string Lunaris = "Metadata/Monsters/LunarisSolaris/Lunaris";                       // 5642 Lunaris, Eternal Moon
    public const string Solaris = "Metadata/Monsters/LunarisSolaris/Solaris";                       // 5666 Solaris, Eternal Sun
    public const string Tolman = "Metadata/Monsters/Zombies/ZombieBossTolman_";                     // 1226 Tolman
    /// <summary>The Grain Gate's legion (WorldAreas Bosses: two of each legionnaire and the captain, mapboss1-7).</summary>
    public static readonly string[] GemlingLegion =
    [
        "Metadata/Monsters/GemlingLegionnaire/GemlingLegionnaireFireball",                           // 4646
        "Metadata/Monsters/GemlingLegionnaire/GemlingLegionnaireEarthquake",                         // 4650
        "Metadata/Monsters/GemlingLegionnaire/GemlingLegionnaireGlacialCascade",                     // 4654
        "Metadata/Monsters/GemlingLegionnaire/GemlingLegionnaireCaptain",                            // 4658 Gemling Captain
    ];
}

/// <summary>BaseItemTypes Ids of Act 8's quest items.</summary>
static class Act8Items
{
    public const string LunarisOrb = "Metadata/Items/QuestItems/Act8/LunarisOrb", SolarisOrb = "Metadata/Items/QuestItems/Act8/SolarisOrb",
        WingsOfVastiri = "Metadata/Items/QuestItems/Act8/WingsOfVastiri", AnkhOfEternity = "Metadata/Items/QuestItems/Act8/AnkhOfEternity";
}

/// <summary>Act 8's quest objects, chests and out-of-town NPCs.</summary>
static class Act8Objects
{
    public const string TituciusChest = "Metadata/Chests/QuestChests/Titucius/TituciusChest", AnkhChest = "Metadata/Chests/QuestChests/AnkhChest",
        GoddessFightStarter = "Metadata/QuestObjects/Act8/GoddessFightStarter", ClarissaQuay = "Metadata/NPC/Act8/ClarissaQuay",
        SinDoedre = "Metadata/NPC/Act8/SinDoedre", SinYugul = "Metadata/NPC/Act8/SinYugul", SinSolarisLunaris = "Metadata/NPC/Act8/SinSolarisLunaris";
}

/// <summary>
/// Act 8's quests as the engine's tables (Agents/docs/game/campaign/act8.md, 12-quests.md §17). The Sarn Encampment's main
/// line: Sin's "Doedre Darktongue" (a8q1: her death in Doedre's Cesspool opens the rest of the act), then Maramoa's two orbs
/// (a8q2, a8q3: Dusk and Dawn drop them in the two temples; both used at the fight starter on The Harbour Bridge bring
/// Solaris and Lunaris, whose deaths open Act 9 and the Pantheon's Solaris and Lunaris). Side quests a8q4 (Yugul: a book and
/// his Pantheon soul), a8q5 (the Wings of Vastiri in Hector Titucius' tomb), a8q6 (the Ankh of Eternity for Clarissa: a
/// book), a8q7 (the Gemling Legion: a book). The campaign plays them (order.py) a8q1, a8q5, a8q4, a8q6, a8q7, a8q3, a8q2.
/// </summary>
sealed class Act8 : Act
{
    public override int Number => 8;

    public override IReadOnlyList<Quest> Table =>
        [
            // a8q1 "Essence of the Hag" (act8.md a8q1): Sin's "Doedre Darktongue"; Doedre the Vile dies in her Cesspool
            // (doedre_boss_room_1.arm mapboss; the real fight starts at the cauldron valve): KilledBoss, the gate of every
            // later Act 8 area; Sin appears (1224, no god). Hargan's "Doedre Reward" is offer 54 (rings, gloves).
            new Quest("a8q1")
            {
                On.Seen(DoedresCesspool, Act8Monsters.Doedre, "a8q1-SeenBoss"),
                On.Killed(DoedresCesspool, Act8Monsters.Doedre, "a8q1-KilledBoss"),
                On.Appears(DoedresCesspool, Act8Monsters.Doedre, Act8Objects.SinDoedre, "SinDoedreReward"),
            },
            // a8q2 "Lunar Eclipse" (act8.md a8q2/a8q3): Dusk in The Lunaris Temple Level 2 (pietyfight_2_1.arm mapboss1) drops
            // the Moon Orb (wiki; the client's orb objects have spawners in no room); LunarisOrb.it: grant a8q2-HaveItem, remove
            // a8q23-DeliveredItems. Both orbs used at the fight starter on The Harbour Bridge (bridge_arena.arm) take them
            // (a8q23-DeliveredItems, no setter in the data) and bring the goddesses where it stands; the area's population
            // leaves them out until then.
            new Quest("a8q2")
            {
                On.Seen(LunarisTemple2, Act8Monsters.Dusk, "a8q2-SeenMiniboss"),
                On.Killed(LunarisTemple2, Act8Monsters.Dusk, "a8q2-KilledMiniboss"),
                On.Drops(LunarisTemple2, Act8Monsters.Dusk, Act8Items.LunarisOrb),
                On.Object(HarbourBridge, Act8Objects.GoddessFightStarter, "a8q23-DeliveredItems"),
                On.UsedWith(HarbourBridge, Act8Objects.GoddessFightStarter, ["a8q2-HaveItem", "a8q3-HaveItem"], "a8q23-DeliveredItems"),
                On.Spawns(HarbourBridge, Act8Objects.GoddessFightStarter, Act8Monsters.Lunaris),
                On.Spawns(HarbourBridge, Act8Objects.GoddessFightStarter, Act8Monsters.Solaris),
            },
            // a8q3 "Solar Eclipse": Dawn in The Solaris Temple Level 2 (sun_arena.arm) drops the Sun Orb. Solaris and Lunaris
            // both dead: a8q23-KilledBosses (the gate of every Act 9 area; the Pantheon's Solaris and Lunaris), the bridge's
            // exit into The Blood Aqueduct opens (a transition the tile disables until then), and Sin appears (1229).
            new Quest("a8q3")
            {
                On.Seen(SolarisTemple2, Act8Monsters.Dawn, "a8q3-SeenMiniboss"),
                On.Killed(SolarisTemple2, Act8Monsters.Dawn, "a8q3-KilledMiniboss"),
                On.Drops(SolarisTemple2, Act8Monsters.Dawn, Act8Items.SolarisOrb),
                On.KilledAll(HarbourBridge, [Act8Monsters.Lunaris, Act8Monsters.Solaris], "a8q23-KilledBosses"),
                On.Appears(HarbourBridge, Act8Monsters.Lunaris, Act8Objects.SinSolarisLunaris, null),
                On.Appears(HarbourBridge, Act8Monsters.Solaris, Act8Objects.SinSolarisLunaris, null),
                On.Exit(HarbourBridge, BloodAqueduct, "a8q23-KilledBosses"),
            },
            // a8q4 "Reflection of Terror" (act8.md a8q4): Sin's row (after The High Gardens or the Wings); Yugul dies in The
            // High Gardens (boss_1_1.arm mapboss): KilledBoss (his Pantheon unlock), Sin appears (1226) until Hargan's reward
            // talk. Hargan's "Yugul Reward" (offer 55) is a book (QSR 22).
            new Quest("a8q4")
            {
                On.Seen(HighGardens, Act8Monsters.Yugul, "a8q4-SeenBoss"),
                On.Killed(HighGardens, Act8Monsters.Yugul, "a8q4-KilledBoss"),
                On.Appears(HighGardens, Act8Monsters.Yugul, Act8Objects.SinYugul, "HarganYugulReward"),
            },
            // a8q5 "The Wings of Vastiri" (act8.md a8q5): Hector Titucius dies in The Bath House (boss_1.arm); his tomb
            // (TituciusChest, Transitionable: not targetable until he is dead) holds the Wings. Hargan's 1172 takes them;
            // offer 56 (jewels, rings).
            new Quest("a8q5")
            {
                On.Seen(BathHouse, Act8Monsters.Titucius, "a8q5-SeenBoss"),
                On.Killed(BathHouse, Act8Monsters.Titucius, "a8q5-KilledBoss"),
                On.Chest(BathHouse, Act8Objects.TituciusChest, Act8Items.WingsOfVastiri, opensWith: "a8q5-KilledBoss"),
            },
            // a8q6 "Love is Dead" (act8.md a8q6): the Sealed Casket on The Quay (quest_chest.arm point line) holds the Ankh;
            // Clarissa at the resurrection site (boss.arm; NPCTalk 1153: QuestFlag4 a8q6-KilledBoss, script SetQuestFlag
            // a8q6-DeliveredItem, the Ankh's remove_flag, and her own Transition) takes it; in 3.29 the row itself completes
            // the fight (no kill rule for Tolman, so the row may set KilledBoss). She is gone with the town's congratulation.
            // Clarissa's "Tolman Reward" (offer 57) is a book (QSR 21: 1 passive, 2 respec).
            new Quest("a8q6")
            {
                On.Chest(Quay, Act8Objects.AnkhChest, Act8Items.AnkhOfEternity),
                On.Npc(Quay, Act8Objects.ClarissaQuay, "a8q6-Congratulated", transitionedBy: "a8q6-DeliveredItem"),
                On.Seen(Quay, Act8Monsters.Tolman, "a8q6-SeenBoss"),
            },
            // a8q7 "The Gemling Legion" (act8.md a8q7): the seven of The Grain Gate (boss_legion.arm mapboss1-7, run by the
            // room's BossController in the real game): KilledBoss once none is left. Maramoa's "Gemling Legion Reward" (offer
            // 58) is a book (QSR 23).
            new Quest("a8q7")
            {
                On.Seen(GrainGate, Act8Monsters.GemlingLegion[3], "a8q7-SeenBoss"),
                On.Seen(GrainGate, Act8Monsters.GemlingLegion[0], "a8q7-SeenBoss"),
                On.KilledAll(GrainGate, Act8Monsters.GemlingLegion, "a8q7-KilledBoss"),
            },
        ];
}
