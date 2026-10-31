using System.Text.Json;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

/// <summary>
/// Waypoints (Agents/docs/protocol/16-waypoints.md): which areas have one and their bit in S→C 0x4c (WorldAreas, see
/// <see cref="WorldAreaWaypoints"/>), and where an area's waypoint stands: the tile object scripts/walkmaps captured
/// for the (area, seed), like the area transitions (08-area-transitions.md §7), or Lioneye's Watch's known place.
/// </summary>
static class WaypointCatalog
{
    const string Folder = "data/walkability";

    /// <summary>The waypoint's server object id (area transitions use 0x2000…, monsters 0x3000…).</summary>
    public const uint ObjectId = 0x2f00;

    static readonly Lazy<WorldAreaWaypoints> Order = new(() =>
    {
        var w = WorldAreaWaypoints.Load("data/tables/WorldAreas.json");
        Console.WriteLine(w.Entries.Count == 0
            ? "[gateway] waypoints: no data/tables/WorldAreas.json (run scripts/dev/extract-data.ps1), waypoints are off"
            : $"[gateway] waypoints: {w.Entries.Count} waypoint areas");
        return w;
    });

    public static WorldAreaWaypoints Areas => Order.Value;

    /// <summary>The Azurite Mine Encampment (WorldAreas Delve_Hub), where Niko's Visit Mine Encampment leads.</summary>
    public const string DelveHub = "Delve_Hub";

    /// <summary>The Menagerie (WorldAreas Menagerie_Hub), Einhar's, where the World panel's Menagerie pin leads.</summary>
    public const string MenagerieHub = "Menagerie_Hub";

    /// <summary>The Forbidden Sanctum (WorldAreas SanctumFoyer_Fellshrine, area type 45), where its World panel pin leads.</summary>
    public const string SanctumHub = "SanctumFoyer_Fellshrine";

    /// <summary>Kingsmarch (WorldAreas KalguuranSettlersLeague, area type 50), where its World panel pin leads.</summary>
    public const string Kingsmarch = "KalguuranSettlersLeague";

    /// <summary>The Rogue Harbour (WorldAreas HeistHub), where its World panel pin leads.</summary>
    public const string HeistHub = "HeistHub";

    /// <summary>The Monastery of the Keepers (WorldAreas ChayulaLeague, area type 51), where the Brequel pin leads.</summary>
    public const string ChayulaHub = "ChayulaLeague";

    /// <summary>The Aspirants' Plaza (WorldAreas Labyrinth_Airlock), the World panel's Labyrinth pin.</summary>
    public const string LabyrinthPlaza = "Labyrinth_Airlock";

    static readonly Lazy<AreaVisitFlags> VisitFlags = new(() => AreaVisitFlags.Load("data/tables/WorldAreas.json", "data/tables/MapPins.json"));

    /// <summary>The quest flags entering an area sets (AreaVisitFlags), and the areas with a World panel pin.</summary>
    public static AreaVisitFlags Visits => VisitFlags.Value;

    // Lioneye's Watch without a capture: the town tile's .tmd record beachencampment_v01_01_v2_waypoint1 as the client
    // places it (TerrainTests.Town_area_transition_and_waypoint_placement; live capture 2026-10-18).
    static readonly WaypointObject TownFallback = new(256, 169, "Metadata/Terrain/Beach/Town_v2/beachencampment_v01_01_v2_waypoint1.ao",
        Rotation: 3 * MathF.PI / 2, Height: -357.52f);

    /// <summary>The area's waypoint, or null when it has none or its place is not known yet.</summary>
    public static WaypointObject? For(ushort hash, string id, uint seed)
    {
        // League hubs (the Azurite Mine Encampment, the Menagerie) have a waypoint their tile places but no WorldAreas
        // waypoint bit of their own: the object is still sent, so it can be clicked; 0x4c cannot light it (0x141fdd130).
        if (Areas.BitOf(hash) is null) return LoadCapture(id, seed) is { } hub ? hub with { Active = 1 } : null;
        var captured = LoadCapture(id, seed);
        if (captured is not null) return captured;
        return hash == AreaLayouts.LioneyesWatch ? TownFallback : null;
    }

    static WaypointObject? LoadCapture(string id, uint seed)
    {
        string file = $"{Folder}/{id}_seed{seed}.transitions.json";
        try
        {
            // No capture: the layout the Gateway generated itself.
            if (!File.Exists(file))
                return ServerLevels.For(id, seed)?.Objects.FirstOrDefault(o => o.Type == WaypointObject.MetadataPath) is { } made
                    ? new WaypointObject(made.X, made.Y, made.Model, made.Rotation, float.IsNaN(made.Z) ? null : made.Z) : null;
            var data = JsonSerializer.Deserialize<CaptureFileData>(File.ReadAllText(file), Json);
            var o = data?.Objects?.FirstOrDefault(o => o.ObjectType == WaypointObject.MetadataPath);
            return o is null ? null : new WaypointObject(o.X, o.Y, o.Model, o.Rotation, o.Z);
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return null;
        }
    }

    static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    sealed record CaptureFileData(List<CapturedObject>? Objects);
    sealed record CapturedObject(string? ObjectType, string? Model, int X, int Y, float Rotation, float? Z);
}
