using GameServer.Core;

/// <summary>An area's own terrain plugin (WorldAreas <c>TerrainPlugin</c> → TerrainPlugins): its Id and the u16 id
/// (<c>__col1</c>) 0x1d's extra data names it by. The client creates it at every generation of the area, with the state
/// the server sent for it or an empty one (<c>0x141eca930</c>).</summary>
public static class AreaTerrainPlugins
{
    static Dictionary<ushort, (ushort Id, string Name)> ByArea = default!;
    static Dictionary<ushort, string> Names = default!;

    public static void LoadGameData()
    {
        var plugins = ClientTables.TableOrEmpty("TerrainPlugins");
        var names = new Dictionary<ushort, string>();
        foreach (var plugin in plugins)
            if (plugin.IntOrNull("__col1") is int id && plugin.Text("Id") is { Length: > 0 } name) names.TryAdd((ushort)id, name);
        Names = names;
        var byArea = new Dictionary<ushort, (ushort, string)>();
        foreach (var area in ClientTables.TableOrEmpty("WorldAreas"))
        {
            if (area.LongOrNull("TerrainPlugin") is not { } row || row < 0 || row >= plugins.Count) continue;
            var plugin = plugins[(int)row];
            byArea[(ushort)area.Int("HASH16")] = ((ushort)plugin.Int("__col1"), plugin.Text("Id") ?? "");
        }
        ByArea = byArea;
    }

    /// <summary>The plugin of the area (WorldAreas HASH16), null for none.</summary>
    public static (ushort Id, string Name)? Of(ushort areaHash) => ByArea.TryGetValue(areaHash, out var p) ? p : null;

    /// <summary>The TerrainPlugins Id of a u16 plugin id (<c>__col1</c>), null when there is none.</summary>
    public static string? NameOf(ushort id) => Names.GetValueOrDefault(id);

    /// <summary>The u16 id of a TerrainPlugins row by its Id, null when there is none.</summary>
    public static ushort? IdOf(string name) =>
        ClientTables.TableOrEmpty("TerrainPlugins").FirstOrDefault(r => r.Text("Id") == name) is { Table: not null } row ? (ushort)row.Int("__col1") : null;
}
