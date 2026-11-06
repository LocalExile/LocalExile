using System.Text.Json;
using GameServer.Core.Imports;

static class NativeProgress
{
    sealed record Node(int Ascendancy, int Points);
    static readonly Dictionary<string, (Dictionary<string, int> Ascendancies, Dictionary<uint, Node> Nodes)> Cache = new();
    public static int AscendancyPoints(PathOfBuildingBuild build, string folder)
    {
        if (!Cache.TryGetValue(folder, out var data))
        {
            using var asc = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(folder, "tables/Ascendancy.json")));
            var names = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            int index = 0;
            foreach (var row in asc.RootElement.EnumerateArray())
            {
                names.TryAdd(row.GetProperty("Id").GetString()!, index);
                names.TryAdd(row.GetProperty("Name").GetString()!, index++);
            }
            using var passives = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(folder, "tables/PassiveSkills.json")));
            var nodes = new Dictionary<uint, Node>();
            foreach (var row in passives.RootElement.EnumerateArray())
                if (row.GetProperty("AscendancyKey") is { ValueKind: JsonValueKind.Number } key && !row.GetProperty("IsAscendancyStartingNode").GetBoolean())
                    nodes.TryAdd(row.GetProperty("PassiveSkillGraphId").GetUInt32(), new(key.GetInt32(), row.GetProperty("SkillPointsGranted").GetInt32()));
            data = (names, nodes); Cache[folder] = data;
        }
        if (build.Ascendancy is null || !data.Ascendancies.TryGetValue(build.Ascendancy, out int selected)) return 0;
        return build.Tree.Nodes.Distinct().Where(id => data.Nodes.TryGetValue(id, out var node) && node.Ascendancy == selected).Sum(id => data.Nodes[id].Points);
    }
}
