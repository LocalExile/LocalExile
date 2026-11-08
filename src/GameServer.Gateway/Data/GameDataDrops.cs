using System.Globalization;
using System.Text;
using GameServer.Storage;

/// <summary>
/// The drop tables' seed and immutable snapshot. The CSV files in
/// <c>Agents/docs/game</c> are the researched seed; the SQLite rows are what the
/// server serves after the first open. A reader takes the snapshot, never SQLite.
/// </summary>
static class GameDataDrops
{
    public const string DefaultFolder = "Agents/docs/game";

    public static bool Seed(GameDataStore store, string folder = DefaultFolder, bool force = false)
    {
        if (!force && store.DropSeeded()) return false;
        try
        {
            string Currency = Path.Combine(folder, "currency-weights.csv");
            string Bosses = Path.Combine(folder, "bosses.csv");
            string BossDrops = Path.Combine(folder, "boss-drops.csv");
            string CardAreas = Path.Combine(folder, "divination-card-areas.csv");
            if (new[] { Currency, Bosses, BossDrops, CardAreas }.Any(path => !File.Exists(path)))
            {
                Console.WriteLine($"[gateway] drop data: seed files missing in {folder}; using the code fallback");
                return false;
            }
            var weights = CurrencyWeights(ReadCsv(Currency));
            var (bosses, bossItems) = BossRows(ReadCsv(Bosses), ReadCsv(BossDrops));
            var cards = CardAreaRows(ReadCsv(CardAreas));
            var cardSources = bossItems.Where(row => row.ItemKind == "card")
                .Select(row => new GameDataStore.CardSourceRow(row.ItemId, "boss", row.BossId, null, 0,
                    row.Confidence, row.Source, row.Notes)).ToList();
            store.InTransaction(() =>
            {
                store.ReplaceCurrencyWeights(weights);
                store.ReplaceBosses(bosses);
                store.ReplaceBossItems(bossItems);
                store.ReplaceCardAreas(cards);
                store.ReplaceCardSources(cardSources);
            });
            store.MarkDropSeeded();
            var counts = store.DropCounts();
            Console.WriteLine($"[gateway] drop data seed: {counts.CurrencyWeights} currency weight(s), " +
                $"{counts.Bosses} boss(es), {counts.BossItems} boss item(s), {counts.CardAreas} card area(s)");
            return true;
        }
        catch (Exception ex) when (ex is IOException or FormatException or ArgumentException or InvalidDataException
            or Microsoft.Data.Sqlite.SqliteException)
        {
            Console.WriteLine($"[gateway] drop data seed failed: {ex.Message}; using the code fallback");
            return false;
        }
    }

    public static void Reload(GameDataStore store, DropDataProvider? provider = null) =>
        (provider ?? DropDataProvider.Default).Replace(store);

    public static void Reset() => DropDataProvider.Default.Reset();

    static List<GameDataStore.CurrencyWeightRow> CurrencyWeights(List<Dictionary<string, string>> rows) =>
        [.. rows.Select(row => new GameDataStore.CurrencyWeightRow(
            Text(row, "currency_id"), Number(row, "weight"), Integer(row, "drop_level"),
            Boolean(row, "enabled", true), Text(row, "source"), Text(row, "confidence", "unknown"),
            Optional(row, "notes")))];

    static (List<GameDataStore.BossRow>, List<GameDataStore.BossItemRow>) BossRows(
        List<Dictionary<string, string>> bossRows, List<Dictionary<string, string>> dropRows)
    {
        var bosses = new Dictionary<string, GameDataStore.BossRow>(StringComparer.Ordinal);
        foreach (var row in bossRows)
        {
            string area = Text(row, "area_id");
            string category = Text(row, "category", "other");
            string[] names = Text(row, "bosses").Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            string[] ids = Text(row, "boss_ids").Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < ids.Length; i++)
                bosses.TryAdd(ids[i], new(ids[i], i < names.Length ? names[i] : ids[i], area, category, true));
        }
        var items = dropRows.Select(row => new GameDataStore.BossItemRow(
            Text(row, "boss_id"), Text(row, "item"), Text(row, "item_kind", "other"),
            OptionalNumber(row, "chance"), Text(row, "confidence", "unknown"), Text(row, "source"),
            Optional(row, "notes"))).ToList();
        return ([.. bosses.Values], items);
    }

    static List<GameDataStore.CardAreaRow> CardAreaRows(List<Dictionary<string, string>> rows) =>
        [.. rows.Select(row =>
        {
            string area = Text(row, "area");
            return new GameDataStore.CardAreaRow(area, area, Text(row, "card"),
                Math.Clamp(Integer(row, "min_map_tier", 1), 1, 16), Text(row, "source"));
        })];

    static string Text(IReadOnlyDictionary<string, string> row, string name, string fallback = "") =>
        row.TryGetValue(name, out string? value) && value.Length > 0 ? value : fallback;

    static string? Optional(IReadOnlyDictionary<string, string> row, string name) =>
        row.TryGetValue(name, out string? value) && value.Length > 0 ? value : null;

    static int Integer(IReadOnlyDictionary<string, string> row, string name, int fallback = 0) =>
        row.TryGetValue(name, out string? value) && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int number)
            ? number : fallback;

    static double Number(IReadOnlyDictionary<string, string> row, string name) =>
        ParseNumber(Text(row, name)) ?? throw new FormatException($"{name} is not a finite number");

    static double? OptionalNumber(IReadOnlyDictionary<string, string> row, string name) =>
        Optional(row, name) is { } text
            ? ParseNumber(text) ?? throw new FormatException($"{name} is not a finite number")
            : null;

    /// <summary>The seed files are written by hand in either decimal style, so both <c>0.15</c> and <c>0,15</c> load.</summary>
    static double? ParseNumber(string text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double number)
            || double.TryParse(text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out number)
            ? double.IsFinite(number) ? number : null
            : null;

    static bool Boolean(IReadOnlyDictionary<string, string> row, string name, bool fallback) =>
        row.TryGetValue(name, out string? value)
            ? value is "1" or "true" or "True" or "yes" or "Yes" ? true
                : value is "0" or "false" or "False" or "no" or "No" ? false : fallback
            : fallback;

    /// <summary>A small CSV reader: quoted fields, doubled quotes and commas inside quotes.</summary>
    internal static List<Dictionary<string, string>> ReadCsv(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException(path);
        var lines = File.ReadAllLines(path, Encoding.UTF8);
        if (lines.Length == 0) return [];
        var header = ParseLine(lines[0]);
        var rows = new List<Dictionary<string, string>>();
        for (int i = 1; i < lines.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(lines[i])) continue;
            var values = ParseLine(lines[i]);
            var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int column = 0; column < header.Count && column < values.Count; column++) row[header[column]] = values[column];
            rows.Add(row);
        }
        return rows;
    }

    static List<string> ParseLine(string line)
    {
        var values = new List<string>();
        var value = new StringBuilder();
        bool quoted = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (quoted)
            {
                if (c == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"') { value.Append('"'); i++; }
                    else quoted = false;
                }
                else value.Append(c);
            }
            else if (c == '"') quoted = true;
            else if (c == ',') { values.Add(value.ToString()); value.Clear(); }
            else value.Append(c);
        }
        values.Add(value.ToString());
        return values;
    }
}

/// <summary>The published drop tables. Empty means the database has not been seeded; callers keep their code fallback.</summary>
sealed class DropDataProvider
{
    public sealed record Snapshot(IReadOnlyDictionary<string, GameDataStore.CurrencyWeightRow> CurrencyWeights,
        IReadOnlyDictionary<string, GameDataStore.CurrencySourceRow> CurrencySources,
        IReadOnlyDictionary<string, GameDataStore.BossRow> Bosses,
        IReadOnlyDictionary<(string BossId, string ItemId), GameDataStore.BossItemRow> BossItems,
        IReadOnlyDictionary<(string AreaId, string CardId), GameDataStore.CardAreaRow> CardAreas,
        IReadOnlyDictionary<(string CardId, string SourceKind, string SourceId), GameDataStore.CardSourceRow> CardSources)
    {
        public static Snapshot Empty { get; } = new(
            new Dictionary<string, GameDataStore.CurrencyWeightRow>(),
            new Dictionary<string, GameDataStore.CurrencySourceRow>(),
            new Dictionary<string, GameDataStore.BossRow>(),
            new Dictionary<(string, string), GameDataStore.BossItemRow>(),
            new Dictionary<(string, string), GameDataStore.CardAreaRow>(),
            new Dictionary<(string, string, string), GameDataStore.CardSourceRow>());
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
        Volatile.Write(ref _snapshot, new Snapshot(weights, sources, bosses, items, areas, cards));
        Volatile.Write(ref _hasTable, weights.Count + sources.Count + bosses.Count + items.Count + areas.Count + cards.Count > 0 ? 1 : 0);
    }

    public void Reset()
    {
        Volatile.Write(ref _snapshot, Snapshot.Empty);
        Volatile.Write(ref _hasTable, 0);
    }
}
