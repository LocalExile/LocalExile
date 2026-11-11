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
        if (!ClientTables.Has("AtlasNode") || !ClientTables.Has("WorldAreas")) return [];
        var n = ClientTables.Table("AtlasNode");
        var a = ClientTables.Table("WorldAreas");
        var result = new List<Layout>();
        foreach (var row in n)
        {
            if (!row.Bool("IsNormalMap") || row.Bool("NotOnAtlas") ||
                row.IntOrNull("Area1") is not int area) continue;
            int index = area;
            if (index < 0 || index >= a.Count) continue;
            var wa = a[index];
            if (wa.IntOrNull("HASH16") is not int hash) continue;
            result.Add(new(row.Text("Id") ?? "", (ushort)hash,
                wa.Text("Name") ?? "", row.Int("Tier"),
                row.Bool("StartingNode")));
        }
        return result;
    }

    static IReadOnlyDictionary<uint, int> LoadKeyTiers(ItemCatalog catalog, string folder)
    {
        if (!ClientTables.Has("MapKeys")) return new Dictionary<uint, int>();
        var doc = ClientTables.Table("MapKeys");
        var result = new Dictionary<uint, int>();
        foreach (var row in doc)
            if (row.IntOrNull("BaseItemType") is int baseRow &&
                catalog.Row(baseRow) is { } type && row.IntOrNull("MapTier") is int tier)
                result.TryAdd(type.Hash, tier);
        return result;
    }
}
