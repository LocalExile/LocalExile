using GameServer.Core.World;

namespace GameServer.Core.Tests;

public class ClientPathfinderTests
{
    static List<(int X, int Y)> Path((int, int) start, (int, int) target, Func<int, int, bool>? walkable = null,
        IReadOnlyList<ClientPathfinder.Blocker>? blockers = null, int w = 100, int h = 100) =>
        ClientPathfinder.FindPath(start, target, walkable ?? ((x, y) => x >= 0 && y >= 0 && x < w && y < h), w, h, blockers);

    [Fact]
    public void A_clear_line_is_one_segment()
    {
        Assert.Equal([(10, 10), (40, 22)], Path((10, 10), (40, 22)));
    }

    [Fact]
    public void Standing_on_the_target_or_aiming_off_the_map_stays()
    {
        Assert.Empty(Path((10, 10), (10, 10)));
        Assert.Empty(Path((10, 10), (-1, 10)));
        Assert.Empty(Path((10, 10), (10, 100)));
    }

    [Fact]
    public void Goes_around_a_wall_by_its_corner()
    {
        // A wall at x = 20 for y < 15: the path's only corner is next to the wall's end.
        bool open(int x, int y) => x >= 0 && y >= 0 && x < 100 && y < 100 && !(x == 20 && y < 15);
        var path = Path((10, 5), (30, 5), open);
        Assert.Equal((10, 5), path[0]);
        Assert.Equal((30, 5), path[^1]);
        Assert.Equal(3, path.Count);
        Assert.Equal(20, path[1].X);
        Assert.True(path[1].Y >= 15, $"corner {path[1]}");
    }

    [Fact]
    public void A_detour_outside_the_ellipse_is_not_taken()
    {
        // The only way from (10, 50) to (30, 50) loops 40 cells north; the search ellipse (clamp(2 × 20, 20, 136) = 40)
        // does not reach it, so the walker stops at the explored cell closest to the target.
        bool open(int x, int y) => x >= 0 && y >= 0 && x < 100 && y < 100 && !(x == 20 && y > 8);
        var path = ClientPathfinder.FindPath((10, 50), (30, 50), open, 100, 100, null, 1, 0, 0, -1, ClientPathfinder.SearchLimit.Player, out bool reached);
        Assert.False(reached);
        Assert.Equal((19, 50), path[^1]);
    }

    [Fact]
    public void A_detour_inside_the_ellipse_is_taken()
    {
        bool open(int x, int y) => x >= 0 && y >= 0 && x < 100 && y < 100 && !(x == 20 && y > 40);
        var path = ClientPathfinder.FindPath((10, 50), (30, 50), open, 100, 100, null, 1, 0, 0, -1, ClientPathfinder.SearchLimit.Player, out bool reached);
        Assert.True(reached);
        Assert.Equal((30, 50), path[^1]);
    }

    [Fact]
    public void An_npc_blocks_a_square_of_chebyshev_radius_five()
    {
        // NPC size 4 (object_size 5), player size 1: a cell is blocked when max(|dx|, |dy|) − 4 < 2.
        var npc = new ClientPathfinder.Blocker(50, 50, 4);
        Assert.True(ClientPathfinder.ObjectBlocks([npc], 55, 45, 2, 5));
        Assert.False(ClientPathfinder.ObjectBlocks([npc], 56, 50, 2, 5));
        var path = Path((30, 50), (70, 50), blockers: [npc]);
        Assert.Equal((70, 50), path[^1]);
        foreach (var (a, b) in path.Zip(path.Skip(1)))
            foreach (var (x, y) in ClientPathfinder.Line(a, b))
                Assert.True(Math.Max(Math.Abs(x - 50), Math.Abs(y - 50)) >= 6, $"({x}, {y}) is inside the NPC");
    }

    [Fact]
    public void Line_is_the_clients_bresenham()
    {
        Assert.Equal([(0, 0), (1, 0), (2, 1), (3, 1), (4, 2)], ClientPathfinder.Line((0, 0), (4, 2)));
        Assert.Equal([(0, 0), (0, 1), (1, 2), (1, 3)], ClientPathfinder.Line((0, 0), (1, 3)));
        Assert.Equal([(3, 3)], ClientPathfinder.Line((3, 3), (3, 3)));
    }

    [Fact]
    public void A_walk_up_stops_within_size_plus_three_of_the_object()
    {
        // An NPC (size 4) at (50, 10): the client walks with max range 7 and tolerance 4, so it stops 11 cells away.
        var npc = new ClientPathfinder.Blocker(50, 10, 4);
        var path = ClientPathfinder.FindPath((10, 10), (50, 10), (x, y) => x >= 0 && y >= 0 && x < 100 && y < 100, 100, 100,
            [npc], 1, 0, 7, 4, ClientPathfinder.SearchLimit.Player, out bool reached);
        Assert.True(reached);
        Assert.Equal((39, 10), path[^1]);

        // A bow's walk-up (max range 119, precision 3) at a monster 40 cells off, with a low rock's band (projectile grid
        // values 2) across the line: in range already, the walker still goes to the nearest cell whose line to the monster
        // holds 3 everywhere. At precision 1 (a sword, a Move) the same line is clear and nothing is walked.
        bool open(int x, int y) => x >= 0 && y >= 0 && x < 100 && y < 100;
        int sight(int x, int y) => !open(x, y) ? 0 : x is >= 29 and <= 31 && y is >= 5 and <= 15 ? 2 : 5;
        Assert.True(ClientPathfinder.LineBelow((10, 10), (50, 10), sight, 3));
        Assert.False(ClientPathfinder.InReach((10, 10), (50, 10), 0, 119, 1, sight, 3));
        Assert.True(ClientPathfinder.InReach((10, 10), (50, 10), 0, 119, 1, sight, 1));
        var bow = ClientPathfinder.FindPath((10, 10), (50, 10), open, 100, 100, null, 1, 0, 119, 1, ClientPathfinder.SearchLimit.Player, sight, 3, out reached);
        Assert.True(reached);
        Assert.True(bow.Count >= 2 && Math.Max(Math.Abs(bow[^1].X - 10), Math.Abs(bow[^1].Y - 10)) >= 5, $"walked {string.Join(" ", bow)}");
        Assert.False(ClientPathfinder.LineBelow(bow[^1], (50, 10), sight, 3));
        Assert.Empty(ClientPathfinder.FindPath((10, 10), (50, 10), open, 100, 100, null, 1, 0, 119, 1, ClientPathfinder.SearchLimit.Player, sight, 1, out reached));
        Assert.True(reached);
        // The line only blocked within the monster's own body (size 2, a cell each side) still counts as in reach.
        int body(int x, int y) => !open(x, y) ? 0 : x is >= 49 and <= 51 && y is >= 9 and <= 11 ? 0 : 5;
        Assert.True(ClientPathfinder.InReach((10, 10), (50, 10), 0, 119, 2, body, 3));
        Assert.False(ClientPathfinder.InReach((10, 10), (50, 10), 0, 119, 0, body, 3));
    }

    [Fact]
    public void A_monster_search_stops_after_100_expansions()
    {
        // A wall far longer than 100 expansions can get round: even guided along it by the coarse pass, the walker ends
        // on the near side, unreached.
        bool open(int x, int y) => x >= 0 && y >= 0 && x < 200 && y < 200 && !(x == 60 && y < 190);
        var path = ClientPathfinder.FindPath((50, 100), (70, 100), open, 200, 200, null, 2, 0, 0, -1,
            ClientPathfinder.SearchLimit.Expansions100, out bool reached);
        Assert.False(reached);
        Assert.True(path[^1].X < 60 && path[^1].Y > 100, $"ended at {path[^1]}");
    }

    [Fact]
    public void A_monster_goes_round_a_wall_with_the_coarse_pass()
    {
        // The straight line is blocked by a wall with a gap at its south end: the first search (100 expansions) fails, the
        // coarse pass finds the gap and guides the second search through it, past the wall.
        bool open(int x, int y) => x >= 0 && y >= 0 && x < 200 && y < 200 && !(x == 60 && y < 130);
        var path = ClientPathfinder.FindPath((50, 100), (70, 100), open, 200, 200, null, 2, 0, 0, -1,
            ClientPathfinder.SearchLimit.Expansions100, out bool reached);
        Assert.True(path[^1].X > 60, $"ended at {path[^1]}");
        Assert.Contains(path, p => p.X == 59 && p.Y >= 130);
    }
}
