namespace GameServer.Data.Terrain.Generation;

/// <summary>A tile object the server creates itself: an area transition or a waypoint, at a grid cell, with its
/// object type (.ot), its virtual model, its angle, its height (the record's z, negative is up) and its tile's
/// orientation code.</summary>
public sealed record PlacedTileObject(string Type, string Model, int X, int Y, float Rotation, float Z, int Code = 0);

/// <summary>A placed .arm room as scripts/walkmaps captures it (&lt;area&gt;_seed&lt;seed&gt;.rooms.json, the room
/// loop of the doodad stage <c>0x142040770</c>): its origin and its unturned size (room data +0x138 / +0x139) in grid
/// cells, and its orientation code.</summary>
public sealed record PlacedArmRoom(string Path, int X, int Y, int Width, int Height, int Code);

/// <summary>The area transitions and waypoints of a generated grid, placed as <see cref="DoodadStage"/>'s tile lines
/// place tile objects (<c>0x142041200</c>): row by row, every tile origin's objects (the record master's Reskin copy
/// when there is one; per sub-tile for a tile made of them), then the .tmd the .tdt names (+0x180), turned by the
/// entry's orientation. What scripts/walkmaps captures from the client as &lt;area&gt;_seed&lt;seed&gt;.transitions.json.
/// The transitions' server-only effects (<see cref="EffectsFolder"/>: Act 5's crater glow and ground shatter) are
/// placed too.</summary>
public static class AreaObjects
{
    /// <summary>The two server-only Transitionable effects a tile places beside an entrance (GlowyFX,
    /// SlavePensIntroGround); no area transition themselves.</summary>
    public const string EffectsFolder = "Metadata/MiscellaneousObjects/AreaTransitionEffects/";

    public static bool IsEffect(string type) => type.StartsWith(EffectsFolder, StringComparison.OrdinalIgnoreCase);

    /// <summary>The quest tile objects scripts/walkmaps records apart (&lt;area&gt;_seed&lt;seed&gt;.objects.json): a1q4's
    /// Rhoa nests and glyph wall in The Mud Flats, The Twilight Strand's support gem chest.</summary>
    public static readonly IReadOnlySet<string> QuestTypes = new HashSet<string>(StringComparer.Ordinal)
    {
        "Metadata/Chests/QuestChests/RhoaChest", "Metadata/Chests/QuestChests/RhoaChest2", "Metadata/Chests/QuestChests/RhoaChest3",
        "Metadata/QuestObjects/WaterCave/GlyphWall", "Metadata/Chests/TutorialSupportGemChest",
    };

    /// <summary>The .arm rooms of a generated grid, in the order the doodad stage visits them (each record's rooms),
    /// once each: what the recorder writes to &lt;area&gt;_seed&lt;seed&gt;.rooms.json.</summary>
    public static List<PlacedArmRoom> Rooms(GenerationResult result)
    {
        var rooms = new List<PlacedArmRoom>();
        foreach (var record in result.Records)
            foreach (var r in record.Rooms)
            {
                if (!r.Room.Path.EndsWith(".arm", StringComparison.OrdinalIgnoreCase) || r.Orientation is < 0 or > 7) continue;
                if (r.Room.Width == 0 || r.Room.Height == 0) continue;
                var room = new PlacedArmRoom(r.Room.Path, r.X * 23, r.Y * 23, r.Room.Width * 23, r.Room.Height * 23, r.Orientation);
                if (!rooms.Contains(room)) rooms.Add(room);
            }
        return rooms;
    }

    public static List<PlacedTileObject> Transitions(TerrainCache files, GenerationResult result) =>
        Place(files, result, t => files.IsTransitionType(t) || IsEffect(t), tile => files.HasTransition(tile));

    /// <summary>The waypoints a placed .arm room holds as a server object line (type <c>Waypoint</c>) instead of a tile
    /// object: The Ledge's <c>Act1/Area5/Rooms/waypoint.arm</c>. Not in the recorder's .transitions.json (it records tile
    /// objects only).</summary>
    public static List<PlacedTileObject> RoomWaypoints(GenerationResult result) => RoomObjects(result, t => t == WaypointType);

    /// <summary>The server object lines of the placed .arm rooms whose type <paramref name="keep"/> takes (default: any type
    /// but the plain doodad: a waypoint, an area transition such as The Submerged Passage's <c>DepthsReturnTransition</c>
    /// boat, a quest object), placed as <see cref="DoodadStage"/>'s room lines (<c>0x142040770</c>: the cell turned by the
    /// room's code, the angle mirrored for codes 4–7 plus (code &amp; 3)·π/2), which skip them as no client doodad; with
    /// <paramref name="points"/> also the
    /// named points named by an object type (a room's point list can place a chest: The Cavern of Anger's
    /// <c>BlackFlagChest</c>, "x y angle "type""). The quest engine's fallback for rooms data/room-points.json does not keep
    /// (The Ascent's Resonator in ascent_summit_01.arm, Kitava's socket in bossentry.arm: their objects are not QuestObjects).
    /// A point has no model and no fixed height (NaN).</summary>
    public static List<PlacedTileObject> RoomObjects(GenerationResult result, Func<string, bool>? keep = null, bool points = false)
    {
        keep ??= t => t != DoodadType;
        var placed = new List<PlacedTileObject>();
        foreach (var record in result.Records)
            foreach (var room in record.Rooms)
            {
                int code = room.Orientation;
                void Add(string type, string model, float x, float y, float rawAngle, float z)
                {
                    var (cx, cy) = Orientation.Map(code, (int)x, (int)y, room.Room.Width * 23, room.Room.Height * 23);
                    float angle = code is >= 4 and < 8 ? MathF.PI * 2 - rawAngle : rawAngle;
                    angle = (angle + (code & 3) * (MathF.PI / 2)) % (MathF.PI * 2);
                    var o = new PlacedTileObject(type, model, room.X * 23 + cx, room.Y * 23 + cy, angle, z, code);
                    if (!placed.Contains(o)) placed.Add(o);
                }
                foreach (var d in room.Room.Doodads)
                    if (keep(d.Type)) Add(d.Type, d.Model, d.X, d.Y, d.Angle, d.FixedHeight ?? float.NaN);
                if (points)
                    foreach (var p in room.Room.Points)
                        if (p.Name.StartsWith("Metadata/", StringComparison.Ordinal) && keep(p.Name)) Add(p.Name, "", p.X, p.Y, p.Angle, float.NaN);
            }
        return placed;
    }

    const string DoodadType = "Metadata/MiscellaneousObjects/Doodad";

    const string WaypointType = "Metadata/MiscellaneousObjects/Waypoint";

    /// <summary>The <see cref="QuestTypes"/> objects of a generated grid, placed as <see cref="Transitions"/>.</summary>
    public static List<PlacedTileObject> QuestObjects(TerrainCache files, GenerationResult result) =>
        Place(files, result, QuestTypes.Contains, _ => false);

    /// <summary>The tile objects of a generated grid whose type <paramref name="keep"/> takes, placed as
    /// <see cref="Transitions"/> (the Gateway's quest objects: seals, levers, quest chests, altars).</summary>
    /// <remarks>A record whose virtual model is no file of its own but which names an .ao (the Ancient Seal's
    /// dungeon_bossrelease_v01_01_ball_replacement record: a static mesh, and bossrelease.ao with the animation its .ot
    /// plays on creation) is placed with that .ao, as the doodad stage takes it; live, the static model hung the client.</remarks>
    public static List<PlacedTileObject> TileObjects(TerrainCache files, GenerationResult result, Func<string, bool> keep) =>
        Place(files, result, keep, _ => false, (o, tdt) => files.Text(o.VirtualModel(tdt)) is null
            && o.Animation is { } ao && ao.EndsWith(".ao", StringComparison.OrdinalIgnoreCase) ? ao : o.VirtualModel(tdt));

    // The tile objects of the types kept; a tile is looked at when it has one of them (has) or an effect.
    static List<PlacedTileObject> Place(TerrainCache files, GenerationResult result, Func<string, bool> keep, Func<TileDef, bool> has,
        Func<TileObject, string, string>? modelOf = null)
    {
        var grid = result.Grid;
        var records = result.Records;
        var placed = new List<PlacedTileObject>();
        for (int y = 0; y < grid.Height; y++)
            for (int x = 0; x < grid.Width; x++)
            {
                if (grid[x, y] is not { IsOrigin: true } e) continue;
                var tile = e.Tile;
                if (!has(tile) && !files.TileObjects(tile).Concat(files.SecondObjects(tile)).Any(o => IsEffect(o.Type) || keep(o.Type))) continue;
                int index = 0;
                for (int k = 0; k < records.Count; k++)
                    if (x >= records[k].X && x < records[k].X + records[k].W && y >= records[k].Y && y < records[k].Y + records[k].H) index = k;
                string? reskin = records.Count != 0 ? records[index].Master.GetValueOrDefault("Reskin") : null;

                void Line(TileObject o, int cellX, int cellY, float angle)
                {
                    if (!keep(o.Type)) return;
                    var (cx, cy) = Orientation.Map(e.Orientation, cellX, cellY, tile.Width * 23, tile.Height * 23);
                    string model = IsEffect(o.Type) && EffectModel(files, o.Type) is { } own ? own : modelOf?.Invoke(o, tile.Path) ?? o.VirtualModel(tile.Path);
                    placed.Add(new PlacedTileObject(o.Type, model, x * 23 + cx, y * 23 + cy,
                        TilePlacement.Angle(e.Orientation, angle), o.Z - e.HeightOffset * 7.8125f, e.Orientation));
                }

                var objects = files.TileObjects(tile);
                if (objects.Count != 0 && (files.TileFlags(tile.Path) & 0x200) != 0) SubTiles(files, tile, reskin, Line);
                else if (objects.Count != 0)
                {
                    if (reskin is { Length: > 0 }) objects = files.ReskinObjects(tile, reskin) ?? objects;
                    foreach (var o in objects) Line(o, o.CellX, o.CellY, o.Angle);
                }
                foreach (var o in files.SecondObjects(tile)) Line(o, o.CellX, o.CellY, o.Angle);
            }
        return placed;
    }

    // An effect's model is its type's Animated animated_object (the record's virtual .ao is not what the client holds:
    // 1_5_1's slave_crater_v01_01_glowyFX record is crater_glow.ao).
    static string? EffectModel(TerrainCache files, string type) =>
        files.Text(type + ".ot") is { } text
            ? TerrainText.Lines(text).Select(l => l.Trim()).Where(l => l.StartsWith("animated_object", StringComparison.Ordinal))
                .Select(l => l[(l.IndexOf('"') + 1)..l.LastIndexOf('"')]).FirstOrDefault()
            : null;

    // As DoodadStage.SubTileDoodads: each sub-tile's mesh objects, turned by the slot's code inside its span.
    static void SubTiles(TerrainCache files, TileDef tile, string? reskin, Action<TileObject, int, int, float> line)
    {
        if (files.Walk(tile.Path) is not { } walkData || walkData.SubTiles.Length < tile.Width * tile.Height) return;
        SubTileWalk Slot(int u, int v) => walkData.SubTiles[v * tile.Width + u];
        for (int v = 0; v < tile.Height; v++)
            for (int u = 0; u < tile.Width; u++)
            {
                var slot = Slot(u, v);
                if ((slot.G & 0xe) != 0) continue;
                if (files.SubTileObjects(tile, slot.Mesh, reskin) is not { } objects) continue;
                int w = 1, h = 1;
                while (u + w < tile.Width && (Slot(u + w, v).G & 2) != 0) w++;
                while (v + h < tile.Height && (Slot(u, v + h).G & 4) != 0) h++;
                int code = slot.Code;
                if ((code & 1) != 0) (w, h) = (h, w);
                var (t0, t1, t2) = TilePlacement.Row(code);
                foreach (var o in objects)
                {
                    Span<float> a = [w * 250 - o.X, o.X, h * 250 - o.Y, o.Y];
                    float px = u * 250f + a[t1 + 2 * t0], py = v * 250f + a[(t0 == 0 ? 2 : 0) + t2];
                    line(o, (int)MathF.Floor(px * 0.092f), (int)MathF.Floor(py * 0.092f), TilePlacement.Angle(code, o.Angle));
                }
            }
    }
}
