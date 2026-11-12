using System.Text.Json;
using GameServer.Core.Protocol.Instance;
using GameServer.Core;

/// <summary>The native sulphite resource curve and the map-owner scarab modifier, separate from player item quantity.</summary>
public static class MapSulphite
{
    public const string Vein = "Metadata/Terrain/Leagues/Delve/Objects/DelveMineralVein";
    public const string Chest = "Metadata/Terrain/Leagues/Delve/Objects/DelveMineralChest";
    public const string EnragedQuantity = "from_league_item_quantity_+%_permyriad";
    public const int FumesQuantity = 5000;
    // The native daemon supplies no radius in its exported skill rows. Keep the server's encounter radius explicit.
    public const int FumesRadius = 60;

    static readonly Lazy<Dictionary<int, int>> Resources = new(() =>
    {
        return ClientTables.Table("DelveResourcePerLevel").ToDictionary(r => r.Int("AreaLevel"), r => r.Int("Sulphite"));
    });

    public static bool Present(MapMods mods) => mods.Get("map_delve_league") > 0;
    public static bool Fumes(MapMods mods) => mods.Get("map_enraging_sulphite") > 0;

    public static int Yield(int level, MapMods mods, bool owner, int nearbyPlayers = 1)
    {
        int count = Math.Max(1, nearbyPlayers);
        decimal party = (100 + 50L * (count - 1)) / (100m * count);
        decimal bonus = owner ? Math.Max(0, 100L + mods.Get("map_owner_sulphite_gained_+%")) / 100m : 1;
        decimal mined = Math.Max(0, 100L + mods.Get("map_sulphite_mined_+%")) / 100m;
        decimal quantity = Math.Max(0, 100L + mods.Quantity) / 100m;
        return (int)Math.Clamp(decimal.Floor(Resources.Value.GetValueOrDefault(level) * quantity * party * bonus * mined), 0, int.MaxValue);
    }

    public static ushort Credit(int current, int gain, int capacity) =>
        (ushort)Math.Clamp((long)current + Math.Min(Math.Max(0L, gain), Math.Max(0L, capacity - (long)current)), 0, ushort.MaxValue);

    public static StateMachineData States(bool mined, bool fumes) =>
        new StateMachineData([0, 0, 0], 0, ["activated", "depleted", "attach_ring"])
            .Set("activated", mined ? 1 : 0).Set("depleted", mined ? 1 : 0).Set("attach_ring", fumes ? (mined ? 2 : 1) : 0);

    // Native read order: Positioned, InteractionAction, BaseEvents, Animated, MinimapIcon, StateMachine, Preload.
    // BaseEvents and Preload consume no bytes. This is not a Chest component or a Transitionable quest object.
    public static byte[] Blob(int x, int y, float? height, bool mined, bool fumes)
    {
        var w = new BlobWriter();
        w.U8(0).U8(0).I32(x).I32(y).F32(0).U16(0).U8((byte)(height is null ? 0 : 4)).U8(2);
        if (height is float z) w.I32((int)MathF.Round(z / 7.8125f)).F32(z);
        w.U8(0).U8(1).U8(0).U8(0).U8((byte)(mined ? 6 : 0));
        States(mined, fumes).Write(w);
        return w.U8(0).ToArray();
    }

    /// <summary>Three separated deposits on the entrance's connected walk component, with clearance for their models.</summary>
    public static (int X, int Y)[] Positions(AreaLayout layout, uint seed)
    {
        if (layout.Walkable is not { } walk || layout.GridW <= 0 || layout.GridH <= 0) return [];
        var origin = (X: layout.SpawnX, Y: layout.SpawnY);
        if (!walk(origin.X, origin.Y)) origin = layout.Near(origin);
        if (!walk(origin.X, origin.Y)) return [];
        var seen = new HashSet<(int, int)> { origin };
        var queue = new Queue<(int X, int Y)>(); queue.Enqueue(origin);
        var candidates = new List<(int X, int Y)>();
        while (queue.TryDequeue(out var at))
        {
            // Native clearance saturates at 5, so a radius >4 can never pass WalkableFor.
            if ((at.X + at.Y * 3 + seed) % 37 == 0 && Distance(at, origin) > 35 && layout.WalkableFor(at.X, at.Y, 4)
                && walk(at.X - 6, at.Y) && walk(at.X + 6, at.Y) && walk(at.X, at.Y - 6) && walk(at.X, at.Y + 6)) candidates.Add(at);
            foreach (var next in new[] { (X: at.X - 1, at.Y), (X: at.X + 1, at.Y), (at.X, Y: at.Y - 1), (at.X, Y: at.Y + 1) })
                if (next.X >= 0 && next.Y >= 0 && next.X < layout.GridW && next.Y < layout.GridH && walk(next.X, next.Y) && seen.Add(next)) queue.Enqueue(next);
        }
        var result = new List<(int X, int Y)>();
        // First findable without searching the whole map; the other two cover the rest of the component.
        if (candidates.Count > 0) result.Add(candidates.MinBy(p => Math.Abs(Distance(p, origin) - 95)));
        while (result.Count < 3 && candidates.Count > 0)
        {
            var next = candidates.MaxBy(p => result.Min(q => Distance(p, q)));
            if (result.Min(p => Distance(p, next)) < 25) break;
            result.Add(next);
        }
        return [.. result];
    }

    public static double Distance((int X, int Y) a, (int X, int Y) b) => Math.Sqrt((double)(a.X - b.X) * (a.X - b.X) + (double)(a.Y - b.Y) * (a.Y - b.Y));
}
