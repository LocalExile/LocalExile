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

        // Pack members can scatter nine cells from the centre. A thin wall must constrain them too.
        bool ThinWall(int x, int y) => x is > 5 and < 595 && y is > 5 and < 595 && x != 280;
        for (int seed = 1; seed <= 8; seed++)
        {
            var thin = AreaPopulation.Plan(Area(), 600, 600, ThinWall, (50, 50), [], [], seed, _ => true, []);
            Assert.NotEmpty(thin);
            Assert.All(thin, s => Assert.True(s.X < 280));
        }

        // Room packs may exceed the ordinary density budget, but cannot consume explicit extra map packs.
        bool Field(int x, int y) => x is > 5 and < 595 && y is > 5 and < 595;
        AreaPopulation.RoomPoint[] rooms = [new("normal", 100, 100), new("normal", 200, 100),
            new("normal", 300, 100), new("normal", 400, 100)];
        var extras = AreaPopulation.Plan(Area() with { Density = 0 }, 600, 600, Field, (50, 50), [], [],
            23, _ => true, [], scale: new(ExtraNormal: 3), roomPacks: rooms);
        Assert.True(extras.Count > rooms.Length * Pack.MaxCount, "The three extra packs were absorbed by the room budget.");
    }
}
