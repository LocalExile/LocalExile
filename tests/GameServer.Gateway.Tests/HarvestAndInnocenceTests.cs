using GameServer.Modules.M_questobjects;

namespace GameServer.Gateway.Tests;

public class HarvestAndInnocenceTests
{
    [Fact]
    public void Abberath_waits_in_his_own_final_arena_after_the_first_fight()
    {
        using var registration = Quests.Register("abberath-arena-test", QuestObjectsModule.Acts());
        var rules = Quests.For(new HashSet<string> { "abberath-arena-test" });
        const string area = Act6Areas.PrisonersGateAct6;
        var form = Assert.Single(rules.FormsAfter(area, Act6Monsters.Abberath));
        Assert.Equal(Act6Monsters.Abberath2, form.Next);
        Assert.Null(rules.SecondForm(area, Act6Monsters.Abberath));
    }

    [Fact]
    public void The_three_organs_open_the_core_and_Malachai_forms_in_the_instances_second_arena()
    {
        using var registration = Quests.Register("campaign-room-test", QuestObjectsModule.Acts());
        var rules = Quests.For(new HashSet<string> { "campaign-room-test" });
        const string core = "Metadata/QuestObjects/Act4/CoreTransition";
        Assert.False(rules.Exists(core, flag => flag == "a4q5-DeliveredOrgan2"));
        Assert.True(rules.Exists(core, flag => flag == "a4q5-QuestComplete"));
        var form = Assert.Single(rules.FormsAfter(Act4Areas.Harvest, Act4Monsters.Malachai));
        Assert.Equal(Act4Monsters.Malachai2, form.Next);
        Assert.Null(rules.SecondForm(Act4Areas.Harvest, Act4Monsters.Malachai));
        var layout = new AreaLayout(1, Act4Areas.Harvest, 100, 100, 5, 5, null, []);
        var instance = new InstanceState();
        var host = new ClientState(layout, 1, 1) { Instance = instance };
        host.LaterFormPoints[form.Next] = (80, 75);
        var returning = new ClientState(layout, 1, 1) { Instance = instance };
        Assert.Equal((80, 75), returning.LaterFormPoints[form.Next]);
        Assert.Empty(new ClientState(layout, 1, 1) { Instance = new() }.LaterFormPoints);
    }

    [Fact]
    public void Only_the_Chambers_disabled_shortcut_waits_for_the_current_instances_kill()
    {
        using var registration = Quests.Register("shortcut-test", QuestObjectsModule.Acts());
        var layout = new AreaLayout(1, Act5Areas.ChamberOfInnocence, 100, 100, 5, 5, null, []);
        var conn = new ClientState(layout, 1, 1) { Instance = new() };
        conn.LeagueModule = new(new GameServer.Core.Accounts.League("test", "Standard", false, false), new HashSet<string> { "shortcut-test" });
        conn.QuestFlags.Add("a5q4-KilledBoss");
        var normal = new GameServer.Core.Protocol.Instance.AreaTransitionObject(8, 8, 1) { TeleportTo = (30, 30) };
        var shortcut = normal with { LayoutDisabled = true };
        Assert.True(Game.TransitionShown(conn, 1, normal));
        Assert.False(Game.TransitionShown(conn, 2, shortcut));
        conn.Instance.SetState(Act5Objects.ShortcutOpen);
        Assert.True(Game.TransitionShown(conn, 2, shortcut));
    }
}
