using Atlas;
using GameServer.Data.Terrain;

namespace Atlas.Tests;

/// <summary>The point decision for an area transition object. The Climb's prison.arm puts the exit tile's two
/// orientations nearest to both a monster point and "entrance", which left the object unresolved and the Climb to
/// Lower Prison connection point-only (campaign-travel-audit.md 3). Only that ambiguity is filtered: a single
/// orientation's pick and the room-wide nearest fallback keep their old behaviour. These fixtures call the helper
/// Resolve uses, so reverting the call site cannot pass silently.</summary>
public sealed class AtlasPairingTests
{
    const string MonsterPoint = "Metadata/Monsters/CorpseWheel/CorpseWheelAggroCharge";
    static readonly GraphNode ClimbNode = new(4, 0, 0, "prison", "I", ["entrance2"]);

    [Fact]
    public void Ambiguous_inside_picks_prefer_the_entrance_over_monster_points()
    {
        // Both points are inside the transition tile; one orientation is nearest to the monster, the other to the exit.
        var room = RoomAt((0, 0, MonsterPoint), (100, 0, "entrance"));
        var named = Named(room);

        var choice = AtlasBuilder.ChoosePoint(named, named, Positions((10, 0), (110, 0)), ClimbNode, room);

        Assert.Equal("entrance", choice.Point?.Name);
        Assert.Equal("inside the transition tile", choice.How);
        Assert.Null(choice.Unresolved);
    }

    [Fact]
    public void Ambiguous_inside_without_a_named_endpoint_stays_unresolved()
    {
        // No endpoint to prefer: the original candidate set is kept, so the ambiguity stays visible.
        var room = RoomAt((0, 0, MonsterPoint), (100, 0, "Metadata/Monsters/Custom/OtherPoint"));
        var named = Named(room);

        var choice = AtlasBuilder.ChoosePoint(named, named, Positions((10, 0), (110, 0)), ClimbNode, room);

        Assert.Null(choice.Point);
        Assert.Contains(MonsterPoint, choice.Unresolved!);
        Assert.Contains("OtherPoint", choice.Unresolved!);
    }

    [Fact]
    public void Unambiguous_inside_pick_is_not_filtered_by_an_endpoint_elsewhere()
    {
        // Only one orientation, so the old nearest pick wins even though the room-wide entrance is an endpoint.
        var room = RoomAt((0, 0, MonsterPoint), (1000, 1000, "entrance"));
        var named = Named(room);
        var inside = new[] { named[0] };

        var choice = AtlasBuilder.ChoosePoint(named, inside, Positions((10, 0)), ClimbNode, room);

        Assert.Equal(MonsterPoint, choice.Point?.Name);
        Assert.Equal("inside the transition tile", choice.How);
        Assert.Null(choice.Unresolved);
    }

    [Fact]
    public void Empty_inside_keeps_the_room_wide_nearest_point_even_if_it_is_a_monster()
    {
        // The room-wide fallback is never filtered: a far entrance must not capture an object near a monster.
        var room = RoomAt((10, 10, MonsterPoint), (500, 500, "entrance"));
        var named = Named(room);

        var choice = AtlasBuilder.ChoosePoint(named, [], Positions((12, 10)), ClimbNode, room);

        Assert.Equal(MonsterPoint, choice.Point?.Name);
        Assert.Equal("nearest point", choice.How);
        Assert.Null(choice.Unresolved);
    }

    [Fact]
    public void Empty_inside_keeps_ambiguous_room_wide_picks_unfiltered()
    {
        // Even when the orientations disagree, no point is inside the tile, so no endpoint preference is applied.
        var room = RoomAt((0, 0, MonsterPoint), (100, 0, "entrance"));
        var named = Named(room);

        var choice = AtlasBuilder.ChoosePoint(named, [], Positions((10, 0), (110, 0)), ClimbNode, room);

        Assert.Null(choice.Point);
        Assert.Equal("nearest point", choice.How);
        Assert.Contains(MonsterPoint, choice.Unresolved!);
        Assert.Contains("entrance", choice.Unresolved!);
    }

    [Fact]
    public void Ambiguous_inside_keeps_a_generic_custom_point_the_node_numbers()
    {
        // "prisonback" is not a RoleOf endpoint, but NodeNameFor pairs a generic point with the node's "prisonback2".
        var room = RoomAt((0, 0, "prisonback"), (100, 0, MonsterPoint));
        var named = Named(room);
        var node = new GraphNode(4, 0, 0, "prison", "I", ["prisonback2"]);

        var choice = AtlasBuilder.ChoosePoint(named, named, Positions((10, 0), (110, 0)), node, room);

        Assert.Equal("prisonback", choice.Point?.Name);
        Assert.Null(choice.Unresolved);
    }

    [Fact]
    public void Ambiguous_inside_keeps_a_custom_point_the_node_names_disabled()
    {
        // NodeNameFor also matches "name" to "namedisabled"; the filter must not drop that custom point.
        var room = RoomAt((0, 0, "prisonback"), (100, 0, MonsterPoint));
        var named = Named(room);
        var node = new GraphNode(4, 0, 0, "prison", "I", ["prisonbackdisabled"]);

        var choice = AtlasBuilder.ChoosePoint(named, named, Positions((10, 0), (110, 0)), node, room);

        Assert.Equal("prisonback", choice.Point?.Name);
        Assert.Null(choice.Unresolved);
    }

    [Fact]
    public void Keeps_teleports_sideareas_and_sublinks()
    {
        var room = Room("default", MonsterPoint, "entrance", "teleport1", "sidearea1", "subarea=side", "link");

        var points = AtlasBuilder.PreferredPoints(Named(room), ClimbNode, room);

        Assert.Equal(new[] { "entrance", "teleport1", "sidearea1", "subarea=side", "link" },
            points.Select(p => p.Name));
    }

    static IReadOnlyList<NamedPoint> Named(RoomFile room) =>
        room.Points.Where(p => !p.Name.Equals("default", StringComparison.OrdinalIgnoreCase)).ToList();

    static (int Code, float X, float Y)[] Positions(params (float X, float Y)[] positions) =>
        positions.Select((p, i) => (i, p.X, p.Y)).ToArray();

    static RoomFile Room(params string[] pointNames) =>
        RoomAt(pointNames.Select((name, i) => ((float)i, (float)i, name)).ToArray());

    /// <summary>A cut-down .arm (version 31): one dictionary string, tag "tag", a 1x1 room key, the named points, then
    /// the 1x1 slot grid ("n" = no tile), as RoomFile.Parse expects.</summary>
    static RoomFile RoomAt(params (float X, float Y, string Name)[] points)
    {
        var key = string.Join(" ", new[] { 1, 1 }.Concat(Enumerable.Repeat(0, 21)));
        var lines = new List<string> { "version 31", "1", "\"ground.gt\"", "\"tag\"", "k " + key };
        lines.AddRange(points.Select(p => $"{p.X} {p.Y} 0 \"{p.Name}\""));
        lines.Add("n");
        var room = RoomFile.Parse(string.Join("\n", lines));
        Assert.Null(room.Problem);
        Assert.Equal(points.Select(p => p.Name), room.Points.Select(p => p.Name));
        return room;
    }
}
