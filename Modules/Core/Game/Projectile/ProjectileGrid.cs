using System.Collections.Concurrent;
using System.Text.Json;


/// <summary>
/// What stops projectiles in an area: the second grid of the client's walkability provider (scripts/walkmaps,
/// data/walkability/&lt;area&gt;_seed&lt;seed&gt;.projectile.bin, one byte per cell, sized like the walk map's .json). 0 blocks
/// (walls, doodads); 1..5 let projectiles through, including water and other cells the walk map blocks
/// (Agents/docs/protocol/11-skills.md §10).
/// </summary>
public static class ProjectileGrid
{
    const string Folder = "data/walkability";

    sealed record Grid(int W, int H, byte[] Cells);

    static readonly ConcurrentDictionary<string, Grid?> Loaded = new();

    /// <summary>Whether a cell stops projectiles (cells off the grid do), or null without a capture or a level the
    /// Gateway generates itself (ServerLevels).</summary>
    public static bool? Blocks(string areaId, uint seed, int x, int y)
    {
        if (Loaded.GetOrAdd($"{areaId}_seed{seed}", Load) is { } g) return x < 0 || y < 0 || x >= g.W || y >= g.H || g.Cells[y * g.W + x] == 0;
        if (ServerLevels.For(areaId, seed) is not { } level) return null;
        return x < 0 || y < 0 || x >= level.Width || y >= level.Height || level.Projectile[y * level.Width + x] == 0;
    }

    /// <summary>An area's cells, looked up once. <see cref="Blocks(string, uint, int, int)"/> finds the area by its name
    /// for every cell asked, and a flight asks for each cell it crosses at every step of the simulation.</summary>
    public sealed class Area
    {
        readonly int w, h;
        readonly byte[] cells;
        internal Area(int w, int h, byte[] cells) => (this.w, this.h, this.cells) = (w, h, cells);
        /// <summary>Whether the cell stops projectiles (cells off the grid do).</summary>
        public bool Blocks(int x, int y) => x < 0 || y < 0 || x >= w || y >= h || cells[y * w + x] == 0;
    }

    /// <summary>The area's cells as <see cref="Blocks(string, uint, int, int)"/> reads them (the capture, else the level
    /// the Gateway generated), or null when it has neither.</summary>
    public static Area? Of(string areaId, uint seed)
    {
        if (Loaded.GetOrAdd($"{areaId}_seed{seed}", Load) is { } g) return new(g.W, g.H, g.Cells);
        return ServerLevels.For(areaId, seed) is { } level ? new(level.Width, level.Height, level.Projectile) : null;
    }

    static Grid? Load(string stem)
    {
        string bin = Path.Combine(Folder, stem + ".projectile.bin"), meta = Path.Combine(Folder, stem + ".json");
        if (!File.Exists(bin) || !File.Exists(meta)) return null;
        using var d = JsonDocument.Parse(File.ReadAllBytes(meta));
        int w = d.RootElement.GetProperty("w").GetInt32(), h = d.RootElement.GetProperty("h").GetInt32();
        byte[] cells = File.ReadAllBytes(bin);
        if (cells.Length != w * h) return null;
        Console.WriteLine($"[gateway] projectile grid {stem}: {w}x{h}, {cells.Count(c => c == 0)} blocking cells");
        return new Grid(w, h, cells);
    }
}
