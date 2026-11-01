using System.Text.Json;
using GameServer.Data.Terrain.Generation;

/// <summary>
/// The quest objects the engine creates itself (Quests.ObjectRule, ChestRule; Agents/docs/protocol/12-quests.md §5): what
/// each type is (its deserialized components from data/object-types.json, its Transitionable states, its size, its model)
/// and where an area places it, from a tile record of the Gateway's own generation (ServerLevels.Level.TileQuestObjects)
/// or a room line (AreaPopulation.LoadObjects).
/// <para>Quest chests without an .ot of their own (the Golden Hand's altar, Victario's stashes, Siosa's book stands) are
/// Chests.dat rows: the client builds them from InheritsFrom (QuestChest), with the row's AOFiles model.</para>
/// </summary>
static class QuestObjectTypes
{
    /// <summary>A quest object type: its deserialized components in order, Transitionable's last state (2 when the .ot
    /// does not say), its Positioned size (object_size − 1, at least 1), whether it blocks, and the model when the type
    /// has no Animated model of its own (a Chests.dat row's AOFiles).</summary>
    public sealed record Info(string Type, IReadOnlyList<string> Components, int States, int Size, bool Blocking, string? Model);

    /// <summary>Tile objects the quest engine may create: quest objects, quest chests, the Dark Altar and the quest
    /// doors, levers and gates of the areas' own objects (Metadata/Terrain/…/Objects/).</summary>
    public static bool IsTileQuestObject(string type) =>
        type.StartsWith("Metadata/QuestObjects/", StringComparison.Ordinal) || type.StartsWith("Metadata/Chests/QuestChests/", StringComparison.Ordinal)
        || type.StartsWith("Metadata/Monsters/IncaShadowBoss/", StringComparison.Ordinal)
        || type.StartsWith("Metadata/Terrain/", StringComparison.Ordinal) && type.Contains("/Objects/", StringComparison.Ordinal);

    public static Info? Find(string type) => Catalog.Value.GetValueOrDefault(type);

    static readonly Lazy<Dictionary<string, Info>> Catalog = new(() => Load());

    public static Dictionary<string, Info> Load(string folder = "data")
    {
        var types = new Dictionary<string, Info>(StringComparer.Ordinal);
        string file = Path.Combine(folder, "object-types.json");
        if (!File.Exists(file)) return types;
        using (var doc = JsonDocument.Parse(File.ReadAllBytes(file)))
            foreach (var t in doc.RootElement.GetProperty("types").EnumerateArray())
            {
                string id = t.GetProperty("type").GetString() ?? "";
                int read = t.GetProperty("read").GetInt32();
                var components = t.GetProperty("components").EnumerateArray().Select(c => c.GetString() ?? "").Take(read).ToList();
                int states = t.TryGetProperty("transitionable", out var tr) && tr.ValueKind == JsonValueKind.Object
                    && tr.TryGetProperty("num_states", out var n) && n.ValueKind == JsonValueKind.Number ? n.GetInt32() : 2;
                int size = 1;
                bool blocking = false;
                if (t.TryGetProperty("positioned", out var pos) && pos.ValueKind == JsonValueKind.Object)
                {
                    if (pos.TryGetProperty("object_size", out var os) && int.TryParse(os.GetString(), out int s)) size = Math.Max(1, s - 1);
                    blocking = pos.TryGetProperty("blocking", out var b) && b.GetString() == "true";
                }
                types[id] = new Info(id, components, Math.Max(2, states), size, blocking, null);
            }
        // Chests.dat rows without an .ot: their InheritsFrom's components, the row's model.
        string chests = Path.Combine(folder, "tables", "Chests.json");
        if (File.Exists(chests))
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(chests));
            foreach (var r in doc.RootElement.EnumerateArray())
            {
                if (r.GetProperty("Id").GetString() is not { Length: > 0 } id || !id.StartsWith("Metadata/Chests/QuestChests/", StringComparison.Ordinal)) continue;
                string? model = r.GetProperty("AOFiles").EnumerateArray().FirstOrDefault().GetString();
                if (types.TryGetValue(id, out var own)) { types[id] = own with { Model = model }; continue; }
                if (r.GetProperty("InheritsFrom").GetString() is { } parent && types.TryGetValue(parent, out var basis))
                    types[id] = basis with { Type = id, Model = model };
            }
        }
        return types;
    }

    /// <summary>Where an area (at a seed) places quest objects: by type, its tile records (the Gateway's own generation:
    /// a placement with the record's height <c>Z</c> and virtual model), else its rooms' lines (no height), else the
    /// server's own placed rooms (rooms data/room-points.json does not keep, and objects a room's point list names). Each
    /// source is read once, on the first question.</summary>
    public sealed class AreaPlacements(string area, uint seed, List<string> notes)
    {
        IReadOnlyList<PlacedTileObject>? tiles;
        IReadOnlyList<AreaPopulation.RoomObject>? rooms;

        public List<GlyphQuest.Placement> Of(string type)
        {
            tiles ??= ServerLevels.For(area, seed)?.TileQuestObjects ?? [];
            var found = tiles.Where(o => o.Type == type).Select(o => new GlyphQuest.Placement(o.Type, o.Model, o.X, o.Y, o.Rotation, o.Z, o.Code)).ToList();
            if (found.Count > 0) return found;
            rooms ??= AreaPopulation.LoadObjects(area, seed, notes);
            found = [.. rooms.Where(o => o.Type == type).Select(o => new GlyphQuest.Placement(o.Type, o.Model, o.X, o.Y, o.Angle, null, 0))];
            if (found.Count > 0) return found;
            // A room data/room-points.json does not keep (its objects are not quest objects: The Ascent's Resonator,
            // Kitava's socket) or a chest on a room's point list (BlackFlagChest): the server's own placed rooms.
            return [.. (ServerLevels.For(area, seed)?.RoomObjects ?? []).Where(o => o.Type == type)
                .Select(o => new GlyphQuest.Placement(o.Type, o.Model, o.X, o.Y, o.Rotation, null, o.Code))];
        }
    }
}
