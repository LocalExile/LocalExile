using System.Text.Json;
using GameServer.Data.Terrain;

namespace Atlas;

/// <summary>
/// data/room-points.json: per area, the rooms of its room sets and fill tiles that carry monster points ("mapboss", the area's
/// unique boss; "%area%-â€¦", a MonsterPacks row named after the area) or a "default" start point, with those points and
/// the room's doodads in room cells (server objects with their type, angle and scale: quest objects the Gateway creates). The Gateway finds where the client placed such a room by matching its doodads against the doodads the
/// walk-map recorder captured (AreaPopulation.FitRooms). Agents/docs/protocol/14-monster-packs.md Â§3.
/// </summary>
static class RoomPoints
{
    public static bool IsMonsterPoint(string name) =>
        name.Equals("mapboss", StringComparison.OrdinalIgnoreCase) || name.StartsWith("%area%-", StringComparison.OrdinalIgnoreCase);

    // "default": where a player starts in the area (The Twilight Strand's washed-up beach, 12-quests.md).
    static bool IsKeptPoint(string name) => IsMonsterPoint(name) || name.Equals("default", StringComparison.OrdinalIgnoreCase)
        || name == "Metadata/Chests/TutorialSupportGemChest";

    // Plain doodads are the client's own; anything else in a room (an NPC, a quest blocker) is an object the server creates.
    const string PlainDoodad = "Metadata/MiscellaneousObjects/Doodad";

    public static void Run(GameFiles files, List<AreaRow> areas, List<TopologyRow> topologies, string output)
    {
        var byId = topologies.ToDictionary(t => t.Id, StringComparer.OrdinalIgnoreCase);
        var areaRooms = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);
        var rooms = new SortedDictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        foreach (var area in areas)
        {
            var found = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var topo in area.Topologies)
            {
                if (!byId.TryGetValue(topo, out var t) || files.Graph(t.Graph) is not { } graph) continue;
                // The room set (.rs) and the fill tiles (.gft: rooms the generator scatters, e.g. The Coast's bonfires).
                var tsi = files.Tsi(graph.MasterFile);
                var lists = new[] { "RoomSet", "FillTiles" }.Select(k => tsi?.GetValueOrDefault(k)).OfType<string>();
                foreach (var arm in lists.SelectMany(l => files.List(TerrainText.Resolve(graph.MasterFile, l.Trim()), ".arm")))
                {
                    if (files.Room(arm) is not { } room || !room.Points.Any(p => IsKeptPoint(p.Name))) continue;
                    found.Add(arm);
                    rooms.TryAdd(arm, new
                    {
                        tag = room.Tag,
                        points = room.Points.Where(p => IsKeptPoint(p.Name)).Select(p => new { name = p.Name, x = p.X, y = p.Y, angle = p.Angle }),
                        doodads = room.Doodads.Select(d => d.Type.Equals(PlainDoodad, StringComparison.OrdinalIgnoreCase)
                            ? (object)new { model = d.Model, x = d.X, y = d.Y }
                            : new { model = d.Model, x = d.X, y = d.Y, type = d.Type, angle = d.Angle, scale = d.Scale }),
                    });
                }
            }
            if (found.Count > 0) areaRooms[area.Id] = [.. found];
        }
        File.WriteAllText(output, JsonSerializer.Serialize(new
        {
            note = "Rooms with monster points (mapboss, %area%-<pack>), default start or tutorial chest spawn points: points and doodads in room cells. scripts/re/atlas room-points.",
            areas = areaRooms,
            rooms,
        }, new JsonSerializerOptions { WriteIndented = false }));
        Console.WriteLine($"{output}: {areaRooms.Count} areas, {rooms.Count} rooms with monster, start or tutorial points");
    }

}
