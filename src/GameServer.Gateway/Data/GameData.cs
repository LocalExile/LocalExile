using GameServer.Storage;

/// <summary>
/// The Gateway's game-data overrides: the SQLite rows loaded at startup and after an admin command, plus the
/// atomically published in-memory snapshot the catalogues read. Reference data stays in <c>data/tables</c>; this class
/// only carries the overrides. A reader takes one snapshot; a writer swaps it after a successful database write.
/// </summary>
static class GameData
{
    public sealed record Snapshot(int Revision,
        IReadOnlyDictionary<(string ModId, int Index), (int Min, int Max)> ModRanges,
        IReadOnlyDictionary<string, int> Limits);

    static readonly Snapshot Empty = new(0,
        new Dictionary<(string, int), (int, int)>(),
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase));

    static GameDataStore? _store;
    static Snapshot _snapshot = Empty;
    static int _revision;

    public static GameDataStore? Store => _store;
    public static Snapshot Current => Volatile.Read(ref _snapshot);

    /// <summary>Open the overlay database and publish its rows. Called once at Gateway startup after Saves.Open.</summary>
    public static void Open(string path)
    {
        var store = new GameDataStore(path);
        var old = _store;
        _store = store;
        try
        {
            Reload("startup");
            // Load the reference catalogues once here rather than on the first item or the first admin request. The
            // mod list and base types are the two lists an interface needs; both are cached for the process lifetime.
            _ = Program.Items.Mods.All;
            if (Program.Items.Error is { } error)
                Console.WriteLine($"[gateway] game data: item reference data unavailable: {error}");
            GameDataReference.Seed(store);
            var (stats, mods, modStats) = store.ReferenceCounts();
            Console.WriteLine(stats == 0 || mods == 0 || modStats == 0
                ? "[gateway] game data reference: empty; extract the game tables (scripts/dev/extract-data.ps1) and restart"
                : $"[gateway] game data reference: {stats} stat(s), {mods} mod(s), {modStats} default mod stat row(s)");
        }
        catch
        {
            _store = old;
            store.Dispose();
            throw;
        }
        old?.Dispose();
    }

    public static void Close()
    {
        var old = _store;
        _store = null;
        Volatile.Write(ref _snapshot, Empty);
        ModRangeProvider.Default.Replace(Empty.ModRanges);
        LimitSettings.Default.Replace(Empty.Limits);
        UniqueWeightProvider.Default.Reset();
        GameDataCatalog.ResetToSeeds();
        SystemSettings.ResetToSeeds();
        old?.Dispose();
    }

    /// <summary>Read all overrides and publish them. A failed read/write leaves the previous snapshot in place.</summary>
    public static void Reload(string reason)
    {
        var store = _store ?? throw new InvalidOperationException("the game-data store is not open");
        var ranges = store.LoadModRanges()
            .ToDictionary(row => (row.ModId, row.StatIndex), row => (row.Minimum, row.Maximum));
        var limits = store.LoadLimits()
            .ToDictionary(row => row.Key, row => row.Value, StringComparer.OrdinalIgnoreCase);
        var snapshot = new Snapshot(Interlocked.Increment(ref _revision), ranges, limits);

        // Build the catalogues and settings first: a bad payload leaves the previous ranges/limits and catalogues intact.
        GameDataWeights.Seed(store);
        GameDataCatalog.Reload(store);
        SystemSettings.Reload(store);
        GameDataWeights.Reload(store);
        ModRangeProvider.Default.Replace(ranges);
        LimitSettings.Default.Replace(limits);
        Volatile.Write(ref _snapshot, snapshot);
        var (uniqueWeights, uniqueWeightOverrides) = store.UniqueWeightCounts();
        Console.WriteLine($"[gateway] game data: revision {snapshot.Revision}, {ranges.Count} mod range override(s), " +
            $"{limits.Count} limit override(s), {SystemSettings.Default.Values.Count} system setting(s) ({reason}); " +
            $"{uniqueWeights} unique weight(s), {uniqueWeightOverrides} unique weight override(s); {GameDataCatalog.Summary}");
    }

    public static int Limit(string key, int baseValue) =>
        Current.Limits.TryGetValue(key, out int value) ? Math.Max(0, value) : baseValue;
}
