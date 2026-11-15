namespace GameServer.Core.World;

/// <summary>
/// The WorldAreas <c>HASH16</c>s a map item opens: <c>Maps</c>' <c>Regular_WorldAreasKey</c> and
/// <c>Unique_WorldAreasKey</c>, both indexes into the WorldAreas table. The seed policy needs them
/// (<see cref="AreaSeeds"/>): seed 1 is the seed an area's captures were dumped at, so a map area nobody captured must not
/// be pinned to it — the client may fail to build that layout and exit, as <c>MapWorldsColosseum</c> does at seed 1
/// ("no suitable room found … cage_turn").
/// </summary>
public static class WorldAreaMaps
{
    /// <summary>These map areas' HASH16s; empty without either table.</summary>
    public static HashSet<ushort> Load(ClientTable? worldAreas, ClientTable? maps)
    {
        var result = new HashSet<ushort>();
        if (worldAreas is null || maps is null) return result;
        var hashes = worldAreas.Select(r => (ushort)r.Int("HASH16")).ToArray();
        foreach (var row in maps)
            foreach (string column in new[] { "Regular_WorldAreasKey", "Unique_WorldAreasKey" })
                if (row.IntOrNull(column) is int index and >= 0 && index < hashes.Length)
                    result.Add(hashes[index]);
        return result;
    }
}
