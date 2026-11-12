namespace GameServer.Gateway.Tests;

public class AreaPopulationTests
{
    // A scattered pack whose centre the arrival cannot walk to is a monster the area's count keeps and nobody can
    // kill: a cleared map still reads "more than 50 monsters remaining" (the follow-up to issue #668).
    [Fact]
    public void Scattered_packs_stand_only_where_the_arrival_walks()
    {
        var coast = new PackCatalog().For("1_1_2")!;
        // A field with a 59×59 block walled off from it: walkable, but not reachable from the arrival at (50, 50).
        bool Walk(int x, int y)
        {
            if (x is <= 5 or >= 595 || y is <= 5 or >= 595) return false;
            bool wall = x is >= 100 and <= 160 && y is >= 100 and <= 160 && (x is 100 or 160 || y is 100 or 160);
            return !wall;
        }
        var notes = new List<string>();
        var spawns = AreaPopulation.Plan(coast, 600, 600, Walk, (50, 50), [], [new("mapboss", 500, 500)],
            AreaPopulation.PopulationSeed(0x7ad2, 1), _ => true, notes);
        Assert.NotEmpty(spawns);
        Assert.All(spawns, s => Assert.False(s.X is >= 100 and <= 160 && s.Y is >= 100 and <= 160,
            $"a pack stands in the walled-off block at ({s.X}, {s.Y})"));
        Assert.Contains(notes, n => n.Contains("cannot walk", StringComparison.Ordinal));
    }
}
