using System.Text.Json;
using GameServer.Storage;
using GameServer.Core;

/// <summary>
/// The drop tables: world.db's <c>drop_*</c> rows (Agents/docs/dev/drop-data-schema.md), published as an immutable
/// snapshot at startup and on <c>/reload</c>. A reader takes the snapshot, never SQLite.
/// </summary>
static class GameDataDrops
{
    public static void Reload(GameDataStore store, DropDataProvider? provider = null) =>
        (provider ?? DropDataProvider.Default).Replace(store);

    public static void Reset() => DropDataProvider.Default.Reset();

}

/// <summary>The published drop tables. Empty until world.db is open; callers keep their code fallback.</summary>
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
