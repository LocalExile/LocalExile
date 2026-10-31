using System.Text.Json;
using GameServer.Storage;

/// <summary>
/// Unique drop weights: how likely a unique is once its base rolls one. The client's
/// <c>VillageUniqueDisenchantValues</c> is the reference (weight = min(1, 1 / disenchant value), with its own
/// Ruthless column); the database keeps the reference rows and the admin overrides. ItemDrops reads the published
/// provider and falls back to the extracted JSON when the database has no reference table yet.
/// </summary>
static class GameDataWeights
{
    public static bool Seed(GameDataStore store, string folder = "data", bool force = false)
    {
        if (!force && store.UniqueWeightsSeeded()) return false;
        var rows = Read(folder);
        if (rows.Count == 0)
        {
            Console.WriteLine("[gateway] game data: unique weights are empty; extract the game tables first");
            return false;
        }
        store.SeedUniqueWeights(rows);
        store.MarkUniqueWeightsSeeded();
        Console.WriteLine($"[gateway] game data unique weights: {rows.Count} row(s)");
        return true;
    }

    public static void Reload(GameDataStore store, UniqueWeightProvider? provider = null)
    {
        var reference = store.LoadUniqueWeights();
        var overrides = store.LoadUniqueWeightOverrides();
        var weights = new Dictionary<string, (double Normal, double Ruthless)>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in reference) weights[row.Name] = (row.Weight, row.RuthlessWeight);
        foreach (var row in overrides) weights[row.Name] = (row.Weight, row.RuthlessWeight);
        // Once the reference has been seeded, an empty table means the admin removed the weights, not "use the JSON
        // fallback": a missing unique weighs zero.
        (provider ?? UniqueWeightProvider.Default).Replace(weights, reference.Count > 0 || store.UniqueWeightsSeeded());
    }

    /// <summary>The client-derived rows, before any override. Kept in the same order as the source table; duplicate
    /// names (none in 3.29.3b) keep their first row.</summary>
    internal static List<GameDataStore.UniqueWeightRow> Read(string folder)
    {
        var rows = new List<GameDataStore.UniqueWeightRow>();
        string values = Path.Combine(folder, "tables", "VillageUniqueDisenchantValues.json");
        string words = Path.Combine(folder, "tables", "Words.json");
        if (!File.Exists(values) || !File.Exists(words)) return rows;
        using var wordsDoc = JsonDocument.Parse(File.ReadAllBytes(words));
        using var valuesDoc = JsonDocument.Parse(File.ReadAllBytes(values));
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in valuesDoc.RootElement.EnumerateArray())
        {
            int word = row.GetProperty("UniqueName").GetInt32();
            if (wordsDoc.RootElement[word].GetProperty("Text").GetString() is not { Length: > 0 } name) continue;
            double value = row.GetProperty("Value").GetDouble();
            double ruthless = row.GetProperty("RuthlessValue").GetDouble();
            double weight = value > 0 ? Math.Min(1, 1 / value) : 0;
            double ruthlessWeight = ruthless > 0 ? Math.Min(1, 1 / ruthless) : 0;
            if (seen.Add(name)) rows.Add(new(name, weight, ruthlessWeight));
        }
        return rows;
    }
}

/// <summary>The published unique weights. Empty means the reference table has not been seeded and ItemDrops should
/// use its extracted-JSON fallback; once the reference exists, a missing name weighs zero (it does not drop).</summary>
sealed class UniqueWeightProvider
{
    static readonly IReadOnlyDictionary<string, (double Normal, double Ruthless)> Empty =
        new Dictionary<string, (double, double)>(StringComparer.OrdinalIgnoreCase);

    public static UniqueWeightProvider Default { get; } = new();

    IReadOnlyDictionary<string, (double Normal, double Ruthless)> _weights = Empty;
    int _hasTable;

    public bool HasTable => Volatile.Read(ref _hasTable) != 0;

    public double Weight(string name, bool ruthless)
    {
        var weights = Volatile.Read(ref _weights);
        if (!weights.TryGetValue(UniqueCatalog.NameKey(name), out var value)) return 0;
        return ruthless ? value.Ruthless : value.Normal;
    }

    /// <summary>Keyed as <see cref="UniqueCatalog.NameKey"/>: a definition's name can differ from the table's Words text.</summary>
    public void Replace(IReadOnlyDictionary<string, (double Normal, double Ruthless)> weights, bool hasTable)
    {
        var keyed = new Dictionary<string, (double Normal, double Ruthless)>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, weight) in weights) keyed[UniqueCatalog.NameKey(name)] = weight;
        Volatile.Write(ref _weights, keyed);
        Volatile.Write(ref _hasTable, hasTable ? 1 : 0);
    }

    public void Reset() => Replace(Empty, false);
}
