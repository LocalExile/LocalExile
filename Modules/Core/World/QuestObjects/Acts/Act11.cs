using static Quests;
using static Act11Areas;

/// <summary>WorldAreas Ids of the Epilogue (Karui Shores).</summary>
static class Act11Areas
{
    public const string KaruiShores = "2_11_endgame_town";
}

/// <summary>NPCs.dat / .ot Ids of the Epilogue's quest NPCs.</summary>
static class Act11Objects
{
    public const string HelenaIntro = "Metadata/NPC/Epilogue/HelenaIntro";
    public const string Kirac = "Metadata/NPC/Epilogue/Kirac";
}

/// <summary>
/// Epilogue quest rules (Act 11): <c>a11q1</c> ("A Call to Arms"), Commander Kirac's Atlas and Map Device
/// introduction in Karui Shores (<c>2_11_endgame_town</c>). Helena's tunnel intro trigger hides once the Map Device
/// has been activated (<c>MapDeviceActive</c>), while Helena's NPCTalk 1970 (league flag 1055) and Kirac's NPCTalk
/// 2258/2297/2259 (league flags 1036/217/1038) plus <c>MapDeviceActive</c> / <c>InMapArea</c> / league flag 1037
/// advance the quest tracker through defeating the first Map Boss.
/// </summary>
sealed class Act11 : Act
{
    public override int Number => 11;

    public override IReadOnlyList<Quest> Table =>
        [
            new Quest("a11q1")
            {
                // HelenaIntro hides once Kirac's introduction has been seen (LQF 1035) or the Map Device is activated.
                On.Npc(KaruiShores, Act11Objects.HelenaIntro, "MapDeviceActive", goneWithLeagueFlag: 1035),
            },
        ];
}
