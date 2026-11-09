using System.Text.Json;
using GameServer.Storage;
using GameServer.Core;

/// <summary>
/// Unique drop weights: how likely a unique is once its base rolls one. world.db's <c>unique_weights</c> (from the
/// client's <c>VillageUniqueDisenchantValues</c>: weight = min(1, 1 / disenchant value), with its own Ruthless column)
/// and <c>unique_weight_overrides</c> on top. ItemDrops reads the published provider and falls back to the client table
/// while world.db is not open.
/// </summary>
static class GameDataWeights
{
    public static void Reload(GameDataStore store, UniqueWeightProvider? provider = null)
    {
        var reference = store.LoadUniqueWeights();
        var overrides = store.LoadUniqueWeightOverrides();
        var weights = new Dictionary<string, (double Normal, double Ruthless)>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in reference) weights[row.Name] = (row.Weight, row.RuthlessWeight);
        foreach (var row in overrides) weights[row.Name] = (row.Weight, row.RuthlessWeight);
        // An empty table means no unique drops, not "use the fallback": a missing unique weighs zero.
        (provider ?? UniqueWeightProvider.Default).Replace(weights, true);
    }

}

/// <summary>The published unique weights. Empty until world.db is open, when ItemDrops uses
/// its client-table fallback; once the reference exists, a missing name weighs zero (it does not drop).</summary>
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
