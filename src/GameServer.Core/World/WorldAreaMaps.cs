using System.Text.Json;

namespace GameServer.Core.World;

/// <summary>
/// The WorldAreas <c>HASH16</c>s a map item opens: <c>Maps.dat</c>'s <c>Regular_WorldAreasKey</c> and
/// <c>Unique_WorldAreasKey</c>, both indexes into the WorldAreas table. The seed policy needs them
/// (<see cref="AreaSeeds"/>): seed 1 is the seed an area's captures were dumped at, so a map area nobody dumped one for
/// must not be pinned to it — the client may fail to build that layout and exit, as <c>MapWorldsColosseum</c> does at
/// seed 1 ("no suitable room found … cage_turn").
/// </summary>
public static class WorldAreaMaps
{
    /// <summary>These map areas' HASH16s; empty when either table is missing.</summary>
    public static HashSet<ushort> Load(string worldAreasJson, string mapsJson)
    {
        var result = new HashSet<ushort>();
        if (!File.Exists(worldAreasJson) || !File.Exists(mapsJson)) return result;
        ushort[] areas;
        using (var doc = JsonDocument.Parse(File.ReadAllBytes(worldAreasJson)))
            areas = [.. doc.RootElement.EnumerateArray().Select(r => (ushort)r.GetProperty("HASH16").GetInt32())];
        using (var doc = JsonDocument.Parse(File.ReadAllBytes(mapsJson)))
            foreach (var row in doc.RootElement.EnumerateArray())
                foreach (string column in new[] { "Regular_WorldAreasKey", "Unique_WorldAreasKey" })
                    if (row.TryGetProperty(column, out var value) && value.ValueKind == JsonValueKind.Number
                        && value.GetInt32() is int index and >= 0 && index < areas.Length)
                        result.Add(areas[index]);
        return result;
    }
}
