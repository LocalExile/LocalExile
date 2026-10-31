using GameServer.Core.World;
using GameServer.Data.Terrain.Generation;
using Xunit.Abstractions;

namespace GameServer.Gateway.Tests;

public sealed class MazeProbeTests(ITestOutputHelper output)
{
    [Fact]
    public void The_maze_room_graph_connects_an_entrance_to_the_exit()
    {
        var level = ServerLevels.For("AzmeriLeagueBoss", 5);
        Assert.NotNull(level);
        bool Walkable(int x, int y) => x >= 0 && y >= 0 && x < level!.Width && y < level.Height
            && level.Walk[y * level.Width + x] >= 2;
        (int X, int Y) Center(PlacedArmRoom room)
        {
            var p = AreaPopulation.LocalToWorld(room.X, room.Y, room.Code, room.Width / 2f, room.Height / 2f);
            if (Walkable(p.X, p.Y)) return p;
            for (int r = 1; r < 50; r++)
                for (int dx = -r; dx <= r; dx++)
                    for (int dy = -r; dy <= r; dy++)
                        if (Walkable(p.X + dx, p.Y + dy)) return (p.X + dx, p.Y + dy);
            return p;
        }
        var rooms = level!.Rooms.Where(r => r.Path.Contains("/TreantArena/Rooms/maze_", StringComparison.OrdinalIgnoreCase)).ToList();
        var start = rooms.First(r => r.Path.EndsWith("/maze_entrance.arm", StringComparison.OrdinalIgnoreCase));
        var exit = rooms.First(r => r.Path.EndsWith("/maze_exit.arm", StringComparison.OrdinalIgnoreCase));
        var centers = rooms.ToDictionary(r => r, Center);
        List<(int X, int Y)> Route((int X, int Y) a, (int X, int Y) b) => ClientPathfinder.FindPath(a, b, Walkable,
            level.Width, level.Height, limit: ClientPathfinder.SearchLimit.Expansions1000);
        var prev = new Dictionary<PlacedArmRoom, PlacedArmRoom?> { [start] = null };
        var q = new Queue<PlacedArmRoom>();
        q.Enqueue(start);
        while (q.Count > 0 && !prev.ContainsKey(exit))
        {
            var room = q.Dequeue();
            foreach (var other in rooms)
            {
                if (prev.ContainsKey(other)) continue;
                var a = centers[room];
                var b = centers[other];
                if (Math.Abs(a.X - b.X) > 90 || Math.Abs(a.Y - b.Y) > 90) continue;
                var p = Route(a, b);
                if (p.Count < 2 || Math.Abs(p[^1].X - b.X) + Math.Abs(p[^1].Y - b.Y) > 6) continue;
                prev[other] = room;
                q.Enqueue(other);
            }
        }
        output.WriteLine($"rooms={rooms.Count} start={centers[start]} exit={centers[exit]} reached={prev.ContainsKey(exit)}");
        Assert.True(prev.ContainsKey(exit), "no room route");
        var route = new List<PlacedArmRoom>();
        for (PlacedArmRoom? r = exit; r is not null; r = prev[r]) route.Add(r);
        route.Reverse();
        output.WriteLine("room route: " + string.Join(" -> ", route.Select(r => Path.GetFileNameWithoutExtension(r.Path))));
        int steps = 0;
        for (int i = 1; i < route.Count; i++)
        {
            var p = Route(centers[route[i - 1]], centers[route[i]]);
            steps += p.Count;
            Assert.True(p.Count > 1, $"no cell path {i}");
        }
        output.WriteLine($"cell steps={steps}");
        Assert.True(steps > 10);
    }
}
