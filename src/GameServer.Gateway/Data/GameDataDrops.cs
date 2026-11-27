using System.Text.Json;
using GameServer.Storage;
using GameServer.Core;

/// <summary>
/// The drop tables: world.db's <c>drop_*</c> rows (Agents/docs/dev/drop-data-schema.md), published as an immutable
/// snapshot at startup and on <c>/reload</c>. A reader takes the snapshot, never SQLite.
/// </summary>
public static class GameDataDrops
{
    public static void Reload(GameDataStore store, DropDataProvider? provider = null) =>
        (provider ?? DropDataProvider.Default).Replace(store);

    public static void Reset() => DropDataProvider.Default.Reset();

}

/// <summary>The published drop tables. Empty until world.db is open; callers keep their code fallback.</summary>
public sealed class DropDataProvider
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

    public static DropDataProvider Default { get; } = new(DropPoolOverlays.Default);

    Snapshot _snapshot = Snapshot.Empty;
    int _hasTable;
    long _dataRevision;

    public DropDataProvider() : this(new DropPoolOverlays()) { }

    public DropDataProvider(DropPoolOverlays overlays)
    {
        Overlays = overlays ?? throw new ArgumentNullException(nameof(overlays));
    }

    /// <summary>Declarative runtime drop-pool overlays layered over the SQLite snapshot and preserved across <c>/reload</c>.</summary>
    public DropPoolOverlays Overlays { get; }

    public Snapshot Current => Volatile.Read(ref _snapshot);
    public bool HasTable => Volatile.Read(ref _hasTable) != 0;

    /// <summary>Advances whenever SQLite drop tables reload or runtime drop-pool overlays change.</summary>
    public long Revision => Volatile.Read(ref _dataRevision) + Overlays.Revision;

    /// <summary>Registers a declarative drop-pool entry on this provider's <see cref="Overlays"/>, owned by <paramref name="owner"/>.</summary>
    public IDisposable RegisterOverlay(string owner, DropPoolEntry entry) => Overlays.Add(owner, entry);

    /// <summary>Registers a batch of declarative drop-pool entries on this provider's <see cref="Overlays"/>, owned by <paramref name="owner"/>.</summary>
    public IDisposable RegisterOverlays(string owner, IEnumerable<DropPoolEntry> entries) => Overlays.AddRange(owner, entries);

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
        Interlocked.Increment(ref _dataRevision);
    }

    public void Reset()
    {
        Volatile.Write(ref _snapshot, Snapshot.Empty);
        Volatile.Write(ref _hasTable, 0);
        Interlocked.Increment(ref _dataRevision);
    }
}
