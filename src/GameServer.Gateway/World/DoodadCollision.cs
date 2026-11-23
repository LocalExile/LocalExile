using System.Text.Json;



/// <summary>The obstacle cells the client's own doodads put in an area's walk grid (an .ao's <c>Hull</c>, stamped by
/// <c>0x141d8ad70</c>), baked ahead of time for the areas whose floor and buildings only exist as client doodads.
///
/// <para>Kingsmarch is such an area: its room's 444 doodads are typed <c>DecoUpgrade*</c>, whose .ot inherits
/// <c>BaseEvents { server_only = true }</c>, so the client builds none of them from the server's side and the walk grid it
/// generates 鈥?the one a capture gives this port 鈥?has no wall, wharf or hut in it. Nothing stops a player there: the
/// client's own collision test only sees the objects it built. Stamping the hulls into the captured grid makes the grid
/// itself refuse those cells, which every walker on this port already honours.</para>
///
/// <para>The data is <c>data/doodad-collision/&lt;area&gt;.json</c>: <c>{"note": 鈥? "objects": [{"i", "model", "runs"}]}</c>,
/// each <c>run</c> a <c>[y, x0, x1)</c> span of cells in the area's grid, produced by
/// <c>scripts/dev/kingsmarch_collision.py</c>.</para>
/// </summary>
static class DoodadCollision
{
    const string Folder = "data/doodad-collision";
    // Declared before Areas: Load() fills it, and a static field initialised later in the class is still null then.
    static readonly Dictionary<string, DateTime> Stamps = [];
    static readonly Dictionary<string, int[][]> Areas = Load();

    /// <summary>The cells an area's doodads block, or an empty array. A file rewritten since it was read is reloaded, so the
    /// baked data can be corrected without restarting the Gateway.</summary>
    public static int[][] Runs(string area)
    {
        string file = Path.Combine(Directory.GetCurrentDirectory(), Folder, area + ".json");
        if (File.Exists(file) && File.GetLastWriteTimeUtc(file) is var written
            && (!Stamps.TryGetValue(area, out var read) || read != written))
        {
            Stamps[area] = written;
            if (Read(file) is { } runs) Areas[area] = runs;
        }
        return Areas.TryGetValue(area, out var found) ? found : [];
    }

    /// <summary>Marks the run cells un-walkable. Only the hull cells themselves are stamped: the walk map counts a cell as
    /// walkable at value 2 and above, so a distance falloff would turn the ring next to every barrel into value 1, i.e. a
    /// second blocked cell. Live 2026-11-21 that ring closed gaps between the town's crates and pinned the player.</summary>
    public static void Stamp(string area, byte[] cells, int width, int height)
    {
        var runs = Runs(area);
        if (runs.Length == 0 || width <= 0 || height <= 0) return;
        int blocked = 0;
        foreach (var run in runs)
        {
            int y = run[0];
            if ((uint)y >= (uint)height) continue;
            for (int x = Math.Max(0, run[1]); x < Math.Min(width, run[2]); x++)
            {
                int i = y * width + x;
                if (cells[i] == 0) continue;
                cells[i] = 0;
                blocked++;
            }
        }
        if (blocked > 0) Console.WriteLine($"[gateway] doodad collision: {area} blocked {blocked} cell(s) of {width}x{height}");
    }

    static Dictionary<string, int[][]> Load()
    {
        var areas = new Dictionary<string, int[][]>(StringComparer.Ordinal);
        string folder = Path.Combine(Directory.GetCurrentDirectory(), Folder);
        if (!Directory.Exists(folder)) return areas;
        foreach (string file in Directory.EnumerateFiles(folder, "*.json"))
            if (Read(file) is { } runs)
            {
                areas[Path.GetFileNameWithoutExtension(file)] = runs;
                Stamps[Path.GetFileNameWithoutExtension(file)] = File.GetLastWriteTimeUtc(file);
            }
        if (areas.Count > 0)
            Console.WriteLine($"[gateway] doodad collision: {areas.Count} area(s): {string.Join(", ", areas.Keys)}");
        return areas;
    }

    static int[][]? Read(string file)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            var runs = new List<int[]>();
            foreach (var o in doc.RootElement.GetProperty("objects").EnumerateArray())
                foreach (var run in o.GetProperty("runs").EnumerateArray())
                    runs.Add([run[0].GetInt32(), run[1].GetInt32(), run[2].GetInt32()]);
            return [.. runs];
        }
        catch (Exception e) when (e is IOException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            Console.WriteLine($"[gateway] doodad collision: {file} is unusable: {e.Message}");
            return null;
        }
    }
}
