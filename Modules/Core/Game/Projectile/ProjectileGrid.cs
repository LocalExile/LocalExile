using System.Collections.Concurrent;
using System.Text.Json;


/// <summary>
/// What stops projectiles in an area: the second grid of the client's walkability provider (scripts/walkmaps,
/// data/walkability/&lt;area&gt;_seed&lt;seed&gt;.projectile.bin, one byte per cell, sized like the walk map's .json). 0 blocks
/// (walls, doodads); 1..5 let projectiles through, including water and other cells the walk map blocks
/// (Agents/docs/protocol/11-skills.md §10).
/// </summary>
static class ProjectileGrid
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
