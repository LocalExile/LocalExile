using GameServer.Core.Protocol.Instance;
using GameServer.Storage;
using Row = System.Collections.Generic.IReadOnlyDictionary<string, object?>;

/// <summary>
/// The item and jewel catalogues: world.db's typed tables (unique_items, radius_jewels, unique_jewels, timeless_jewels,
/// tattoo_effects, jewel_visuals and their child tables). Each catalogue is loaded at startup and replaced atomically
/// after <c>/reload</c>; a disabled entry is left out.
/// </summary>
public static class GameDataCatalog
{
    static UniqueCatalog.Definition[] _uniques = [];
    static RadiusJewelCatalogue.Entry[] _radius = [];
    static RadiusJewelCatalogue.Entry[] _threshold = [];
    static RadiusJewelCatalogue.Entry[] _radiusAll = [];
    static UniqueJewelCatalogue.Entry[] _globals = [];
    static UniqueJewelCatalogue.Entry[] _specials = [];
    static TimelessJewelCatalog.Entry[] _timeless = [];
    static IReadOnlyDictionary<string, (string Stat, int Value)[]> _tattoos = Empty<(string, int)[]>();
    static IReadOnlyDictionary<string, ushort> _visuals = Empty<ushort>();

    public static UniqueCatalog.Definition[] Uniques => Volatile.Read(ref _uniques);
    public static RadiusJewelCatalogue.Entry[] RadiusJewels => Volatile.Read(ref _radiusAll);
    public static RadiusJewelCatalogue.Entry[] ThresholdJewels => Volatile.Read(ref _threshold);
    public static UniqueJewelCatalogue.Entry[] UniqueJewelGlobals => Volatile.Read(ref _globals);
    public static UniqueJewelCatalogue.Entry[] UniqueJewelSpecials => Volatile.Read(ref _specials);
    public static TimelessJewelCatalog.Entry[] TimelessJewels => Volatile.Read(ref _timeless);
    public static IReadOnlyDictionary<string, (string Stat, int Value)[]> TattooEffects => Volatile.Read(ref _tattoos);
    public static IReadOnlyDictionary<string, ushort> JewelVisuals => Volatile.Read(ref _visuals);

    public static string Summary =>
        $"{Uniques.Length} unique item(s), {RadiusJewels.Length} radius jewel(s), {UniqueJewelGlobals.Length + UniqueJewelSpecials.Length} unique jewel(s), " +
        $"{TimelessJewels.Length} timeless jewel(s), {TattooEffects.Count} tattoo effect(s), {JewelVisuals.Count} jewel art row(s)";

    /// <summary>Empties the catalogues (the store was closed).</summary>
    public static void Reset()
    {
        Volatile.Write(ref _uniques, []);
        Volatile.Write(ref _radius, []);
        Volatile.Write(ref _threshold, []);
        Volatile.Write(ref _radiusAll, []);
        Volatile.Write(ref _globals, []);
        Volatile.Write(ref _specials, []);
        Volatile.Write(ref _timeless, []);
        Volatile.Write(ref _tattoos, Empty<(string, int)[]>());
        Volatile.Write(ref _visuals, Empty<ushort>());
    }

    public static void Reload(GameDataStore store)
    {
        var uniques = LoadUniques(store);
        var (radius, threshold) = LoadRadiusJewels(store);
        var (globals, specials) = LoadUniqueJewels(store);
        var timeless = LoadTimeless(store);
        var tattoos = LoadTattoos(store);
        var visuals = Enabled(store.LoadRows("jewel_visuals", "name"))
            .ToDictionary(row => Text(row, "name"), row => (ushort)Int(row, "hash"), StringComparer.OrdinalIgnoreCase);

        Volatile.Write(ref _uniques, uniques);
        Volatile.Write(ref _radius, radius);
        Volatile.Write(ref _threshold, threshold);
        Volatile.Write(ref _radiusAll, [.. radius, .. threshold]);
        Volatile.Write(ref _globals, globals);
        Volatile.Write(ref _specials, specials);
        Volatile.Write(ref _timeless, timeless);
        Volatile.Write(ref _tattoos, tattoos);
        Volatile.Write(ref _visuals, visuals);
    }

    static UniqueCatalog.Definition[] LoadUniques(GameDataStore store)
    {
        var mods = Children(store, "unique_item_mods", "name, base_type, implicit, ord", "name", "base_type");
        var ranges = Children(store, "unique_item_ranges", "name, base_type, ord", "name", "base_type");
        var groups = Children(store, "unique_item_roll_groups", "name, base_type, grp", "name", "base_type");
        var options = Children(store, "unique_item_roll_options", "name, base_type, grp, ord", "name", "base_type", "grp");
        var identities = store.LoadRows("unique_item_identities", "name, base_type")
            .ToDictionary(r => Key(r, "name", "base_type"), r => Text(r, "visual_identity"));
        return [.. Enabled(store.LoadRows("unique_items", "name, base_type")).Select(row =>
        {
            string key = Key(row, "name", "base_type");
            var own = mods[key].ToLookup(mod => Bool(mod, "implicit"), mod => Text(mod, "mod"));
            return new UniqueCatalog.Definition(Text(row, "name"), Text(row, "base_type"), [.. own[false]],
                (byte)Int(row, "item_level"), Bool(row, "corrupted"), Bool(row, "own_implicits") ? [.. own[true]] : null,
                ranges[key].Any() ? [.. ranges[key].Select(r => new UniqueCatalog.RollRange(Text(r, "mod"), (int)Int(r, "stat"),
                    (int)Int(r, "min"), (int)Int(r, "max"), Bool(r, "implicit")))] : null,
                Bool(row, "mirrored"), (ItemInfluence)Int(row, "influences"), (byte)Int(row, "fractured"),
                Bool(row, "encounter_only"),
                groups[key].Any() ? [.. groups[key].Select(g => new UniqueCatalog.RollGroup((int)Int(g, "count"),
                    [.. options[Key(g, "name", "base_type", "grp")].Select(o => Text(o, "mod"))],
                    IntOrNull(g, "high_level"), IntOrNull(g, "high_level_count"),
                    [.. options[Key(g, "name", "base_type", "grp")].Select(o => o.ContainsKey("weight") ? (int)Int(o, "weight") : 1)]))] : null,
                (int)Int(row, "jewel_limit")) { EncounterItemLevelBonus = IntOrNull(row, "encounter_item_level_bonus"),
                    VisualIdentityId = identities.GetValueOrDefault(key) };
        })];
    }

    static (RadiusJewelCatalogue.Entry[] Radius, RadiusJewelCatalogue.Entry[] Threshold) LoadRadiusJewels(GameDataStore store)
    {
        var mods = Children(store, "radius_jewel_mods", "kind, name, base, role, ord", "kind", "name", "base");
        var variants = Children(store, "radius_jewel_variants", "kind, name, base, ord", "kind", "name", "base");
        var entries = Enabled(store.LoadRows("radius_jewels", "kind, name, base")).Select(row =>
        {
            string key = Key(row, "kind", "name", "base");
            var byRole = mods[key].ToLookup(mod => Text(mod, "role"), mod => Text(mod, "mod"));
            return (Kind: Text(row, "kind"), Entry: new RadiusJewelCatalogue.Entry(Text(row, "name"), Text(row, "base"),
                [.. byRole["global"]], Text(row, "conversion_mod"), (int)Int(row, "jewel_limit"), (byte)Int(row, "level"),
                Bool(row, "corrupted"), (int)Int(row, "radius"),
                variants[key].Any() ? [.. variants[key].Select(v => new RadiusJewelCatalogue.Variant(Text(v, "variant"),
                    Text(v, "mod_id"), IntOrNull(v, "value")))] : null,
                byRole["extra_conversion"].Any() ? [.. byRole["extra_conversion"]] : null,
                Bool(row, "variant_keeps_conversion_mod")));
        }).ToList();
        return ([.. entries.Where(e => e.Kind == "radius").Select(e => e.Entry)],
            [.. entries.Where(e => e.Kind == "threshold").Select(e => e.Entry)]);
    }

    static (UniqueJewelCatalogue.Entry[] Global, UniqueJewelCatalogue.Entry[] Special) LoadUniqueJewels(GameDataStore store)
    {
        var mods = Children(store, "unique_jewel_mods", "kind, name, ord", "kind", "name");
        var entries = Enabled(store.LoadRows("unique_jewels", "kind, name")).Select(row =>
        {
            var own = mods[Key(row, "kind", "name")].ToList();
            return (Kind: Text(row, "kind"), Entry: new UniqueJewelCatalogue.Entry(Text(row, "name"), Text(row, "base"),
                Bool(row, "corrupted"), (int)Int(row, "level"), [.. own.Select(m => Text(m, "mod"))],
                [.. own.Select(m => IntOrNull(m, "value"))]));
        }).ToList();
        return ([.. entries.Where(e => e.Kind == "global").Select(e => e.Entry)],
            [.. entries.Where(e => e.Kind == "special").Select(e => e.Entry)]);
    }

    static TimelessJewelCatalog.Entry[] LoadTimeless(GameDataStore store)
    {
        var conquerors = Children(store, "timeless_jewel_conquerors", "jewel, ord", "jewel");
        return [.. Enabled(store.LoadRows("timeless_jewels", "name")).Select(row => new TimelessJewelCatalog.Entry(
            Text(row, "name"), (int)Int(row, "tree_version"), Text(row, "faction"), Text(row, "mod_id"),
            (int)Int(row, "min_seed"), (int)Int(row, "max_seed"), (int)Int(row, "display_multiplier"),
            [.. conquerors[Text(row, "name")].Select(c => new TimelessJewelCatalog.Conqueror(Text(c, "name"),
                (int)Int(c, "conqueror_index"), (int)Int(c, "version")))]))];
    }

    static IReadOnlyDictionary<string, (string Stat, int Value)[]> LoadTattoos(GameDataStore store)
    {
        var stats = Children(store, "tattoo_effect_stats", "tattoo, ord", "tattoo");
        return Enabled(store.LoadRows("tattoo_effects", "name")).ToDictionary(row => Text(row, "name"),
            row => stats[Text(row, "name")].Select(s => (Text(s, "stat"), (int)Int(s, "value"))).ToArray(), StringComparer.Ordinal);
    }

    /// <summary>A child table's rows by their parent's key (its key columns joined with '|'), in the given order.</summary>
    static ILookup<string, Row> Children(GameDataStore store, string table, string orderBy, params string[] parent) =>
        store.LoadRows(table, orderBy).ToLookup(row => Key(row, parent), StringComparer.Ordinal);

    static string Key(Row row, params string[] columns) =>
        string.Join('|', columns.Select(column => Convert.ToString(row[column], System.Globalization.CultureInfo.InvariantCulture)));

    static IEnumerable<Row> Enabled(IEnumerable<Row> rows) => rows.Where(row => Bool(row, "enabled"));

    static string Text(Row row, string column) => row[column] as string
        ?? throw new InvalidOperationException($"catalogue row {Key(row, [.. row.Keys.Take(2)])}: {column} is not text");

    static long Int(Row row, string column) => row[column] is long value ? value
        : throw new InvalidOperationException($"catalogue row {Key(row, [.. row.Keys.Take(2)])}: {column} is not an integer");

    static int? IntOrNull(Row row, string column) => row[column] is null ? null : (int)Int(row, column);

    static bool Bool(Row row, string column) => Int(row, column) != 0;

    static IReadOnlyDictionary<string, T> Empty<T>() => new Dictionary<string, T>();
}
