using System.Text.Json;
using GameServer.Data.Ggpk;
using GameServer.Data.Terrain;

namespace Atlas;

// Read-only extraction of the authored rail graph, including original object indexes used by "next" links.
static class DelvePaths
{
    public static void Run(GameFileSystem fs, string output)
    {
        var rooms = new SortedDictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in fs.EnumeratePaths().Where(p => p.StartsWith("Metadata/Terrain/Leagues/Delve/Biomes/Mine/Rooms/", StringComparison.OrdinalIgnoreCase)
            && p.EndsWith(".arm", StringComparison.OrdinalIgnoreCase)))
        {
            string text = TerrainText.Decode(fs.ReadFile(path));
            var room = RoomFile.Parse(text);
            if (!room.Doodads.Any(d => d.Type.Contains("/DelvePath", StringComparison.Ordinal))) continue;
            if (room.Problem is not null) throw new InvalidDataException($"{path}: {room.Problem}");
            var links = new List<int[]>();
            foreach (var line in TerrainText.Lines(text))
            {
                var t = TerrainText.Tokenize(line);
                if (t.Count == 3 && t[0].IsNumber && t[1].IsNumber && t[2].Quoted && t[2].Text == "next")
                {
                    int a = t[0].Int, b = t[1].Int;
                    if (a < 0 || b < 0 || a >= room.Doodads.Count || b >= room.Doodads.Count)
                        throw new InvalidDataException($"{path}: invalid rail link {a}->{b}");
                    links.Add([a, b]);
                }
            }
            rooms[path] = new { nodes = room.Doodads.Select((d, i) => new { index = i, x = d.X, y = d.Y, type = d.Type })
                .Where(d => d.type.Contains("/DelvePath", StringComparison.Ordinal) || d.type.Contains("/DelveNode", StringComparison.Ordinal)),
                links, spawns = room.Points.Where(p => p.Section == 0 && p.Name != "default") };
        }
        File.WriteAllText(output, JsonSerializer.Serialize(new { version = 1, rooms }));
        Console.WriteLine($"{output}: {rooms.Count} authored Delve rail graphs");
    }
}
