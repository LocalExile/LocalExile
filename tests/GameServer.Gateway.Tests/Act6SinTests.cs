using GameServer.Modules.M_questobjects;

namespace GameServer.Gateway.Tests;

public class Act6SinTests
{
    [Fact]
    public void Collecting_the_soul_keeps_the_current_speaker_for_the_Pantheon_script()
    {
        const string area = Act6Areas.WetlandsAct6;
        using var registration = Quests.Register("sin-dialogue-test", QuestObjectsModule.Acts());
        var rules = Quests.For(new HashSet<string> { "sin-dialogue-test" });
        var appearance = Assert.Single(rules.AppearOnKill(area, Act6Monsters.Ryslatha));
        var npc = Game.Npcs.Value.Find(appearance.NpcType)!;
        var speaker = Game.AppearingNpc(area, appearance, npc, 10, 20);
        Assert.True(speaker.Present(rules, _ => false));
        speaker.Id = 42;
        Assert.True(speaker.Present(rules, f => f == appearance.GoneWith));
        var nextVisit = Game.AppearingNpc(area, appearance, npc, 10, 20);
        Assert.False(nextVisit.Present(rules, f => f == appearance.GoneWith));
    }
}
