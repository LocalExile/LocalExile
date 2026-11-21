namespace GameServer.Core.World;

/// <summary>
/// The WorldAreas <c>HASH16</c>s a map item opens: <c>Maps</c>' <c>Regular_WorldAreasKey</c> and
/// <c>Unique_WorldAreasKey</c>, plus destinations named only by <c>UniqueMaps</c>. The seed policy needs them
/// (<see cref="AreaSeeds"/>): seed 1 is the seed an area's captures were dumped at, so a map area nobody captured must not
/// be pinned to it — the client may fail to build that layout and exit, as <c>MapWorldsColosseum</c> does at seed 1
/// ("no suitable room found … cage_turn").
/// </summary>
public static class WorldAreaMaps
{
    /// <summary>Map areas without a usable seed-1 walk grid; empty without either table.</summary>
    public static HashSet<ushort> Load(ClientTable? worldAreas, ClientTable? maps, string captureFolder = WalkMapCapture.Folder) =>
        Load(worldAreas, maps, captureFolder, null);

    public static HashSet<ushort> Load(ClientTable? worldAreas, ClientTable? maps, string captureFolder, ClientTable? uniqueMaps)
    {
        var result = new HashSet<ushort>();
        if (worldAreas is null) return result;
        var indices = new HashSet<int>();
        if (maps is not null)
            foreach (var row in maps)
            {
                if (row.IntOrNull("Regular_WorldAreasKey") is int regular) indices.Add(regular);
                if (row.IntOrNull("Unique_WorldAreasKey") is int unique) indices.Add(unique);
            }
        if (uniqueMaps is not null)
            foreach (var row in uniqueMaps)
                indices.Add(row.Int("WorldAreasKey"));
        foreach (int index in indices)
        {
            if (index < 0 || index >= worldAreas.Count) continue;
            var area = worldAreas[index];
            if (area.Text("Id") is { } id && !WalkMapCapture.Available(id, 1, captureFolder))
                result.Add((ushort)area.Int("HASH16"));
        }
        return result;
    }
}
