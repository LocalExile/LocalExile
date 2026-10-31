/// <summary>
/// Server-side item limits (radius jewels, timeless jewels, Impossible Escape, tattoos). The base limits stay in the
/// catalogues and command checks; this provider holds an immutable override snapshot. A value above zero is the
/// maximum, zero means no limit, and an absent key uses the base value.
/// </summary>
sealed class LimitSettings
{
    static readonly IReadOnlyDictionary<string, int> Empty = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    public static LimitSettings Default { get; } = new();

    IReadOnlyDictionary<string, int> _limits = Empty;

    public IReadOnlyDictionary<string, int> All => Volatile.Read(ref _limits);

    public int Get(string key, int baseValue)
    {
        var limits = Volatile.Read(ref _limits);
        return limits.TryGetValue(key, out int value) ? Math.Max(0, value) : baseValue;
    }

    public void Replace(IReadOnlyDictionary<string, int> limits) =>
        Volatile.Write(ref _limits, new Dictionary<string, int>(limits, StringComparer.OrdinalIgnoreCase));
}
