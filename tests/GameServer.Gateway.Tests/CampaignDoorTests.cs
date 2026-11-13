using GameServer.Modules.M_questobjects;

namespace GameServer.Gateway.Tests;

public class CampaignDoorTests
{
    [Fact]
    public void The_Ossuary_lever_requires_the_Sign_and_opens_only_this_instances_wall()
    {
        using var registration = Quests.Register("campaign-door-test", QuestObjectsModule.Acts());
        var rules = Quests.For(new HashSet<string> { "campaign-door-test" });
        Assert.Empty(rules.OnUsed(Act5Areas.Ossuary, Act5Objects.OssuaryLever, _ => false));
        string flag = Assert.Single(rules.OnUsed(Act5Areas.Ossuary, Act5Objects.OssuaryLever, f => f == "a5q6-SeenItem"));
        var wall = Assert.Single(rules.Blockers(Act5Areas.Ossuary));
        Assert.Equal(flag, wall.UnblockedBy);
        Assert.True(Quests.IsInstanceFlag(flag));
        var layout = new AreaLayout(1, Act5Areas.Ossuary, 100, 100, 5, 5, null, []);
        var conn = new ClientState(layout, 1, 1) { Instance = new(), QuestBlockers = [(wall, 50, 50)] };
        Game.UpdateQuestBlockers(conn);
        Assert.Single(conn.StationaryBlockers);
        conn.Instance.SetState(flag);
        Game.UpdateQuestBlockers(conn);
        Assert.Empty(conn.StationaryBlockers);
        Assert.False(new InstanceState().HasState(flag));
    }

    [Fact]
    public void Each_Harvest_guardian_unlocks_only_its_own_room()
    {
        using var registration = Quests.Register("guardian-door-test", QuestObjectsModule.Acts());
        var rules = Quests.For(new HashSet<string> { "guardian-door-test" });
        var guardians = rules.Doors(Act4Areas.Harvest).Where(r => r.Monster != null).ToList();
        Assert.Equal(3, guardians.Count);
        Assert.Equal(3, guardians.Select(d => d.Room).Distinct().Count());
        Assert.Equal(new[] { Act4Monsters.Doedre, Act4Monsters.Maligaro, Act4Monsters.Shavronne }, guardians.Select(d => d.Monster));
        Assert.All(guardians, d => Assert.Equal(Act4Objects.ArenaDoor, d.Type));
    }
}
