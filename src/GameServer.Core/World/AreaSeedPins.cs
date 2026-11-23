namespace GameServer.Core.World;

/// <summary>
/// The seeds pinned by data (<c>data/area-seeds.json</c>, an object of WorldAreas <c>Id</c> to seed): an area whose port
/// generation does not match what the client builds — so its layout comes from a capture (<c>data/walkability</c>) — is
/// played at the one seed that capture exists for. Without the file, nothing is pinned.
/// </summary>
public static class AreaSeedPins
{
    public const string File = "data/area-seeds.json";

    /// <summary>These pins keyed by WorldAreas <c>HASH16</c>; empty without the file or the table.</summary>
    public static Dictionary<ushort, uint> Load(ClientTable? worldAreas, string file = File)
    {
        var byId = new Dictionary<string, uint>(StringComparer.Ordinal);
        if (!System.IO.File.Exists(file)) return [];
        using (var doc = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllBytes(file)))
            foreach (var row in doc.RootElement.EnumerateObject())
                if (row.Value.TryGetUInt32(out uint seed)) byId[row.Name] = seed;
        return ServerLevelAreas.ByHash(worldAreas, byId);
    }
}
