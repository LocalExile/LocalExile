using System.Collections.Concurrent;
using System.Text.Json;


/// <summary>
/// The terrain height of every cell of an area, captured from the client by scripts/walkmaps
/// (data/walkability/&lt;area&gt;_seed&lt;seed&gt;.heights.bin: int16 per cell, row by row, with .heights.json {w, h}). The
/// client's height of a cell is 0x141d88190(terrain, cell), in steps of 7.8125 world units: the same unit as the
/// height u32 of S→C 0x1ba and Positioned's fixed height (Agents/docs/protocol/11-skills.md §10).
/// </summary>
public static class TerrainHeights
{
    const string Folder = "data/walkability";

    /// <summary>A cell of a tile the recorder could not read.</summary>
    const short Unknown = short.MinValue;

    sealed record Grid(int W, int H, short[] Cells);

    static readonly ConcurrentDictionary<string, Grid?> Loaded = new();

    /// <summary>The height (steps of 7.8125) of a cell, or null without a capture for the area and seed or a level the
    /// Gateway generates itself (ServerLevels).</summary>
    public static int? At(string areaId, uint seed, int x, int y)
    {
        var g = Of(areaId, seed);
        if (g is null || x < 0 || y < 0 || x >= g.W || y >= g.H) return null;
        short v = g.Cells[y * g.W + x];
        return v == Unknown ? null : v;
    }

    /// <summary>An area's heights, looked up once (<see cref="At"/> finds the area by its name for every cell asked). A
    /// reader that keeps one asks again when <see cref="Forgotten"/> has moved.</summary>
    public sealed class Area
    {
        readonly int w, h;
        readonly short[] cells;
        internal Area(int w, int h, short[] cells) => (this.w, this.h, this.cells) = (w, h, cells);
        /// <summary>The height (steps of 7.8125) of a cell, or null outside the grid or where it is not known.</summary>
        public int? At(int x, int y)
        {
            if (x < 0 || y < 0 || x >= w || y >= h) return null;
            short v = cells[y * w + x];
            return v == Unknown ? null : v;
        }
    }

    /// <summary>The area's heights as <see cref="At"/> reads them, or null when it has neither a capture nor a level.</summary>
    public static Area? AreaOf(string areaId, uint seed) => Of(areaId, seed) is { } g ? new(g.W, g.H, g.Cells) : null;

    /// <summary>How many times an area was forgotten (<see cref="Forget"/>): what was looked up before may be stale.</summary>
    public static int Forgotten => Volatile.Read(ref forgotten);
    static int forgotten;

    /// <summary>The grid's size in cells, or null without a capture or a generated level.</summary>
    public static (int W, int H)? Size(string areaId, uint seed) => Of(areaId, seed) is { } g ? (g.W, g.H) : null;

    // Only captures are kept here: a generated level stays in ServerLevels' own bounded cache.
    static Grid? Of(string areaId, uint seed) =>
        Loaded.GetOrAdd($"{areaId}_seed{seed}", Load)
        ?? (ServerLevels.For(areaId, seed) is { } level ? new Grid(level.Width, level.Height, level.Heights) : null);

    static Grid? Load(string stem)
    {
        string bin = Path.Combine(Folder, stem + ".heights.bin"), meta = Path.Combine(Folder, stem + ".heights.json");
        if (!File.Exists(bin) || !File.Exists(meta)) return null;
        using var d = JsonDocument.Parse(File.ReadAllBytes(meta));
        int w = d.RootElement.GetProperty("w").GetInt32(), h = d.RootElement.GetProperty("h").GetInt32();
        byte[] raw = File.ReadAllBytes(bin);
        if (raw.Length != w * h * 2) return null;
        var cells = new short[w * h];
        Buffer.BlockCopy(raw, 0, cells, 0, raw.Length);
        var known = cells.Where(c => c != Unknown).ToArray();
        Console.WriteLine($"[gateway] terrain heights {stem}: {w}x{h}, {known.Length} known cells" +
            (known.Length > 0 ? $", {known.Min()}..{known.Max()}" : ""));
        return new Grid(w, h, cells);
    }

    /// <summary>Forgets a cached area (a capture written after the first lookup is then read on the next one).</summary>
    public static void Forget(string areaId, uint seed)
    {
        Loaded.TryRemove($"{areaId}_seed{seed}", out _);
        Interlocked.Increment(ref forgotten);
    }
}
