using System.Text.Json;
using GameServer.Core.Protocol.Instance;

/// <summary>
/// a1q4 "Breaking Some Eggs" in The Mud Flats (Agents/docs/protocol/12-quests.md §2–4): three Rhoa Nests drop the three
/// glyphs, the Glyph Wall takes them and sets <see cref="PlacedGlyphs"/>, which opens the Submerged Passage. The nests and
/// the wall are tile objects the walk-map recorder captures (<c>&lt;area&gt;_seed&lt;seed&gt;.objects.json</c>); the
/// waterfall and the client's invisible blockers stand in the cave room <c>to_cave.arm</c>, whose cells the wall's tile
/// shares, so they are placed from the wall's record.
/// </summary>
static class GlyphQuest
{
    public const string Area = "1_1_3", Passage = "1_1_4_1";
    public const string KnowsAboutGlyphs = "a1q4-KnowsAboutGlyphs", SeenGlyphWall = "a1q4-SeenGlyphWall", PlacedGlyphs = "a1q4-PlacedGlyphs";
    /// <summary>QuestFlags rows 50–52, set while the character holds each glyph (the glyph's .it Quest grant_flag).</summary>
    public static readonly string[] HaveGlyph = ["a1q4-HaveGlyph1", "a1q4-HaveGlyph2", "a1q4-HaveGlyph3"];
    /// <summary>BaseItemTypes 1531–1533: Haliotis, Ammonite and Roseus Glyph.</summary>
    public static readonly string[] Glyphs =
        ["Metadata/Items/QuestItems/Glyphs/Glyph1", "Metadata/Items/QuestItems/Glyphs/Glyph2", "Metadata/Items/QuestItems/Glyphs/Glyph3"];
    /// <summary>Chests rows 183–185: each drops one glyph (its BaseItemTypesKey). Tile data has only RhoaChest; the server
    /// picks which nest is which.</summary>
    public static readonly string[] NestTypes =
        ["Metadata/Chests/QuestChests/RhoaChest", "Metadata/Chests/QuestChests/RhoaChest2", "Metadata/Chests/QuestChests/RhoaChest3"];
    public const string Wall = "Metadata/QuestObjects/WaterCave/GlyphWall", Water = "Metadata/QuestObjects/WaterCave/WaterTransition";
    const string WaterModel = "Metadata/Terrain/Beach/Mudflats/waterdrop.ao";

    /// <summary>Positioned sizes (object_size − 1): a nest (Chest.ot 2), the wall (GlyphWall.ot 8) and the invisible
    /// blockers (SunkenPassageBlocker.ot 4, blocking).</summary>
    public const int NestSize = 1, WallSize = 7, BlockerSize = 3;

    /// <summary>The Transitionable component's index in the wall's and the waterfall's component order (Positioned,
    /// BaseEvents, Animated, Transitionable, …), and its two states: 1 runs create_untransitioned, 2 the transition.</summary>
    public const byte TransitionableIndex = 3, Untransitioned = 1, Transitioned = 2;

    public sealed record Placement(string Type, string Model, int X, int Y, float Rotation, float? Z, int Code);
    /// <summary>A Rhoa Nest: where it stands and the glyph it drops (0–2).</summary>
    public sealed record Nest(Placement At, int Glyph)
    {
        public string Type => NestTypes[Glyph];
    }
    public sealed record Layout(IReadOnlyList<Nest> Nests, Placement? Wall, (int X, int Y, float Rotation)? Water,
        IReadOnlyList<(int X, int Y)> Blockers);

    // to_cave.arm (Metadata/Terrain/Act1/Area3/Rooms) shares its cells with the cave tile beach_to_watercave_v2.tdt: the
    // GlyphWall's .tmd record at tile cell (38.5, 70.5) is room cell (38, 70). The room's WaterTransition stands at (71, 147),
    // its SunkenPassageBlockers on a staircase line across the way to the cave (the client spawns those itself).
    static readonly (int X, int Y) WallCell = (38, 70), WaterCell = (71, 147);
    static readonly (int X, int Y)[] BlockerCells =
    [
        (109, 60), (103, 60), (97, 60), (91, 60), (91, 66), (85, 66), (85, 72), (79, 72), (79, 78), (73, 78),
        (73, 84), (67, 84), (67, 90), (67, 96), (67, 102), (67, 108), (61, 108), (61, 114), (55, 114),
    ];

    /// <summary>The glyph (0–2) an item type is, or null.</summary>
    public static int? GlyphOf(string? baseTypeId) => baseTypeId is null ? null : Array.IndexOf(Glyphs, baseTypeId) is int i and >= 0 ? i : null;

    /// <summary>The flags picking up glyph <paramref name="glyph"/> sets (the .it Quest extra_flag and grant_flag).</summary>
    public static string[] PickupFlags(int glyph) => [KnowsAboutGlyphs, HaveGlyph[glyph]];

    /// <summary>The nests and the wall captured for (area, seed), or null without a capture.</summary>
    public static Layout? Load(string area, uint seed, string folder = "data/walkability")
    {
        if (area != Area) return null;
        string file = Path.Combine(folder, $"{area}_seed{seed}.objects.json");
        var found = new List<Placement>();
        if (File.Exists(file))
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(file));
            var root = doc.RootElement;
            if (root.GetProperty("area").GetString() != area || root.GetProperty("seed").GetUInt32() != seed) return null;
            foreach (var r in root.GetProperty("objects").EnumerateArray())
            {
                if (r.GetProperty("model").GetString() is not { Length: > 0 } model) continue;
                float? z = r.TryGetProperty("z", out var zv) && zv.ValueKind == JsonValueKind.Number ? zv.GetSingle() : null;
                int code = r.TryGetProperty("code", out var cv) && cv.ValueKind == JsonValueKind.Number ? cv.GetInt32() : 0;
                found.Add(new Placement(r.GetProperty("objectType").GetString() ?? "", model, r.GetProperty("x").GetInt32(), r.GetProperty("y").GetInt32(),
                    r.GetProperty("rotation").GetSingle(), z, code));
            }
        }
        // Without a capture, the area generated by the Gateway itself has the same objects from the port (ServerLevels).
        else if (ServerLevels.InPlaceOf(folder, area, seed) is { } level)
            found.AddRange(level.QuestObjects.Select(o => new Placement(o.Type, o.Model, o.X, o.Y, o.Rotation, o.Z, o.Code)));
        else return null;
        var nests = new List<Placement>();
        Placement? wall = null;
        foreach (var p in found)
        {
            if (Array.IndexOf(NestTypes, p.Type) < 0 && p.Type != Wall) continue;
            if (p.Type == Wall) wall ??= p; else if (!nests.Any(n => (n.X, n.Y) == (p.X, p.Y))) nests.Add(p);
        }
        if (nests.Count == 0 && wall is null) return null;
        return new Layout(Assign(nests, seed), wall, wall is null ? null : WaterOf(wall), wall is null ? [] : [.. BlockerCells.Select(c => FromRoom(wall, c))]);
    }

    /// <summary>Which nest drops which glyph: the nests in cell order, shuffled by the seed (the same every time for an
    /// area and seed); one glyph each, extra nests drop the glyphs again in turn.</summary>
    public static List<Nest> Assign(IEnumerable<Placement> nests, uint seed)
    {
        var order = nests.OrderBy(n => n.X).ThenBy(n => n.Y).ToList();
        var rng = new Random(unchecked((int)seed));
        for (int i = order.Count - 1; i > 0; i--) { int j = rng.Next(i + 1); (order[i], order[j]) = (order[j], order[i]); }
        return [.. order.Select((n, i) => new Nest(n with { Type = NestTypes[i % 3] }, i % 3))];
    }

    /// <summary>A cell of to_cave.arm in the area: the wall's record placed the room with its rotation/flip code (as the
    /// recorder's rooms: code 1 turns a quarter, 4–7 flip first).</summary>
    public static (int X, int Y) FromRoom(Placement wall, (int X, int Y) cell)
    {
        int dx = cell.X - WallCell.X, dy = cell.Y - WallCell.Y;
        var (gx, gy) = wall.Code switch
        {
            0 => (dx, dy), 1 => (-dy, dx), 2 => (-dx, -dy), 3 => (dy, -dx),
            4 => (-dx, dy), 5 => (-dy, -dx), 6 => (dx, -dy), 7 => (dy, dx),
            _ => throw new ArgumentOutOfRangeException(nameof(wall), $"rotation code {wall.Code}"),
        };
        return (wall.X + gx, wall.Y + gy);
    }

    static (int X, int Y, float Rotation) WaterOf(Placement wall)
    {
        var (x, y) = FromRoom(wall, WaterCell);
        return (x, y, (wall.Code & 3) * MathF.PI / 2);   // the room's angle 0 through the code
    }

    /// <summary>A nest's 0x2b9 blob: Positioned (the record's height), Animated (the tile's virtual model), Chest (opened
    /// bit 0, 0x141e138b0), Stats, ObjectMagicProperties, InteractionAction, MinimapIcon (u8 flags), tasks.</summary>
    public static byte[] NestBlob(Placement p, bool opened)
    {
        var w = new BlobWriter().U8(0).U8(0);
        Positioned(w, p.X, p.Y, p.Rotation, p.Z);
        w.U8(1).WStr32(p.Model);
        w.U8((byte)(opened ? 1 : 0)).U8(0).U8(0);
        w.U8(0).U32(0).U32(0).U8(0);
        w.U16(0).U8(0).U8(0);
        return w.U8(0).U8(0).U8(0).ToArray();
    }

    /// <summary>The Glyph Wall's 0x2b9 blob: Positioned, Animated, Transitionable (u8 state), MinimapIcon, InteractionAction,
    /// tasks.</summary>
    public static byte[] WallBlob(Placement p, bool placed)
    {
        var w = new BlobWriter().U8(0).U8(0);
        Positioned(w, p.X, p.Y, p.Rotation, p.Z);
        w.U8(1).WStr32(p.Model);
        return w.U8(placed ? Transitioned : Untransitioned).U8(0).U8(0).U8(0).ToArray();
    }

    /// <summary>The waterfall's 0x2b9 blob: Positioned (terrain height), Animated (waterdrop.ao), Transitionable, tasks.</summary>
    public static byte[] WaterBlob(int x, int y, float rotation, bool placed)
    {
        var w = new BlobWriter().U8(0).U8(0);
        Positioned(w, x, y, rotation, null);
        w.U8(1).WStr32(WaterModel);
        return w.U8(placed ? Transitioned : Untransitioned).U8(0).ToArray();
    }

    // x, y, rotation, u16, flags0 (0x04: fixed height, i32 steps of 7.8125 + f32 z), flags1 0x02 (u8 hidden 0, u8 shown 1).
    static void Positioned(BlobWriter w, int x, int y, float rotation, float? z)
    {
        w.I32(x).I32(y).F32(rotation).U16(0).U8((byte)(z is null ? 0 : 0x04)).U8(2);
        if (z is float h) w.I32((int)MathF.Round(h / 7.8125f)).F32(h);
        w.U8(0).U8(1);
    }

    /// <summary>A glyph picked up: puts it and sets the flags it brings that the save lacks, in one save transaction.
    /// Refused (null, nothing put) once the save has <see cref="PlacedGlyphs"/>: a copy left on another instance's ground
    /// is not needed any more.</summary>
    public static T? Claim<T>(string character, int glyph, Func<T> put) where T : class => Saves.Atomically(() =>
    {
        var saved = CharacterSaves.Load(character)?.QuestFlags ?? [];
        if (saved.Contains(PlacedGlyphs)) return null;
        var placed = put();
        var missing = PickupFlags(glyph).Where(f => !saved.Contains(f)).ToList();
        if (missing.Count > 0) CharacterSaves.ChangeQuestFlags(character, missing, true);
        return placed;
    });

    /// <summary>Every item the character carries: each of its inventories and the cursor.</summary>
    public static IEnumerable<(byte Row, InventoryItem Item)> Carried(PlayerInventories items)
    {
        var rows = Enumerable.Range(0, InstanceInventories.PlayerDefaults.Length).Select(r => (byte)r).Append(PlayerInventories.Cursor).Distinct();
        foreach (byte row in rows)
            foreach (var it in items.Items(row))
                yield return (row, it);
    }

    /// <summary>Whether a nest drops this glyph: not once the glyphs are placed, nor while a copy exists where the player
    /// can get it (carried, in the stash, on this instance's ground). A glyph lost any other way (dropped in another
    /// instance, destroyed, sold) drops again.</summary>
    public static bool NestDrops(uint glyphHash, bool placed, PlayerInventories items, Func<uint, int> stashCount, InstanceGround ground) =>
        !placed && !Carried(items).Any(c => c.Item.Item.TypeHash == glyphHash) && stashCount(glyphHash) == 0
        && !ground.All().Any(e => e.Item.Item.TypeHash == glyphHash);

    /// <summary>The flags that make the HaveGlyph flags follow the glyphs the character carries (the .it grant_flag),
    /// with KnowsAboutGlyphs once one is carried. None are held once the glyphs are placed.</summary>
    public static (List<string> Set, List<string> Clear) SyncFlags(IReadOnlySet<string> flags, bool placed, IReadOnlySet<int> carried)
    {
        List<string> set = [], clear = [];
        for (int g = 0; g < HaveGlyph.Length; g++)
        {
            bool want = !placed && carried.Contains(g);
            if (want && !flags.Contains(HaveGlyph[g])) set.Add(HaveGlyph[g]);
            if (!want && flags.Contains(HaveGlyph[g])) clear.Add(HaveGlyph[g]);
        }
        if (!placed && carried.Count > 0 && !flags.Contains(KnowsAboutGlyphs)) set.Add(KnowsAboutGlyphs);
        return (set, clear);
    }

    sealed class PlacementRefused(string message) : Exception(message);

    /// <summary>The Glyph Wall takes the three glyphs (main inventory or cursor): removes them, sets
    /// <see cref="PlacedGlyphs"/> and clears the HaveGlyph flags, in one save transaction. The removed items by inventory
    /// row, or null (nothing changes) when a glyph is missing. The lookup runs inside the transaction, which is one at a
    /// time: of two quick uses only the first finds the glyphs.</summary>
    public static List<(byte Row, InventoryItem Item)>? Place(string character, PlayerInventories items, Func<Item, int?> glyphOf)
    {
        try
        {
            return Saves.Atomically(() =>
            {
                var found = new (byte Row, InventoryItem Item)?[3];
                foreach (byte row in new[] { PlayerInventories.MainInventory, PlayerInventories.Cursor })
                    foreach (var it in items.Items(row))
                        if (glyphOf(it.Item) is int g && found[g] is null) found[g] = (row, it);
                if (found.Any(f => f is null)) return null;
                var removed = new List<(byte Row, InventoryItem Item)>();
                foreach (var (row, it) in found.Select(f => f!.Value))
                    removed.Add((row, items.Remove(row, it.Id) ?? throw new PlacementRefused($"glyph item {it.Id} cannot leave row {row}")));
                CharacterSaves.ChangeQuestFlags(character, [PlacedGlyphs], true);
                CharacterSaves.ChangeQuestFlags(character, HaveGlyph, false);
                return removed;
            });
        }
        catch (PlacementRefused)
        {
            return null;   // rolled back: a glyph was on offer to a vendor
        }
    }
}
