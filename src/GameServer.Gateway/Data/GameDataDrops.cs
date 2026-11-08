using System.Text.Json;
using GameServer.Storage;

/// <summary>
/// The drop tables' seed and immutable snapshot. The seed is the client's own tables (the bosses of every area from
/// WorldAreas/MonsterVarieties, each currency's DropLevel from BaseItemTypes), the compiled currency weights
/// (<see cref="ItemDrops.Currency"/>) and the researched rows in <see cref="DropDataSeed"/>. The SQLite rows are what the
/// server serves after the first open. A reader takes the snapshot, never SQLite.
/// </summary>
static class GameDataDrops
{
    /// <summary>Bump when the seed changes: a database seeded by an older generation is refilled.</summary>
    public const string SeedVersion = "3";

    public static bool Seed(GameDataStore store, string folder = "data", bool force = false)
    {
        if (!force && store.DropSeedVersion() == SeedVersion) return false;
        try
        {
            if (ReadTables(folder) is not var (weights, bosses))
            {
                Console.WriteLine("[gateway] drop data: the game tables are missing; extract them first");
                return false;
            }
            var bossItems = DropDataSeed.BossItems;
            var cardSources = bossItems.Where(row => row.ItemKind == "card")
                .Select(row => new GameDataStore.CardSourceRow(row.ItemId, "boss", row.BossId, null, 0,
                    row.Confidence, row.Source, row.Notes)).ToList();
            store.InTransaction(() =>
            {
                store.ReplaceCurrencyWeights(weights);
                store.ReplaceBosses(bosses);
                store.ReplaceBossItems(bossItems);
                store.ReplaceCardAreas([.. DropDataSeed.CardAreas]);
                store.ReplaceCardSources(cardSources);
                store.ReplaceItemWeights([.. DropDataSeed.ItemWeights]);
            });
            store.MarkDropSeeded();
            store.MarkDropSeedVersion(SeedVersion);
            var counts = store.DropCounts();
            Console.WriteLine($"[gateway] drop data seed: {counts.CurrencyWeights} currency weight(s), " +
                $"{counts.Bosses} boss(es), {counts.BossItems} boss item(s), {counts.CardAreas} card area(s), " +
                $"{counts.ItemWeights} item weight(s)");
            return true;
        }
        catch (Exception ex) when (ex is IOException or JsonException or KeyNotFoundException or InvalidOperationException
            or Microsoft.Data.Sqlite.SqliteException)
        {
            Console.WriteLine($"[gateway] drop data seed failed: {ex.Message}; using the code fallback");
            return false;
        }
    }

    public static void Reload(GameDataStore store, DropDataProvider? provider = null) =>
        (provider ?? DropDataProvider.Default).Replace(store);

    public static void Reset() => DropDataProvider.Default.Reset();

    /// <summary>The rows the client's tables give: the compiled currency weights with each base's DropLevel, and every
    /// area's bosses (a boss in several areas keeps its first). Null when the tables have not been extracted.</summary>
    static (List<GameDataStore.CurrencyWeightRow>, List<GameDataStore.BossRow>)? ReadTables(string folder)
    {
        string baseTypes = Path.Combine(folder, "tables", "BaseItemTypes.json");
        string areas = Path.Combine(folder, "tables", "WorldAreas.json");
        string varieties = Path.Combine(folder, "tables", "MonsterVarieties.json");
        if (!File.Exists(baseTypes) || !File.Exists(areas) || !File.Exists(varieties)) return null;

        var dropLevels = new Dictionary<string, int>(StringComparer.Ordinal);
        using (var doc = JsonDocument.Parse(File.ReadAllBytes(baseTypes)))
            foreach (var row in doc.RootElement.EnumerateArray())
                if (row.GetProperty("Id").GetString() is { } id) dropLevels.TryAdd(id, row.GetProperty("DropLevel").GetInt32());
        var weights = ItemDrops.Currency.Select(c => new GameDataStore.CurrencyWeightRow(c.Id, c.Weight,
            dropLevels.GetValueOrDefault(c.Id), true, "core", "estimated", "ItemDrops.Currency")).ToList();

        using var varietyDoc = JsonDocument.Parse(File.ReadAllBytes(varieties));
        var monsters = varietyDoc.RootElement;
        using var areaDoc = JsonDocument.Parse(File.ReadAllBytes(areas));
        var bosses = new Dictionary<string, GameDataStore.BossRow>(StringComparer.Ordinal);
        foreach (var area in areaDoc.RootElement.EnumerateArray())
        {
            string areaId = area.GetProperty("Id").GetString() ?? "";
            string category = Category(area);
            foreach (var key in area.GetProperty("Bosses_MonsterVarietiesKeys").EnumerateArray())
            {
                int row = key.GetInt32();
                if (row < 0 || row >= monsters.GetArrayLength()) continue;
                var monster = monsters[row];
                if (monster.GetProperty("Id").GetString() is not { Length: > 0 } bossId) continue;
                string name = monster.GetProperty("Name").GetString() is { Length: > 0 } n ? n : bossId;
                bosses.TryAdd(bossId, new(bossId, name, areaId, category, true));
            }
        }
        return (weights, [.. bosses.Values]);
    }

    /// <summary>The area's kind, from WorldAreas' own flags: a unique map, a map, the Labyrinth, an act area, or other
    /// (act 0: Delve, Synthesis).</summary>
    static string Category(JsonElement area) =>
        area.GetProperty("IsUniqueMapArea").GetBoolean() ? "unique-map"
        : area.GetProperty("IsMapArea").GetBoolean() ? "map"
        : area.GetProperty("IsLabyrinthArea").GetBoolean() || area.GetProperty("IsLabyrinthBossArea").GetBoolean() ? "labyrinth"
        : area.GetProperty("Act").GetInt32() > 0 ? "campaign"
        : "other";
}

/// <summary>The published drop tables. Empty means the database has not been seeded; callers keep their code fallback.</summary>
sealed class DropDataProvider
{
    public sealed record Snapshot(IReadOnlyDictionary<string, GameDataStore.CurrencyWeightRow> CurrencyWeights,
        IReadOnlyDictionary<string, GameDataStore.CurrencySourceRow> CurrencySources,
        IReadOnlyDictionary<string, GameDataStore.BossRow> Bosses,
        IReadOnlyDictionary<(string BossId, string ItemId), GameDataStore.BossItemRow> BossItems,
        IReadOnlyDictionary<(string AreaId, string CardId), GameDataStore.CardAreaRow> CardAreas,
        IReadOnlyDictionary<(string CardId, string SourceKind, string SourceId), GameDataStore.CardSourceRow> CardSources,
        IReadOnlyDictionary<(string Category, string ItemId), GameDataStore.ItemWeightRow> ItemWeights)
    {
        public static Snapshot Empty { get; } = new(
            new Dictionary<string, GameDataStore.CurrencyWeightRow>(),
            new Dictionary<string, GameDataStore.CurrencySourceRow>(),
            new Dictionary<string, GameDataStore.BossRow>(),
            new Dictionary<(string, string), GameDataStore.BossItemRow>(),
            new Dictionary<(string, string), GameDataStore.CardAreaRow>(),
            new Dictionary<(string, string, string), GameDataStore.CardSourceRow>(),
            new Dictionary<(string, string), GameDataStore.ItemWeightRow>());
    }

    public static DropDataProvider Default { get; } = new();

    Snapshot _snapshot = Snapshot.Empty;
    int _hasTable;

    public Snapshot Current => Volatile.Read(ref _snapshot);
    public bool HasTable => Volatile.Read(ref _hasTable) != 0;

    public void Replace(GameDataStore store)
    {
        var weights = store.LoadCurrencyWeights().ToDictionary(row => row.CurrencyId);
        var sources = store.LoadCurrencySources().ToDictionary(row => $"{row.CurrencyId}|{row.SourceKind}|{row.SourceId}");
        var bosses = store.LoadBosses().ToDictionary(row => row.BossId);
        var items = store.LoadBossItems().ToDictionary(row => (row.BossId, row.ItemId));
        var areas = store.LoadCardAreas().ToDictionary(row => (row.AreaId, row.CardId));
        var cards = store.LoadCardSources().ToDictionary(row => (row.CardId, row.SourceKind, row.SourceId));
        var itemWeights = store.LoadItemWeights().ToDictionary(row => (row.Category, row.ItemId));
        Volatile.Write(ref _snapshot, new Snapshot(weights, sources, bosses, items, areas, cards, itemWeights));
        Volatile.Write(ref _hasTable, weights.Count + sources.Count + bosses.Count + items.Count + areas.Count + cards.Count
            + itemWeights.Count > 0 ? 1 : 0);
    }

    public void Reset()
    {
        Volatile.Write(ref _snapshot, Snapshot.Empty);
        Volatile.Write(ref _hasTable, 0);
    }
}
