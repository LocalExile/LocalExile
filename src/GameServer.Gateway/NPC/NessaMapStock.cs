using System.Text.Json;
using GameServer.Core.Protocol.Instance;
using GameServer.Core;

/// <summary>The map catalogue Nessa's development shop sells, independent of ambiguous display-name lookup: one
/// generic "Map (Tier N)" key for every tier 1-16, the tier-17 Nightmare Map, the Originator-influenced entries and
/// the unique map identities.</summary>
static class NessaMapStock
{
    public sealed record Entry(ItemCatalog.BaseType Type, Item Item, string Name, int Tier)
    {
        /// <summary>An Originator-influenced copy of a map key (the MapZanaInfluenced implicit).</summary>
        public bool Originator { get; init; }
    }

    public static List<Entry> Build(ItemCatalog items, string folder = "data")
    {
        string Table(string name) => Path.Combine(folder, "tables", name + ".json");
        if (!ClientTables.Exists(Table("Maps"))) return [];
        using var maps = JsonDocument.Parse(ClientTables.ReadAllBytes(Table("Maps")));
        var rows = maps.RootElement.EnumerateArray().ToArray();
        // The generic map keys name their tier themselves (MapKey component); the client reads the tier from the base.
        var result = new List<Entry>();
        for (int tier = 1; tier <= 16; tier++)
            if (items.Find($"Metadata/Items/Maps/MapKeyTier{tier}") is { Supported: true } type)
                result.Add(new(type, ItemCatalog.Create(type), type.Name, tier));
        // 3.29's tier-17 map is the Nightmare Map key; the old area-named MapWorlds tier-17 items are not used.
        if (items.Find("Metadata/Items/Maps/MapKeyNightmareBoss") is { Supported: true } nightmare)
            result.Add(new(nightmare, ItemCatalog.Create(nightmare), nightmare.Name, 17));
        // Originator influence is the MapZanaInfluenced implicit (map_zana_influence: "Area is Influenced by the
        // Originator's Memories"). Show it on a tier-16 map and on the Nightmare Map.
        if (items.Mods.ById("MapZanaInfluenced") is { } originator)
        {
            AddInfluenced("Metadata/Items/Maps/MapKeyTier16", 16, "Originator Influenced Map (Tier 16)");
            AddInfluenced("Metadata/Items/Maps/MapKeyNightmareBoss", 17, "Originator Influenced Nightmare Map");

            void AddInfluenced(string id, int tier, string name)
            {
                if (items.Find(id) is not { Supported: true } type) return;
                var item = ItemCatalog.Create(type);
                result.Add(new(type, item with
                {
                    Mods = [.. item.Mods, originator.With(ModSection.Implicit, [1, 5], Random.Shared)],
                }, name, tier) { Originator = true });
            }
        }
        if (!ClientTables.Exists(Table("UniqueMaps")) || !ClientTables.Exists(Table("Words")) || !ClientTables.Exists(Table("ItemVisualIdentity"))) return result;
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
            // 3.29's maps are the generic MapKeyTierN bases (MapStashUniqueMapInfo's BaseItem is the tier-1 one), so a
            // unique map with no MapWorlds base of its own still gets one and can be created (`/item Mao Kun`).
            var type = match.Type ?? (name == "Infused Beachhead" ? items.Find("MapWorldsHarbingerUber")
                : name == "The Tower of Ordeals" ? items.Find("MapWorldsTrialmaster")
                : items.Find("Metadata/Items/Maps/MapKeyTier1"));
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
