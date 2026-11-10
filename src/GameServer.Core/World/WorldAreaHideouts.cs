using System.Text.Json;

namespace GameServer.Core.World;

/// <summary>
/// The hideout areas of the game: the WorldAreas rows with <c>IsHideout</c> set (data/tables/WorldAreas.json), by
/// <c>HASH16</c>. Their layout is fixed, so their instances always use seed 1 (Agents/docs/protocol/21-hideouts.md).
/// </summary>
public static class WorldAreaHideouts
{
    /// <summary>The hideout rows' HASH16s; empty when the file is missing.</summary>
    public static IReadOnlySet<ushort> Load(string worldAreasJson)
        => LoadAreas(worldAreasJson).Values.ToHashSet();

    public static IReadOnlyDictionary<string, ushort> LoadAreas(string worldAreasJson)
    {
        if (!ClientTables.Exists(worldAreasJson)) return new Dictionary<string, ushort>();
        using var doc = JsonDocument.Parse(ClientTables.ReadAllBytes(worldAreasJson));
        return doc.RootElement.EnumerateArray()
            .Where(r => r.TryGetProperty("IsHideout", out var h) && h.ValueKind == JsonValueKind.True)
            .ToDictionary(r => r.GetProperty("Id").GetString()!, r => (ushort)r.GetProperty("HASH16").GetInt32());
    }
}
