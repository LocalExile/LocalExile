using GameServer.Storage;

/// <summary>
/// The Gateway's game content: world.db (built from <c>sql/</c>, Agents/docs/dev/databases.md) read at startup and on
/// <c>/reload</c>, published as atomically swapped snapshots the catalogues read. A reload that fails keeps the
/// previous snapshots.
/// </summary>
public static class GameData
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

    /// <summary>data/world.db, or <c>Game_WORLD_DB</c>.</summary>
    public static string DefaultPath => GameDataStore.DefaultPath;

    public static GameDataStore? Store => _store;

    /// <summary>world.db's <c>effect_rules</c> rows as last read (enabled and reviewed ones), for the game module that
    /// gives them their meaning; <see cref="Snapshot.Revision"/> changes when they are read again.</summary>
    public static IReadOnlyList<GameDataStore.EffectRuleRow> EffectRules => Volatile.Read(ref _effectRules);
    static IReadOnlyList<GameDataStore.EffectRuleRow> _effectRules = [];
    public static IReadOnlyList<GameDataStore.EffectSkillRow> EffectSkills => Volatile.Read(ref _effectSkills);
    static IReadOnlyList<GameDataStore.EffectSkillRow> _effectSkills = [];
    public static Snapshot Current => Volatile.Read(ref _snapshot);

    /// <summary>Open world.db (bringing it up to date from sql/) and publish its rows. Called once at Gateway startup.</summary>
    public static void Open(string path)
    {
        var store = new GameDataStore(path);
        var old = _store;
        _store = store;
        try
        {
            Reload("startup");
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
        Volatile.Write(ref _effectRules, []);
        Volatile.Write(ref _effectSkills, []);
        ModRangeProvider.Default.Replace(Empty.ModRanges);
        LimitSettings.Default.Replace(Empty.Limits);
        UniqueWeightProvider.Default.Reset();
        GameDataDrops.Reset();
        GameDataCatalog.Reset();
        SystemSettings.Reset();
        old?.Dispose();
    }

    /// <summary>Read the content and publish it. A failed read leaves the previous snapshot in place.</summary>
    public static void Reload(string reason)
    {
        var store = _store ?? throw new InvalidOperationException("the game-data store is not open");
        var ranges = store.LoadModRanges()
            .ToDictionary(row => (row.ModId, row.StatIndex), row => (row.Minimum, row.Maximum));
        var limits = store.LoadLimits()
            .ToDictionary(row => row.Key, row => row.Value, StringComparer.OrdinalIgnoreCase);
        var snapshot = new Snapshot(Interlocked.Increment(ref _revision), ranges, limits);
        var effectRules = store.LoadEffectRules();
        var effectSkills = store.LoadEffectSkills();

        // Build the catalogues and settings first: a bad payload leaves the previous ranges/limits and catalogues intact.
        GameDataCatalog.Reload(store);
        SystemSettings.Reload(store);
        GameDataWeights.Reload(store);
        GameDataDrops.Reload(store);
        ModRangeProvider.Default.Replace(ranges);
        LimitSettings.Default.Replace(limits);
        Volatile.Write(ref _effectRules, effectRules);
        Volatile.Write(ref _effectSkills, effectSkills);
        Volatile.Write(ref _snapshot, snapshot);
        var (uniqueWeights, uniqueWeightOverrides) = store.UniqueWeightCounts();
        var drop = store.DropCounts();
        Console.WriteLine($"[gateway] game data: revision {snapshot.Revision}, {ranges.Count} mod range override(s), " +
            $"{limits.Count} limit override(s), {SystemSettings.Default.Values.Count} system setting(s) ({reason}); " +
            $"{uniqueWeights} unique weight(s), {uniqueWeightOverrides} unique weight override(s); " +
            $"{drop.CurrencyWeights} currency weight(s), {drop.Bosses} boss(es), {drop.BossItems} boss item(s), " +
            $"{drop.CardAreas} card area(s), {drop.ItemWeights} item weight(s); {effectRules.Count} effect rule(s), {effectSkills.Count} effect skill(s); {GameDataCatalog.Summary}");
    }

    public static int Limit(string key, int baseValue) =>
        Current.Limits.TryGetValue(key, out int value) ? Math.Max(0, value) : baseValue;
}
