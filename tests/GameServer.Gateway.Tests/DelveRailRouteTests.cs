using System.Text.Json;

namespace GameServer.Gateway.Tests;

public class DelveRailRouteTests
{
    [Theory]
    [InlineData(35, true)]
    [InlineData(70, false)]
    public void Rail_graph_joins_connected_endpoints_and_matches_room_paths_without_case(int offset, bool connected)
    {
        string folder = Path.Combine(Path.GetTempPath(), "lx-delve-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(folder, "walkability"));
        try
        {
            string entrance = "Metadata/test/entrance_up.arm", quest = "Metadata/test/encounter_major_azurite_1_Q.arm";
            const string prefix = "Metadata/Terrain/Leagues/Delve/Objects/";
            object Node(int index, int x, string type) => new { index, x, y = 10, type = prefix + type };
            File.WriteAllText(Path.Combine(folder, "delve-paths.json"), JsonSerializer.Serialize(new
            {
                version = 1, rooms = new Dictionary<string, object>
                {
                    [entrance.ToLowerInvariant()] = new { nodes = new[] { Node(0, 10, "DelveNodeNoEncounter"), Node(1, 20, "DelvePathEndpoint") }, links = new[] { new[] { 0, 1 } }, spawns = Array.Empty<object>() },
                    [quest.ToLowerInvariant()] = new { nodes = new[] { Node(0, 4, "DelvePathEndpoint"), Node(1, 14, "DelveNode") }, links = new[] { new[] { 0, 1 } }, spawns = Array.Empty<object>() }
                }
            }));
            File.WriteAllText(Path.Combine(folder, "walkability", "test_seed1.rooms.json"), JsonSerializer.Serialize(new
            {
                area = "test", seed = 1, rooms = new[]
                {
                    new { path = entrance, x = 0, y = 0, width = 50, height = 50, code = 0 },
                    new { path = quest, x = offset, y = 0, width = 50, height = 50, code = 0 }
                }
            }));
            var route = DelveRailRoute.Load("test", 1, folder);
            Assert.Equal(connected, route is not null);
            if (connected) Assert.Equal(new[] { (10, 10), (20, 10), (39, 10), (49, 10) }, route!.Points);
            Assert.Null(DelveRailRoute.Load("test", 2, folder));
        }
        finally { Directory.Delete(folder, true); }
    }
}
