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
    public static readonly MapMods None = new(new Dictionary<uint, int>());

    public MapMods(IReadOnlyDictionary<uint, int> stats, int quality = 0)
    {
        Stats = stats;
        Quality = Math.Clamp(quality, 0, 100);
    }

    public IReadOnlyDictionary<uint, int> Stats { get; }
    public int Quality { get; }

    /// <summary>What the area stats are composed from (InstanceEvents.AreaStats): each explicit (prefix / suffix) mod's
    /// stats on its own (they are scaled one by one, truncated, before summing), the implicit, enchant and quality stats,
    /// the fragments' MapFragmentMods stats; how many explicit mods, fragments and scarabs, the scarabs by kind
    /// ("divination", "harvest", …), the map's rarity and corruption. <see cref="Stats"/> is their flat sum.</summary>
    public IReadOnlyList<IReadOnlyDictionary<uint, int>> ExplicitMods { get; init; } = [];
    public IReadOnlyDictionary<uint, int> Implicit { get; init; } = new Dictionary<uint, int>();
    public IReadOnlyDictionary<uint, int> Fragments { get; init; } = new Dictionary<uint, int>();
    public int FragmentCount { get; init; }
    public int ScarabCount { get; init; }
    public IReadOnlyDictionary<string, int> ScarabKinds { get; init; } = new Dictionary<string, int>();
    public ItemRarity MapRarity { get; init; }
    public bool Corrupted { get; init; }
    /// <summary>The Atlas passive families the map counts as held beside the opener's tree ("blight", "harvest", the
    /// second part of a node's id): what a module's own sources of those families' stats name. A family's league block is
    /// carried once, whoever holds it.</summary>
    public IReadOnlySet<string> AtlasFamilies { get; init; } = System.Collections.Frozen.FrozenSet<string>.Empty;

    public int Get(uint id) => Stats.GetValueOrDefault(id);

    /// <summary>Replaces one fragment contribution while preserving every other map source.</summary>
    public MapMods WithFragmentStat(uint id, int value)
    {
        var fragments = Fragments.ToDictionary();
        var stats = Stats.ToDictionary();
        stats[id] = checked(stats.GetValueOrDefault(id) - fragments.GetValueOrDefault(id) + value);
        fragments[id] = value;
        return new(stats, Quality)
        {
            ExplicitMods = ExplicitMods, Implicit = Implicit, Fragments = fragments,
            FragmentCount = FragmentCount, ScarabCount = ScarabCount, ScarabKinds = ScarabKinds,
            MapRarity = MapRarity, Corrupted = Corrupted, AtlasFamilies = AtlasFamilies,
        };
    }

    public int Quantity => Get(Stat.MapItemDropQuantityPlusPct) + Get(Stat.MapImplicitItemDropQuantityPlusPct)
        + Get(Stat.MapUnscaledItemDropQuantityPlusPct);

    public int Rarity => Get(Stat.MapItemDropRarityPlusPct) + Get(Stat.MapImplicitItemDropRarityPlusPct)
        + Get(Stat.MapUnscaledItemDropRarityPlusPct)
        + (Get(Stat.MapItemDropQualityAlsoAppliesToMapItemDropRarity) > 0 ? Quality : 0);

    public int PackSize => Get(Stat.MapPackSizePlusPct) + Get(Stat.MapImplicitPackSizePlusPct)
        + Get(Stat.MapUnscaledPackSizePlusPct);

    public int MonsterLife => Get(Stat.MapMonstersLifePlusPct);
    public int MonsterDamage => Get(Stat.MapMonstersDamagePlusPct);
    public int ExtraRarePacks => Math.Max(0, Get(Stat.MapContainsXAdditionalRarePacks));
    public int ExtraMagicPacks => Math.Max(0, Get(Stat.MapContainsXAdditionalMagicPacks) + PacksDropCards);
    public int ExtraNormalPacks => Math.Max(0, Get(Stat.MapContainsXAdditionalNormalPacks));
    /// <summary>Extra magic packs whose monsters drop cards (the Divination Scarabs').</summary>
    public int PacksDropCards => Get(Stat.MapXMagicPacksDropCardsFromArea);
    public int CardDropChance => SumWhere(id => id.Contains("divination_card", StringComparison.OrdinalIgnoreCase)
        && id.Contains("chance", StringComparison.OrdinalIgnoreCase));
    public int ScarabDropChance => SumWhere(id => id.Contains("scarab", StringComparison.OrdinalIgnoreCase)
        && id.Contains("drop_chance", StringComparison.OrdinalIgnoreCase));
    public int CurrencyDropChance => SumWhere(id => id.Contains("currency", StringComparison.OrdinalIgnoreCase)
        && id.Contains("drop_chance", StringComparison.OrdinalIgnoreCase)
        && !id.Contains("divination", StringComparison.OrdinalIgnoreCase));

    int SumWhere(Func<string, bool> match) => Stats.Where(kv => match(Program.Stats.Name(kv.Key))).Sum(kv => kv.Value);
    public int RarePackIncrease => Get(Stat.MapNumberOfRarePacksPlusPct);
    public int MagicPackIncrease => Get(Stat.MapNumberOfMagicPacksPlusPct);

    public double QuantityFactor => Math.Max(0, 100 + Quantity) / 100.0;
    public double PackSizeFactor => Math.Max(0, 100 + PackSize) / 100.0;

    public AreaPopulation.PackScale Packs =>
        new(PackSizeFactor, ExtraNormalPacks, ExtraMagicPacks, ExtraRarePacks, RarePackIncrease, MagicPackIncrease);

    /// <summary>Folded into a spawned monster's life and hit (maximum_life_+% / damage_+%).</summary>
    public Dictionary<uint, int> MonsterEffectStats()
    {
        var extra = new Dictionary<uint, int>();
        if (MonsterLife != 0) extra[Stat.MaximumLifePlusPct] = MonsterLife;
        if (MonsterDamage != 0) extra[Stat.DamagePlusPct] = MonsterDamage;
        if (Get(Stat.MapMonsterDamageTakenPlusPctFinalFromAtlasKeystone) is int taken and not 0)
            extra[Stat.MapMonsterDamageTakenPlusPctFinalFromAtlasKeystone] = taken;
        // The map tier's hidden more life and damage (the area stats' tier block).
        foreach (uint hidden in new[] { MonsterModCatalog.MapHiddenLifeStat, MonsterModCatalog.MapHiddenDamageStat })
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
    readonly Dictionary<int, (int Scale, IReadOnlyList<uint> Stats)> _qualityTypes;
    readonly int? _defaultQualityType;

    public MapRunCatalog(IReadOnlyDictionary<int, Fragment> fragments, IReadOnlyList<Recipe> recipes,
        IReadOnlyDictionary<int, (int Scale, IReadOnlyList<uint> Stats)>? qualityTypes = null)
    {
        _fragments = new(fragments);
        _recipes = [.. recipes];
        _qualityTypes = qualityTypes is null ? [] : new(qualityTypes);
        _defaultQualityType = _qualityTypes
            .Where(kv => kv.Value.Stats.Any(s => Program.Stats.Name(s).Contains("item_drop_quantity", StringComparison.OrdinalIgnoreCase)))
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

    static Dictionary<int, (int Scale, IReadOnlyList<uint> Stats)> LoadQualityTypes(string folder)
    {
        var types = new Dictionary<int, (int, IReadOnlyList<uint>)>();
        if (!ClientTables.Has("Stats") || !ClientTables.Has("AlternateQualityTypes")) return types;
        var stats = ClientTables.Table("Stats").Select(r => r.Text("Id") ?? "").ToList();
        var alt = ClientTables.Table("AlternateQualityTypes");
        int row = 0;
        foreach (var r in alt)
        {
            int scale = (r.IntOrNull("__col5") ?? 100);
            uint[] ids = [.. r.Ints("MapStats")
                    .Where(i => i >= 0 && i < stats.Count && stats[i].Length > 0).Select(i => (uint)i + 1)];
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
        var explicitMods = new List<IReadOnlyDictionary<uint, int>>();
        var implicitStats = new Dictionary<uint, int>();
        var fragmentStats = new Dictionary<uint, int>();
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
                    var own = new Dictionary<uint, int>();
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
        var sum = new Dictionary<uint, int>();
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
        var sum = new Dictionary<uint, int>();
        AddModRows(sum, recipe.ModRows, catalog);
        // The recipe's mods go into the area stats unscaled, as an item's implicits do.
        return sum.Count == 0 ? MapMods.None : new MapMods(sum) { Implicit = sum };
    }

    public bool FragmentsAllowed(IReadOnlyList<MapDevice.Slot> fragments, int areaLevel, out string why)
    {
        why = "";
        var count = new Dictionary<int, int>();
        int divineVessels = 0;
        foreach (var slot in fragments)
        {
            if (PantheonCatalog.IsDivineVessel(slot.BaseId) && ++divineVessels > 1)
            {
                why = "Only 1 Divine Vessel can be used at once.";
                return false;
            }
            if (FragmentOf(slot.BaseRow) is not { } fragment) continue;
            if (areaLevel > 0 && areaLevel < fragment.MinAreaLevel)
            {
                why = $"{slot.Name} needs an area level of {fragment.MinAreaLevel}.";
                return false;
            }
            // MapFragmentMods stores 149 (or null) for ungrouped fragments/oils/vessels without a family; count each BaseRow separately.
            int familyKey = fragment.Family is < 0 or 149 ? -slot.BaseRow - 1 : fragment.Family;
            int n = count[familyKey] = count.GetValueOrDefault(familyKey) + 1;
            if (n > fragment.Limit)
            {
                why = $"Only {fragment.Limit} of that kind of map fragment can be used at once.";
                return false;
            }
        }
        return true;
    }

    static void AddItemMods(Dictionary<uint, int> sum, IReadOnlyList<ItemMod> mods, ModCatalog catalog)
    {
        foreach (var (id, value) in ItemModStats.Sum([new Item(0, [], 0) { Mods = mods }], catalog))
            if (value != 0) sum[id] = sum.GetValueOrDefault(id) + value;
    }

    void AddQuality(Dictionary<uint, int> sum, byte quality, byte? alternate)
    {
        if (quality == 0) return;
        if ((alternate ?? _defaultQualityType) is not int type || !_qualityTypes.TryGetValue(type, out var spec)) return;
        int amount = quality * spec.Scale / 100;
        if (amount == 0) return;
        foreach (uint id in spec.Stats)
            sum[id] = sum.GetValueOrDefault(id) + amount;
    }

    static void AddModRows(Dictionary<uint, int> sum, IReadOnlyList<int> rows, ModCatalog catalog)
    {
        foreach (int row in rows)
            if (catalog.Row(row) is { } mod)
                foreach (var stat in mod.Stats)
                    if (stat.Min != 0) sum[stat.Id] = sum.GetValueOrDefault(stat.Id) + stat.Min;
    }
}
