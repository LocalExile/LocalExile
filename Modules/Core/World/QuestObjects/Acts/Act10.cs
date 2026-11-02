using static Quests;
using static Act10Areas;

/// <summary>WorldAreas Ids of Act 10's quest areas and the Epilogue's Karui Shores (where Lilly sails, A11q0).</summary>
static class Act10Areas
{
    public const string OriathDocks = "2_10_town", CathedralRooftop = "2_10_1", RavagedSquare = "2_10_2", DesecratedChambers = "2_10_4",
        Canals = "2_10_5", FeedingTrough = "2_10_6", ControlBlocks = "2_10_7", Reliquary = "2_10_8", Ossuary = "2_10_9",
        KaruiShores = "2_11_endgame_town";
}

/// <summary>MonsterVarieties Ids of Act 10's quest monsters (rows in brackets).</summary>
static class Act10Monsters
{
    public const string Plaguewing = "Metadata/Monster/KitavaDemon/KitavaDemonPlaguewing";            // 5521 (Monster, not Monsters)
    public const string Avarius = "Metadata/Monsters/AvariusCasticus/AvariusCasticusWickerman";       // 5469 Avarius, Reassembled
    public const string Vilenta = "Metadata/Monsters/Vilenta/Vilenta";                                // 6686 Vilenta
    public const string Kitava = "Metadata/Monsters/KitavaBoss/KitavaFinal";                          // 5756 Kitava, the Insatiable
}

/// <summary>BaseItemTypes Ids of Act 10's quest items.</summary>
static class Act10Items
{
    public const string AvariusStaff = "Metadata/Items/QuestItems/Act10/AvariusStaff", Elixir = "Metadata/Items/QuestItems/Act10/Potion",
        Teardrop = "Metadata/Items/QuestItems/Act10/Teardrop";
}

/// <summary>Act 10's quest objects, chests and NPCs (Oriath Docks' two stages of Bannon included).</summary>
static class Act10Objects
{
    public const string PlaguewingSpawner = "Metadata/Monsters/KitavaDemon/KitavaDemonSpawnerPlaguewing",
        HornBlockage = "Metadata/QuestObjects/Act10/Square/CanalHornBlockage", HornBlocker = "Metadata/QuestObjects/Act10/Square/HornBlocker_1",
        ElixirChest = "Metadata/Chests/QuestChests/Potion", TeardropChest = "Metadata/Chests/QuestChests/Teardrop",
        BannonRooftop = "Metadata/NPC/Act10/BannonRooftop", BannonTown = "Metadata/NPC/Act10/BannonTown",
        InnocenceTown = "Metadata/NPC/Act10/InnocenceTown", InnocenceSquare = "Metadata/NPC/Act10/InnocenceSquare",
        SinTrough = "Metadata/NPC/Act10/SinTrough", InnocenceTrough = "Metadata/NPC/Act10/InnocenceTrough",
        EpiloguePortal = "Metadata/QuestObjects/Act10/EpiloguePortal";
}

/// <summary>
/// Act 10's quests as the engine's tables (Agents/docs/game/campaign/act10.md, 12-quests.md §19). Oriath Docks' main line:
/// Bannon rescued on The Cathedral Rooftop (a10q1: Plaguewing comes as the player reaches him), Avarius' Staff of Purity
/// turns Bannon into Innocence (a10q2), Innocence breaks Kitava's Horns in The Ravaged Square and Kitava falls in The
/// Feeding Trough (a10q3: EndGameAvailable, the second −30% resistance penalty, the Epilogue), then Lilly sails to Karui
/// Shores (A11q0, NPCTalk 1433: its script's SetQuestFlag and AreaTransitionTo, no rule). a10q3a11 is a10q3's reward in the
/// Epilogue (Lani in Karui Shores, NPCTalk 1497/1498; no rules of its own), so the Epilogue has no act file. Side quests
/// a10q4 (the Elixir of Allure: a Book of Regrets), a10q5 (The Teardrop), a10q6 (Vilenta: a book). The campaign plays them
/// (order.py) a10q1, a10q6, a10q4, a10q5, a10q2, a10q3, A11q0, a10q3a11.
/// </summary>
sealed class Act10 : Act
{
    public override int Number => 10;

    public override IReadOnlyList<Quest> Table =>
        [
            // a10q1 "Safe Passage" (act10.md a10q1): Lani's "Bannon". On The Cathedral Rooftop Bannon crouches among the
            // cultists (bossentry.arm; Transitionable 1, not targetable); coming near sets a10q1-SeenBannon and brings
            // Plaguewing from its spawner (the real event sends cultist waves first: KitavaCultistEventController). Its death
            // rescues him (a10q1-RescuedBannon, no setter in the data) and he stands up (state 2) to say "Thank You". Lani's
            // "Bannon Reward" is offer 64 (flasks).
            new Quest("a10q1")
            {
                On.Npc(CathedralRooftop, Act10Objects.BannonRooftop, null, transitionedBy: "a10q1-RescuedBannon"),
                On.Near(CathedralRooftop, Act10Objects.BannonRooftop, "a10q1-SeenBannon"),
                On.SpawnsNear(CathedralRooftop, Act10Objects.PlaguewingSpawner, Act10Monsters.Plaguewing, until: "a10q1-RescuedBannon"),
                On.Seen(CathedralRooftop, Act10Monsters.Plaguewing, "a10q1-SeenBoss"),
                On.Killed(CathedralRooftop, Act10Monsters.Plaguewing, "a10q1-RescuedBannon"),
            },
            // a10q2 "Death and Rebirth" (act10.md a10q2): Bannon's "The Staff of Avarius"; Avarius, Reassembled dies in The
            // Desecrated Chambers (boss_02.arm) and the staff is taken from his corpse (dropped). Bannon takes it (1455), then
            // 1456 (a10q2-BannonTransformed; his DirtyTransition and innocence.DirtyTransition) turns him into Innocence: the
            // town room lists both on one cell (TownNpcs: dock_town.arm), so Bannon goes and InnocenceTown stands
            // (Transitionable 2: visible). Lani's "Innocence Reward" is offer 65 (armours, an Uncarved Gemstone).
            new Quest("a10q2")
            {
                On.Seen(DesecratedChambers, Act10Monsters.Avarius, "a10q2-SeenBoss"),
                On.Killed(DesecratedChambers, Act10Monsters.Avarius, "a10q2-KilledBoss"),
                On.Drops(DesecratedChambers, Act10Monsters.Avarius, Act10Items.AvariusStaff),
                On.Npc(OriathDocks, Act10Objects.BannonTown, "a10q2-BannonTransformed"),
                On.Npc(OriathDocks, Act10Objects.InnocenceTown, null, transitionedBy: "a10q2-BannonTransformed", shownBy: "a10q2-BannonTransformed"),
            },
            // a10q3 "An End to Hunger" (act10.md a10q3): the horns are seen (a10q3-SeenGate); Innocence in The Ravaged
            // Square (InnocenceSquare 1462, a10q3-OpenedGate) breaks them: the horn blockage transitions, its twelve
            // blockers lift and the horn gate into The Canals opens (The Canals' and The Feeding Trough's
            // EndQuestFlags). In The Feeding Trough SinTrough's "Kitava" (1492, session SinPreKitavaTalk) starts the
            // fight: Sin and Innocence fly off. Kitava cannot die (set_cannot_die; the real fight ends in a script):
            // the hit that would kill him (or /kill) ends the fight, EndGameAvailable and he leaves. EndGameAvailable
            // is QuestStaticRewards 39 (another −30% to elemental and chaos resistances: Game ResistancePenalty), the
            // Epilogue's gate (Karui Shores' and Oriath's EndQuestFlags) and the condition of Lilly's 1433 and Lani's
            // 1415/1416; every character is given it for the Atlas, but until earned here it is sent only in a hideout
            // and the maps (Quests.EndgameOnly, ClientState.UnearnedQuestFlags). Sin's portal back to the docks
            // (EpiloguePortal, kitava.arm) opens with the fight's end: in the real game the post-fight Sin's 1942 opens
            // it, a monster with an NPC component that is not created, so it opens with EndGameAvailable. Lani's
            // "Kitava Reward" (offer 66, Oriath Docks 1416 or Karui Shores 1498) is a book of two passive points (QSR
            // 29).
            new Quest("a10q3")
            {
                On.Near(RavagedSquare, Act10Objects.HornBlockage, "a10q3-SeenGate"),
                On.Npc(RavagedSquare, Act10Objects.InnocenceSquare, null, transitionedBy: "a10q3-OpenedGate"),
                On.Object(RavagedSquare, Act10Objects.HornBlockage, "a10q3-OpenedGate"),
                On.Blocker(RavagedSquare, Act10Objects.HornBlocker, 4, "a10q3-OpenedGate"),
                On.Exit(RavagedSquare, Canals, "a10q3-OpenedGate"),
                On.Npc(FeedingTrough, Act10Objects.SinTrough, null, transitionedBy: "SinPreKitavaTalk"),
                On.Npc(FeedingTrough, Act10Objects.InnocenceTrough, null, transitionedBy: "SinPreKitavaTalk"),
                On.Seen(FeedingTrough, Act10Monsters.Kitava, "a10q3-SeenBoss"),
                On.Threshold(FeedingTrough, Act10Monsters.Kitava, 1, EndGameAvailable),
                On.Portal(FeedingTrough, Act10Objects.EpiloguePortal, OriathDocks, Act10Objects.EpiloguePortal),
                On.Exit(FeedingTrough, OriathDocks, EndGameAvailable),
            },
            // a10q4 "No Love for Old Ghosts" (act10.md a10q4): the Sealed Chest in The Ossuary (boss_01.arm point line) holds
            // the Elixir of Allure; Weylam takes it (1442); offer 67 is a Book of Regrets (QSR 27: 2 respec).
            new Quest("a10q4")
            {
                On.Chest(Ossuary, Act10Objects.ElixirChest, Act10Items.Elixir),
            },
            // a10q5 "Map to Tsoatha" (act10.md a10q5): the Pearl Case in The Reliquary (kitavachest_03.arm) holds The Teardrop;
            // Lilly takes it (1423); offer 68 (belts).
            new Quest("a10q5")
            {
                On.Chest(Reliquary, Act10Objects.TeardropChest, Act10Items.Teardrop),
            },
            // a10q6 "Vilenta's Vengeance" (act10.md a10q6): Vilenta dies in The Control Blocks (boss_vilenta.arm); Lani's
            // "Vilenta Reward" (offer 69) is a book (QSR 28).
            new Quest("a10q6")
            {
                On.Seen(ControlBlocks, Act10Monsters.Vilenta, "a10q6-SeenBoss"),
                On.Killed(ControlBlocks, Act10Monsters.Vilenta, "a10q6-KilledBoss"),
            },
        ];
}
