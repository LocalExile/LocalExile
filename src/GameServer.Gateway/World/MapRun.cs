using System.Text.Json;
using GameServer.Core.Protocol.Instance;
using GameServer.Core;

/// <summary>
/// What a map device activation puts on the instance (21-hideouts.md §5): the map item's affixes and quality, plus the
/// scarabs and fragments' MapFragmentMods. Recipes without a map (Maven's Writ, four Sacrifice fragments) open their
/// MapDeviceRecipes area instead.
/// </summary>
public sealed class MapMods
{
    public static readonly MapMods None = new(new Dictionary<string, int>());

    public MapMods(IReadOnlyDictionary<string, int> stats, int quality = 0)
    {
        Stats = stats;
        Quality = Math.Clamp(quality, 0, 100);
    }

    public IReadOnlyDictionary<string, int> Stats { get; }
    public int Quality { get; }

    public int Get(string id) => Stats.GetValueOrDefault(id);

    public int Quantity => Get("map_item_drop_quantity_+%") + Get("map_implicit_item_drop_quantity_+%")
        + Get("map_unscaled_item_drop_quantity_+%");

    public int Rarity => Get("map_item_drop_rarity_+%") + Get("map_implicit_item_drop_rarity_+%")
        + Get("map_unscaled_item_drop_rarity_+%")
        + (Get("map_item_drop_quality_also_applies_to_map_item_drop_rarity") > 0 ? Quality : 0);

    public int PackSize => Get("map_pack_size_+%") + Get("map_implicit_pack_size_+%")
        + Get("map_unscaled_pack_size_+%");

    public int MonsterLife => Get("map_monsters_life_+%");
    public int MonsterDamage => Get("map_monsters_damage_+%");
    public int ExtraRarePacks => Math.Max(0, SumWhere(id => id.Contains("additional_rare_pack", StringComparison.OrdinalIgnoreCase)));
    public int ExtraMagicPacks => Math.Max(0, SumWhere(id => id.Contains("additional_magic_pack", StringComparison.OrdinalIgnoreCase)) + PacksDropCards);
    public int ExtraNormalPacks => Math.Max(0, SumWhere(id => id.Contains("additional_normal_pack", StringComparison.OrdinalIgnoreCase)));
    /// <summary>Scarab stats of the form map_X_*_packs_drop_cards*: extra packs whose monsters drop cards.</summary>
    public int PacksDropCards => SumWhere(id => id.Contains("packs_drop_cards", StringComparison.OrdinalIgnoreCase));
    public int CardDropChance => SumWhere(id => id.Contains("divination_card", StringComparison.OrdinalIgnoreCase)
        && id.Contains("chance", StringComparison.OrdinalIgnoreCase));
    public int ScarabDropChance => SumWhere(id => id.Contains("scarab", StringComparison.OrdinalIgnoreCase)
        && id.Contains("drop_chance", StringComparison.OrdinalIgnoreCase));
    public int CurrencyDropChance => SumWhere(id => id.Contains("currency", StringComparison.OrdinalIgnoreCase)
        && id.Contains("drop_chance", StringComparison.OrdinalIgnoreCase)
        && !id.Contains("divination", StringComparison.OrdinalIgnoreCase));

    int SumWhere(Func<string, bool> match) => Stats.Where(kv => match(kv.Key)).Sum(kv => kv.Value);
    public int RarePackIncrease => Get("map_number_of_rare_packs_+%");
    public int MagicPackIncrease => Get("map_number_of_magic_packs_+%");

    public double QuantityFactor => Math.Max(0, 100 + Quantity) / 100.0;
    public double PackSizeFactor => Math.Max(0, 100 + PackSize) / 100.0;

    public AreaPopulation.PackScale Packs =>
        new(PackSizeFactor, ExtraNormalPacks, ExtraMagicPacks, ExtraRarePacks, RarePackIncrease, MagicPackIncrease);

    /// <summary>Folded into a spawned monster's life and hit (maximum_life_+% / damage_+%).</summary>
    public Dictionary<string, int> MonsterEffectStats()
    {
        var extra = new Dictionary<string, int>();
        if (MonsterLife != 0) extra["maximum_life_+%"] = MonsterLife;
        if (MonsterDamage != 0) extra["damage_+%"] = MonsterDamage;
        if (Get("map_monster_damage_taken_+%_final_from_atlas_keystone") is int taken and not 0)
            extra["map_monster_damage_taken_+%_final_from_atlas_keystone"] = taken;
        return extra;
    }

    public override string ToString() =>
        $"qty {Quantity:+#;-#;0}% rar {Rarity:+#;-#;0}% pack {PackSize:+#;-#;0}% q{Quality}";
}

/// <summary>MapFragmentMods.dat and MapDeviceRecipes.dat for one device activation.</summary>
public sealed class MapRunCatalog
{
    public sealed record Fragment(int Family, int Limit, int MinAreaLevel, IReadOnlyList<int> ModRows);

    public sealed record Recipe(string Id, IReadOnlyList<int> Items, bool AnyOne, int WorldArea, int AreaLevel,
        IReadOnlyList<int> ModRows);

    readonly Dictionary<int, Fragment> _fragments;
    readonly List<Recipe> _recipes;
    readonly Dictionary<int, (int Scale, IReadOnlyList<string> Stats)> _qualityTypes;
    readonly int? _defaultQualityType;

    public MapRunCatalog(IReadOnlyDictionary<int, Fragment> fragments, IReadOnlyList<Recipe> recipes,
        IReadOnlyDictionary<int, (int Scale, IReadOnlyList<string> Stats)>? qualityTypes = null)
    {
        _fragments = new(fragments);
        _recipes = [.. recipes];
        _qualityTypes = qualityTypes is null ? [] : new(qualityTypes);
        _defaultQualityType = _qualityTypes
            .Where(kv => kv.Value.Stats.Any(s => s.Contains("item_drop_quantity", StringComparison.OrdinalIgnoreCase)))
            .Select(kv => (int?)kv.Key).FirstOrDefault();
    }

    public static MapRunCatalog Load(string folder)
    {
        var fragments = new Dictionary<int, Fragment>();
        string fragPath = Path.Combine(folder, "tables", "MapFragmentMods.json");
        if (ClientTables.Exists(fragPath))
        {
            using var doc = GameTables.Open(fragPath);
            foreach (var row in doc.RootElement.EnumerateArray())
            {
                if (row.GetProperty("BaseItemTypesKey") is not { ValueKind: JsonValueKind.Number } baseKey) continue;
                int[] mods = row.TryGetProperty("ModsKeys", out var keys) && keys.ValueKind == JsonValueKind.Array
                    ? [.. keys.EnumerateArray().Where(k => k.ValueKind == JsonValueKind.Number).Select(k => k.GetInt32())]
                    : [];
                fragments[baseKey.GetInt32()] = new(
                    row.TryGetProperty("MapFragmentFamilies", out var fam) && fam.ValueKind == JsonValueKind.Number ? fam.GetInt32() : -1,
                    row.TryGetProperty("MapFragmentLimit", out var lim) && lim.ValueKind == JsonValueKind.Number ? Math.Max(1, lim.GetInt32()) : 1,
                    row.TryGetProperty("MinAreaLevel", out var min) && min.ValueKind == JsonValueKind.Number ? min.GetInt32() : 1,
                    mods);
            }
        }

        var recipes = new List<Recipe>();
        string recPath = Path.Combine(folder, "tables", "MapDeviceRecipes.json");
        if (ClientTables.Exists(recPath))
        {
            using var doc = GameTables.Open(recPath);
            foreach (var row in doc.RootElement.EnumerateArray())
            {
                int area = row.TryGetProperty("WorldArea", out var wa) && wa.ValueKind == JsonValueKind.Number ? wa.GetInt32() : 0;
                if (area <= 0) continue;
                int[] items = row.TryGetProperty("RecipeItems", out var list) && list.ValueKind == JsonValueKind.Array
                    ? [.. list.EnumerateArray().Where(k => k.ValueKind == JsonValueKind.Number).Select(k => k.GetInt32())]
                    : [];
                if (items.Length == 0) continue;
                // __col9: one item from the list (Maven invitations). Else the full multiset (Atziri, Shaper, 4× uber).
                bool anyOne = row.TryGetProperty("__col9", out var one) && one.ValueKind is JsonValueKind.True or JsonValueKind.False
                    && one.GetBoolean();
                int[] recipeMods = row.TryGetProperty("Mods", out var recMods) && recMods.ValueKind == JsonValueKind.Array
                    ? [.. recMods.EnumerateArray().Where(k => k.ValueKind == JsonValueKind.Number).Select(k => k.GetInt32())]
                    : [];
                recipes.Add(new(row.GetProperty("Id").GetString() ?? "", items, anyOne,
                    area, row.TryGetProperty("AreaLevel", out var lv) && lv.ValueKind == JsonValueKind.Number ? lv.GetInt32() : 0,
                    recipeMods));
            }
        }
        return new MapRunCatalog(fragments, recipes, LoadQualityTypes(folder));
    }

    static Dictionary<int, (int Scale, IReadOnlyList<string> Stats)> LoadQualityTypes(string folder)
    {
        var types = new Dictionary<int, (int, IReadOnlyList<string>)>();
        string statsPath = Path.Combine(folder, "tables", "Stats.json"), altPath = Path.Combine(folder, "tables", "AlternateQualityTypes.json");
        if (!ClientTables.Exists(statsPath) || !ClientTables.Exists(altPath)) return types;
        using var statsDoc = GameTables.Open(statsPath);
        var stats = statsDoc.RootElement.EnumerateArray().Select(r => r.GetProperty("Id").GetString() ?? "").ToList();
        using var alt = GameTables.Open(altPath);
        int row = 0;
        foreach (var r in alt.RootElement.EnumerateArray())
        {
            int scale = r.TryGetProperty("__col5", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetInt32() : 100;
            string[] ids = r.TryGetProperty("MapStats", out var list) && list.ValueKind == JsonValueKind.Array
                ? [.. list.EnumerateArray().Where(k => k.ValueKind == JsonValueKind.Number).Select(k => k.GetInt32())
                    .Where(i => i >= 0 && i < stats.Count).Select(i => stats[i]).Where(id => id.Length > 0)]
                : [];
            if (ids.Length > 0) types[row] = (scale, ids);
            row++;
        }
        return types;
    }

    public IReadOnlyList<Recipe> Recipes => _recipes;

    public Fragment? FragmentOf(int baseRow) => _fragments.GetValueOrDefault(baseRow);

    public Recipe? Match(IReadOnlyList<int> baseRows)
    {
        if (baseRows.Count == 0) return null;
        var have = baseRows.Order().ToList();
        foreach (var recipe in _recipes)
        {
            if (recipe.AnyOne)
            {
                if (have.Count == 1 && recipe.Items.Contains(have[0])) return recipe;
            }
            else if (have.SequenceEqual(recipe.Items.Order())) return recipe;
        }
        return null;
    }

    /// <summary>The map item's rolled mods and quality, plus each scarab/fragment's MapFragmentMods (StatNMin).</summary>
    public MapMods Of(IReadOnlyList<MapDevice.Slot> slots, ModCatalog mods)
    {
        var sum = new Dictionary<string, int>();
        int quality = 0;
        foreach (var slot in slots)
        {
            if (slot.IsMap)
            {
                quality = slot.Quality;
                AddItemMods(sum, slot.Mods, mods);
                AddQuality(sum, slot.Quality, slot.AlternateQualityType);
            }
            else if (FragmentOf(slot.BaseRow) is { } fragment)
                AddModRows(sum, fragment.ModRows, mods);
        }
        return new MapMods(sum, quality);
    }

    /// <summary>A recipe's own Mods (uber keys: monsters take 70% less damage).</summary>
    public MapMods OfRecipe(Recipe recipe, ModCatalog catalog)
    {
        if (recipe.ModRows.Count == 0) return MapMods.None;
        var sum = new Dictionary<string, int>();
        AddModRows(sum, recipe.ModRows, catalog);
        return sum.Count == 0 ? MapMods.None : new MapMods(sum);
    }

    public bool FragmentsAllowed(IReadOnlyList<MapDevice.Slot> fragments, int areaLevel, out string why)
    {
        why = "";
        var count = new Dictionary<int, int>();
        foreach (var slot in fragments)
        {
            if (FragmentOf(slot.BaseRow) is not { } fragment) continue;
            if (areaLevel > 0 && areaLevel < fragment.MinAreaLevel)
            {
                why = $"{slot.Name} needs an area level of {fragment.MinAreaLevel}.";
                return false;
            }
            int n = count[fragment.Family] = count.GetValueOrDefault(fragment.Family) + 1;
            if (n > fragment.Limit)
            {
                why = $"Only {fragment.Limit} of that kind of map fragment can be used at once.";
                return false;
            }
        }
        return true;
    }

    static void AddItemMods(Dictionary<string, int> sum, IReadOnlyList<ItemMod> mods, ModCatalog catalog)
    {
        foreach (var (id, value) in ItemModStats.Sum([new Item(0, [], 0) { Mods = mods }], catalog))
            if (value != 0) sum[id] = sum.GetValueOrDefault(id) + value;
    }

    void AddQuality(Dictionary<string, int> sum, byte quality, byte? alternate)
    {
        if (quality == 0) return;
        if ((alternate ?? _defaultQualityType) is not int type || !_qualityTypes.TryGetValue(type, out var spec)) return;
        int amount = quality * spec.Scale / 100;
        if (amount == 0) return;
        foreach (string id in spec.Stats)
            sum[id] = sum.GetValueOrDefault(id) + amount;
    }

    static void AddModRows(Dictionary<string, int> sum, IReadOnlyList<int> rows, ModCatalog catalog)
    {
        foreach (int row in rows)
            if (catalog.Row(row) is { } mod)
                foreach (var stat in mod.Stats)
                    if (stat.Min != 0) sum[stat.Id] = sum.GetValueOrDefault(stat.Id) + stat.Min;
    }
}
