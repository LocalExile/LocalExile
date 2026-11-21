namespace GameServer.Modules.Deepwater;

/// <summary>The Allflame tutorial's table for the quest engine (Quest.dat DeepwaterTutorial, "The Maiden Voyage"): Valerie on
/// the Deepwater Coast until the first visit of The Sovereign, and <see cref="DeepwaterModule.HubVisited"/> on arrival.
/// Coming near her on the Coast sets DeepwaterNPCSeen, which her "Ready for Adventure?" (NPCTalk 3932) needs; that row's
/// start flag (DeepwaterNPCIntroductionSeen) and league flag (LQF 1834 DeepwaterBoatUnlocked) are applied by the opted-in
/// league-flag NPC talk flow. All Sovereign hub NPCs (Valerie variants, VesperCrafting, below-deck Valeries) and scene props
/// are managed dynamically by <see cref="DeepwaterProgression"/>.</summary>
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
        },
    ];
}
