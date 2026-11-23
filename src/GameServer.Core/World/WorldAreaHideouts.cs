using System.Text.Json;

namespace GameServer.Core.World;

/// <summary>
/// The hideout areas of the game: the WorldAreas rows with <c>IsHideout</c> set (the WorldAreas client table), by
/// <c>HASH16</c>. Their layout is fixed, so their instances always use seed 1 (Agents/docs/protocol/21-hideouts.md).
/// </summary>
public static class WorldAreaHideouts
{
    /// <summary>The hideout rows' HASH16s; empty without the table.</summary>
    public static IReadOnlySet<ushort> Load(ClientTable? worldAreas)
        => LoadAreas(worldAreas).Values.ToHashSet();

    public static IReadOnlyDictionary<string, ushort> LoadAreas(ClientTable? worldAreas)
    {
        if (worldAreas is null) return new Dictionary<string, ushort>();
        return worldAreas
            .Where(r => r.BoolOrNull("IsHideout") == true)
            .ToDictionary(r => r.Text("Id")!, r => (ushort)r.Int("HASH16"));
    }
}
