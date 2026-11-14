namespace GameServer.Gateway.Tests;

public class AreaPopulationTests
{
    static readonly PackCatalog.Pack Pack = new(0, "test-pack", 4, 4, 0, 0, [], [(5, 1)], new Dictionary<int, int>());

    static PackCatalog.AreaPacks Area() => new("test", 1, [(Pack, 100)], new Dictionary<string, PackCatalog.Pack>(), []);

    // A scattered pack whose centre the arrival cannot walk to is a monster the area keeps and nobody can reach or
    // kill (the follow-up to issue #668).
    [Fact]
    public void Scattered_packs_stand_only_where_the_arrival_walks()
    {
        // A field whose right part is walled off from the arrival: walkable, but not reachable from (50, 50).
        bool Walk(int x, int y)
        {
            if (x is <= 5 or >= 595 || y is <= 5 or >= 595) return false;
            return x is < 280 or > 330;
        }
        var notes = new List<string>();
        var spawns = AreaPopulation.Plan(Area(), 600, 600, Walk, (50, 50), [], [],
            AreaPopulation.PopulationSeed(0x7ad2, 1), _ => true, notes);
        Assert.NotEmpty(spawns);
        Assert.All(spawns, s => Assert.False(s.X >= 280, $"a pack stands beyond the wall at ({s.X}, {s.Y})"));
        Assert.Contains(notes, n => n.Contains("cannot walk", StringComparison.Ordinal));
    }
}
