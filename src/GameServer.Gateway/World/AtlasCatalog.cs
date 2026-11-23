using System.Text.Json;
using GameServer.Core;

/// <summary>
/// The 3.29 Atlas layouts (AtlasNode.dat + WorldAreas.dat) and the generic map keys (MapKeys.dat). A generic map item
/// names only its tier; the layout is selected on the Atlas. Until the native Atlas panel is implemented, the first
/// starting node is the default layout.
/// </summary>
public sealed class AtlasCatalog
{
    readonly ItemCatalog catalog;
    readonly string folder;

    public AtlasCatalog(ItemCatalog catalog, string folder = "data")
    {
        this.catalog = catalog;
        this.folder = folder;
        (_keyTiers, _specialKeys) = LoadKeys(catalog, folder);
        _layouts = LoadLayouts(catalog, folder);
    }

    public sealed record Layout(string Id, ushort Hash16, string Name, int Tier, bool Starting)
    {
        public bool RequiresSpecificItem { get; init; }
        public uint? RequiredKeyHash { get; init; }
        public int AreaLevel { get; init; }
    }

    readonly IReadOnlyList<Layout> _layouts;
    readonly IReadOnlyDictionary<uint, int> _keyTiers;
    readonly IReadOnlySet<uint> _specialKeys;

    public IReadOnlyList<Layout> Layouts => _layouts;

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
    public int? TierOf(uint itemType) => _keyTiers.TryGetValue(itemType, out int tier) ? tier : null;

    /// <summary>Whether this MapKey base item is a special key (MapKeys.IsSpecial) reserved for specific layouts.</summary>
    public bool IsSpecialKey(uint itemType) => _specialKeys.Contains(itemType);

    public uint? KeyOfTier(int tier)
    {
        uint? fallback = null;
        foreach (var (key, value) in _keyTiers)
        {
            if (value != tier) continue;
            if (!_specialKeys.Contains(key)) return key;
            fallback ??= key;
        }
        return fallback;
    }

    static IReadOnlyList<Layout> LoadLayouts(ItemCatalog catalog, string folder)
    {
        if (!ClientTables.Has("AtlasNode") || !ClientTables.Has("WorldAreas")) return [];
        var n = ClientTables.Table("AtlasNode");
        var a = ClientTables.Table("WorldAreas");
        var deviceLayouts = ClientTables.Has("MapDeviceLayouts") ? ClientTables.Table("MapDeviceLayouts") : null;
        var mapKeys = ClientTables.Has("MapKeys") ? ClientTables.Table("MapKeys") : null;
        var result = new List<Layout>();
        foreach (var row in n)
        {
            if (!row.Bool("IsNormalMap") || row.Bool("NotOnAtlas") ||
                row.IntOrNull("Area1") is not int area) continue;
            int index = area;
            if (index < 0 || index >= a.Count) continue;
            var wa = a[index];
            if (wa.IntOrNull("HASH16") is not int hash) continue;
            uint? requiredKey = null;
            if (deviceLayouts is not null && mapKeys is not null
                && row.IntOrNull("MapDeviceLayout") is int layoutRow && layoutRow >= 0 && layoutRow < deviceLayouts.Count
                && deviceLayouts[layoutRow].IntOrNull("MapKey") is int mapKeyRow && mapKeyRow >= 0 && mapKeyRow < mapKeys.Count
                && mapKeys[mapKeyRow].IntOrNull("BaseItemType") is int baseRow
                && catalog.Row(baseRow) is { } keyType)
                requiredKey = keyType.Hash;
            result.Add(new(row.Text("Id") ?? "", (ushort)hash,
                wa.Text("Name") ?? "", row.Int("Tier"),
                row.Bool("StartingNode"))
            {
                RequiresSpecificItem = row.Bool("RequiresSpecificItem"),
                RequiredKeyHash = requiredKey,
                AreaLevel = wa.IntOrNull("AreaLevel") ?? 0,
            });
        }
        return result;
    }

    static (IReadOnlyDictionary<uint, int> Tiers, IReadOnlySet<uint> Special) LoadKeys(ItemCatalog catalog, string folder)
    {
        var tiers = new Dictionary<uint, int>();
        var special = new HashSet<uint>();
        if (!ClientTables.Has("MapKeys")) return (tiers, special);
        var doc = ClientTables.Table("MapKeys");
        foreach (var row in doc)
            if (row.IntOrNull("BaseItemType") is int baseRow &&
                catalog.Row(baseRow) is { } type && row.IntOrNull("MapTier") is int tier)
            {
                tiers.TryAdd(type.Hash, tier);
                if (row.Bool("IsSpecial")) special.Add(type.Hash);
            }
        return (tiers, special);
    }
}
