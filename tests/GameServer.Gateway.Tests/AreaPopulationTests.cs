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

        var gated = AreaPopulation.Plan(Area(), 600, 600, Walk, (50, 50), [], [], 1, _ => true, [],
            localTransitions: [((200, 100), (400, 100))]);
        Assert.Contains(gated, s => s.X > 330);
        Assert.All(gated, s => Assert.True(Walk(s.X, s.Y)));
        var reverse = AreaPopulation.Plan(Area(), 600, 600, Walk, (50, 50), [], [], 1, _ => true, [],
            localTransitions: [((400, 100), (200, 100)), ((-1, 100), (400, 100))]);
        Assert.All(reverse, s => Assert.True(s.X < 280));

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

    [Fact]
    public void High_pack_size_scales_members_linearly_and_keeps_member_spacing()
    {
        bool Field(int x, int y) => x is > 5 and < 595 && y is > 5 and < 595;
        var baseline = AreaPopulation.Plan(Area(), 600, 600, Field, (50, 50), [], [], 42, _ => true, []);
        var scaled = AreaPopulation.Plan(Area(), 600, 600, Field, (50, 50), [], [], 42, _ => true, [],
            scale: new(Size: 12));

        Assert.NotEmpty(baseline);
        // Size multiplies members per pack (linear ~12x), not pack count x members (quadratic ~144x).
        Assert.InRange(scaled.Count, baseline.Count * 10, baseline.Count * 13);

        // Every placed monster stays walkable and at least MemberSpacing (3) cells apart on at least one axis.
        var placed = new HashSet<(int, int)>();
        foreach (var s in scaled)
        {
            Assert.True(Field(s.X, s.Y));
            for (int dy = -2; dy <= 2; dy++)
                for (int dx = -2; dx <= 2; dx++)
                    Assert.DoesNotContain((s.X + dx, s.Y + dy), placed);
            placed.Add((s.X, s.Y));
        }
    }

    // A league encounter never stands in a sub-area an ExtraTerrainFeatures row adds (the Sacred Grove beside a map, a
    // Harvest grove's room): that room holds its own mechanic, and the map's content belongs to the main layout only.
    [Fact]
    public void Encounter_spots_stay_out_of_feature_areas()
    {
        const int w = 240, h = 240;
        bool Walk(int x, int y) => x is > 6 and < w - 7 && y is > 6 and < h - 7;
        var room = new List<(int X0, int Y0, int X1, int Y1)> { (w / 2, h / 2, w, h) };
        static bool InRoom((int X, int Y) p, int half) => p.X >= half && p.Y >= half;
        int unguardedInside = 0;
        for (int seed = 0; seed < 40; seed++)
        {
            var free = AreaPopulation.EncounterSpot(w, h, Walk, (10, 10), [], [], new Random(seed));
            if (free is { } f && InRoom(f, w / 2)) unguardedInside++;
            var guarded = AreaPopulation.EncounterSpot(w, h, Walk, (10, 10), [], [], new Random(seed), featureAreas: room);
            Assert.NotNull(guarded);
            Assert.False(InRoom(guarded!.Value, w / 2), $"seed {seed}: an encounter stood inside the feature area");
        }
        Assert.True(unguardedInside > 0, "the chooser never reached the room, so the check proves nothing");
    }
}
