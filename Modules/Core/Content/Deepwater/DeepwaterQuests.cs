namespace GameServer.Modules.Deepwater;

/// <summary>The Allflame tutorial's table for the quest engine (Quest.dat DeepwaterTutorial, "The Maiden Voyage"): Valerie on
/// the Deepwater Coast until the first visit of The Sovereign, Valerie on its deck and Vesper below deck. Her dialogue flags
/// (DeepwaterNPCIntroductionSeen from "Ready for Adventure?") are NPCTalk's own columns, which need no rule. Which of Valerie's
/// other variants stands where for each quest stage (her ValeriePlacer markers) is not decided yet.</summary>
internal sealed class DeepwaterQuests : Quests.Act
{
    const string Npc = "Metadata/NPC/League/Deepwater/";

    public override int Number => 1;

    public override IReadOnlyList<Quests.Quest> Table { get; } =
    [
        new Quests.Quest("DeepwaterTutorial")
        {
            Quests.On.Entered(DeepwaterModule.HubId, DeepwaterModule.HubVisited),
            Quests.On.Npc(DeepwaterModule.Coast, Npc + "ValerieTutorial", goneWith: DeepwaterModule.HubVisited),
            Quests.On.Npc(DeepwaterModule.HubId, Npc + "Valerie", goneWith: null),
            Quests.On.Npc(DeepwaterModule.HubId, Npc + "VesperCrafting", goneWith: null),
        },
    ];
}
