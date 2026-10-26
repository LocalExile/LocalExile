using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public class TeleportTargetsTests
{
    // Select an unmapped synthetic destination so real extracted area names cannot change these label controls.
    static readonly ushort Town = Enumerable.Range(0, ushort.MaxValue + 1).Select(n => (ushort)n)
        .First(hash => AreaTransitionCatalog.IdOf(hash) is null);
    static string Transition => $"transition to 0x{Town:x4}";

    // A 200 x 200 area whose walkable cells are x >= 50; the player starts at the spawn (70, 70); one exit at (40, 20)
    // and a waypoint at (150, 150).
    static ClientState Conn(bool walkMap = true) => new(new AreaLayout(0x1234, "1_1_3", 200, 200, 70, 70,
        walkMap ? (x, y) => x >= 50 && x < 200 && y >= 0 && y < 200 : null,
        [(0x2000, new AreaTransitionObject(GridX: 40, GridY: 20, DestinationHash16: Town))])
    { Waypoint = new WaypointObject(150, 150) }, 1, 1);

    static List<TeleportTargets.Target> Places(ClientState conn) =>
    [
        .. TeleportTargets.Candidates(conn, folder: "no-such-folder"),
        new("RhoaChest", null, 100, 30), new("RhoaChest", null, 120, 90), new("GlyphWall", null, 180, 60),
    ];

    static TeleportTargets.Destination? Tp(ClientState conn, params string[] args) =>
        TeleportTargets.Resolve(conn, args, Places(conn), out _);

    [Fact]
    public void A_walkable_cell_is_reached_exactly_and_a_blocked_one_moves_to_the_nearest_walkable_cell()
    {
        var conn = Conn();
        Assert.Equal(new TeleportTargets.Destination(120, 130, null), Tp(conn, "120", "130"));
        var blocked = Tp(conn, "45", "100")!;
        Assert.True(blocked.X >= 50, $"({blocked.X}, {blocked.Y}) is not walkable");
        Assert.Contains("not walkable", blocked.Next);
    }

    [Fact]
    public void Signed_values_are_relative_to_the_player_per_axis()
    {
        var conn = Conn();
        Assert.Equal((80, 65), Pos(Tp(conn, "+10", "-5")));
        Assert.Equal((90, 100), Pos(Tp(conn, "+20", "100")));
    }

    [Fact]
    public void Cells_outside_the_area_are_refused()
    {
        var conn = Conn();
        Assert.Null(TeleportTargets.Resolve(conn, ["250", "10"], Places(conn), out string? error));
        Assert.Contains("outside the area", error);
        Assert.Null(TeleportTargets.Resolve(conn, ["-100", "+0"], Places(conn), out _));
    }

    [Fact]
    public void A_named_place_lands_on_a_walkable_cell_next_to_it_and_n_picks_among_equal_names()
    {
        var conn = Conn();
        var first = Tp(conn, "rhoachest")!;
        var second = Tp(conn, "RhoaChest", "2")!;
        Assert.InRange(Math.Max(Math.Abs(first.X - 100), Math.Abs(first.Y - 30)), 6, 20);
        Assert.InRange(Math.Max(Math.Abs(second.X - 120), Math.Abs(second.Y - 90)), 6, 20);
        Assert.Equal("RhoaChest at (120, 90)", second.Next);
        Assert.Null(TeleportTargets.Resolve(conn, ["RhoaChest", "3"], Places(conn), out string? error));
        Assert.Contains("Only 2", error);
    }

    [Fact]
    public void Names_match_exactly_first_then_by_part()
    {
        var conn = Conn();
        Assert.Equal("GlyphWall at (180, 60)", Tp(conn, "glyph")!.Next);
        Assert.Equal("waypoint at (150, 150)", Tp(conn, "waypoint")!.Next);
        Assert.Equal($"{Transition} at (40, 20)", Tp(conn, "transition")!.Next);
        Assert.Null(TeleportTargets.Resolve(conn, ["Hillock"], Places(conn), out string? error));
        Assert.Contains("/tp list", error);
    }

    [Fact]
    public void An_id_picks_a_monster_or_transition_and_a_monster_is_found_where_it_is_now()
    {
        var conn = Conn();
        lock (conn.Monsters) conn.Monsters[0x3001] = new SpawnedMonster("Gravel Eater", conn.NewWalk(160, 40, 10), 30, 2, 100);
        Assert.Equal("Gravel Eater at (160, 40)", TeleportTargets.Resolve(conn, ["0x3001"], TeleportTargets.Candidates(conn, "none"), out _)!.Next);
        Assert.Equal($"{Transition} at (40, 20)", Tp(conn, "0x2000")!.Next);
        Assert.Equal("Gravel Eater at (160, 40)", TeleportTargets.Resolve(conn, ["gravel"], TeleportTargets.Candidates(conn, "none"), out _)!.Next);
    }

    [Fact]
    public void Captured_objects_are_named_by_their_object_types_last_part()
    {
        string folder = Directory.CreateTempSubdirectory().FullName;
        try
        {
            File.WriteAllText(Path.Combine(folder, "1_1_3_seed1.objects.json"), """
                {"area": "1_1_3", "seed": 1, "objects": [
                  {"objectType": "Metadata/QuestObjects/WaterCave/GlyphWall", "model": "a.ao", "x": 829, "y": 375, "rotation": 4.7},
                  {"objectType": "Metadata/Chests/QuestChests/RhoaChest", "model": "b.ao", "x": 793, "y": 563, "rotation": 0}]}
                """);
            Assert.Equal(
                [new TeleportTargets.Target("GlyphWall", null, 829, 375), new TeleportTargets.Target("RhoaChest", null, 793, 563)],
                TeleportTargets.CapturedObjects("1_1_3", 1, folder));
            Assert.Empty(TeleportTargets.CapturedObjects("1_1_3", 2, folder));
            var all = TeleportTargets.Candidates(Conn(), folder);
            Assert.Equal([Transition, "waypoint", "arrival", "GlyphWall", "RhoaChest"], all.Select(c => c.Name));
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public void List_numbers_repeated_names_and_filters()
    {
        var places = Places(Conn());
        string list = TeleportTargets.List(places, null);
        Assert.Contains("RhoaChest (100, 30)", list);
        Assert.Contains("RhoaChest 2 (120, 90)", list);
        Assert.Contains($"{Transition} 0x2000 (40, 20)", list);
        Assert.Equal("RhoaChest (100, 30)\nRhoaChest 2 (120, 90)", TeleportTargets.List(places, "rhoa"));
        Assert.Equal("No places.", TeleportTargets.List(places, "nothing"));
    }

    [Fact]
    public void Without_arguments_the_usage_is_the_answer()
    {
        var conn = Conn();
        Assert.Null(TeleportTargets.Resolve(conn, [], Places(conn), out string? error));
        Assert.StartsWith("Usage: /tp", error);
    }

    static (int, int) Pos(TeleportTargets.Destination? d) => (d!.X, d.Y);
}
