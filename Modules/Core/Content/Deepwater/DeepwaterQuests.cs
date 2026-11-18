namespace GameServer.Modules.Deepwater;

/// <summary>The Allflame tutorial's table for the quest engine (Quest.dat DeepwaterTutorial, "The Maiden Voyage"): Valerie on
/// the Deepwater Coast until the first visit of The Sovereign, Valerie on its deck and Vesper below deck. Coming near her on
/// the Coast sets DeepwaterNPCSeen, which her "Ready for Adventure?" (NPCTalk 3932) needs; that row's own start flag,
/// DeepwaterNPCIntroductionSeen, needs no rule. On the deck she is ValerieTutorial too (hub.arm places her line beside
/// Valerie's, and the hub's tutorial rows 3929-3931 are hers); Valerie (NPCs 789) takes over when the tutorial ends, by a
/// flag the client data does not name, so she is not placed yet. hub.arm's Valerie lines (x 84-97) lie off the ship's
/// walkable deck; the real server moved her to one of the server-only ValeriePlacer markers by her shared state
/// "placement". Which marker each stage used is unsourced: she stands at ValeriePlacerOne. Vesper's StateMachine hides her (cansee 0) until the
/// league flag DeepwaterValVesperFlameGiven.</summary>
internal sealed class DeepwaterQuests : Quests.Act
{
    const string Npc = "Metadata/NPC/League/Deepwater/";

    public override int Number => 1;

    public override IReadOnlyList<Quests.Quest> Table { get; } =
    [
        new Quests.Quest("DeepwaterTutorial")
        {
            Quests.On.Entered(DeepwaterModule.HubId, DeepwaterModule.HubVisited),
            Quests.On.Near(DeepwaterModule.Coast, Npc + "ValerieTutorial", "DeepwaterNPCSeen"),
            Quests.On.Npc(DeepwaterModule.Coast, Npc + "ValerieTutorial", goneWith: DeepwaterModule.HubVisited),
            Quests.On.Npc(DeepwaterModule.HubId, Npc + "ValerieTutorial", goneWith: null, atObject: Npc + "ValeriePlacerOne"),
            Quests.On.Npc(DeepwaterModule.HubId, Npc + "VesperCrafting", goneWith: null),
        },
    ];
}
