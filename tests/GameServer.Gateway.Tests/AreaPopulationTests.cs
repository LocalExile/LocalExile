using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public class AreaPopulationTests
{
    static readonly PackCatalog.Pack Pack = new(0, "test-pack", 4, 4, 0, 0, [], [(5, 1)], new Dictionary<int, int>());

    static PackCatalog.AreaPacks Area() => new("test", 1, [(Pack, 100)], new Dictionary<string, PackCatalog.Pack>(), []);

    [Fact]
    public void Pack_centres_respect_ground_and_member_counts_follow_the_entry_roles()
    {
        var wet = Pack with { Id = "wet", MinCount = 1, MaxCount = 1, Grounds = ["Wet"] };
        var dry = wet with { Id = "dry", Grounds = ["Dry"] };
        var area = Area() with { Scattered = [(wet, 3), (dry, 7)], Density = 0 };
        bool Walk(int x, int y) => x is >= 0 and < 360 && y is >= 0 and < 240;
        bool Candidate(int x, int y) => y == 90 && x is 90 or 96 or 210 or 330;
        string? Ground(int x, int y) => (x, y) switch
        {
            (90, 90) => "wet",
            (210, 90) or (300, 210) => "Dry",
            (89, 90) => "Rock",
            _ => null,
        };
        // The rejected room point lies one cell from a valid scattered centre.
        AreaPopulation.RoomPoint[] rooms = [new("normal", 89, 90), new("magic", 300, 210)];
        List<AreaPopulation.Spawn> Plan(PackCatalog.AreaPacks pool, Func<int, int, string?>? ground, int extra = 2) =>
            AreaPopulation.Plan(pool with { GroundAt = ground }, 360, 240, Walk, (20, 20), [], [], 2, _ => true, [],
                scale: new(ExtraNormal: extra), roomPacks: rooms, scatterable: Candidate);
        var spawns = Plan(area, Ground);
        Assert.Equal(3, spawns.Count);
        Assert.Contains(spawns, s => (s.X, s.Y, s.Pack) == (90, 90, "wet"));
        Assert.Contains(spawns, s => (s.X, s.Y, s.Pack) == (210, 90, "dry"));
        Assert.Contains(spawns, s => (s.X, s.Y, s.Pack, s.Rarity) == (300, 210, "dry", MonsterRarity.Magic));
        Assert.Equal(spawns, Plan(area with { Density = 0.3 }, Ground, extra: 0));

        var mixed = area with { Scattered = [(wet, 3), (dry with { Grounds = [] }, 7)] };
        var unknown = Plan(mixed, (_, _) => null);
        Assert.Equal(4, unknown.Count);
        Assert.All(unknown, s => Assert.Equal("dry", s.Pack));
        var marker = area with { Scattered = [(wet, 3), (dry with { Grounds = ["-"] }, 7)] };
        Assert.Equal(Plan(mixed, Ground), Plan(marker, Ground));
        // A capture without grounds and a field allowing both rows make the same seeded weighted draws.
        Assert.Equal(Plan(area, null), Plan(mixed, (_, _) => "Wet"));
        CheckPackMembers();
    }

    static void CheckPackMembers()
    {
        var catalog = new PackCatalog();
        var thugs = catalog.Find("Act8-ThugTorch")!;
        int smuggler = thugs.Entries.Single(e => e.Weight == 1).Variety;
        int arsonist = thugs.Entries.Single(e => e.Weight == 100).Variety;
        int firestarter = Assert.Single(thugs.BossVarieties);
        foreach (double size in new[] { 0.0, 0.5, 1.0, 1.25, 2.0 })
        {
            var observed = new HashSet<int>();
            for (int seed = 0; seed < 64; seed++)
            {
                var group = AreaPopulation.AtPacks([(thugs, 100, 100, MonsterRarity.Normal)],
                    (_, _) => true, _ => true, seed, size);
                Assert.Single(group, m => m.Variety == smuggler);
                Assert.Single(group, m => m.Variety == firestarter);
                observed.Add(group.Count(m => m.Variety == arsonist));
            }
            int min = (int)Math.Floor(2 * size), max = (int)Math.Ceiling(4 * size);
            Assert.Equal(Enumerable.Range(min, max - min + 1), observed.Order());
        }
        var trio = catalog.Find("pantheon-dominusbosses")!;
        Assert.Equal(trio.Entries.Select(e => e.Variety), trio.RollMembers(new Random(3), 2));
        var empty = catalog.Find("DoedreUniqueMapSpawner1")!;
        Assert.Empty(AreaPopulation.AtPacks([(empty, 100, 100, MonsterRarity.Normal)], (_, _) => true, _ => true, 11));
        var statues = catalog.Find("1_1_11_2-merveilstatues")!;
        var statuesPlaced = statues.RollMembers(new Random(11)).ToArray();
        Assert.Equal(3, statuesPlaced.Length);
        Assert.All(statues.Entries, e => Assert.Contains(e.Variety, statuesPlaced));
        var excluded = AreaPopulation.AtPacks([(thugs, 100, 100, MonsterRarity.Normal)],
            (_, _) => true, v => v != smuggler, 7);
        Assert.DoesNotContain(excluded, m => m.Variety == smuggler);
        Assert.Single(excluded, m => m.Variety == firestarter);
        var weta = catalog.Find("delve-azurite-weta-pack")!;
        int unstable = weta.Entries.Single(e => e.Weight == 2).Variety;
        var deep = weta.RollMembers(new Random(8), 100).Take(100).ToArray();
        Assert.Equal(100, deep.Length);
        Assert.Equal(2, deep.Count(v => v == unstable));

        var mapped = Area() with { Density = 0, DirectPacks = new Dictionary<string, PackCatalog.Pack> { [thugs.Id] = thugs } };
        var point = new AreaPopulation.RoomPoint(thugs.Id, 110, 110);
        var scaledRoom = AreaPopulation.Plan(mapped, 250, 250, (_, _) => true, (15, 15), [], [], 7,
            _ => true, [], scale: new(Size: 3), roomPacks: [point]);
        var roomGroup = scaledRoom.Where(m => m.Pack == thugs.Id).ToList();
        Assert.Single(roomGroup, m => m.Variety == smuggler);
        Assert.InRange(roomGroup.Count(m => m.Variety == arsonist), 6, 12);
        foreach (var (scale, min, max) in new[] { (default(AreaPopulation.PackScale), 2, 4), (new AreaPopulation.PackScale(Size: 0), 0, 0) })
        {
            var group = AreaPopulation.Plan(mapped, 250, 250, (_, _) => true, (15, 15), [], [], 7,
                _ => true, [], scale: scale, roomPacks: [point]).Where(m => m.Pack == thugs.Id).ToList();
            Assert.Single(group, m => m.Variety == smuggler);
            Assert.Single(group, m => m.Variety == firestarter);
            Assert.InRange(group.Count(m => m.Variety == arsonist), min, max);
        }
    }

    [GameFilesFact]
    public void Coast_drowned_match_the_ground_names_of_generated_tiles()
    {
        var catalog = new PackCatalog();
        var area = catalog.For("1_1_2")!;
        var drowned = area.Scattered.Single(p => p.Pack.Id == "Act1-ZombieDrowned");
        Assert.Equal(5, drowned.Pack.Grounds.Count);
        Assert.Empty(catalog.Find("Act1-ZombieDrownedStrand")!.Grounds);
        var level = ServerLevels.For("1_1_2", 1)!;
        var ground = level.GroundAt;
        Assert.NotNull(ground);
        // Two mixed Coast tiles at seed 1, sampled on either side of their 11/12-cell midlines.
        Assert.Equal("BeachBottomWet", ground(1253, 1115));
        Assert.Equal("SandDuneSlope", ground(1254, 1115));
        Assert.Equal("BeachBottomWet", ground(471, 494));
        Assert.Equal("BeachOceanShallowWet", ground(471, 495));
        Assert.Null(ground(-1, 0));
        Assert.Null(ground(level.Width, 0));

        bool Walk(int x, int y) => (uint)x < (uint)level.Width && (uint)y < (uint)level.Height && level.Walk[y * level.Width + x] >= 2;
        Assert.True(Walk(1143, 1101));
        Assert.Equal("BeachMiddleSand", ground(1143, 1101));
        Assert.DoesNotContain("BeachMiddleSand", drowned.Pack.Grounds);
        var spawns = AreaPopulation.Plan(area with { Scattered = [drowned], Bosses = [], Density = 0, GroundAt = ground },
            level.Width, level.Height, Walk, (0, 0), [], [], 1, _ => true, [],
            roomPacks: [new("magic", 1350, 1200), new("rare", 1143, 1101)]);
        Assert.Contains(spawns, s => (s.X, s.Y) == (1350, 1200));
        Assert.All(spawns, s => Assert.Equal(MonsterRarity.Magic, s.Rarity));
    }

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
        var authored = Pack with { Id = "room-guards", Row = 2 };
        var roomArea = Area() with { Density = 0, DirectPacks = new Dictionary<string, PackCatalog.Pack> { [authored.Id] = authored } };
        AreaPopulation.RoomPoint[] rooms = [new(authored.Id, 100, 100), new("normal", 200, 100),
            new("normal", 300, 100), new("normal", 400, 100)];
        var extras = AreaPopulation.Plan(roomArea, 600, 600, Field, (50, 50), [], [],
            23, _ => true, [], scale: new(ExtraNormal: 3), roomPacks: rooms);
        Assert.Equal(authored.MaxCount, extras.Count(s => s.Pack == authored.Id));
        Assert.Equal((rooms.Length + 3) * Pack.MaxCount, extras.Count);
        var ordinary = AreaPopulation.Plan(roomArea with { Density = 0.1 }, 600, 600, Field, (50, 50), [], [],
            23, _ => true, [], roomPacks: rooms);
        Assert.Equal(rooms.Length * Pack.MaxCount, ordinary.Count);
    }
}
