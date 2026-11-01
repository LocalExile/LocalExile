using static Quests;
using static Act9Areas;

/// <summary>WorldAreas Ids of Act 9's quest areas.</summary>
static class Act9Areas
{
    public const string Highgate = "2_9_town", VastiriDesert = "2_9_3", Oasis = "2_9_4", Foothills = "2_9_5", BoilingLake = "2_9_6",
        Quarry = "2_9_8", Refinery = "2_9_9", BellyOfTheBeast = "2_9_10_1", RottingCore = "2_9_10_2";
}

/// <summary>MonsterVarieties Ids of Act 9's quest monsters (rows in brackets).</summary>
static class Act9Monsters
{
    public const string Adus = "Metadata/Monsters/GeneralAdus/GeneralAdus";                           // 6211 General Adus
    public const string Basilisk = "Metadata/Monsters/GemMonster/IguanaBossPhoenix";                  // 6150 The Basilisk
    public const string Garukhan = "Metadata/Monsters/Garukhan/Garukhan";                             // 6103 Garukhan, Queen of the Winds
    public const string Boulderback = "Metadata/Monsters/Bull/BullBossFoothills";                     // 4328 Boulderback
    public const string Shakari = "Metadata/Monsters/Shakari/Shakari";                                // 6275 Shakari (first stage, mapboss1WG)
    public const string Shakari2 = "Metadata/Monsters/Shakari/Shakari2";                              // 6278 Shakari (second stage, mapboss2)
    public const string SandWarrior = "Metadata/Monsters/Garukhan/GarukhanMinionSmallMummyEvent";     // 6133 the mummy event's
    public const string SandGoliath = "Metadata/Monsters/Garukhan/GarukhanMinionLargeMummyEvent";     // 6136
    public const string DoedreSoul = "Metadata/Monsters/Doedre/DoedreSoul";                           // 6159 Doedre, Darksoul
    public const string MaligaroSoul = "Metadata/Monsters/Maligaro/MaligaroSoul";                     // 6175 Maligaro, The Broken
    public const string ShavronneSoul = "Metadata/Monsters/Shavronne/ShavronneSoul";                  // 6169 Shavronne, Unbound
    public const string DepravedTrinity = "Metadata/Monsters/UnholyTrio/UnholyTrio";                  // 6192 The Depraved Trinity
}

/// <summary>BaseItemTypes Ids of Act 9's quest items.</summary>
static class Act9Items
{
    public const string TrarthanPowder = "Metadata/Items/QuestItems/Act9/Ingredient1", BasiliskAcid = "Metadata/Items/QuestItems/Act9/Ingredient2",
        SekhemaFeather = "Metadata/Items/QuestItems/Act9/SekhemaFeather", StormBlade = "Metadata/Items/QuestItems/Act9/StormSword",
        Calendar = "Metadata/Items/QuestItems/Act9/Calendar";
}

/// <summary>Act 9's quest objects, chests and out-of-town NPCs.</summary>
static class Act9Objects
{
    public const string TheurgicPrecipitate = "Metadata/Chests/QuestChests/TheurgicPrecipitate", MummyEventChest = "Metadata/QuestObjects/Act9/MummyEventChest",
        SandBarrier = "Metadata/QuestObjects/Act9/OasisSandBarrier", SandBarrierSeen = "Metadata/QuestObjects/Act9/OasisSandBarrierSeen",
        MembraneAnimated = "Metadata/QuestObjects/Act9/BeastMembraneAnimated", MembraneBlocker = "Metadata/QuestObjects/Act9/BeastMembraneBlocker",
        SinQuarry = "Metadata/NPC/Act9/SinQuarry", SinCore = "Metadata/NPC/Act9/SinCore", Lilly = "Metadata/NPC/Act9/Lilly",
        SinGarukhan = "Metadata/NPC/Act9/SinGarukhan", SinShakari = "Metadata/NPC/Act9/SinShakari";
}

/// <summary>
/// Act 9's quests as the engine's tables (Agents/docs/game/campaign/act9.md, 12-quests.md §18). Highgate's main line a9q1:
/// Sin needs the Trarthan Powder (the Theurgic Precipitate Machine in The Refinery, after General Adus) and the Basilisk Acid
/// (The Basilisk in The Boiling Lake); at The Quarry he burns the membrane into The Belly of the Beast; in The Rotting Core
/// the three spirits die, Sin manifests The Depraved Trinity, and its death opens Act 10 (Lilly sails to Oriath Docks,
/// NPCTalk 1395). Side quests a9q2 (Garukhan and the Sekhema Feather: a book and her Pantheon soul), a9q3 (the Storm Blade's
/// mummy event), a9q4 (the Maraketh Calendar: a Book of Regrets), a9q5 (the Bottled Storm, Shakari: a book and her soul).
/// The campaign plays them (order.py) a9q3, a9q5, a9q4, a9q2, a9q1.
/// </summary>
sealed class Act9 : Act
{
    public override int Number => 9;

    public override IReadOnlyList<Quest> Table =>
        [
            // a9q1 "Recurring Nightmare" (act9.md a9q1): General Adus' death (a9q1-KilledAdus) makes the Theurgic Precipitate
            // Machine (Transitionable: not targetable before) give the Trarthan Powder; The Basilisk drops its acid (wiki: no
            // table names the dropper). Sin's rows take them (their remove_flags). SinQuarry's "The Beast" (1376: SinEnterBeast,
            // the membrane's transitions) opens the way into The Belly of the Beast (its seven blockers lift). In The Rotting
            // Core, SinCore's "The Dark Ember" opens the heart portals (session a9q1-PortalsOpen); the three spirits' deaths,
            // "United Trio" (a9q1-BlackHeartOpen); The Depraved Trinity, which the area does not place (AreaPopulation.LaterForms),
            // forms at its own boss point once the three spirits are dead (On.Forms); its death: a9q1-KilledBoss (Act 10's
            // gate) and the session AfterBanditFight, and Lilly waits to sail (1395 AreaTransitionTo 2_10_town).
            new Quest("a9q1")
            {
                On.Killed(Refinery, Act9Monsters.Adus, "a9q1-KilledAdus"),
                On.Chest(Refinery, Act9Objects.TheurgicPrecipitate, Act9Items.TrarthanPowder, opensWith: "a9q1-KilledAdus"),
                On.Drops(BoilingLake, Act9Monsters.Basilisk, Act9Items.BasiliskAcid),
                On.Npc(Quarry, Act9Objects.SinQuarry, null),
                On.Object(Quarry, Act9Objects.MembraneAnimated, "SinEnterBeast"),
                On.Blocker(Quarry, Act9Objects.MembraneBlocker, 4, "SinEnterBeast"),
                On.Exit(Quarry, BellyOfTheBeast, "SinEnterBeast"),
                On.Npc(RottingCore, Act9Objects.SinCore, null),
                On.Killed(RottingCore, Act9Monsters.DoedreSoul, "a9q1-KilledDoedre"),
                On.Killed(RottingCore, Act9Monsters.MaligaroSoul, "a9q1-KilledMaligaro"),
                On.Killed(RottingCore, Act9Monsters.ShavronneSoul, "a9q1-KilledShavronne"),
                On.Forms(RottingCore, [Act9Monsters.DoedreSoul, Act9Monsters.MaligaroSoul, Act9Monsters.ShavronneSoul], Act9Monsters.DepravedTrinity),
                On.Killed(RottingCore, Act9Monsters.DepravedTrinity, "a9q1-KilledBoss", "AfterBanditFight"),
                On.Npc(RottingCore, Act9Objects.Lilly, null, shownBy: "a9q1-KilledBoss"),
            },
            // a9q2 "The Ruler of Highgate" (act9.md a9q2): Tasuni's or Irasha's "Oyun" starts it (a9q2-QuestReceived: no row
            // sets it, their QuestFlag3 names it). Garukhan dies in The Quarry (boss_garukhan.arm mapboss): KilledBoss (her
            // Pantheon unlock), the Sekhema Feather drops (no table names the dropper; the log: taken after the kills), Sin
            // appears (1380) until the feather is given. Whoever takes it (Irasha 1313 / Tasuni 1302) is recorded
            // (IrashaTakeFeather / TasuniTakeFeather: no setter in the data), which her "Feather Reward" (offer 59, a book) needs.
            new Quest("a9q2")
            {
                On.TalkRow(1301, "a9q2-QuestReceived"),
                On.TalkRow(1312, "a9q2-QuestReceived"),
                On.TalkRow(1302, "TasuniTakeFeather"),
                On.TalkRow(1313, "IrashaTakeFeather"),
                On.Seen(Quarry, Act9Monsters.Garukhan, "a9q2-SeenBoss"),
                On.Killed(Quarry, Act9Monsters.Garukhan, "a9q2-KilledBoss"),
                On.Drops(Quarry, Act9Monsters.Garukhan, Act9Items.SekhemaFeather),
                On.Appears(Quarry, Act9Monsters.Garukhan, Act9Objects.SinGarukhan, "a9q2-DeliveredItem"),
            },
            // a9q3 "The Storm Blade" (act9.md a9q3): the mummy event chest in The Vastiri Desert (boss_questchest.arm; a server
            // object with a StateMachine the quest-object blob does not write): coming to it begins the encounter
            // (a9q3-BeginEncounter) and brings its sand warriors; once they are dead the encounter is over
            // (a9q3-FinishEncounter) and the Storm Blade drops (simplified: one warrior of each kind instead of the room's
            // spawner waves, the blade from the last instead of the opened chest). Petarus and Vanja take it (1281); offer 60
            // (rare weapons and armour).
            new Quest("a9q3")
            {
                On.Near(VastiriDesert, Act9Objects.MummyEventChest, "a9q3-BeginEncounter"),
                On.SpawnsNear(VastiriDesert, Act9Objects.MummyEventChest, Act9Monsters.SandWarrior, until: "a9q3-FinishEncounter"),
                On.SpawnsNear(VastiriDesert, Act9Objects.MummyEventChest, Act9Monsters.SandGoliath, until: "a9q3-FinishEncounter"),
                On.KilledAll(VastiriDesert, [Act9Monsters.SandWarrior, Act9Monsters.SandGoliath], "a9q3-FinishEncounter"),
                On.Drops(VastiriDesert, Act9Monsters.SandGoliath, Act9Items.StormBlade),
                On.Drops(VastiriDesert, Act9Monsters.SandWarrior, Act9Items.StormBlade),
            },
            // a9q4 "Fastis Fortuna" (act9.md a9q4): Boulderback dies in The Foothills (boss.arm mapboss) and drops the
            // Maraketh Calendar (wiki); Petarus and Vanja take it (1291); offer 61 is a Book of Regrets (QSR 26: 2 respec).
            new Quest("a9q4")
            {
                On.Seen(Foothills, Act9Monsters.Boulderback, "a9q4-SeenBoss"),
                On.Killed(Foothills, Act9Monsters.Boulderback, "a9q4-KilledBoss"),
                On.Drops(Foothills, Act9Monsters.Boulderback, Act9Items.Calendar),
            },
            // a9q5 "Queen of the Sands" (act9.md a9q5): the sandstorm before The Oasis is seen (OasisSandBarrierSeen:
            // a9q5-SeenStorm). Petarus and Vanja's "Take Bottled Storm" (offer 63) gives the storm; the barrier has no
            // interaction and the bottle no use action, so carrying it to the barrier uses it (a9q5-UsedItem, the bottle's
            // remove_flag): the storm clears and The Oasis opens (its EndQuestFlags). Shakari fights in two stages (boss1.arm
            // mapboss1WG, boss2.arm mapboss2; the area places only the first, the second appears where she falls); the
            // second's death sets KilledBoss (her Pantheon unlock; inferred) and Sin appears (1383). Irasha's "Shakari Reward"
            // (offer 62) is a book (QSR 24).
            new Quest("a9q5")
            {
                On.Near(VastiriDesert, Act9Objects.SandBarrierSeen, "a9q5-SeenStorm"),
                On.Object(VastiriDesert, Act9Objects.SandBarrier, "a9q5-UsedItem"),
                On.NearWith(VastiriDesert, Act9Objects.SandBarrier, ["a9q5-HaveItem"], "a9q5-UsedItem"),
                On.Exit(VastiriDesert, Oasis, "a9q5-UsedItem"),
                On.Seen(Oasis, Act9Monsters.Shakari, "a9q5-SeenBoss"),
                On.Seen(Oasis, Act9Monsters.Shakari2, "a9q5-SeenBoss"),
                On.SecondForm(Oasis, Act9Monsters.Shakari, Act9Monsters.Shakari2),
                On.Killed(Oasis, Act9Monsters.Shakari2, "a9q5-KilledBoss"),
                On.Appears(Oasis, Act9Monsters.Shakari2, Act9Objects.SinShakari, "IrashaShakariReward"),
            },
        ];
}
