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
        Assert.Equal(70, rules.ThresholdOf(Act4Areas.Harvest, Act4Monsters.Malachai)?.LifePct);
        Assert.Null(rules.ThresholdOf(Act4Areas.Harvest, Act4Monsters.Malachai2));
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
    public async Task Shakari_retires_at_65_percent_and_resumes_in_her_second_arena()
    {
        using var fixture = new MinionUtilityTests.Fixture();
        using var registration = Quests.Register("oasis-test", QuestObjectsModule.Acts());
        const string area = Act9Areas.Oasis;
        var c = new ClientState(new(1, area, 300, 300, 20, 20, (_, _) => true, []), 1, 1)
        {
            Character = fixture.Conn.Character, Instance = new(),
            LeagueModule = new(new GameServer.Core.Accounts.League("oasis-test", "Standard", false, false),
                new HashSet<string> { "oasis-test", "Game" }),
            Wire = ("oasis", fixture.Stream, fixture.Ciphers),
        };
        var rules = Game.QuestsOf(c);
        var form = Assert.Single(rules.FormsAfter(area, Act9Monsters.Shakari));
        Assert.Equal(65, form.InitialLifePercent);
        Assert.Equal(Act9Monsters.Shakari2, form.Next);
        Assert.Null(rules.SecondForm(area, Act9Monsters.Shakari));
        var first = new SpawnedMonster("Shakari", c.NewMonsterWalk(1, 40, 40, 0, 0, unique: true), 50, 5, 1000)
        {
            Variety = Game.Monsters.Find(Act9Monsters.Shakari), Level = 61,
            Threshold = rules.ThresholdOf(area, Act9Monsters.Shakari),
        };
        Assert.Equal(65, first.Threshold!.LifePct);
        c.Monsters[1] = first;
        c.LaterFormPoints[form.Next] = (180, 180);
        first.Life = Game.TakeDamageLocked(first, int.MaxValue).Life;
        Assert.Equal(650u, first.Life);
        await Game.QuestThreshold("oasis", fixture.Stream, fixture.Ciphers, c, 1, first);
        await Game.QuestThreshold("oasis", fixture.Stream, fixture.Ciphers, c, 1, first);
        var second = Assert.Single(c.Monsters).Value;
        Assert.Equal(form.Next, second.Variety!.Id);
        Assert.Equal((180, 180), second.Walk.Position(0));
        Assert.Equal((uint)(second.MaxLife * 65L / 100), second.Life);
        Assert.False(c.HasQuestFlag("a9q5-KilledBoss"));
    }

    [Fact]
    public void Approximate_town_placements_move_to_floor_but_room_placements_keep_their_cell()
    {
        var layout = new AreaLayout(1, "test", 100, 100, 50, 50, (x, y) => x >= 50 && y == 50, []);
        var placement = new NpcCatalog.Placement("TestNpc", 44, 50, 0);
        Assert.Equal((50, 50), Game.TownNpcCell(layout, placement));
        Assert.Equal((44, 50), Game.TownNpcCell(layout, placement with { FromRoom = true }));
        Assert.Equal((44, 50), Game.TownNpcCell(layout with { Walkable = null }, placement));
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
        Assert.Contains(Act10Objects.ShortcutOpen, Quests.For(new HashSet<string> { "shortcut-test" }).OnMonsterKilled(Act10Areas.DesecratedChambers, Act10Monsters.Avarius));
        var desecrated = new ClientState(layout with { Id = Act10Areas.DesecratedChambers }, 1, 1)
        { Instance = new(), LeagueModule = conn.LeagueModule };
        desecrated.QuestFlags.Add("a10q2-KilledBoss");
        Assert.True(Game.TransitionShown(desecrated, 1, normal));
        Assert.False(Game.TransitionShown(desecrated, 2, shortcut));
        var flags = Game.QuestsOf(desecrated).OnMonsterKilled(Act10Areas.DesecratedChambers, Act10Monsters.Avarius);
        Assert.NotNull(Game.CommitQuestFlags(desecrated, flags));
        Assert.DoesNotContain(Act10Objects.ShortcutOpen, desecrated.QuestFlags);
        Assert.True(Game.TransitionShown(desecrated, 2, shortcut));
        Assert.False(Game.TransitionShown(new ClientState(desecrated.Layout, 1, 1)
        { Instance = new(), LeagueModule = conn.LeagueModule }, 2, shortcut));
    }
}
