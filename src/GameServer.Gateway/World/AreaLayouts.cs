using System.Text.Json;
using GameServer.Core.Protocol.Instance;
using SaveFiles = GameServer.Core.Saves.SaveFiles;

/// <summary>What the Gateway knows about an area's layout: the terrain grid (for movement estimates), where the player
/// appears and the area transitions. Towns are fixed; other areas are generated from the seed, so their walkability only exists as
/// a dump of the client's walkability grid per (area, seed).</summary>
public sealed record AreaLayout(ushort Hash, string Id, int GridW, int GridH, int SpawnX, int SpawnY,
    Func<int, int, bool>? Walkable, (uint Id, AreaTransitionObject Transition)[] AreaTransitions)
{
    /// <summary>The layout's own spawn: an arrival's <c>with { SpawnX = … }</c> copy keeps it (the town portal's anchor).</summary>
    public (int X, int Y) Anchor { get; init; } = (SpawnX, SpawnY);
    public float SpawnRotation { get; init; }
    /// <summary>The area's waypoint (WaypointCatalog), if it has one and its place is known.</summary>
    public WaypointObject? Waypoint { get; init; }
    /// <summary>The area transitions' effects (<see cref="AreaTransitionCatalog.Effects"/>).</summary>
    public (uint Id, TransitionEffect Effect)[] Effects { get; init; } = [];
    public Func<int, int, int, bool>? Clearance { get; init; }

    /// <summary>The client's projectile grid (its walk layer 1: 0 stops projectiles, 1 … 5 a distance from what does;
    /// 0 outside the map), from the capture's <c>.projectile.bin</c> or the generated level: what a walk-up's line to
    /// its target is tested on (<see cref="GameServer.Core.World.ClientPathfinder.InReach"/>). Null without one.</summary>
    public Func<int, int, int>? Sight { get; init; }

    /// <summary>A generated layout's named entrance point for a player arriving from an area
    /// (<see cref="AreaTransitionCatalog.EntrancePoint"/>), preferring the room's authored entrance slot.</summary>
    public Func<ushort, int?, (int X, int Y, bool NamedPoint)?>? EntranceFrom { get; init; }

    /// <summary>Every cell's ground height, row by row (GridW × GridH), when the Gateway generated the layout itself
    /// (<see cref="ServerLevels"/>); null for a capture.</summary>
    public short[]? CellHeights { get; init; }

    /// <summary>The level the Gateway generated this layout from (<see cref="ServerLevels"/>); holding it keeps it
    /// available to the projectile and height lookups for as long as a connection plays the layout.</summary>
    public ServerLevels.Level? Level { get; init; }

    /// <summary>Whether the Gateway generated this layout from the seed (<see cref="ServerLevels"/>).</summary>
    public bool ServerGenerated => CellHeights is not null;

    /// <summary>The ground height at a cell in world units (the client's 0x141d88270 at the cell's centre: the cell's
    /// height × 7.8125), or null without heights or outside the grid.</summary>
    public float? GroundHeight(int x, int y) =>
        CellHeights is { } h && x >= 0 && y >= 0 && x < GridW && y < GridH ? h[y * GridW + x] * 7.8125f : null;
    public bool WalkableFor(int x, int y, int size) => Clearance?.Invoke(x, y, size)
        ?? (Walkable?.Invoke(x, y) != false);

    public string Describe() =>
        $"area {Id} (0x{Hash:x4}) grid {GridW}x{GridH} spawn ({SpawnX}, {SpawnY}), {(Walkable is null ? "no walkability" : "walkability")}{(Sight is null ? "" : ", projectile grid")}, {AreaTransitions.Length} area transition(s)" +
        (Waypoint is { } w ? $", waypoint at ({w.GridX}, {w.GridY})" : "");

    // One-way entries have no exit back to stand next to: the arrival is the layout's named entrance point
    // (08-area-transitions.md §3). Lioneye's Watch from The Twilight Strand: "entrance1" (disabled in beachtown.dgr).
    static readonly Dictionary<(ushort Area, ushort From), (int X, int Y)> OneWayArrivals = new()
    {
        [(AreaLayouts.LioneyesWatch, 0xf00a /* 1_1_1 The Twilight Strand */)] = (123, 308),
    };

    /// <summary>Where a player entering this area appears: the room's authored entrance point for
    /// <paramref name="cameFrom"/> when available, else next to the exit leading back to <paramref name="cameFrom"/>,
    /// else the spawn (logins, Resurrect in Town, no exit back).</summary>
    public (int X, int Y) Arrival(ushort? cameFrom, int? entrance = null)
    {
        if (cameFrom is not { } from) return (SpawnX, SpawnY);
        if (OneWayArrivals.TryGetValue((Hash, from), out var point)) return point;
        if (EntranceFrom?.Invoke(from, entrance) is { } authored)
        {
            var pt = (authored.X, authored.Y);
            bool onTransition = AreaTransitions.Any(t => t.Transition.DestinationHash16 == from && t.Transition.TeleportTo is null
                && Math.Abs(t.Transition.GridX - pt.X) <= 4 && Math.Abs(t.Transition.GridY - pt.Y) <= 4);
            if (authored.NamedPoint && !onTransition && (Walkable is null || Walkable(pt.X, pt.Y)))
                return pt;
            return Near(pt);
        }
        foreach (var (_, t) in AreaTransitions)
            if (t.DestinationHash16 == from && t.TeleportTo is null
                && (entrance is null || t.Entrance == entrance)) return Near((t.GridX, t.GridY));
        return (SpawnX, SpawnY);
    }

    /// <summary>Where "Resurrect at Checkpoint" revives: next to the area's waypoint once the character has activated it
    /// (waypoints are checkpoints), else where the player entered the area (the spawn, set to the arrival on entry).</summary>
    public (int X, int Y) Checkpoint(bool waypointActivated) =>
        waypointActivated && Waypoint is { } w ? Near((w.GridX, w.GridY)) : (SpawnX, SpawnY);

    /// <summary>A walkable cell a few cells from (x, y), nearest first; (x + 8, y) without a walk map.</summary>
    public (int X, int Y) Near((int X, int Y) at)
    {
        if (Walkable is not { } walkable) return (at.X + 8, at.Y);
        for (int r = 6; r <= 20; r++)
            for (int dy = -r; dy <= r; dy++)
                foreach (int dx in new[] { -r, r })
                {
                    if (walkable(at.X + dx, at.Y + dy)) return (at.X + dx, at.Y + dy);
                    if (walkable(at.X + dy, at.Y + dx)) return (at.X + dy, at.Y + dx);
                }
        return at;
    }
}

public static class AreaLayouts
{
    public const ushort LioneyesWatch = 0x235c, TheCoast = 0x7ad2;

    /// <summary>The area each transfer's destination ticket left through an exit; the new connection arrives next to the
    /// exit back (<see cref="AreaLayout.Arrival"/>). In memory: a Gateway restart drops the client to the login anyway.</summary>
    public static readonly System.Collections.Concurrent.ConcurrentDictionary<uint, ushort> CameFrom = new();
    public static readonly System.Collections.Concurrent.ConcurrentDictionary<uint, int> ArrivalEntrances = new();

    // Lioneye's Watch is one room, Metadata/Terrain/Act1/Town/Rooms/beach_town_unique_normal.arm (18 x 30 tiles), placed
    // R270 by Graphs/beachtown.dgr ("default" "entrance2" "entrance1disabled"). Spawn (197, 261): the walkable cell farthest
    // from any edge (Agents/docs/protocol/03 §7).
    // Area transitions come from AreaTransitionCatalog (the atlas, or the client's generated layout captured by
    // scripts/walkmaps). Without the atlas the town still gets its gate to The Coast: the town tile's .tmd record
    // beachencampment_v01_01_area_transition2 as the client places it (grid (388, 231), 3π/2, z −351.04) with its
    // virtual .ao (the arch highlighted on hover). Agents/docs/protocol/08-area-transitions.md §5.
    static readonly (uint Id, AreaTransitionObject Transition)[] TownFallback =
        [(AreaTransitionCatalog.FirstId, new AreaTransitionObject(GridX: 388, GridY: 231, DestinationHash16: TheCoast,
            Model: "Metadata/Terrain/Beach/Town_v2/beachencampment_v01_01_area_transition2.ao",
            Rotation: 3 * MathF.PI / 2, Height: -351.04f) { InTown = true })];

    static readonly Dictionary<ushort, string> Ids = new() { [LioneyesWatch] = "1_1_town", [TheCoast] = "1_1_2" };

    const string Folder = "data/walkability";

    internal static string IdOf(ushort hash) =>
        Ids.GetValueOrDefault(hash) ?? AreaTransitionCatalog.IdOf(hash) ?? HideoutCatalog.ByArea(hash)?.AreaId ?? $"0x{hash:x4}";

    /// <summary>The seed the instance's layout is generated and played at (<see cref="ServerLevels.PlayableSeed"/>).</summary>
    public static uint PlayableSeed(ushort hash, uint seed) => PlayableSeed(hash, seed, 0);

    /// <inheritdoc cref="PlayableSeed(ushort, uint)"/>
    public static uint PlayableSeed(ushort hash, uint seed, int areaLevel) => ServerLevels.PlayableSeed(IdOf(hash), seed, areaLevel);

    /// <inheritdoc cref="PlayableSeed(ushort, uint)"/>
    public static uint PlayableSeed(ushort hash, uint seed, int areaLevel, IReadOnlyDictionary<uint, int>? stats) =>
        ServerLevels.PlayableSeed(IdOf(hash), seed, areaLevel, stats);

    /// <inheritdoc cref="ServerLevels.PlayableSeed(string, uint, int, IReadOnlyDictionary{uint, int}?, int)"/>
    public static uint PlayableSeed(ushort hash, uint seed, int areaLevel, IReadOnlyDictionary<uint, int>? stats, int category) =>
        ServerLevels.PlayableSeed(IdOf(hash), seed, areaLevel, stats, category);

    /// <inheritdoc cref="ServerLevels.PlayableSeed(string, uint, int, IReadOnlyDictionary{uint, int}?, int, IReadOnlyList{ValueTuple{ushort, byte[]}}?)"/>
    public static uint PlayableSeed(ushort hash, uint seed, int areaLevel, IReadOnlyDictionary<uint, int>? stats, int category,
        IReadOnlyList<(ushort Plugin, byte[] State)>? pluginStates) =>
        ServerLevels.PlayableSeed(IdOf(hash), seed, areaLevel, stats, category, pluginStates);

    public static AreaLayout For(ushort hash, uint seed)
    {
        var hideout = HideoutCatalog.ByArea(hash);
        string id = IdOf(hash);
        // A hideout's waypoint is one of its decoration's doodads (21-hideouts.md §2), not a tile object.
        var layout = WithoutWaypoint(hash, id, seed);
        var waypoint = hideout is null ? WaypointCatalog.For(hash, id, seed) : HideoutCatalog.Waypoint(hideout);
        // League hubs whose tiles place no waypoint (the Rogue Harbour, the Monastery of the Keepers): one by the arrival,
        // so the player can travel back (the Waypoint type's own model).
        if (waypoint is null && id is WaypointCatalog.HeistHub or WaypointCatalog.ChayulaHub) waypoint = new WaypointObject(layout.SpawnX, layout.SpawnY - 12, Active: 1);
        return layout with { Waypoint = waypoint };
    }

    static AreaLayout WithoutWaypoint(ushort hash, string id, uint seed)
    {
        // An area the port reproduces is generated here from the seed; any other needs the client's capture.
        if (ServerLevels.For(id, seed) is { } generated)
            return WithCells(hash, id, seed, generated.Walk, generated.Width, generated.Height,
                SightOf(generated.Projectile, generated.Width, generated.Height), generated.Start, generated.FeatureAreas) with
            {
                CellHeights = generated.Heights,
                EntranceFrom = (from, entrance) => AreaTransitionCatalog.EntrancePoint(hash, generated.Entrances, from, entrance),
                Level = generated,
            };
        byte[]? cells = LoadCells(id, seed, out int w, out int h);
        return WithCells(hash, id, seed, cells, w, h, LoadSight(id, seed));
    }

    static AreaLayout WithCells(ushort hash, string id, uint seed, byte[]? cells, int w, int h, Func<int, int, int>? sight,
        (int X, int Y, float Angle)? start = null, IReadOnlyList<(int X0, int Y0, int X1, int Y1)>? featureAreas = null)
    {
        var layout = WithTransitions(hash, id, seed, cells, w, h, start, featureAreas);
        return layout with { Effects = AreaTransitionCatalog.Effects(id, seed), Sight = sight };
    }

    /// <param name="start">The start a terrain plugin marks (<see cref="ServerLevels.Level.Start"/>): the spawn, without
    /// the room fit.</param>
    static AreaLayout WithTransitions(ushort hash, string id, uint seed, byte[]? cells, int w, int h, (int X, int Y, float Angle)? start = null,
        IReadOnlyList<(int X0, int Y0, int X1, int Y1)>? featureAreas = null)
    {
        var notes = new List<string>();
        var transitions = AreaTransitionCatalog.For(hash, id, seed, notes);
        foreach (var n in notes) Console.WriteLine($"[gateway] area {id} seed {seed}: {n}");
        if (hash == LioneyesWatch)
        {
            if (transitions.Length == 0) transitions = TownFallback;
            // Older dumps were saved as data/walkability/1_1_town.bin without a .json.
            if (cells is null && File.Exists($"{Folder}/1_1_town.bin")) { cells = File.ReadAllBytes($"{Folder}/1_1_town.bin"); (w, h) = (690, 414); }
            bool fits = cells is not null && cells.Length == 690 * 414 && (w, h) == (690, 414);
            return new AreaLayout(hash, id, 690, 414, 197, 261, fits ? Walkable(cells!, w, h) : null, transitions)
            { Clearance = fits ? Clearance(cells!, w, h) : null };
        }
        if (cells is not null)
        {
            // The area's start point (a room's "default", e.g. The Twilight Strand's beach), else the deepest cell.
            if (start is null && AreaPopulation.LoadPoints(id, seed, []).FirstOrDefault(p => p.Name == "default") is { } point)
                start = (point.X, point.Y, point.Angle);
            var walkable = Walkable(cells, w, h);
            (int X, int Y) spawn;
            if (start is { } marker)
            {
                spawn = (marker.X, marker.Y);
                if (!walkable(spawn.X, spawn.Y)) spawn = NearestWalkableSpawn(cells, w, h, spawn);
            }
            else spawn = DeepestCell(cells, w, h, featureAreas);
            return new AreaLayout(hash, id, w, h, spawn.X, spawn.Y, walkable, transitions)
                { Clearance = Clearance(cells, w, h), SpawnRotation = start?.Angle ?? 0 };
        }
        // No walk map yet: a position inside any grid; the client may place the player on unwalkable ground.
        return new AreaLayout(hash, id, 0, 0, 197, 261, null, transitions);
    }

    /// <summary>Dev: the walk-map recorder runs beside the Gateway (live.ps1 -Walkmaps sets Game_WALKMAPS=1). Only then does
    /// the Gateway write anything for an area load (<see cref="Announce"/>) and run capture tours (/tour, CaptureTour,
    /// TourRecords); by default it records nothing.</summary>
    public static readonly bool Recording = Environment.GetEnvironmentVariable("Game_WALKMAPS") == "1";

    /// <summary>Tells scripts/walkmaps which area the next generated grid belongs to (data/walkability/current.json),
    /// when it runs (<see cref="Recording"/>).</summary>
    public static void Announce(AreaLayout layout, uint seed)
    {
        if (!Recording) return;
        // A read-only data folder (a container image) has no recorder to tell; stop instead of waiting on every entry.
        if (!folderWritable.Value) return;
        try
        {
            string json = JsonSerializer.Serialize(new
            {
                id = layout.Id, seed, hash = layout.Hash, written = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                nonce = Guid.NewGuid().ToString("N"),
                // The terrain plugin states of the load, for the trace: levelgen generates with them.
                pluginStates = ServerLevels.PluginStatesOf(layout.Id, seed)?.ToDictionary(p => p.Key.ToString(), p => Convert.ToHexString(p.Value)),
            });
            SaveFiles.Write($"{Folder}/current.json", json);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    // A fresh file name nobody else holds, so a denial means the folder itself is not writable.
    static readonly Lazy<bool> folderWritable = new(() =>
    {
        try
        {
            Directory.CreateDirectory(Folder);
            using (new FileStream(Path.Combine(Folder, $"probe.{Guid.NewGuid():N}.tmp"), FileMode.CreateNew,
                FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose)) { }
            return true;
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException)
        {
            Console.WriteLine($"[gateway] {Folder} is not writable ({e.Message}): the walk-map recorder is not told about areas");
            return false;
        }
    });

    /// <summary>When the client reports the area generated (0x7d): the walk map scripts/walkmaps saved meanwhile, if
    /// any. Waits up to 2 s for the file while the recorder runs (<see cref="Recording"/>); without it nothing writes
    /// the file, so it looks once and does not wait: the player is already past the loading screen, and the wait held
    /// every arrival in an area without a walk map for 2 s (2026-11-17).</summary>
    public static async Task<AreaLayout?> WaitFor(AreaLayout layout, uint seed)
    {
        string bin = $"{Folder}/{layout.Id}_seed{seed}.bin";
        for (int i = 0; i < 20; i++)
        {
            if (File.Exists(bin) && File.Exists(Path.ChangeExtension(bin, ".json")))
            {
                var better = For(layout.Hash, seed);
                return better.Walkable is null ? null : better;
            }
            if (!Recording) return null;
            await Task.Delay(100);
        }
        return null;
    }

    /// <summary>When the client reports the area generated (0x7d) and the layout has no area transitions yet: the ones
    /// scripts/walkmaps captured meanwhile, if any. Waits up to 1 s for the file while the recorder runs
    /// (<see cref="Recording"/>); without it it looks once and does not wait. A hideout has no area transitions, so the
    /// wait held every arrival in one for a second (2026-11-17).</summary>
    public static async Task<AreaLayout?> WaitForTransitions(AreaLayout layout, uint seed)
    {
        for (int i = 0; i < 10; i++)
        {
            if (AreaTransitionCatalog.Captured(layout.Id, seed))
            {
                if (Recording) await Task.Delay(200);   // the capture is rewritten per object; let the last ones land
                var better = For(layout.Hash, seed);
                return better.AreaTransitions.Length > 0 || better.Waypoint is not null ? better : null;
            }
            if (!Recording) return null;
            await Task.Delay(100);
        }
        return null;
    }

    static byte[]? LoadCells(string id, uint seed, out int w, out int h) =>
        GameServer.Core.World.WalkMapCapture.Load(id, seed, out w, out h);

    /// <summary>The capture's projectile grid beside its walk map (scripts/walkmaps: <c>&lt;id&gt;_seed&lt;seed&gt;.projectile.bin</c>,
    /// one byte per cell, sized by the walk map's .json), or null without one.</summary>
    static Func<int, int, int>? LoadSight(string id, uint seed)
    {
        string bin = $"{Folder}/{id}_seed{seed}.projectile.bin", meta = $"{Folder}/{id}_seed{seed}.json";
        if (!File.Exists(bin) || !File.Exists(meta)) return null;
        try
        {
            var m = JsonDocument.Parse(File.ReadAllText(meta)).RootElement;
            int w = m.GetProperty("w").GetInt32(), h = m.GetProperty("h").GetInt32();
            byte[] cells = File.ReadAllBytes(bin);
            return cells.Length == w * h ? SightOf(cells, w, h) : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }

    // 1 byte per cell, 0 … 5; a cell off the grid reads 0, as the client's does (0x141d888f0).
    static Func<int, int, int> SightOf(byte[] cells, int w, int h) =>
        (x, y) => x >= 0 && y >= 0 && x < w && y < h ? cells[y * w + x] : 0;

    static Func<int, int, int, bool> Clearance(byte[] cells, int w, int h) =>
        (x, y, size) => x >= 0 && y >= 0 && x < w && y < h && cells[y * w + x] >= size + 1;

    // 1 byte per cell; the client walks on values >= 2 (Agents/docs/protocol/07 §5).
    static Func<int, int, bool> Walkable(byte[] cells, int w, int h) =>
        (x, y) => x >= 0 && y >= 0 && x < w && y < h && cells[y * w + x] >= 2;


    // A room's default marker can be farther from the floor than the walk-up search for waypoints and transitions.
    // Search the grid only for a blocked marker, preserving the marker as a fallback if the grid has no floor at all.
    static (int X, int Y) NearestWalkableSpawn(byte[] cells, int w, int h, (int X, int Y) marker)
    {
        var nearest = marker;
        long distance = long.MaxValue;
        for (int y = 0; y < h; y++)
        {
            long dy = (long)y - marker.Y;
            if (dy * dy >= distance) continue;
            for (int x = 0; x < w; x++)
            {
                if (cells[y * w + x] < 2) continue;
                long dx = (long)x - marker.X;
                long squared = dx * dx + dy * dy;
                if (squared >= distance) continue;
                distance = squared;
                nearest = (x, y);
            }
        }
        return nearest;
    }

    /// <summary>The walkable cell farthest from any blocked cell (multi-source BFS from blocked cells), preferring
    /// primary map cells outside <paramref name="featureAreas"/>.</summary>
    static (int X, int Y) DeepestCell(byte[] cells, int w, int h, IReadOnlyList<(int X0, int Y0, int X1, int Y1)>? featureAreas = null)
    {
        // Run for every connection to an area without a start point, a hideout's five million cells among them: plain
        // arrays (a cell is queued once, when its distance is set) instead of a Queue, a tuple loop and LINQ per cell.
        int n = w * h;
        var dist = new int[n];
        var queue = new int[n];
        int head = 0, tail = 0;
        for (int i = 0; i < cells.Length; i++)
            if (cells[i] < 2) queue[tail++] = i; else dist[i] = int.MaxValue;
        while (head < tail)
        {
            int i = queue[head++], x = i % w, next = dist[i] + 1;
            if (x + 1 < w && dist[i + 1] > next) { dist[i + 1] = next; queue[tail++] = i + 1; }
            if (x > 0 && dist[i - 1] > next) { dist[i - 1] = next; queue[tail++] = i - 1; }
            if (i + w < n && dist[i + w] > next) { dist[i + w] = next; queue[tail++] = i + w; }
            if (i >= w && dist[i - w] > next) { dist[i - w] = next; queue[tail++] = i - w; }
        }
        bool InFeature(int x, int y)
        {
            if (featureAreas is null || featureAreas.Count == 0) return false;
            foreach (var (x0, y0, x1, y1) in featureAreas)
                if (x >= x0 && x < x1 && y >= y0 && y < y1) return true;
            return false;
        }
        int best = -1, deepest = -1, fallback = -1, fallbackDeepest = -1;
        for (int i = 0; i < n; i++)
        {
            if (dist[i] == int.MaxValue) continue;
            if (InFeature(i % w, i / w))
            {
                if (dist[i] > fallbackDeepest) { fallbackDeepest = dist[i]; fallback = i; }
                continue;
            }
            if (dist[i] > deepest) { deepest = dist[i]; best = i; }
        }
        int chosen = best >= 0 ? best : fallback;
        if (chosen < 0) throw new InvalidOperationException("Sequence contains no elements");
        return (chosen % w, chosen / w);
    }

}
