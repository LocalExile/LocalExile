using System.Text.Json;
using System.Text.RegularExpressions;
using GameServer.Data.Ggpk;
using GameServer.Data.Terrain;
using GameServer.Data.Terrain.Generation;
using GameServer.Core.World;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls($"http://127.0.0.1:{(int.TryParse(Environment.GetEnvironmentVariable("MAP_VIEWER_PORT"), out var port) && port is > 0 and <= 65535 ? port : 5187)}");
var app = builder.Build();
var root = Directory.GetCurrentDirectory();
var web = Path.Combine(root, "scripts", "dev", "map-viewer", "wwwroot");
var areasPath = Path.Combine(root, "data", "tables", "WorldAreas.json");
var areas = JsonSerializer.Deserialize<List<Area>>(File.ReadAllText(areasPath), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
var monsters = new Lazy<List<Monster>>(() =>
{
    string path = Path.Combine(root, "data", "tables", "MonsterVarieties.json");
    return File.Exists(path) ? JsonSerializer.Deserialize<List<Monster>>(File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [] : [];
});
var pack = GamePack.Default;
var files = new TerrainCache(pack.TryRead);
var gate = new object();
var replacementSeeds = new Lazy<Dictionary<string, uint>>(() => ServerLevelAreas.SeedsInPlaceOfOne(ServerLevelAreas.BaselineFile));

app.MapGet("/api/areas", () => areas.Where(a => a.TopologiesKeys.Length > 0).Select(a => new { a.Id, a.Name, a.AreaLevel }));
app.MapGet("/api/map", (string id, uint seed, int? level) =>
{
    var area = areas.Find(a => a.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
    if (area is null) return Results.NotFound(new { error = "Unknown area" });
    lock (gate)
    {
        try
        {
            var info = AreaInfo.Load(pack.TryRead, area.Id, Math.Clamp(level ?? Math.Max(1, area.AreaLevel), 1, 100));
            uint requestedSeed = seed;
            if (area.IsTown || area.IsHideout) seed = 1;
            else if (seed == 1 && replacementSeeds.Value.TryGetValue(area.Id, out uint replacement)) seed = replacement;
            GenerationResult result;
            try { result = LevelGenerator.Generate(files, info, seed); }
            catch (Exception e) when (!area.IsTown && !area.IsHideout && e is InvalidOperationException or InvalidDataException or ArgumentException)
            {
                var original = e;
                GenerationResult? generated = null;
                uint failedSeed = seed;
                for (uint k = 1; k <= 8 && generated is null; k++)
                {
                    uint other = unchecked(failedSeed * 0x9e3779b1u + k * 0x85ebca77u) & 0x7fffffff;
                    if (other is 0 or 1) other = k + 1;
                    try { generated = LevelGenerator.Generate(files, info, other); seed = other; }
                    catch (Exception retry) when (retry is InvalidOperationException or InvalidDataException or ArgumentException) { }
                }
                result = generated ?? throw original;
            }
            var grid = result.Grid;
            var walk = Walkability.Build(grid, t => files.Walk(t.Path));
            var warnings = new List<string>();
            try { new DoodadStage(files, result.Random, grid, walk, warnings).Run(result.Records); }
            catch (Exception e) when (e is NotSupportedException or InvalidDataException) { warnings.Add("Doodad collision incomplete: " + e.Message); }
            var exits = AreaExits.Resolve(files, result);
            Area? Connection(int number) => number > 0 && number <= area.Connections_WorldAreasKeys.Length
                && area.Connections_WorldAreasKeys[number - 1] is int n && n >= 0 && n < areas.Count ? areas[n] : null;
            var objects = AreaObjects.Transitions(files, result).Concat(AreaObjects.RoomObjects(result, points: true))
                .Concat(AreaObjects.TileObjects(files, result, t => t.StartsWith("Metadata/QuestObjects/", StringComparison.OrdinalIgnoreCase) || t.StartsWith("Metadata/Chests/Quest", StringComparison.OrdinalIgnoreCase) || t.Equals("Metadata/MiscellaneousObjects/CraftRecipeUnlockMarker", StringComparison.OrdinalIgnoreCase)))
                .DistinctBy(o => (o.Type, o.X, o.Y)).Select(o => new { o.Type, o.Model, o.X, o.Y }).ToArray();
            var points = result.Records.SelectMany(r => r.Rooms).SelectMany(room => room.Room.Points
                .Where(p => p.Name.StartsWith("mapboss", StringComparison.OrdinalIgnoreCase) || p.Name.StartsWith("force:mapboss", StringComparison.OrdinalIgnoreCase))
                .Select(p => { var (x, y) = Orientation.Map(room.Orientation, (int)p.X, (int)p.Y, room.Room.Width * 23, room.Room.Height * 23);
                    var (bosses, candidates) = BossNames.Resolve(p.Name, area.Bosses_MonsterVarietiesKeys ?? [], monsters.Value);
                    return new { name = p.Name, x = room.X * 23 + x, y = room.Y * 23 + y, room = room.Room.Path, bosses, bossesAreCandidates = candidates }; }))
                .DistinctBy(p => (p.name, p.x, p.y, p.room)).ToArray();
            var tiles = new List<object>();
            var used = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int y = 0; y < grid.Height; y++) for (int x = 0; x < grid.Width; x++)
                if (grid[x, y] is { } t)
                {
                    tiles.Add(new { x, y, path = t.Tile.Path, orientation = t.Orientation, height = t.HeightOffset, ox = t.OffsetX, oy = t.OffsetY });
                    if (t.IsOrigin) used[t.Tile.Path] = used.GetValueOrDefault(t.Tile.Path) + 1;
                }
            var catalog = new HashSet<string>(used.Keys, StringComparer.OrdinalIgnoreCase);
            foreach (var record in result.Records)
                if (record.Master.TryGetValue("TileSet", out var path))
                    foreach (var tile in files.TileList(TerrainText.Resolve(record.MasterPath, path))) catalog.Add(tile.Path);
            return Results.Ok(new {
                area = area.Name, id = area.Id, seed, requestedSeed, level = info.Level, width = grid.Width, height = grid.Height,
                tiles, walkWidth = walk.Width, walkHeight = walk.Height, walk = Convert.ToBase64String(walk.Walk),
                catalog = catalog.Order().Select(p => new { path = p, count = used.GetValueOrDefault(p) }), objects, points,
                exits = exits.Exits.Select(e => new { e.Object.X, e.Object.Y, kind = e.Kind.ToString(), e.Number, e.Enabled, e.Label, targetX = e.Target?.X, targetY = e.Target?.Y, e.Why,
                    destination = e.Kind == ExitKind.Entrance ? Connection(e.Number)?.Name : null,
                    destinationId = e.Kind == ExitKind.Entrance ? Connection(e.Number)?.Id : null }),
                entrances = exits.Entrances.Select(e => new { e.Number, e.Enabled, e.X, e.Y, destination = Connection(e.Number)?.Name, destinationId = Connection(e.Number)?.Id }),
                warnings = warnings.Distinct().ToArray()
            });
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or NotSupportedException or InvalidDataException or FileNotFoundException)
        { return Results.BadRequest(new { error = e.Message }); }
    }
});
app.MapGet("/", () => Results.File(Path.Combine(web, "index.html"), "text/html; charset=utf-8"));
app.MapGet("/app.js", () => Results.File(Path.Combine(web, "app.js"), "text/javascript; charset=utf-8"));
app.MapGet("/style.css", () => Results.File(Path.Combine(web, "style.css"), "text/css; charset=utf-8"));
app.Run();

sealed record Area(string Id, string Name, int AreaLevel, int[] TopologiesKeys, int?[] Connections_WorldAreasKeys,
    int[]? Bosses_MonsterVarietiesKeys = null, bool IsTown = false, bool IsHideout = false);
sealed record Monster(string Id, string Name);

static class BossNames
{
    static readonly Regex Point = new(@"^(?:force:)?mapboss(?:([1-9][0-9]*(?:&[1-9][0-9]*)*)|pack)?(?:WG)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    public static (Monster[] Bosses, bool Candidates) Resolve(string point, IReadOnlyList<int> areaBosses, IReadOnlyList<Monster> monsters)
    {
        var match = Point.Match(point);
        if (!match.Success) return ([], false);
        bool candidates = !match.Groups[1].Success;
        var indices = candidates ? Enumerable.Range(0, areaBosses.Count) : match.Groups[1].Value.Split('&')
            .Select(s => int.TryParse(s, out int index) ? index - 1 : -1);
        return (indices.Where(i => i >= 0 && i < areaBosses.Count).Select(i => areaBosses[i])
            .Where(i => i >= 0 && i < monsters.Count).Distinct().Select(i => monsters[i]).ToArray(), candidates);
    }
}
