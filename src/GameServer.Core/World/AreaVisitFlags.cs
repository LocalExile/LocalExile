using System.Text.Json;

namespace GameServer.Core.World;

/// <summary>
/// The quest flag an area sets when a character enters it (QuestFlags rows): MapPins <c>QuestFlagVisit</c> of the pins
/// that show the area, and the area's WorldAreas <c>StartQuestFlags</c> (the same flag where both exist, e.g. 124 for
/// Lioneye's Watch). The World panel reveals a pin when any of its MapPins <c>QuestFlags</c> is set (0x140ab4c10), and
/// those are the neighbouring areas' visit flags (The Coast's pin: Lioneye's Watch's 124), so visiting an area reveals
/// the next one; a connection line is drawn only between two revealed or activated pins (0x140aa5fb0).
/// Agents/docs/protocol/16-waypoints.md §4.
/// </summary>
public sealed class AreaVisitFlags
{
    readonly Dictionary<string, int[]> _byArea;

    /// <summary>The WorldAreas Ids that have a pin on the World panel, in MapPins order.</summary>
    public IReadOnlyList<string> PinnedAreas { get; }

    public AreaVisitFlags(Dictionary<string, int[]> byArea, IReadOnlyList<string> pinnedAreas) =>
        (_byArea, PinnedAreas) = (byArea, pinnedAreas);

    /// <summary>The area's visit flags; empty for an unknown area.</summary>
    public IReadOnlyList<int> For(string areaId) => _byArea.GetValueOrDefault(areaId) ?? [];

    /// <summary>Loads both tables; missing or unreadable files leave the flags empty.</summary>
    public static AreaVisitFlags Load(string worldAreasJson, string mapPinsJson)
    {
        var byArea = new Dictionary<string, HashSet<int>>();
        var pinned = new List<string>();
        try
        {
            if (!File.Exists(worldAreasJson)) return new([], []);
            using var areas = JsonDocument.Parse(File.ReadAllBytes(worldAreasJson));
            var ids = areas.RootElement.EnumerateArray().Select(r => r.GetProperty("Id").GetString() ?? "").ToList();
            int row = 0;
            foreach (var r in areas.RootElement.EnumerateArray())
            {
                if (r.TryGetProperty("StartQuestFlags", out var start) && start.ValueKind == JsonValueKind.Array)
                    foreach (var f in start.EnumerateArray()) Add(ids[row], f.GetInt32());
                row++;
            }
            if (File.Exists(mapPinsJson))
            {
                using var pins = JsonDocument.Parse(File.ReadAllBytes(mapPinsJson));
                foreach (var p in pins.RootElement.EnumerateArray())
                {
                    if (!p.TryGetProperty("WorldAreasKeys", out var keys) || keys.ValueKind != JsonValueKind.Array) continue;
                    foreach (var k in keys.EnumerateArray())
                    {
                        int i = k.GetInt32();
                        if (i < 0 || i >= ids.Count) continue;
                        if (!pinned.Contains(ids[i])) pinned.Add(ids[i]);
                        if (p.TryGetProperty("QuestFlagVisit", out var visit) && visit.ValueKind == JsonValueKind.Array)
                            foreach (var f in visit.EnumerateArray()) Add(ids[i], f.GetInt32());
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return new([], []);
        }
        return new(byArea.ToDictionary(e => e.Key, e => e.Value.Order().ToArray()), pinned);

        void Add(string id, int flag)
        {
            if (!byArea.TryGetValue(id, out var set)) byArea[id] = set = [];
            set.Add(flag);
        }
    }
}
