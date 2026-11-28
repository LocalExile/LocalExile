using GameServer.Core.World;

namespace GameServer.Modules.Delirium;

/// <summary>The fog plugin samples the walk grid every half tile and labels adjoining occupied samples in row order.</summary>
public static class MirrorFogRegions
{
    public static AfflictionRuleset.FogRegion[] Build(AreaLayout layout, int mirrorX, int mirrorY)
    {
        if (layout.Walkable is not { } walkable || layout.GridW <= 0 || layout.GridH <= 0) return [];
        int width = 2 * (layout.GridW / 23) + 1;
        int height = 2 * (layout.GridH / 23) + 1;
        var occupied = new bool[width * height];
        static int Cell(int sample) => sample * 23 / 2;
        int Walk(int x, int y) => walkable(x, y) ? 2 : 0;

        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                var from = (Cell(x), Cell(y));
                if (!walkable(from.Item1, from.Item2)) continue;
                Link(x + 1, y);
                Link(x, y + 1);
                Link(x + 1, y + 1);
                Link(x + 1, y - 1);
                void Link(int nx, int ny)
                {
                    if ((uint)nx >= width || (uint)ny >= height) return;
                    if (ClientPathfinder.LineBelow(from, (Cell(nx), Cell(ny)), Walk, 2)) return;
                    occupied[y * width + x] = occupied[ny * width + nx] = true;
                }
            }

        var regions = new List<(AfflictionRuleset.FogRegion Region, int Samples)>();
        var pending = new Queue<int>();
        int mirrorRegion = -1;
        long nearestDistance = long.MaxValue;
        for (int start = 0; start < occupied.Length; start++)
        {
            if (!occupied[start]) continue;
            pending.Enqueue(start);
            occupied[start] = false;
            int minX = int.MaxValue, minY = int.MaxValue, maxX = 0, maxY = 0, originX = 0, originY = 0, samples = 0;
            long closest = long.MaxValue;
            while (pending.TryDequeue(out int index))
            {
                samples++;
                int x = index % width, y = index / width;
                int cx = Cell(x), cy = Cell(y);
                minX = Math.Min(minX, cx);
                minY = Math.Min(minY, cy);
                maxX = Math.Max(maxX, cx + 1);
                maxY = Math.Max(maxY, cy + 1);
                long dx = cx - mirrorX, dy = cy - mirrorY;
                long distance = dx * dx + dy * dy;
                if (distance < closest) (closest, originX, originY) = (distance, cx, cy);
                for (int ny = Math.Max(0, y - 1); ny <= Math.Min(height - 1, y + 1); ny++)
                    for (int nx = Math.Max(0, x - 1); nx <= Math.Min(width - 1, x + 1); nx++)
                    {
                        int next = ny * width + nx;
                        if (!occupied[next]) continue;
                        occupied[next] = false;
                        pending.Enqueue(next);
                    }
            }
            if (closest < nearestDistance) (nearestDistance, mirrorRegion) = (closest, regions.Count);
            regions.Add((new(minX, minY, maxX, maxY, originX, originY, MathF.Sqrt(closest)), samples));
        }
        if (mirrorRegion >= 0)
            regions[mirrorRegion] = (regions[mirrorRegion].Region with { OriginX = mirrorX, OriginY = mirrorY, DistanceOffset = 0 }, regions[mirrorRegion].Samples);

        if (regions.Count > byte.MaxValue)
            return [.. AfflictionRuleset.CompactRegions([.. regions.Select(r => r.Region)], i => regions[i].Samples)];

        regions.Sort(static (a, b) => a.Region.DistanceOffset.CompareTo(b.Region.DistanceOffset));
        return [.. regions.Select(r => r.Region)];
    }
}
