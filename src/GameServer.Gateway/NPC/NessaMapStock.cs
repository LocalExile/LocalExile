using System.Text.Json;
using GameServer.Core.Protocol.Instance;

/// <summary>The map catalogue Nessa's development shop sells, independent of ambiguous display-name lookup: one
/// generic "Map (Tier N)" key for every tier 1-16, the tier-17 map items, and the unique map identities.</summary>
static class NessaMapStock
{
    public sealed record Entry(ItemCatalog.BaseType Type, Item Item, string Name, int Tier);

    public static List<Entry> Build(ItemCatalog items, string folder = "data")
    {
        string Table(string name) => Path.Combine(folder, "tables", name + ".json");
        if (!File.Exists(Table("Maps"))) return [];
        using var maps = JsonDocument.Parse(File.ReadAllBytes(Table("Maps")));
        var rows = maps.RootElement.EnumerateArray().ToArray();
        // The generic map keys name their tier themselves (MapKey component); the client reads the tier from the base.
        var result = new List<Entry>();
        for (int tier = 1; tier <= 16; tier++)
            if (items.Find($"Metadata/Items/Maps/MapKeyTier{tier}") is { Supported: true } type)
                result.Add(new(type, ItemCatalog.Create(type), type.Name, tier));
        // There is no MapKeyTier17: the tier-17 maps are ordinary MapWorlds items.
        var tier17 = rows.Select(r => (Row: r, Type: items.Row(r.GetProperty("BaseItemTypesKey").GetInt32())))
            .Where(p => p.Row.GetProperty("Tier").GetInt32() == 17
                && p.Type is { Supported: true, Class: "Map" }
                && p.Type.Id.StartsWith("Metadata/Items/Maps/MapWorlds", StringComparison.Ordinal)
                && !p.Type.Name.StartsWith('['))
            .GroupBy(p => p.Type!.Name, StringComparer.Ordinal)
            .Select(g => g.OrderBy(p => p.Type!.Id, StringComparer.Ordinal).First())
            .OrderBy(p => p.Type!.Name, StringComparer.Ordinal)
            .Select(p => new Entry(p.Type!, ItemCatalog.Create(p.Type!), p.Type!.Name, 17));
        result.AddRange(tier17);
        if (!File.Exists(Table("UniqueMaps")) || !File.Exists(Table("Words")) || !File.Exists(Table("ItemVisualIdentity"))) return result;
        using var uniques = GameTables.Open(Table("UniqueMaps"));
        using var words = GameTables.Open(Table("Words"));
        using var visuals = GameTables.Open(Table("ItemVisualIdentity"));
        foreach (var unique in uniques.RootElement.EnumerateArray().GroupBy(r => r.GetProperty("Name").GetString())
            .Select(g => g.First()).OrderBy(r => r.GetProperty("Name").GetString()))
        {
            int area = unique.GetProperty("WorldAreasKey").GetInt32();
            var match = rows.Where(r => r.GetProperty("Unique_WorldAreasKey") is { ValueKind: JsonValueKind.Number } a
                    && a.GetInt32() == area)
                .Select(r => (Row: r, Type: items.Row(r.GetProperty("BaseItemTypesKey").GetInt32())))
                .FirstOrDefault(p => p.Type is { Supported: true, Class: "Map" }
                    && p.Type.Id.StartsWith("Metadata/Items/Maps/MapWorlds", StringComparison.Ordinal));
            string name = unique.GetProperty("Name").GetString()!;
            var type = match.Type ?? (name == "Infused Beachhead" ? items.Find("MapWorldsHarbingerUber")
                : name == "The Tower of Ordeals" ? items.Find("MapWorldsTrialmaster") : null);
            if (type is not { Supported: true }) continue;
            uint word = words.RootElement[unique.GetProperty("WordsKey").GetInt32()].GetProperty("HASH32").GetUInt32();
            var item = ItemCatalog.Create(type) with
            {
                Rarity = ItemRarity.Unique, NameWords = [word],
                VisualIdentity = (ushort)visuals.RootElement[unique.GetProperty("ItemVisualIdentityKey").GetInt32()]
                    .GetProperty("HASH16").GetInt32()
            };
            result.Add(new(type, item, name, match.Type is null ? 16 : match.Row.GetProperty("Tier").GetInt32()));
        }
        return result;
    }
}
