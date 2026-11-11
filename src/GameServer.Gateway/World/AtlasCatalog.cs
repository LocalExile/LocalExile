using System.Text.Json;
using GameServer.Core;

/// <summary>
/// The 3.29 Atlas layouts (AtlasNode.dat + WorldAreas.dat) and the generic map keys (MapKeys.dat). A generic map item
/// names only its tier; the layout is selected on the Atlas. Until the native Atlas panel is implemented, the first
/// starting node is the default layout.
/// </summary>
public sealed class AtlasCatalog(ItemCatalog catalog, string folder = "data")
{
    public sealed record Layout(string Id, ushort Hash16, string Name, int Tier, bool Starting);

    readonly Lazy<IReadOnlyList<Layout>> _layouts = new(() => LoadLayouts(folder));
    readonly Lazy<IReadOnlyDictionary<uint, int>> _keyTiers = new(() => LoadKeyTiers(catalog, folder));

    public IReadOnlyList<Layout> Layouts => _layouts.Value;

    /// <summary>The layout the character selected, read from its saved character data so it survives a Gateway restart
    /// (382). The first starting node when the character has none (or the save names a layout the data no longer has).</summary>
    public Layout? Selected(string? character) =>
        character is not null && CharacterSaves.AtlasLayout(character) is { } id ? Find(id) ?? Default : Default;

    /// <summary>Records the character's choice (its AtlasNode Id) in its character save.</summary>
    public void Select(string character, Layout layout) => CharacterSaves.SetAtlasLayout(character, layout.Id);

    public Layout? Default => Layouts.FirstOrDefault(l => l.Starting) ?? Layouts.FirstOrDefault();

    public Layout? ByHash16(ushort hash16) => Layouts.FirstOrDefault(l => l.Hash16 == hash16);

    public Layout? Find(string idOrName) => Layouts.FirstOrDefault(l =>
        l.Id.Equals(idOrName, StringComparison.OrdinalIgnoreCase) || l.Name.Equals(idOrName, StringComparison.OrdinalIgnoreCase));

    /// <summary>The map tier of a generic MapKey base item, or null for an old area-named map.</summary>
    public int? TierOf(uint itemType) => _keyTiers.Value.TryGetValue(itemType, out int tier) ? tier : null;

    public uint? KeyOfTier(int tier) => _keyTiers.Value.FirstOrDefault(kv => kv.Value == tier) is { Key: not 0 } hit ? hit.Key : null;

    static IReadOnlyList<Layout> LoadLayouts(string folder)
    {
        string nodes = Path.Combine(folder, "tables", "AtlasNode.json"), areas = Path.Combine(folder, "tables", "WorldAreas.json");
        if (!ClientTables.Exists(nodes) || !ClientTables.Exists(areas)) return [];
        using var n = GameTables.Open(nodes);
        using var a = GameTables.Open(areas);
        var result = new List<Layout>();
        foreach (var row in n.RootElement.EnumerateArray())
        {
            if (!row.GetProperty("IsNormalMap").GetBoolean() || row.GetProperty("NotOnAtlas").GetBoolean() ||
                row.GetProperty("Area1") is not { ValueKind: JsonValueKind.Number } area) continue;
            int index = area.GetInt32();
            if (index < 0 || index >= a.RootElement.GetArrayLength()) continue;
            var wa = a.RootElement[index];
            if (wa.GetProperty("HASH16") is not { ValueKind: JsonValueKind.Number } hash) continue;
            result.Add(new(row.GetProperty("Id").GetString() ?? "", (ushort)hash.GetInt32(),
                wa.GetProperty("Name").GetString() ?? "", row.GetProperty("Tier").GetInt32(),
                row.GetProperty("StartingNode").GetBoolean()));
        }
        return result;
    }

    static IReadOnlyDictionary<uint, int> LoadKeyTiers(ItemCatalog catalog, string folder)
    {
        string path = Path.Combine(folder, "tables", "MapKeys.json");
        if (!ClientTables.Exists(path)) return new Dictionary<uint, int>();
        using var doc = JsonDocument.Parse(ClientTables.ReadAllBytes(path));
        var result = new Dictionary<uint, int>();
        foreach (var row in doc.RootElement.EnumerateArray())
            if (row.GetProperty("BaseItemType") is { ValueKind: JsonValueKind.Number } baseRow &&
                catalog.Row(baseRow.GetInt32()) is { } type && row.GetProperty("MapTier") is { ValueKind: JsonValueKind.Number } tier)
                result.TryAdd(type.Hash, tier.GetInt32());
        return result;
    }
}
