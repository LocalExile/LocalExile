using static Quests;
using static Act3Areas;
using static Act4Areas;

/// <summary>WorldAreas Ids of Act 4's quest areas (The Aqueduct, 1_4_1, is Act3Areas.Aqueduct: the tower top's exit leads
/// there; and Act 5's first, The Slave Pens, where the Oriath Portal leads).</summary>
static class Act4Areas
{
    public const string Highgate = "1_4_town", DriedLake = "1_4_2", Mines1 = "1_4_3_1", Mines2 = "1_4_3_2",
        CrystalVeins = "1_4_3_3", KaomsDream = "1_4_4_1", KaomsStronghold = "1_4_4_3", DaressosDream = "1_4_5_1", GrandArena = "1_4_5_2",
        Belly1 = "1_4_6_1", Belly2 = "1_4_6_2", Harvest = "1_4_6_3", Ascent = "1_4_7", SlavePens = "1_5_1";
}

/// <summary>MonsterVarieties Ids of Act 4's quest monsters (rows in brackets).</summary>
static class Act4Monsters
{
    public const string Voll = "Metadata/Monsters/Voll/VollBoss";                      // 4095 Voll, Emperor of Purity (mapboss1)
    public const string Kaom = "Metadata/Monsters/Kaom/KaomBoss";                      // 4270 King Kaom
    public const string Daresso = "Metadata/Monsters/Daresso/DaressoBoss";             // 4334 Daresso, King of Swords (mapboss1)
    public const string PietyBeast = "Metadata/Monsters/Axis/PietyBeastBoss";          // 1425 Piety, the Abomination
    public const string Malachai = "Metadata/Monsters/Malachai/MalachaiBoss";          // 4407 (mapboss1, cannot die below 70%)
    public const string Malachai2 = "Metadata/Monsters/Malachai/MalachaiBoss2";        // 4409 the second stage (mapboss2)
    public const string Doedre = "Metadata/Monsters/Doedre/Doedre";                    // 4391 (mapboss3, organ_1.arm)
    public const string Maligaro = "Metadata/Monsters/Maligaro/Maligaro";              // 4382 (mapboss4, organ_2.arm)
    public const string Shavronne = "Metadata/Monsters/Shavronne/Shavronne";           // 4400 (mapboss5, organ_3.arm)
}

/// <summary>BaseItemTypes Ids of Act 4's quest items dropped by rules (rows in brackets).</summary>
static class Act4Items
{
    public const string RedBanner = "Metadata/Items/QuestItems/Act4/RedBanner";         // 1561 Deshret's Banner
    public const string KaomGem = "Metadata/Items/QuestItems/Act4/KaomGem";             // 1562 The Eye of Fury
    public const string DaressoGem = "Metadata/Items/QuestItems/Act4/DaressoGem";       // 1563 The Eye of Desire
    public const string Lungs = "Metadata/Items/QuestItems/Act4/Organ1";                // 1564 Malachai's Lungs
    public const string Heart = "Metadata/Items/QuestItems/Act4/Organ2";                // 1565 Malachai's Heart
    public const string Entrails = "Metadata/Items/QuestItems/Act4/Organ3";             // 1566 Malachai's Entrails
}

/// <summary>.ot types of Act 4's quest objects the engine creates (rooms and tiles; QuestObjectTypes places them).</summary>
static class Act4Objects
{
    public const string Seal = "Metadata/QuestObjects/Act4/MineEntranceSeal", SealBlocker = "Metadata/QuestObjects/Act4/MineEntranceSealBlocker";
    public const string DeshretSpirit = "Metadata/QuestObjects/Act4/DeshretSpirit", DeshretHand = "Metadata/QuestObjects/Act4/DeshretHand";
    public const string RaptureDevice = "Metadata/QuestObjects/Act4/RaptureDevice";
    public const string ResonatorLever = "Metadata/Terrain/Act4/Area7/Objects/PortalDeviceLever",
        Resonator = "Metadata/Terrain/Act4/Area7/Objects/PortalDevice";   // ascent_summit_01.arm: a room room-points does not keep
}

/// <summary>NPCs.dat / .ot Ids of Act 4's quest NPCs.</summary>
static class Act4Npcs
{
    public const string DiallaAqueduct = "Metadata/NPC/Act4/DiallaAqueduct", DiallaTown = "Metadata/NPC/Act4/DiallaTown",
        DiallaTownWithered = "Metadata/NPC/Act4/DiallaTownWithered", DiallaRapture = "Metadata/NPC/Act4/DiallaRapture",
        PietyBelly = "Metadata/NPC/Act4/PietyBelly", PietyHarvest = "Metadata/NPC/Act4/PietyHarvest";
}

/// <summary>
/// Act 4's quests as the engine's tables (Agents/docs/game/campaign/act4.md, 12-quests.md §13). Highgate is the town; a4q1
/// "The Eternal Nightmare" wraps the act: a4q2 (Voll, Deshret's Banner, the seal) opens the Mines, a4q3 and a4q4 (Kaom's and
/// Daresso's Eyes) the Rapture Device and the Belly, a4q5 (Piety, Malachai's organs) the Black Core, Malachai's death the
/// Ascent; a5q1a (the Resonator) leads into Act 5. a4q6 (Deshret's spirit) is the side quest.
/// The game's order (Quest.dat rows): a4q1 (34), a4q2, a4q3, a4q4, a4q5, a4q6 (39), a5q1a (62). The order the campaign plays
/// them (scripts/dev/campaign/order.py): a4q2, a4q6, a4q3, a4q4, a4q5, a4q1, a5q1a.
/// </summary>
sealed class Act4 : Act
{
    public override int Number => 4;

    public override IReadOnlyList<Quest> Table =>
        [
            // a4q1 "The Eternal Nightmare": Dialla waits on The Aqueduct (hub_1.arm; NPCTalk 509 "Highgate"). In Highgate the
            // town room lists both Diallas: DiallaTown until the Rapture Device works, DiallaTownWithered (offer 29 "Rapture
            // Reward", NPCTalk 521 which opens the Ascent) after (TownNpcs: her second line). At the Rapture Device in The
            // Crystal Veins (rapture_1.arm) coming near her sets SeenRaptureDialla (no NPCTalk sets it); NPCTalk 534 withers
            // her (her own DirtyTransition: state 1 → 2; withered for good with RaptureActivated) and 535 opens the wound into
            // the Belly (RaptureTransition; its row flag DiallaOnTransformed keeps it open). Piety, the Abomination dies in
            // the Belly and stays as PietyBelly where she fell (no room places her; NPCTalk 620/621 start a4q5). In The
            // Harvest the three guardians' deaths set their Killed flags (assumed: no table names the setter). KillGuardians
            // (log rows 5-12, "kill the guardians") is not set: no data sets or clears it, and the "complete" rows 0 and 2
            // need it missing (Act4Complete has none; ConsoleAscent keeps it). Malachai's second stage, which the area does
            // not place (SecondForm), appears where the first falls; its death sets QuestComplete (the first stage cannot die
            // below 70% in the game, a floor the server does not model: /kill ends it). The withered Dialla's 521 (its flag
            // DiallaOnMalachaiDead) opens the Ascent.
            new Quest("a4q1")
            {
                On.Npc(Aqueduct, Act4Npcs.DiallaAqueduct, null),
                On.Npc(Highgate, Act4Npcs.DiallaTown, "a4q1-RaptureActivated"),
                On.Npc(Highgate, Act4Npcs.DiallaTownWithered, null, shownBy: "a4q1-RaptureActivated"),
                On.Npc(CrystalVeins, Act4Npcs.DiallaRapture, null, transitionedBy: "a4q1-RaptureActivated"),
                On.Near(CrystalVeins, Act4Npcs.DiallaRapture, "a4q1-SeenRaptureDialla"),
                On.Object(CrystalVeins, Act4Objects.RaptureDevice, "a4q1-RaptureActivated"),
                On.Exit(CrystalVeins, Belly1, "a4q1-DiallaOnTransformed"),
                On.Seen(Belly2, Act4Monsters.PietyBeast, "a4q1-SeenPiety"),
                On.Killed(Belly2, Act4Monsters.PietyBeast, "a4q1-KilledPiety"),
                On.Appears(Belly2, Act4Monsters.PietyBeast, Act4Npcs.PietyBelly, "a4q5-QuestReceived"),
                On.Killed(Harvest, Act4Monsters.Doedre, "a4q1-KilledDoedre"),
                On.Killed(Harvest, Act4Monsters.Maligaro, "a4q1-KilledMaligaro"),
                On.Killed(Harvest, Act4Monsters.Shavronne, "a4q1-KilledShavronne"),
                On.Seen(Harvest, Act4Monsters.Malachai, "a4q1-SeenMalachai"),
                On.Seen(Harvest, Act4Monsters.Malachai2, "a4q1-SeenMalachai"),
                On.SecondForm(Harvest, Act4Monsters.Malachai, Act4Monsters.Malachai2),
                On.Killed(Harvest, Act4Monsters.Malachai2, "a4q1-QuestComplete"),
                On.Exit(Highgate, Ascent, "DiallaOnMalachaiDead"),
            },
            // a4q2 "Breaking the Seal": Voll stands at mapboss1 of The Dried Lake's boss room and drops Deshret's Banner (wiki:
            // "acquired after defeating Voll"; RedBanner.it: grant a4q2-HaveBanner, extra a4q2-QuestReceived, remove
            // a4q2-UsedBanner). The banner on Deshret's Seal in Highgate sets UsedBanner: the six MineEntranceSealBlockers
            // (object_size 5, unblock_quest_flag) stop blocking and the Mines open (EndQuestFlags of every Mines, Dream and
            // Arena area). Oyun's "Red Banner Reward" is offer 30.
            new Quest("a4q2")
            {
                On.Seen(DriedLake, Act4Monsters.Voll, "a4q2-SeenVoll"),
                On.Killed(DriedLake, Act4Monsters.Voll, "a4q2-KilledVoll"),
                On.Drops(DriedLake, Act4Monsters.Voll, Act4Items.RedBanner),
                On.Object(Highgate, Act4Objects.Seal, "a4q2-UsedBanner"),
                On.UsedWith(Highgate, Act4Objects.Seal, ["a4q2-HaveBanner"], "a4q2-UsedBanner"),
                On.Blocker(Highgate, Act4Objects.SealBlocker, 4, "a4q2-UsedBanner"),
                On.Exit(Highgate, Mines1, "a4q2-UsedBanner"),
            },
            // a4q3 "The King of Fury": Kaom in his Stronghold (boss_epic_2a.arm mapboss) drops The Eye of Fury (KaomGem.it:
            // grant a4q3-HaveGem, extra a4q1-GemQuestsReceived, remove a4q3-DeliveredGem); Dialla at the Rapture takes it
            // (NPCTalk 530, the remove_flag: hand-in).
            new Quest("a4q3")
            {
                On.Seen(KaomsStronghold, Act4Monsters.Kaom, "a4q3-SeenKaom"),
                On.Killed(KaomsStronghold, Act4Monsters.Kaom, "a4q3-KilledKaom"),
                On.Drops(KaomsStronghold, Act4Monsters.Kaom, Act4Items.KaomGem),
            },
            // a4q4 "The King of Desire": Daresso (mapboss1 of the Colosseum's boss_1.arm; the gladiators at mapboss2–4) drops
            // The Eye of Desire, handed in to Dialla (NPCTalk 531).
            new Quest("a4q4")
            {
                On.Seen(GrandArena, Act4Monsters.Daresso, "a4q4-SeenDaresso"),
                On.Killed(GrandArena, Act4Monsters.Daresso, "a4q4-KilledDaresso"),
                On.Drops(GrandArena, Act4Monsters.Daresso, Act4Items.DaressoGem),
            },
            // a4q5 "Corpus Malachus": PietyBelly's 621 opens The Harvest (its flag a4q5-QuestReceived, the area's
            // EndQuestFlags; the script transitions the BellyArenaTransition). Piety waits at The Harvest's entrance
            // (core_entrance_1.arm) for the three organs the guardians drop (wiki; Organ1-3.it remove a4q5-DeliveredOrgan1-3:
            // NPCTalk 624-626 hand them in), then 627 opens the Black Core (CoreTransition, in the area).
            new Quest("a4q5")
            {
                On.Exit(Belly2, Harvest, "a4q5-QuestReceived"),
                On.Npc(Harvest, Act4Npcs.PietyHarvest, null),
                On.Drops(Harvest, Act4Monsters.Doedre, Act4Items.Lungs),
                On.Drops(Harvest, Act4Monsters.Maligaro, Act4Items.Heart),
                On.Drops(Harvest, Act4Monsters.Shavronne, Act4Items.Entrails),
                // Whichever "Malachai's Organs" row is clicked takes every organ carried, in one save (online servers).
                On.HandIn(Act4Npcs.PietyHarvest, "Malachai's Organs", Act4Items.Lungs),
                On.HandIn(Act4Npcs.PietyHarvest, "Malachai's Organs", Act4Items.Heart),
                On.HandIn(Act4Npcs.PietyHarvest, "Malachai's Organs", Act4Items.Entrails),
            },
            // a4q6 "An Indomitable Spirit": Deshret's spirit in The Mines Level 2 (deshret_quest.arm). Coming near sets
            // DeshretSpiritSeen (no setter in the data); using it frees him (DeshretFreed; the spirit and its hand
            // transition). Tasuni's "Deshret Reward" (offer 31) is a Book of Skill (QSR 11: +1 passive, +2 respec).
            new Quest("a4q6")
            {
                On.Near(Mines2, Act4Objects.DeshretSpirit, "a4q6-DeshretSpiritSeen"),
                On.Object(Mines2, Act4Objects.DeshretSpirit, "a4q6-DeshretFreed"),
                On.Object(Mines2, Act4Objects.DeshretHand, "a4q6-DeshretFreed"),
                On.Used(Mines2, Act4Objects.DeshretSpirit, "a4q6-DeshretFreed"),
            },
            // a5q1a "Return to Oriath": the Resonator on The Ascent's summit (ascent_summit_01.arm, a room room-points does not
            // keep: AreaObjects.RoomObjects places it). Coming near the lever sets SeenResonator (no setter in the data);
            // pulling it sets UsedResonator (The Slave Pens' EndQuestFlags) and the device transitions. The Oriath Portal
            // (PortalDeviceTransition, a room line the layout's exits create) leads there once the flag is set.
            new Quest("a5q1a")
            {
                On.Near(Ascent, Act4Objects.ResonatorLever, "a5q1-SeenResonator"),
                On.Object(Ascent, Act4Objects.ResonatorLever, "a5q1-UsedResonator"),
                On.Object(Ascent, Act4Objects.Resonator, "a5q1-UsedResonator"),
                On.Used(Ascent, Act4Objects.ResonatorLever, "a5q1-UsedResonator"),
                On.Exit(Ascent, SlavePens, "a5q1-UsedResonator"),
            },
        ];
}
