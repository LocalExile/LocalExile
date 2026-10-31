/// <summary>
/// Effective mod roll ranges. The base ranges stay in <see cref="ModCatalog.ModStat"/>; this provider holds an
/// immutable override snapshot so a database edit can be published without rebuilding the catalogues or the mod
/// records. Readers take one snapshot reference; a writer swaps it atomically.
/// </summary>
sealed class ModRangeProvider
{
    static readonly IReadOnlyDictionary<(string ModId, int Index), (int Min, int Max)> Empty =
        new Dictionary<(string, int), (int, int)>();

    public static ModRangeProvider Default { get; } = new();

    IReadOnlyDictionary<(string ModId, int Index), (int Min, int Max)> _ranges = Empty;

    public IReadOnlyDictionary<(string ModId, int Index), (int Min, int Max)> All => Volatile.Read(ref _ranges);

    /// <summary>The override for one stat, or the base range when none applies. A reversed range is normalised so a
    /// bad row can never make <see cref="Random.Next(int, int)"/> throw.</summary>
    public (int Min, int Max) Range(string modId, int index, int baseMin, int baseMax)
    {
        var ranges = Volatile.Read(ref _ranges);
        if (!ranges.TryGetValue((modId, index), out var range)) range = (baseMin, baseMax);
        return (Math.Min(range.Min, range.Max), Math.Max(range.Min, range.Max));
    }

    public void Replace(IReadOnlyDictionary<(string ModId, int Index), (int Min, int Max)> ranges) =>
        Volatile.Write(ref _ranges, ranges);
}
