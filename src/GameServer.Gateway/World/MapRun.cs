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

    /// <summary>What the area stats are composed from (InstanceEvents.AreaStats): each explicit (prefix / suffix) mod's
    /// stats on its own (they are scaled one by one, truncated, before summing), the implicit, enchant and quality stats,
    /// the fragments' MapFragmentMods stats; how many explicit mods, fragments and scarabs, the scarabs by kind
    /// ("divination", "harvest", …), the map's rarity and corruption. <see cref="Stats"/> is their flat sum.</summary>
    public IReadOnlyList<IReadOnlyDictionary<string, int>> ExplicitMods { get; init; } = [];
    public IReadOnlyDictionary<string, int> Implicit { get; init; } = new Dictionary<string, int>();
    public IReadOnlyDictionary<string, int> Fragments { get; init; } = new Dictionary<string, int>();
    public int FragmentCount { get; init; }
    public int ScarabCount { get; init; }
    public IReadOnlyDictionary<string, int> ScarabKinds { get; init; } = new Dictionary<string, int>();
    public ItemRarity MapRarity { get; init; }
    public bool Corrupted { get; init; }
    public sealed record EncounterRoster(int Generation, IReadOnlyList<string> Monsters);
    public EncounterRoster? Roster { get; init; }

    public int Get(string id) => Stats.GetValueOrDefault(id);

    /// <summary>Replaces one fragment contribution while preserving every other map source.</summary>
    public MapMods WithFragmentStat(string id, int value)
    {
        var fragments = Fragments.ToDictionary();
        var stats = Stats.ToDictionary();
        stats[id] = checked(stats.GetValueOrDefault(id) - fragments.GetValueOrDefault(id) + value);
        fragments[id] = value;
        return new(stats, Quality)
        {
            ExplicitMods = ExplicitMods, Implicit = Implicit, Fragments = fragments,
            FragmentCount = FragmentCount, ScarabCount = ScarabCount, ScarabKinds = ScarabKinds,
            MapRarity = MapRarity, Corrupted = Corrupted, Roster = Roster,
        };
    }

    public int Quantity => Get("map_item_drop_quantity_+%") + Get("map_implicit_item_drop_quantity_+%")
        + Get("map_unscaled_item_drop_quantity_+%");

    public int Rarity => Get("map_item_drop_rarity_+%") + Get("map_implicit_item_drop_rarity_+%")
        + Get("map_unscaled_item_drop_rarity_+%")
        + (Get("map_item_drop_quality_also_applies_to_map_item_drop_rarity") > 0 ? Quality : 0);

    public int PackSize => Get("map_pack_size_+%") + Get("map_implicit_pack_size_+%")
        + Get("map_unscaled_pack_size_+%");

    public int MonsterLife => Get("map_monsters_life_+%");
    public int MonsterDamage => Get("map_monsters_damage_+%");
    public int ExtraRarePacks => Math.Max(0, Get("map_contains_x_additional_rare_packs"));
    public int ExtraMagicPacks => Math.Max(0, Get("map_contains_x_additional_magic_packs") + PacksDropCards);
    public int ExtraNormalPacks => Math.Max(0, Get("map_contains_x_additional_normal_packs"));
    /// <summary>Extra magic packs whose monsters drop cards (the Divination Scarabs').</summary>
    public int PacksDropCards => Get("map_X_magic_packs_drop_cards_from_area");
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
        // The map tier's hidden more life and damage (the area stats' tier block).
        foreach (string hidden in new[] { MonsterModCatalog.MapHiddenLifeStat, MonsterModCatalog.MapHiddenDamageStat })
            if (Get(hidden) is int more and not 0) extra[hidden] = more;
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
        if (ClientTables.Has("MapFragmentMods"))
        {
            foreach (var row in ClientTables.Table("MapFragmentMods"))
            {
                if (row.IntOrNull("BaseItemTypesKey") is not int baseKey) continue;
                int[] mods = [.. row.Ints("ModsKeys")];
                fragments[baseKey] = new(
                    (row.IntOrNull("MapFragmentFamilies") ?? -1),
                    row.IntOrNull("MapFragmentLimit") is int lim ? Math.Max(1, lim) : 1,
                    (row.IntOrNull("MinAreaLevel") ?? 1),
                    mods);
            }
        }

        var recipes = new List<Recipe>();
        if (ClientTables.Has("MapDeviceRecipes"))
        {
            foreach (var row in ClientTables.Table("MapDeviceRecipes"))
            {
                int area = (row.IntOrNull("WorldArea") ?? 0);
                if (area <= 0) continue;
                int[] items = [.. row.Ints("RecipeItems")];
                if (items.Length == 0) continue;
                // __col9: one item from the list (Maven invitations). Else the full multiset (Atziri, Shaper, 4× uber).
                bool anyOne = row.BoolOrNull("__col9") == true;
                int[] recipeMods = [.. row.Ints("Mods")];
                recipes.Add(new(row.Text("Id") ?? "", items, anyOne,
                    area, (row.IntOrNull("AreaLevel") ?? 0),
                    recipeMods));
            }
        }
        return new MapRunCatalog(fragments, recipes, LoadQualityTypes(folder));
    }

    static Dictionary<int, (int Scale, IReadOnlyList<string> Stats)> LoadQualityTypes(string folder)
    {
        var types = new Dictionary<int, (int, IReadOnlyList<string>)>();
        if (!ClientTables.Has("Stats") || !ClientTables.Has("AlternateQualityTypes")) return types;
        var stats = ClientTables.Table("Stats").Select(r => r.Text("Id") ?? "").ToList();
        var alt = ClientTables.Table("AlternateQualityTypes");
        int row = 0;
        foreach (var r in alt)
        {
            int scale = (r.IntOrNull("__col5") ?? 100);
            string[] ids = [.. r.Ints("MapStats")
                    .Where(i => i >= 0 && i < stats.Count).Select(i => stats[i]).Where(id => id.Length > 0)];
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
        var explicitMods = new List<IReadOnlyDictionary<string, int>>();
        var implicitStats = new Dictionary<string, int>();
        var fragmentStats = new Dictionary<string, int>();
        var scarabKinds = new Dictionary<string, int>();
        int quality = 0, fragmentCount = 0, scarabCount = 0;
        var rarity = ItemRarity.Normal;
        bool corrupted = false;
        foreach (var slot in slots)
        {
            if (slot.IsMap)
            {
                quality = slot.Quality;
                rarity = slot.Rarity;
                corrupted = slot.Corrupted;
                foreach (var mod in slot.Mods.Where(m => m.Section == ModSection.Explicit))
                {
                    var own = new Dictionary<string, int>();
                    AddItemMods(own, [mod], mods);
                    explicitMods.Add(own);
                }
                AddItemMods(implicitStats, [.. slot.Mods.Where(m => m.Section != ModSection.Explicit)], mods);
                AddQuality(implicitStats, slot.Quality, slot.AlternateQualityType);
            }
            else if (FragmentOf(slot.BaseRow) is { } fragment)
            {
                fragmentCount++;
                AddModRows(fragmentStats, fragment.ModRows, mods);
                // A scarab's BaseItemTypes Id (Metadata/Items/MapFragments/ScarabDivination2 …) names its kind.
                string leaf = slot.BaseId.Split('/')[^1];
                if (leaf.StartsWith("Scarab", StringComparison.Ordinal))
                {
                    scarabCount++;
                    if (System.Text.RegularExpressions.Regex.Match(leaf, @"^Scarab([A-Za-z]+?)\d") is { Success: true } kind)
                    {
                        string name = kind.Groups[1].Value.ToLowerInvariant();
                        scarabKinds[name] = scarabKinds.GetValueOrDefault(name) + 1;
                    }
                }
            }
        }
        var sum = new Dictionary<string, int>();
        foreach (var part in explicitMods.Append(implicitStats).Append(fragmentStats))
            foreach (var (id, value) in part) sum[id] = sum.GetValueOrDefault(id) + value;
        return new MapMods(sum, quality)
        {
            ExplicitMods = explicitMods, Implicit = implicitStats, Fragments = fragmentStats, FragmentCount = fragmentCount,
            ScarabCount = scarabCount, ScarabKinds = scarabKinds, MapRarity = rarity, Corrupted = corrupted,
        };
    }

    /// <summary>A recipe's own Mods (uber keys: monsters take 70% less damage).</summary>
    public MapMods OfRecipe(Recipe recipe, ModCatalog catalog)
    {
        if (recipe.ModRows.Count == 0) return MapMods.None;
        var sum = new Dictionary<string, int>();
        AddModRows(sum, recipe.ModRows, catalog);
        // The recipe's mods go into the area stats unscaled, as an item's implicits do.
        return sum.Count == 0 ? MapMods.None : new MapMods(sum) { Implicit = sum };
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
