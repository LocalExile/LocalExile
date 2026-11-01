using static Quests;
using static Act7Areas;

/// <summary>WorldAreas Ids of Act 7's quest areas (and Act 8's first, The Sarn Ramparts, where Arakaali's bridge leads).</summary>
static class Act7Areas
{
    public const string BridgeEncampment = "2_7_town", BrokenBridge = "2_7_1", Crossroads = "2_7_2", Crypt = "2_7_4", SinsLevel1 = "2_7_5_1",
        SinsLevel2 = "2_7_5_2", MaligarosSanctum = "2_7_5_map", Den = "2_7_6", AshenFields = "2_7_7", NorthernForest = "2_7_8",
        DreadThicket = "2_7_9", Causeway = "2_7_10", VaalCity = "2_7_11", TempleOfDecay1 = "2_7_12_1", TempleOfDecay2 = "2_7_12_2",
        SarnRamparts = "2_8_1";
}

/// <summary>MonsterVarieties Ids of Act 7's quest monsters (rows in brackets).</summary>
static class Act7Monsters
{
    public const string Greust = "Metadata/Monsters/Ralakesh/Greust/Greust";                       // 5724 Greust, Lord of the Forest
    public const string RalakeshOak = "Metadata/Monsters/Ralakesh/Oak/Oak";                        // 5731 (the spared bandit's avatar)
    public const string RalakeshAlira = "Metadata/Monsters/Ralakesh/Alira/Alira";                  // 5734
    public const string RalakeshKraityn = "Metadata/Monsters/Ralakesh/Kraityn/Kraityn";            // 5737
    public const string Maligaro = "Metadata/Monsters/Maligaro/Maligaro2";                         // 5692 Maligaro, the Artist
    public const string BlackDeath = "Metadata/Monsters/Spiders/BlackDeath/BlackDeathMaligaro";    // 5698 Black Death, Pain Unending
    public const string Arakaali = "Metadata/Monsters/VaalSpiderGod/Arakaali";                     // 5740 Arakaali, Spinner of Shadows
    public const string Gruthkul = "Metadata/Monsters/DespairBear/DespairBear";                    // 6047 Gruthkul, Mother of Despair
}

/// <summary>BaseItemTypes Ids of Act 7's quest items.</summary>
static class Act7Items
{
    public const string MaligaroMap = "Metadata/Items/QuestItems/Act7/MaligaroMap", BlackVenom = "Metadata/Items/QuestItems/Act7/BlackVenom",
        SilverLocket = "Metadata/Items/QuestItems/Act7/SilverLocket", KisharaStar = "Metadata/Items/QuestItems/Act7/KisharaStar";
    /// <summary>Firefly1 … Firefly7 (grant a7q7-HaveItem1 … 7, remove a7q7-DeliveredItems).</summary>
    public static readonly string[] Fireflies = [.. Enumerable.Range(1, 7).Select(n => $"Metadata/Items/QuestItems/Act7/Firefly{n}")];
}

/// <summary>Act 7's quest objects, chests and out-of-town NPCs (.ot / Chests / NPCs Ids).</summary>
static class Act7Objects
{
    public const string MaligaroMapChest = "Metadata/Chests/QuestChests/MaligaroMapChest", LocketChest = "Metadata/Chests/QuestChests/LocketChest",
        KisharaStarChest = "Metadata/Chests/QuestChests/KisharaStarChest", Orrery = "Metadata/QuestObjects/Act7/MaligaroOrrery",
        SinsBarrier = "Metadata/QuestObjects/Act7/MaligaroSinsBarrier", SinsBarrierBlocker = "Metadata/QuestObjects/Act7/MaligaroSinsBarrierBlocker",
        PassageCover = "Metadata/QuestObjects/Act7/MaligaroPassageCover", BanditBlock = "Metadata/QuestObjects/Act7/BanditBlock",
        GreustShrine = "Metadata/QuestObjects/Act7/GreustShrine", TempleWeb = "Metadata/QuestObjects/Act7/ArakaaliTempleEntranceBlock",
        ArakaaliBridge = "Metadata/Terrain/Act7/Area12Level2/Objects/Arakaali_Bridge",
        ArakaaliBridgeTransition = "Metadata/Terrain/Act7/Area12Level2/Objects/Arakaali_Bridge_Transition";
    /// <summary>The quest portals the Reverie Device spawns beside itself, into Maligaro's Sanctum.</summary>
    public const string MaligaroPortal = "Metadata/QuestObjects/Act7/MaligaroPortalTransition";
    /// <summary>FireflyChest1 … FireflyChest7 (Chests 212–218): no room or tile has them, the engine scatters them.</summary>
    public static readonly string[] FireflyChests = [.. Enumerable.Range(1, 7).Select(n => $"Metadata/Chests/QuestChests/Fireflies/FireflyChest{n}")];
    public const string SilkSins = "Metadata/NPC/Act7/SilkSins", YeenaVaalCity = "Metadata/NPC/Act7/YeenaVaalCity",
        SinRalakesh = "Metadata/NPC/Act7/SinRalakesh", SinMaligaro = "Metadata/NPC/Act7/SinMaligaro",
        SinArakaali = "Metadata/NPC/Act7/SinArakaali", SinGruthkul = "Metadata/NPC/Act7/SinGruthkul";
}

/// <summary>
/// Act 7's quests as the engine's tables (Agents/docs/game/campaign/act7.md, 12-quests.md §16). The Bridge Encampment's main
/// line: Sin sends the player after Maligaro (a7q2: his map in The Crypt opens his Sanctum from the Reverie Device in The
/// Chamber of Sins Level 1), Silk trades the Obsidian Key for Black Death's venom (a7q3: the key opens the passage from
/// Level 2 into The Den), Greust/Ralakesh dies in The Ashen Fields (a7q1: the way north opens), Yeena burns the webs of the
/// Temple of Decay with seven Fireflies (a7q7) and Arakaali's death opens the bridge into Act 8 (a7q4). Side quests a7q5
/// (the locket), a7q6 (Kishara's Star), a7q8 (Greust's Necklace), a7q9 (Gruthkul). Pantheon souls: Ralakesh, Arakaali,
/// Gruthkul (their KilledBoss flags; Sin appears where each falls). The game's order (Quest.dat rows): a7q1 … a7q9; the
/// campaign plays them (scripts/dev/campaign/order.py) a7q5, a7q2, a7q3, a7q1, a7q8, a7q9, a7q7, a7q6, a7q4.
/// </summary>
sealed class Act7 : Act
{
    public override int Number => 7;

    public override IReadOnlyList<Quest> Table =>
        [
            // a7q1 "The Master of a Million Faces" (act7.md a7q1): Eramir's "Greust" (a row per Act 2 bandit choice); the
            // Crossroads' BanditBlock is seen (no setter in the data: proximity). Through The Den into The Ashen Fields (both
            // gated by a7q1-UsedKey, a7q3's key): Ralakesh's avatar is Greust when all three bandit lords died, else the spared
            // lord (MapBossOverride); his death sets KilledBoss (the gate of the north; Ralakesh's Pantheon unlock) and Sin
            // appears where he fell (1130), gone with Eramir's congratulation. Eramir's "Ralakesh Reward" is offer 46, a book.
            new Quest("a7q1")
            {
                On.Near(Crossroads, Act7Objects.BanditBlock, "a7q1-SeenBlockage"),
                On.Seen(AshenFields, Act7Monsters.Greust, "a7q1-SeenBoss"),
                On.Killed(AshenFields, Act7Monsters.Greust, "a7q1-KilledBoss"),
                On.Killed(AshenFields, Act7Monsters.RalakeshOak, "a7q1-KilledBoss"),
                On.Killed(AshenFields, Act7Monsters.RalakeshAlira, "a7q1-KilledBoss"),
                On.Killed(AshenFields, Act7Monsters.RalakeshKraityn, "a7q1-KilledBoss"),
                On.Appears(AshenFields, Act7Monsters.Greust, Act7Objects.SinRalakesh, "a7q1-Congratulated"),
                On.Exit(AshenFields, NorthernForest, "a7q1-KilledBoss"),
            },
            // a7q2 "Essence of the Artist" (act7.md a7q2): Sin's "Inquisitor Maligaro", Helena's clue; the map lies in The
            // Crypt's Container of Sins (maligarochest_01.arm; MaligaroMap.it: grant a7q2-HaveItem, extra a7q3-SpawnSilk, kept
            // until Silk takes it). Used at the Reverie Device in The Chamber of Sins Level 1 (tile temple_maporrery4) it opens
            // the portals (a7q2-DeliveredItem; no setter in the data): MaligaroPortalTransition, which the device spawns beside
            // itself, leads into the Sanctum (a quest portal, shown with the flag). Maligaro (mapboss1 with Black Death and
            // Fidelitas) dies: KilledBoss, and Sin appears (1137: no god). Helena's "Maligaro Reward" is offer 47 (class
            // boots, rare/magic).
            new Quest("a7q2")
            {
                On.Chest(Crypt, Act7Objects.MaligaroMapChest, Act7Items.MaligaroMap),
                On.Object(SinsLevel1, Act7Objects.Orrery, "a7q2-DeliveredItem"),
                On.UsedWith(SinsLevel1, Act7Objects.Orrery, ["a7q2-HaveItem"], "a7q2-DeliveredItem"),
                On.Portal(SinsLevel1, Act7Objects.Orrery, MaligarosSanctum, Act7Objects.MaligaroPortal),
                On.Exit(SinsLevel1, MaligarosSanctum, "a7q2-DeliveredItem"),
                On.Seen(MaligarosSanctum, Act7Monsters.Maligaro, "a7q2-SeenBoss"),
                On.Seen(MaligarosSanctum, Act7Monsters.BlackDeath, "a7q2-SeenBoss"),
                On.Killed(MaligarosSanctum, Act7Monsters.Maligaro, "a7q2-KilledBoss"),
                On.Appears(MaligarosSanctum, Act7Monsters.Maligaro, Act7Objects.SinMaligaro, "SinMaligaroReward"),
            },
            // a7q3 "Web of Secrets" (act7.md a7q3): Silk waits beside the orrery from the map's pickup (block_quest_flag
            // a7q3-SpawnSilk) until the key is used (unblock a7q1-UsedKey). Black Death drops her venom (BlackVenom.it: remove
            // a7q3-DeliveredItem, which also takes the map); her "Black Death Reward" (offer 48) is the Obsidian Key. Coming to
            // the barrier before Level 2 with the key lifts it (a7q2-ClearedSinBlock: no setter in the data; its minimap icon
            // shows with the key and hides with the flag) and its nine blockers; the key used at the passage cover in Level 2
            // (a7q1-UsedKey, the key's remove_flag) opens the passage into The Den.
            new Quest("a7q3")
            {
                On.Npc(SinsLevel1, Act7Objects.SilkSins, "a7q1-UsedKey", shownBy: "a7q3-SpawnSilk"),
                On.Drops(MaligarosSanctum, Act7Monsters.BlackDeath, Act7Items.BlackVenom),
                On.Object(SinsLevel1, Act7Objects.SinsBarrier, "a7q2-ClearedSinBlock"),
                On.NearWith(SinsLevel1, Act7Objects.SinsBarrier, ["a7q1-HaveKey"], "a7q2-ClearedSinBlock"),
                On.Blocker(SinsLevel1, Act7Objects.SinsBarrierBlocker, 4, "a7q2-ClearedSinBlock"),
                On.Exit(SinsLevel1, SinsLevel2, "a7q2-ClearedSinBlock"),
                On.Object(SinsLevel2, Act7Objects.PassageCover, "a7q1-UsedKey"),
                On.UsedWith(SinsLevel2, Act7Objects.PassageCover, ["a7q1-HaveKey"], "a7q1-UsedKey"),
                On.Exit(SinsLevel2, Den, "a7q1-UsedKey"),
            },
            // a7q4 "The Mother of Spiders" (act7.md a7q4): Yeena's "Silk"; the webbed temple in The Vaal City is seen
            // (a7q4-SeenBlockage, no setter: proximity), the Fireflies burn it (a7q7). Arakaali dies in The Temple of Decay
            // Level 2 (boss.arm mapboss): KilledBoss (her Pantheon unlock), Sin appears (1135) until Act 8, and her bridge
            // (Arakaali_Bridge, a TriggerableBlockage that starts blocked) lowers to the exit into The Sarn Ramparts.
            new Quest("a7q4")
            {
                On.Near(VaalCity, Act7Objects.TempleWeb, "a7q4-SeenBlockage"),
                On.Seen(TempleOfDecay2, Act7Monsters.Arakaali, "a7q4-SeenBoss"),
                On.Killed(TempleOfDecay2, Act7Monsters.Arakaali, "a7q4-KilledBoss"),
                On.Appears(TempleOfDecay2, Act7Monsters.Arakaali, Act7Objects.SinArakaali, "Visited-8_1"),
                On.Object(TempleOfDecay2, Act7Objects.ArakaaliBridge, "a7q4-KilledBoss"),
                On.Object(TempleOfDecay2, Act7Objects.ArakaaliBridgeTransition, "a7q4-KilledBoss"),
                On.Exit(TempleOfDecay2, SarnRamparts, "a7q4-KilledBoss"),
            },
            // a7q5 "The Silver Locket" (act7.md a7q5): the Dirty Lockbox in The Broken Bridge's manor (manor.arm short line).
            // Weylam's 1018 takes the locket (its remove_flag); "Silver Locket Reward" is offer 49 (flasks).
            new Quest("a7q5")
            {
                On.Chest(BrokenBridge, Act7Objects.LocketChest, Act7Items.SilverLocket),
            },
            // a7q6 "Kishara's Star" (act7.md a7q6): Kishara's Lockbox in The Causeway's city (city.arm short line); Weylam's
            // 1021 takes the star; "Kishara's Star Reward" (offer 50) is a book (QSR 20: 1 passive, 2 respec).
            new Quest("a7q6")
            {
                On.Chest(Causeway, Act7Objects.KisharaStarChest, Act7Items.KisharaStar),
            },
            // a7q7 "Lighting the Way" (act7.md a7q7): seven Fireflies in The Dread Thicket. No room or tile has their chests
            // (the real server places them): the engine scatters FireflyChest1-7 over the area. Yeena in The Vaal City (NPCTalk
            // 1042, all seven) takes them (remove a7q7-DeliveredItems); her script transitions herself, every web and the
            // temple entrance, which opens The Temple of Decay (its EndQuestFlags too).
            new Quest("a7q7")
            {
                On.ScatteredChest(DreadThicket, Act7Objects.FireflyChests[0], Act7Items.Fireflies[0]),
                On.ScatteredChest(DreadThicket, Act7Objects.FireflyChests[1], Act7Items.Fireflies[1]),
                On.ScatteredChest(DreadThicket, Act7Objects.FireflyChests[2], Act7Items.Fireflies[2]),
                On.ScatteredChest(DreadThicket, Act7Objects.FireflyChests[3], Act7Items.Fireflies[3]),
                On.ScatteredChest(DreadThicket, Act7Objects.FireflyChests[4], Act7Items.Fireflies[4]),
                On.ScatteredChest(DreadThicket, Act7Objects.FireflyChests[5], Act7Items.Fireflies[5]),
                On.ScatteredChest(DreadThicket, Act7Objects.FireflyChests[6], Act7Items.Fireflies[6]),
                On.Npc(VaalCity, Act7Objects.YeenaVaalCity, null, transitionedBy: "a7q7-DeliveredItems"),
                On.Object(VaalCity, Act7Objects.TempleWeb, "a7q7-DeliveredItems"),
                On.Exit(VaalCity, TempleOfDecay1, "a7q7-DeliveredItems"),
            },
            // a7q8 "In Memory of Greust" (act7.md a7q8): Helena gives Greust's Necklace (offer 52, a quest item); the Azmeri
            // shrine in The Northern Forest (tile forest_azmeri_shrine) is seen (a7q8-SeenShrine) and takes it
            // (a7q8-DeliveredItem, the necklace's remove_flag). Helena's "Greust's Necklace Reward" is offer 51 (amulets).
            new Quest("a7q8")
            {
                On.Near(NorthernForest, Act7Objects.GreustShrine, "a7q8-SeenShrine"),
                On.Object(NorthernForest, Act7Objects.GreustShrine, "a7q8-DeliveredItem"),
                On.UsedWith(NorthernForest, Act7Objects.GreustShrine, ["a7q8-HaveItem"], "a7q8-DeliveredItem"),
            },
            // a7q9 "Queen of Despair" (act7.md a7q9): Sin's "Gruthkul"; she dies in The Dread Thicket (despair_boss.arm
            // mapboss1): KilledBoss (her Pantheon unlock), Sin appears (1132) until Eramir's EramirGruthkulReward. Eramir's
            // "Gruthkul Reward" (offer 53) is a book (QSR 19).
            new Quest("a7q9")
            {
                On.Seen(DreadThicket, Act7Monsters.Gruthkul, "a7q9-SeenBoss"),
                On.Killed(DreadThicket, Act7Monsters.Gruthkul, "a7q9-KilledBoss"),
                On.Appears(DreadThicket, Act7Monsters.Gruthkul, Act7Objects.SinGruthkul, "EramirGruthkulReward"),
            },
        ];
}
