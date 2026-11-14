using GameServer.Modules.M_questobjects;

namespace GameServer.Gateway.Tests;

public class RyslathaEncounterTests
{
    // The Wetlands layout and its room points come from the extracted game files, so a checkout without them skips.
    [GameFilesFact]
    public void The_nests_use_the_three_room_markers_and_the_boss_uses_her_own()
    {
        foreach (uint seed in new uint[] { 1, 2 }) Seed(seed);
    }

    static void Seed(uint seed)
    {
        const string area = RyslathaEncounter.Area;
        using var registration = Quests.Register("nest-sequence", QuestObjectsModule.Acts());
        var rules = Quests.For(new HashSet<string> { "nest-sequence" });
        Assert.Equal(RyslathaEncounter.Boss, Assert.Single(rules.FormsAfter(area, RyslathaEncounter.Nest)).Next);
        Assert.True(rules.QuestSpawned(area, RyslathaEncounter.Boss));
        var layout = AreaLayouts.For(AreaTransitionCatalog.HashOf(area)!.Value, seed);
        var (points, _, _) = AreaPopulation.FitRooms(area, seed, [], "data");
        var nests = new List<AreaPopulation.Spawn>();
        var boss = RyslathaEncounter.Place(points, layout.Walkable!, nests);
        Assert.NotNull(boss);
        Assert.Equal(3, nests.Count);
        Assert.All(nests, n => Assert.NotEqual(boss.Value, (n.X, n.Y)));
        nests.Clear();
        Assert.Null(RyslathaEncounter.Place(points.Where(p => p.Name != RyslathaEncounter.Nest).ToList(), layout.Walkable!, nests));
        Assert.Empty(nests);
    }
}
