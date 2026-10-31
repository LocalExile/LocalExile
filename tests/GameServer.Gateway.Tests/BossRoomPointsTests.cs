using System.Text.Json;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

/// <summary>Unique bosses at their room points: "mapboss&lt;n&gt;" takes the n-th of WorldAreas Bosses (The Upper Prison's
/// Brutus is mapboss2, The Cavern of Anger's Merveil too), and a boss room takes its actual placement instead of the doodad
/// fit, which can match a repeated room more than once (one unique boss per match).</summary>
public sealed class BossRoomPointsTests : IDisposable
{
    readonly string folder = Path.Combine(Path.GetTempPath(), "boss-room-points-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(folder)) Directory.Delete(folder, true);
    }

    static PackCatalog.AreaPacks Area(params int[] bosses) => new("9_9_9", 9, [], new Dictionary<string, PackCatalog.Pack>(), bosses);

    static List<AreaPopulation.Spawn> Plan(PackCatalog.AreaPacks area, params AreaPopulation.RoomPoint[] points) =>
        AreaPopulation.Plan(area, 400, 400, (_, _) => true, (0, 0), [], points, 1, _ => true, []);

    // "" = any boss no other point takes; null = not a boss point; else the 1-based places in WorldAreas Bosses.
    [Theory]
    [InlineData("mapboss", "")]
    [InlineData("force:mapboss", "")]
    [InlineData("mapbosspack", "")]
    [InlineData("mapbossWG", "")]
    [InlineData("mapboss1", "1")]
    [InlineData("mapboss2", "2")]
    [InlineData("mapboss12", "12")]
    [InlineData("mapboss1WG", "1")]
    [InlineData("mapboss3&4", "3,4")]
    [InlineData("mapbossx", null)]
    [InlineData("mapboss0", null)]
    [InlineData("mapbosswarbandfire", null)]
    [InlineData("mapboss-Metadata/Monsters/Kaom/KaomMapBossShaper", null)]
    [InlineData("%area%-bosspack", null)]
    [InlineData("default", null)]
    public void Boss_points_name_their_place_in_the_list(string name, string? places) =>
        Assert.Equal(places?.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToArray(), AreaPopulation.BossPoint(name)?.ToArray());

    [Fact]
    public void Numbered_boss_points_take_their_own_boss()
    {
        // The Upper Prison: Bosses [NecromancerBossPrison, BossBrute]; mapboss1 in the Necromancer's room, mapboss2 in
        // the cells before Brutus's arena. Before, "mapboss2" was not a monster point and Brutus never appeared.
        var spawns = Plan(Area(11, 22), new("mapboss2", 200, 100), new("mapboss1", 50, 60));
        Assert.Equal([(22, 200, 100), (11, 50, 60)], spawns.Select(s => (s.Variety, s.X, s.Y)));
        Assert.All(spawns, s => Assert.Equal(MonsterRarity.Unique, s.Rarity));
    }

    [Fact]
    public void A_numbered_boss_point_beyond_the_list_places_nothing()
    {
        var notes = new List<string>();
        var spawns = AreaPopulation.Plan(Area(11), 400, 400, (_, _) => true, (0, 0), [], [new("mapboss2", 10, 10)], 1, _ => true, notes);
        Assert.Empty(spawns);
        Assert.Contains(notes, n => n.Contains("mapboss2"));
    }

    [Fact]
    public void A_plain_boss_point_takes_every_boss_left_once()
    {
        // The Den (Act 7): Bosses [The Great White Bones, The Bone Sculptor] and one "mapboss": both stand there, where one
        // of them was picked at random before. The Toxic Conduits' "mapbosspack": its five Lingering Aberrations.
        var spawns = Plan(Area(11, 22), new AreaPopulation.RoomPoint("mapboss", 100, 100));
        Assert.Equal([11, 22], spawns.Select(s => s.Variety).Order());
        Assert.All(spawns, s => Assert.True(Math.Abs(s.X - 100) <= 9 && Math.Abs(s.Y - 100) <= 9));
        Assert.Equal(5, Plan(Area(7, 7, 7, 7, 7), new AreaPopulation.RoomPoint("mapbosspack", 50, 50)).Count(s => s.Variety == 7));
        // Two plain points (The Den's treasure room and golden chest room both placed): still one of each boss, together.
        var two = Plan(Area(11, 22), new("mapboss", 100, 100), new("force:mapboss", 300, 300));
        Assert.Equal([11, 22], two.Select(s => s.Variety).Order());
        Assert.Single(two.Select(s => s.X > 200).Distinct());
    }

    [Fact]
    public void Numbered_points_go_first_and_a_plain_point_takes_only_the_rest()
    {
        // The Dread Thicket: Gruthkul at mapboss1, The Dreadstone at mapboss2 (a "WG" suffix reads the same); a plain
        // "mapboss" room takes no boss (it took a random one before).
        var spawns = Plan(Area(11, 22), new("mapboss", 10, 10), new("mapboss1WG", 100, 100), new("mapboss2", 200, 200));
        Assert.Equal([(11, 100, 100), (22, 200, 200)], spawns.Select(s => (s.Variety, s.X, s.Y)));
        // A repeated mapboss1 (two balcony rooms placed) does not make a second boss; "mapboss2&3" takes two.
        var sceptre = Plan(Area(1, 2, 3), new("mapboss1", 10, 10), new("mapboss1", 50, 50), new("mapboss2&3", 100, 100));
        Assert.Equal([1, 2, 3], sceptre.Select(s => s.Variety));
        // The rest at the plain point: the Upper Sceptre's balcony boss is numbered, the others are not.
        var rest = Plan(Area(1, 2, 3), new("mapboss", 300, 300), new("mapboss2", 100, 100));
        Assert.Equal([(2, true), (1, false), (3, false)], rest.Select(s => (s.Variety, s.X < 200)));
    }

    [Fact]
    public void A_named_pack_brings_its_unique_boss_once()
    {
        // The Mines Level 1: "%area%-minerhammerbosspack" (Miners, BossMonster Pikerivet at 100 %), and Pikerivet is the
        // area's boss: he stands in his pack, unique, and a plain point does not add a second one.
        var pack = new PackCatalog.Pack(0, "9_9_9-minerhammerbosspack", 2, 2, 100, 1, [33], [(44, 100)], new Dictionary<int, int>());
        var area = new PackCatalog.AreaPacks("9_9_9", 9, [], new Dictionary<string, PackCatalog.Pack> { ["minerhammerbosspack"] = pack }, [33]);
        var spawns = AreaPopulation.Plan(area, 400, 400, (_, _) => true, (0, 0), [],
            [new("%area%-minerhammerbosspack", 100, 100), new("mapboss", 300, 300)], 1, _ => true, []);
        Assert.Equal((33, MonsterRarity.Unique), spawns.Where(s => s.Variety == 33).Select(s => (s.Variety, s.Rarity)).Single());
        Assert.Equal(2, spawns.Count(s => s.Variety == 44 && s.Rarity == MonsterRarity.Normal));
    }

    [Fact]
    public void The_boss_pack_stands_once_per_area()
    {
        // The Dried Lake: Voll at mapboss1, Nightwane at mapboss2, and its bosspack: once, at the first boss point (before,
        // at every boss point).
        var guards = new PackCatalog.Pack(0, "9_9_9-bosspack", 3, 3, 0, 0, [], [(44, 100)], new Dictionary<int, int>());
        var area = new PackCatalog.AreaPacks("9_9_9", 9, [], new Dictionary<string, PackCatalog.Pack> { ["bosspack"] = guards }, [11, 22]);
        var spawns = AreaPopulation.Plan(area, 400, 400, (_, _) => true, (0, 0), [], [new("mapboss1", 100, 100), new("mapboss2", 300, 300)], 1, _ => true, []);
        Assert.Equal(3, spawns.Count(s => s.Variety == 44));
        Assert.All(spawns.Where(s => s.Variety == 44), s => Assert.True(s.X < 200));
        // The Cavern of Anger has "%area%-bosspack" points of its own: the boss points add none.
        var own = AreaPopulation.Plan(area, 400, 400, (_, _) => true, (0, 0), [],
            [new("mapboss1", 100, 100), new("mapboss2", 300, 300), new("%area%-bosspack", 200, 50)], 1, _ => true, []);
        Assert.Equal(3, own.Count(s => s.Variety == 44));
        Assert.All(own.Where(s => s.Variety == 44), s => Assert.True(Math.Abs(s.X - 200) <= 9 && Math.Abs(s.Y - 50) <= 9));
    }

    [Fact]
    public void A_later_form_is_left_to_its_fight()
    {
        // Prisoner's Gate: Bosses [Abberath, Abberath2], one name: the second form waits for its fight (it stood beside
        // the first, ~300 cells apart, before). The Rotting Core: The Depraved Trinity forms after its three souls die.
        (string, string)? Variety(int v) => v switch
        {
            10 => ("Metadata/Monsters/Abberath/Abberath", "Abberath, the Cloven One"),
            11 => ("Metadata/Monsters/Abberath/Abberath2", "Abberath, the Cloven One"),
            20 => ("Metadata/Monsters/UnholyTrio/UnholyTrio", "The Depraved Trinity"),
            21 => ("Metadata/Monsters/Doedre/DoedreSoul", "Doedre, Darksoul"),
            30 => ("Metadata/Monsters/Zombies/ZombieGiantSummoningPackMember", "Lingering Aberration"),
            40 => ("Metadata/Monsters/KaomWarrior/KaomArcherMinion1", "Forgotten Warrior"),
            41 => ("Metadata/Monsters/KaomWarrior/KaomArcherMinion2", "Forgotten Warrior"),
            42 => ("Metadata/Monsters/GemlingLegionnaire/GemlingLegionnaireFireball", "Gemling Legionnaire"),
            43 => ("Metadata/Monsters/GemlingLegionnaire/GemlingLegionnaireEarthquake", "Gemling Legionnaire"),
            _ => null,
        };
        Assert.Equal([1], AreaPopulation.LaterForms(Area(10, 11), Variety).Absent);
        Assert.Equal([0], AreaPopulation.LaterForms(Area(20, 21), Variety).Absent);
        // The same variety five times (The Toxic Conduits) is five bosses, not forms.
        Assert.Empty(AreaPopulation.LaterForms(Area(30, 30, 30, 30, 30), Variety).Absent);
        // Bosses that only share a name are separate bosses (The Mud Flats' Forgotten Warriors, The Grain Gate's Legion).
        Assert.Empty(AreaPopulation.LaterForms(Area(40, 41, 42, 43), Variety).Absent);
        var spawns = Plan(AreaPopulation.LaterForms(Area(10, 11), Variety), new("mapboss1WG", 100, 100), new("mapboss2WG", 300, 300));
        Assert.Equal([(10, 100, 100)], spawns.Select(s => (s.Variety, s.X, s.Y)));
    }

    [Fact]
    public void A_boss_whose_point_is_off_the_walk_map_stands_on_the_nearest_walkable_cell()
    {
        // Kitava's "force:mapboss" is in his pit, Arakaali's "mapboss" in her web: the nearest walkable cell (here x >= 120).
        var notes = new List<string>();
        var spawns = AreaPopulation.Plan(Area(5), 400, 400, (x, _) => x >= 120, (0, 0), [], [new("force:mapboss", 100, 200)], 1, _ => true, notes);
        Assert.Equal((5, 120, 200), spawns.Select(s => (s.Variety, s.X, s.Y)).Single());
        Assert.Contains(notes, n => n.Contains("not walkable"));
    }

    [Fact]
    public void An_absent_boss_stands_nowhere()
    {
        var spawns = Plan(Area(11, 22, 33) with { Absent = new HashSet<int> { 0, 2 } }, new AreaPopulation.RoomPoint("mapboss", 100, 100));
        Assert.Equal([22], spawns.Select(s => s.Variety));
    }

    [Fact]
    public void A_boss_spawner_object_becomes_its_boss_point()
    {
        // The Quay's boss.arm: TolmanBossSpawner (Tolman in the stocks) is mapboss<n> of Tolman's place in Bosses.
        var area = Area(7, 1226);
        AreaPopulation.RoomObject[] objects =
        [
            new("Metadata/NPC/Act8/ClarissaQuay", "Clarissa.ao", 10, 10, 0, 1),
            new("Metadata/Monsters/Zombies/Tolman/TolmanBossSpawner", "Tolman.ao", 40, 50, 0, 1),
        ];
        var points = AreaPopulation.SpawnerPoints(area, objects, v => v == 1226 ? "Metadata/Monsters/Zombies/ZombieBossTolman_" : "x").ToList();
        Assert.Equal([("mapboss2", 40, 50)], points.Select(p => (p.Name, p.X, p.Y)));
        Assert.Empty(AreaPopulation.SpawnerPoints(Area(7), objects, _ => "x"));
    }

    [Theory]
    [InlineData("", "Greust/Greust")]
    [InlineData("a2b-KilledStrBandit,a2b-KilledIntBandit,a2b-KilledDexBandit", "Greust/Greust")]
    [InlineData("a2b-KilledIntBandit,a2b-KilledDexBandit", "Oak/Oak")]
    [InlineData("a2b-KilledStrBandit,a2b-KilledDexBandit", "Alira/Alira")]
    [InlineData("a2b-KilledStrBandit,a2b-KilledIntBandit", "Kraityn/Kraityn")]
    [InlineData("a2b-KilledStrBandit", "Greust/Greust")]
    public void The_ashen_fields_boss_is_the_spared_bandit_lord_or_greust(string flags, string boss)
    {
        // WorldAreas 2_7_7 Bosses: Greust, Oak, Alira, Kraityn (MapBossOverrideAshenFields).
        string[] ids = ["Greust/Greust", "Oak/Oak", "Alira/Alira", "Kraityn/Kraityn"];
        string Id(int v) => "Metadata/Monsters/Ralakesh/" + ids[v - 100];
        var area = new PackCatalog.AreaPacks("2_7_7", 53, [], new Dictionary<string, PackCatalog.Pack>(), [100, 101, 102, 103]);
        var set = flags.Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        var chosen = MapBossOverride.Apply(area, set, Id);
        Assert.Equal([Array.IndexOf(ids, boss)], Enumerable.Range(0, 4).Where(i => !chosen.Absent.Contains(i)));
        // Any other area is left as it is.
        var other = area with { AreaId = "1_1_1" };
        Assert.Same(other, MapBossOverride.Apply(other, set, Id));
        // A later form marked before stays absent.
        var marked = MapBossOverride.Apply(area with { Absent = new HashSet<int> { 0 } }, set, Id);
        Assert.Contains(0, marked.Absent);
    }

    [Fact]
    public void A_boss_room_the_layout_did_not_place_has_no_boss()
    {
        // broken_bridge and broken_bridge_left share their doodads; only one is placed (rooms.json), but both fit it.
        string data = Rooms(siblings: true);
        var (points, _, _) = AreaPopulation.FitRooms("9_9_9", 1, [], data);
        Assert.Equal([("mapboss1", 310, 320)], points.Where(p => p.Name.StartsWith("mapboss")).Select(p => (p.Name, p.X, p.Y)));
    }

    [Fact]
    public void A_boss_room_takes_its_placement_not_every_doodad_fit()
    {
        string data = Rooms();
        var notes = new List<string>();
        var (points, _, captured) = AreaPopulation.FitRooms("9_9_9", 1, notes, data);
        // The boss room's doodads fit twice; the capture placed it once, at (300, 300).
        Assert.Equal([("mapboss1", 310, 320)], points.Where(p => p.Name.StartsWith("mapboss")).Select(p => (p.Name, p.X, p.Y)));
        Assert.Contains(BossRoom, captured);
        // A room without a boss point keeps the fit: both matches.
        Assert.Equal(2, points.Count(p => p.Name == "%area%-nestpack"));
    }

    const string SiblingRoom = "Metadata/Terrain/Test/boss_left.arm";
    const string BossRoom = "Metadata/Terrain/Test/boss.arm", PackRoom = "Metadata/Terrain/Test/pack.arm";

    // Two rooms with the same four doodads, captured twice in the area (at (100, 100) and (300, 300)); rooms.json places
    // the boss room once. With siblings, a second boss room with the same doodads that the layout did not place.
    string Rooms(bool siblings = false)
    {
        string data = Path.Combine(folder, "data");
        Directory.CreateDirectory(Path.Combine(data, "walkability"));
        (int X, int Y)[] doodads = [(1, 2), (5, 9), (12, 3), (8, 14)];
        object Room(string tag, string point, int x, int y) => new
        {
            tag, points = new[] { new { name = point, x, y, angle = 0 } },
            doodads = doodads.Select(d => new { model = "rock.ao", x = d.X, y = d.Y }),
        };
        File.WriteAllText(Path.Combine(data, "room-points.json"), JsonSerializer.Serialize(new
        {
            note = "test",
            areas = new Dictionary<string, string[]> { ["9_9_9"] = siblings ? [BossRoom, SiblingRoom, PackRoom] : [BossRoom, PackRoom] },
            rooms = new Dictionary<string, object> { [BossRoom] = Room("boss", "mapboss1", 10, 20), [SiblingRoom] = Room("boss", "mapboss1", 10, 20), [PackRoom] = Room("pool", "%area%-nestpack", 4, 4) },
        }));
        File.WriteAllText(Path.Combine(data, "walkability", "9_9_9_seed1.doodads.json"), JsonSerializer.Serialize(new
        {
            area = "9_9_9", seed = 1, models = new[] { "rock.ao" },
            doodads = new[] { 100, 300 }.SelectMany(o => doodads.Select(d => new[] { 0, o + d.X, o + d.Y, 0 })).ToArray(),
        }));
        File.WriteAllText(Path.Combine(data, "walkability", "9_9_9_seed1.rooms.json"), JsonSerializer.Serialize(new
        {
            area = "9_9_9", seed = 1, rooms = new[] { new { path = BossRoom, x = 300, y = 300, width = 30, height = 30, code = 0 } },
        }));
        return data;
    }
}
