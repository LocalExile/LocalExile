using System.Text.Json;
using GameServer.Storage;

/// <summary>
/// The item and jewel catalogues: world.db's <c>catalog_entries</c> rows (one JSON payload per entry and kind). Each
/// kind is loaded at startup and replaced atomically after <c>/reload</c>.
/// </summary>
static class GameDataCatalog
{
    public sealed record TattooStatRow(string Stat, int Value);
    public sealed record TattooEffectRow(string Name, TattooStatRow[] Stats);
    public sealed record VisualRow(string Name, ushort Hash);

    static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    static UniqueCatalog.Definition[] _uniques = [];
    static RadiusJewelCatalogue.Entry[] _radius = [];
    static RadiusJewelCatalogue.Entry[] _threshold = [];
    static RadiusJewelCatalogue.Entry[] _radiusAll = [];
    static UniqueJewelCatalogue.Entry[] _globals = [];
    static UniqueJewelCatalogue.Entry[] _specials = [];
    static TimelessJewelCatalog.Entry[] _timeless = [];
    static IReadOnlyDictionary<string, (string Stat, int Value)[]> _tattoos = ToTattooMap([]);
    static IReadOnlyDictionary<string, ushort> _visuals = ToVisualMap([]);

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
        Volatile.Write(ref _tattoos, ToTattooMap([]));
        Volatile.Write(ref _visuals, ToVisualMap([]));
    }

    public static void Reload(GameDataStore store)
    {
        var uniques = Load<UniqueCatalog.Definition>(store, "unique_item");
        var radius = Load<RadiusJewelCatalogue.Entry>(store, "radius_jewel");
        var threshold = Load<RadiusJewelCatalogue.Entry>(store, "threshold_jewel");
        var globals = Load<UniqueJewelCatalogue.Entry>(store, "unique_jewel_global");
        var specials = Load<UniqueJewelCatalogue.Entry>(store, "unique_jewel_special");
        var timeless = Load<TimelessJewelCatalog.Entry>(store, "timeless_jewel");
        var tattoos = ToTattooMap(Load<TattooEffectRow>(store, "tattoo_effect"));
        var visuals = ToVisualMap(Load<VisualRow>(store, "unique_jewel_visual"));

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

    static T[] Load<T>(GameDataStore store, string kind) =>
        [.. store.LoadCatalog(kind).Where(row => row.Enabled)
            .Select(row => JsonSerializer.Deserialize<T>(row.Payload, Json)
                ?? throw new InvalidOperationException($"catalog {kind} row {row.Key} is empty"))];

    static IReadOnlyDictionary<string, (string Stat, int Value)[]> ToTattooMap(IEnumerable<TattooEffectRow> rows) =>
        rows.ToDictionary(row => row.Name,
            row => row.Stats.Select(stat => (stat.Stat, stat.Value)).ToArray(), StringComparer.Ordinal);

    static IReadOnlyDictionary<string, ushort> ToVisualMap(IEnumerable<VisualRow> rows) =>
        rows.ToDictionary(row => row.Name, row => row.Hash, StringComparer.OrdinalIgnoreCase);
}
