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

    [GameFilesFact]
    public void A_guardian_door_only_blocks_a_room_with_a_working_inbound_teleport()
    {
        const string module = "guardian-door-test";
        using var registration = Quests.Register(module, QuestObjectsModule.Acts());
        foreach (var (area, expected) in new[] { (Act4Areas.Belly2, 1), (Act4Areas.Harvest, 3) })
        {
            foreach (uint seed in new uint[] { 1, 2 })
            {
                var layout = AreaLayouts.For(AreaTransitionCatalog.HashOf(area)!.Value, seed);
                var placed = new QuestObjectTypes.AreaPlacements(area, seed, []);
                var entry = new ClientState(layout, 1, seed) { Instance = new() };
                entry.LeagueModule = new(new GameServer.Core.Accounts.League("test", "Standard", false, false), new HashSet<string> { module });
                var doors = Game.PlacedQuestDoors(entry, placed);
                var arena = doors.Where(d => !d.Object.OpensOnUse).ToList();
                Assert.Equal(expected, arena.Count);
                Assert.All(doors, d => Assert.True(d.Object.GoneWhenTransitioned));
                Assert.Equal(doors.Count, doors.Select(d => d.Object.TransitionedBy).Distinct().Count());

                var entrances = layout.AreaTransitions.Where(t => t.Transition.SourceType == Act4Objects.ArenaEntrance).ToList();
                Assert.Equal(expected, entrances.Count);
                foreach (var gate in entrances)
                {
                    Assert.True(Game.TransitionShown(entry, gate.Id, gate.Transition));
                    var missing = layout with { AreaTransitions = [.. layout.AreaTransitions.Where(t => t.Id != gate.Id)] };
                    var peer = new ClientState(missing, 1, seed) { Instance = entry.Instance, LeagueModule = entry.LeagueModule };
                    var remaining = Game.PlacedQuestDoors(peer, placed);
                    var removed = Assert.Single(doors.Except(remaining));
                    Assert.False(removed.Object.OpensOnUse);
                    var landing = Game.GateArrival(layout, gate.Transition);
                    var at = removed.At;
                    long sides = (long)(gate.Transition.GridX - at.X) * (landing.X - at.X)
                        + (long)(gate.Transition.GridY - at.Y) * (landing.Y - at.Y);
                    Assert.True(sides < 0);
                    Assert.True(layout.WalkableFor(landing.X, landing.Y, 1));
                }
            }
        }
    }
}
