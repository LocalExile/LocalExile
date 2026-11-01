using static Quests;
using static Act6Areas;

/// <summary>WorldAreas Ids of Act 6's quest areas (and Act 7's town, The Bridge Encampment, where Weylam sails).</summary>
static class Act6Areas
{
    public const string TwilightStrandAct6 = "2_6_1", CoastAct6 = "2_6_2", TidalIslandAct6 = "2_6_3", MudFlatsAct6 = "2_6_4",
        KaruiFortress = "2_6_5", Ridge = "2_6_6", LowerPrisonAct6 = "2_6_7_1", ShavronnesTower = "2_6_7_2", PrisonersGateAct6 = "2_6_8",
        WetlandsAct6 = "2_6_11", CavernOfAngerAct6 = "2_6_13", Beacon = "2_6_14", BrineKingsReef = "2_6_15", BridgeEncampment = "2_7_town";
}

/// <summary>MonsterVarieties Ids of Act 6's quest monsters (rows in brackets).</summary>
static class Act6Monsters
{
    public const string DishonouredQueen = "Metadata/Monsters/KaomWarrior/KaruiElder1";               // 5933 (mapboss1 of floodedcave.arm)
    public const string Tukohama = "Metadata/Monsters/Tukohama/Tukohama";                            // 5783 Tukohama, Karui God of War
    public const string Shavronne = "Metadata/Monsters/Shavronne/ShavronneTower";                    // 5488 Shavronne the Returned (mapboss1)
    public const string Brutus = "Metadata/Monsters/Brute/BruteTower";                                // 5482 Reassembled Brutus (mapboss2)
    public const string Abberath = "Metadata/Monsters/Abberath/Abberath";                             // 6008 the first phase (mapboss1WG)
    public const string Abberath2 = "Metadata/Monsters/Abberath/Abberath2";                           // 6012 the second phase (mapboss2WG)
    public const string Ryslatha = "Metadata/Monsters/InsectSpawner/InsectSpawnerParasiteBoss";       // 1610 Ryslatha, the Puppet Mistress
    public const string BrineKing = "Metadata/Monsters/BrineKing/BrineKing";                          // 5493 Tsoagoth, The Brine King
}

/// <summary>BaseItemTypes Ids of Act 6's quest items dropped by rules (rows in brackets).</summary>
static class Act6Items
{
    public const string EyeOfConquest = "Metadata/Items/QuestItems/Act6/KaruiEye";               // 1576
    public const string BestelsManuscript = "Metadata/Items/QuestItems/Act6/BestelsManuscript";  // 1577
    public const string BlackFlag = "Metadata/Items/QuestItems/Act6/BlackFlag";                  // 1578
}

/// <summary>.ot types and Chests.dat Ids of Act 6's quest objects and chests the engine creates or a rule watches.</summary>
static class Act6Objects
{
    public const string MudFlatsKaruiDoor = "Metadata/QuestObjects/Act6/MudFlatsKaruiDoor",
        FortressKaruiDoor = "Metadata/QuestObjects/Act6/FortressKaruiDoor";
    public const string BestelChest = "Metadata/Chests/QuestChests/BestelChest";                  // Chests 207
    public const string BlackFlagChest = "Metadata/Chests/QuestChests/BlackFlagChest";            // a point line of Nessa's room
    public const string BeaconCamera = "Metadata/MiscellaneousObjects/CameraZoom/Act6Beacon";
    public const string BeaconLever = "Metadata/QuestObjects/Act6/BlackCrest_BeaconLever",
        BeaconStatue = "Metadata/QuestObjects/Act6/BlackCrest_BeaconInteract",
        BeaconFuelTank = "Metadata/QuestObjects/Act6/BeaconFuelTank", BeaconFlame = "Metadata/QuestObjects/Act6/BlackCrest_BeaconFlame";
    public const string ReefCrest = "Metadata/QuestObjects/Act6/BlackCrest_Reef";
    public const string LillyShip = "Metadata/QuestObjects/Act6/LillyShip";
}

/// <summary>NPCs.dat / .ot Ids of Act 6's quest NPCs.</summary>
static class Act6Npcs
{
    public const string NessaMudFlats = "Metadata/NPC/Act6/NessaMudFlats", NessaPrisonersGate = "Metadata/NPC/Act6/NessaPrisonersGate",
        NessaMerveilsCaverns = "Metadata/NPC/Act6/NessaMerveilsCaverns", WeylamBeacon = "Metadata/NPC/Act6/WeylamBeacon",
        WeylamReef1 = "Metadata/NPC/Act6/WeylamReef1", WeylamReef2 = "Metadata/NPC/Act6/WeylamReef2",
        SinTukohama = "Metadata/NPC/Act6/SinTukohama", SinShavronne = "Metadata/NPC/Act6/SinShavronne",
        SinAbberath = "Metadata/NPC/Act6/SinAbberath", SinRyslatha = "Metadata/NPC/Act6/SinRyslatha",
        SinBrineKing = "Metadata/NPC/Act6/SinBrineKing";
}

/// <summary>
/// Act 6's quests as the engine's tables (Agents/docs/game/campaign/act6.md, 12-quests.md §15). Lioneye's Watch again
/// (2_6_town): a6q3 (the Eye of Conquest, Tukohama) opens the way to Shavronne's Tower, a6q2 (Shavronne and Brutus)
/// Prisoner's Gate and the forests, a6q1 (Nessa, the Black Flag, the Beacon, the Brine King) runs through the act and ends
/// it: Weylam sails to the Brine King's Reef and from there to the Bridge Encampment (NPCTalk 1006 / 1010 AreaTransitionTo).
/// The Pantheon starts here (Modules/Core/Player/Pantheon): after each god dies Sin appears beside it (NPCTalk 993, 997,
/// 1000, 1003: CanChangePantheon, the Pantheon panel). Side quests: a6q4 (clear the Twilight Strand), a6q5 (Bestel's
/// manuscript), a6q6 (Ryslatha), a6q7 (Abberath).
/// The game's order (Quest.dat rows): a6q1 (70), a6q2, a6q3, a6q4, a6q5, a6q6, a6q7 (76). The order the campaign plays
/// them (scripts/dev/campaign/order.py): a6q4, a6q5, a6q3, a6q2, a6q7, a6q6, a6q1.
/// </summary>
sealed class Act6 : Act
{
    public override int Number => 6;

    public override IReadOnlyList<Quest> Table =>
        [
            // a6q1 "The Brine King": Nessa is seen three times (a server script in the real game: coming near her sets
            // SeenNessa1-3 and she transitions away; no data sets them): the Mud Flats' flooded cave, Prisoner's Gate,
            // Merveil's empty cavern. The Black Flag (a quest chest on the point list of her room) is burnt at the Beacon: the
            // real game has the player push two fuel carts to the tank (BeaconPayload, a Movement object the server cannot
            // write yet), pull the ignition lever and throw the flag into the flames. Here the lever refuels and lights the
            // beacon (RefueledBeacon; the carts are left out) and the beacon statue takes the flag (DeliveredFlag). That
            // brings Weylam ashore (WeylamBeacon is hidden until a6q1-DeliveredFlag) and his 1006 sails to the Reef (its
            // EndQuestFlags a6q1-DeliveredFlag). Tsoagoth's death sets KilledBoss (the Brine King's Pantheon unlock); Weylam
            // takes his throne (WeylamReef2 state 2) and Sin appears where the king fell (1003: the Pantheon). WeylamReef2's
            // 1010 sails to the Bridge Encampment (EndQuestFlags a6q1-KilledBoss).
            new Quest("a6q1")
            {
                On.Npc(MudFlatsAct6, Act6Npcs.NessaMudFlats, null, transitionedBy: "a6q1-SeenNessa1"),
                On.Near(MudFlatsAct6, Act6Npcs.NessaMudFlats, "a6q1-SeenNessa1"),
                On.Npc(PrisonersGateAct6, Act6Npcs.NessaPrisonersGate, null, transitionedBy: "a6q1-SeenNessa2"),
                On.Near(PrisonersGateAct6, Act6Npcs.NessaPrisonersGate, "a6q1-SeenNessa2"),
                On.Npc(CavernOfAngerAct6, Act6Npcs.NessaMerveilsCaverns, null, transitionedBy: "a6q1-SeenNessa3"),
                On.Near(CavernOfAngerAct6, Act6Npcs.NessaMerveilsCaverns, "a6q1-SeenNessa3"),
                On.Chest(CavernOfAngerAct6, Act6Objects.BlackFlagChest, Act6Items.BlackFlag),
                On.Near(Beacon, Act6Objects.BeaconCamera, "a6q1-SeenBeacon"),
                On.Object(Beacon, Act6Objects.BeaconLever, "a6q1-RefueledBeacon"),
                On.Used(Beacon, Act6Objects.BeaconLever, "a6q1-RefueledBeacon"),
                On.Object(Beacon, Act6Objects.BeaconFuelTank, "a6q1-RefueledBeacon"),
                On.Object(Beacon, Act6Objects.BeaconFlame, "a6q1-DeliveredFlag"),
                On.Object(Beacon, Act6Objects.BeaconStatue, "a6q1-DeliveredFlag"),
                On.UsedWith(Beacon, Act6Objects.BeaconStatue, ["a6q1-HaveFlag", "a6q1-RefueledBeacon"], "a6q1-DeliveredFlag"),
                On.Npc(Beacon, Act6Npcs.WeylamBeacon, null, transitionedBy: "a6q1-DeliveredFlag"),
                On.Npc(BrineKingsReef, Act6Npcs.WeylamReef1, null),
                On.Npc(BrineKingsReef, Act6Npcs.WeylamReef2, null, transitionedBy: "a6q1-KilledBoss"),
                On.Object(BrineKingsReef, Act6Objects.ReefCrest, "a6q1-KilledBoss"),
                On.Seen(BrineKingsReef, Act6Monsters.BrineKing, "a6q1-SeenBoss"),
                On.Killed(BrineKingsReef, Act6Monsters.BrineKing, "a6q1-KilledBoss"),
                On.Appears(BrineKingsReef, Act6Monsters.BrineKing, Act6Npcs.SinBrineKing, "SinBrineKingReward"),
            },
            // a6q2 "Essence of Umbra": Shavronne the Returned and Reassembled Brutus in her tower (boss1.arm
            // mapboss1/mapboss2); her death sets KilledBoss (the gate of Prisoner's Gate and the forests; which death sets it
            // is not in the data: hers, the quest's name). Sin harvests her soul (SinShavronne, 995: no god). Tarkleigh's
            // "Shavronne Reward" is offer 40.
            new Quest("a6q2")
            {
                On.Seen(ShavronnesTower, Act6Monsters.Shavronne, "a6q2-SeenBoss"),
                On.Killed(ShavronnesTower, Act6Monsters.Shavronne, "a6q2-KilledBoss"),
                On.Appears(ShavronnesTower, Act6Monsters.Shavronne, Act6Npcs.SinShavronne, "SinShavronneReward"),
            },
            // a6q3 "The Father of War": the Dishonoured Queen (floodedcave.arm mapboss1, with three Forgotten Warriors) drops
            // the Eye of Conquest (wiki; KaruiEye.it: grant a6q3-HaveItem, remove a6q3-DeliveredItem), which opens the totem
            // door into The Karui Fortress (its EndQuestFlags a6q3-DeliveredItem). Coming near the door sets SeenEntrance,
            // near the locked exit SeenExit (server scripts in the real game: the exits beside them). Tukohama's death opens
            // the exit to The Ridge (KilledBoss; Tukohama's Pantheon unlock) and brings Sin (993). Lilly's ship leaves
            // Lioneye's Watch then (lillyship.ot serialisation_stop_flag). Tarkleigh's "Tukohama Reward" (offer 41) is a Book
            // of Skill.
            new Quest("a6q3")
            {
                On.Near(MudFlatsAct6, Act6Objects.MudFlatsKaruiDoor, "a6q3-SeenEntrance"),
                On.Drops(MudFlatsAct6, Act6Monsters.DishonouredQueen, Act6Items.EyeOfConquest),
                On.Object(MudFlatsAct6, Act6Objects.MudFlatsKaruiDoor, "a6q3-DeliveredItem"),
                On.UsedWith(MudFlatsAct6, Act6Objects.MudFlatsKaruiDoor, ["a6q3-HaveItem"], "a6q3-DeliveredItem"),
                On.Exit(MudFlatsAct6, KaruiFortress, "a6q3-DeliveredItem"),
                On.Near(KaruiFortress, Act6Objects.FortressKaruiDoor, "a6q3-SeenExit"),
                On.Object(KaruiFortress, Act6Objects.FortressKaruiDoor, "a6q3-KilledBoss"),
                On.Seen(KaruiFortress, Act6Monsters.Tukohama, "a6q3-SeenBoss"),
                On.Killed(KaruiFortress, Act6Monsters.Tukohama, "a6q3-KilledBoss"),
                On.Exit(KaruiFortress, Ridge, "a6q3-KilledBoss"),
                On.Appears(KaruiFortress, Act6Monsters.Tukohama, Act6Npcs.SinTukohama, "SinTukohamaReward"),
                On.Exists(Act6Objects.LillyShip, until: "a6q3-KilledBoss"),
            },
            // a6q4 "Fallen from Grace": Lilly's task, then every monster of The Twilight Strand dies ("Clear the Twilight
            // Strand"). Her "Twilight Strand Reward" (offer 42) is a Book of Regrets (QSR 16: 2 respec).
            new Quest("a6q4")
            {
                On.Cleared(TwilightStrandAct6, "a6q4-QuestReceived", "a6q4-AreaCleared"),
            },
            // a6q5 "Bestel's Epic": the storage chest on The Tidal Island's wreck (medicine_boat_1/2.arm, Chests 207) holds the
            // manuscript (remove_flag a6q5-DeliveredItem: Bestel's 934 takes it). Offer 43.
            new Quest("a6q5")
            {
                On.Chest(TidalIslandAct6, Act6Objects.BestelChest, Act6Items.BestelsManuscript),
            },
            // a6q6 "The Puppet Mistress": Ryslatha in The Wetlands (bossfight.arm mapboss): her Pantheon unlock, and Sin
            // (1000). Tarkleigh's "Puppet Mistress Reward" (offer 44) is a Book of Skill.
            new Quest("a6q6")
            {
                On.Seen(WetlandsAct6, Act6Monsters.Ryslatha, "a6q6-SeenBoss"),
                On.Killed(WetlandsAct6, Act6Monsters.Ryslatha, "a6q6-KilledBoss"),
                On.Appears(WetlandsAct6, Act6Monsters.Ryslatha, Act6Npcs.SinRyslatha, "SinRyslathaReward"),
            },
            // a6q7 "The Cloven One": Abberath fights at Prisoner's Gate in two phases (bossphase1/2.arm, mapboss1WG /
            // mapboss2WG); the second phase's death sets KilledBoss (inferred: the final death), his Pantheon unlock, and Sin
            // appears (997; the flag's Id is spelt SinAberrathReward). Bestel's "Abberath Reward" (offer 45) is a Book of
            // Skill.
            new Quest("a6q7")
            {
                On.Seen(PrisonersGateAct6, Act6Monsters.Abberath, "a6q7-SeenBoss"),
                On.Seen(PrisonersGateAct6, Act6Monsters.Abberath2, "a6q7-SeenBoss"),
                On.SecondForm(PrisonersGateAct6, Act6Monsters.Abberath, Act6Monsters.Abberath2),
                On.Killed(PrisonersGateAct6, Act6Monsters.Abberath2, "a6q7-KilledBoss"),
                On.Appears(PrisonersGateAct6, Act6Monsters.Abberath2, Act6Npcs.SinAbberath, "SinAberrathReward"),
            },
        ];
}
