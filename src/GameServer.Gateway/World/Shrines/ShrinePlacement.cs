using GameServer.Data.Terrain.Generation;

/// <summary>
/// Where a generated map's shrines stand and which type each one is. The level generator's shrine rooms place
/// <c>Metadata/Shrines/Shrine</c> lines in their .arm rooms (<see cref="AreaObjects.RoomObjects"/>); a line on walkable ground
/// is a shrine, and its type comes from the area seed and the cell, so the same layout and seed always roll the same shrines
/// (a re-entered instance keeps its shrines). Types are drawn without repeating one the layout already has, from
/// <see cref="ShrineCatalog.Standard"/>: the basic shrines, so the apparition shrines (their scarab is not implemented)
/// and the lesser, greater, PVP, CTF, Royale and boss rows are not a map's random shrines.
/// </summary>
public static class ShrinePlacement
{
    /// <summary>A shrine to create: the room line's cell, angle and statue model, and the shrine type it rolls. The
    /// shrine's own .ot has no animated_object (object-types.json), so its statue is the room line's model: the
    /// tileset's <c>Metadata/Shrines/&lt;tileset&gt;_shrine.ao</c> (room-points.json, e.g. generic_shrine.ao).</summary>
    public sealed record Placed(int X, int Y, float Rotation, string Model, ShrineCatalog.ShrineDefinition Shrine);

    /// <summary>The shrine type's object type (the one the .arm rooms name), before and after its "lesser" variant.</summary>
    public const string Type = "Metadata/Shrines/Shrine", LesserType = "Metadata/Shrines/LesserShrine";

    /// <summary>The shrine type's object size (object-types.json): the walk-up range a click must be inside.</summary>
    public const int ObjectSize = 7;

    /// <summary>The shrine's own statue when a room line names none (a dev placement still has to draw).</summary>
    public const string DefaultModel = "Metadata/Shrines/generic_shrine.ao";

    /// <summary>How many areas in a hundred roll a shrine of their own where the level generator placed none: campaign
    /// zones have no shrine rooms at all, and a map without one is rolled the same way.</summary>
    public const int AreaChancePercent = 5;

    /// <summary>How far (cells) from the arrival a rolled shrine may stand: far enough not to be under the player's feet,
    /// near enough that a zone's shrine is found on the way through.</summary>
    public const int NearestCell = 24, FarthestCell = 72;

    /// <summary>How many cells the roll tries before it gives up on an area whose walkable ground offers none.</summary>
    const int Tries = 512;

    /// <summary>The room the statue needs: its cell and the cells around it walkable (no wall or object through it).</summary>
    const int StatueRoom = 3;

    /// <summary>
    /// One area in twenty carries a shrine the level generator did not place, for the campaign (whose zones have no shrine
    /// rooms) as much as for a map beyond its shrine rooms. The roll and the type come from the area's hash and seed, so the
    /// same instance rolls the same shrine on every entry; the cell is drawn on a ring
    /// <see cref="NearestCell"/>…<see cref="FarthestCell"/> cells from the arrival point where the statue has room. Towns and
    /// hideouts are the caller's to exclude, as is an area that already has a shrine where the player arrives.
    /// </summary>
    public static Placed? AreaShrine(ushort hash16, uint seed, Func<int, int, bool> walkable, (int X, int Y) arrival)
    {
        var standard = ShrineCatalog.Standard;
        if (standard.Count == 0) return null;
        uint roll = Mixed(seed, hash16);
        if (roll % 100u >= AreaChancePercent) return null;
        var shrine = standard[(int)(roll / 100u % (uint)standard.Count)];
        uint rnd = roll;
        for (int i = 0; i < Tries; i++)
        {
            rnd = Next(rnd);
            double angle = (rnd >> 8) % 360 * Math.PI / 180;
            rnd = Next(rnd);
            int distance = NearestCell + (int)((rnd >> 8) % (uint)(FarthestCell - NearestCell + 1));
            int x = arrival.X + (int)Math.Round(Math.Cos(angle) * distance);
            int y = arrival.Y + (int)Math.Round(Math.Sin(angle) * distance);
            if (Room(walkable, x, y)) return new(x, y, 0f, DefaultModel, shrine);
        }
        return null;
    }

    /// <summary>Whether a statue at this cell has its room: the cell and four around it walkable.</summary>
    static bool Room(Func<int, int, bool> walkable, int x, int y) =>
        walkable(x, y) && walkable(x + StatueRoom, y) && walkable(x - StatueRoom, y)
        && walkable(x, y + StatueRoom) && walkable(x, y - StatueRoom);

    /// <summary>The area's roll, 0 to 99: the area seed and hash mixed, so it is stable for an instance.</summary>
    public static uint Roll(ushort hash16, uint seed) => Mixed(seed, hash16) % 100u;

    static uint Mixed(uint seed, ushort hash16) =>
        Next(unchecked(seed * 0x9e3779b1u ^ (uint)hash16 * 0x85ebca77u));

    static uint Next(uint v) => unchecked(v * 1664525u + 1013904223u);

    /// <summary>The shrines of these room lines on this walk grid, at most <paramref name="limit"/>, in placement order.</summary>
    public static List<Placed> Plan(IReadOnlyList<PlacedTileObject> roomObjects, Func<int, int, bool> walkable, uint seed,
        int limit = int.MaxValue)
    {
        var placed = new List<Placed>();
        if (limit <= 0 || ShrineCatalog.Standard.Count == 0) return placed;
        var used = new HashSet<int>();
        var seen = new HashSet<(int X, int Y)>();
        foreach (var o in roomObjects)
        {
            if (!string.Equals(o.Type, Type, StringComparison.Ordinal)) continue;
            if (o.X < 0 || o.Y < 0) continue;
            if (!walkable(o.X, o.Y) || !seen.Add((o.X, o.Y))) continue;
            var shrine = Roll(seed, o.X, o.Y, used);
            if (shrine is null) break;   // every standard type is already on the layout
            placed.Add(new Placed(o.X, o.Y, o.Rotation, string.IsNullOrEmpty(o.Model) ? DefaultModel : o.Model, shrine));
            if (placed.Count >= limit) break;
        }
        return placed;
    }

    /// <summary>The type at a cell: a seeded index into the standard shrines, skipping the rows already used.</summary>
    static ShrineCatalog.ShrineDefinition? Roll(uint seed, int x, int y, HashSet<int> used)
    {
        var standard = ShrineCatalog.Standard;
        uint hash = unchecked(seed * 0x9e3779b1u ^ (uint)x * 0x85ebca77u ^ (uint)y * 0xc2b2ae3du);
        int start = (int)(hash % (uint)standard.Count);
        for (int i = 0; i < standard.Count; i++)
        {
            int index = (start + i) % standard.Count;
            if (used.Add(index)) return standard[index];
        }
        return null;
    }
}
