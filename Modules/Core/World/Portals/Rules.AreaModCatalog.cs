using System.Text.Json;
using GameServer.Core;


/// <summary>
/// The mods an area has of its own (WorldAreas ModsKeys → Mods → Stats): The Twilight Strand has MapNoPortals
/// (map_disable_portal_use 1), so Portal Scrolls do nothing there. Built from the client tables; empty without them.
/// </summary>
sealed class AreaModCatalog(Dictionary<string, Dictionary<string, int>> stats)
{
    public const string DisablePortalUse = "map_disable_portal_use";

    /// <summary>The value of an area's own stat (summed over its mods), 0 when it has none.</summary>
    public int Stat(string areaId, string stat) => stats.GetValueOrDefault(areaId)?.GetValueOrDefault(stat) ?? 0;

    public bool DisablesPortals(string areaId) => Stat(areaId, DisablePortalUse) > 0;

    public static AreaModCatalog Load()
    {
        if (!ClientTables.Has("WorldAreas") || !ClientTables.Has("Mods") || !ClientTables.Has("Stats")) return new([]);
        try
        {
            return Parse(ClientTables.Table("WorldAreas"), ClientTables.Table("Mods"), ClientTables.Table("Stats"));
        }
        catch (Exception ex) when (ex is IOException or InvalidCastException or KeyNotFoundException or InvalidOperationException)
        {
            Console.WriteLine($"[gateway] area mods: {ex.Message}");
            return new([]);
        }
    }

    /// <summary>From the WorldAreas, Mods and Stats client tables.</summary>
    public static AreaModCatalog Parse(ClientTable worldAreas, ClientTable mods, ClientTable stats)
    {
        var statIds = stats.Select(r => r.TextOrNull("Id") ?? "").ToList();
        var result = new Dictionary<string, Dictionary<string, int>>();
        foreach (var area in worldAreas)
        {
            if (area.IsNull("ModsKeys")) continue;
            var own = new Dictionary<string, int>();
            foreach (int row in area.Ints("ModsKeys"))
            {
                if (row < 0 || row >= mods.Count) continue;
                var mod = mods[row];
                for (int i = 1; i <= 6; i++)
                {
                    if (mod.IntOrNull($"StatsKey{i}") is not int key || key >= statIds.Count) continue;
                    if (mod.IntOrNull($"Stat{i}Min") is not int min) continue;
                    string stat = statIds[key];
                    own[stat] = own.GetValueOrDefault(stat) + min;
                }
            }
            if (own.Count > 0) result[area.TextOrNull("Id") ?? ""] = own;
        }
        return new(result);
    }
}
