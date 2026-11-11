/// <summary>
/// The replacement stats of the tattoo pilot. A tattoo replaces the passive's stats; these are the first simple,
/// unconditional stats (resistances, defences, movement and a few global damage values). The remaining catalogue
/// entries need their condition/special consumers before they can be listed here.
/// </summary>
public static class TattooStats
{
    public static IReadOnlyDictionary<string, (string Stat, int Value)[]> Entries => GameDataCatalog.TattooEffects;

    public static IReadOnlyCollection<string> Names => [.. Entries.Keys];

    /// <summary>The tattoo's replacement stats as client stat keys, or null when the tattoo is not in the pilot (or
    /// its stat names are missing from the client's Stats table).</summary>
    public static IReadOnlyList<(uint Key, int Value)>? For(string name)
    {
        if (!Entries.TryGetValue(name, out var definitions)) return null;
        var stats = new List<(uint Key, int Value)>(definitions.Length);
        foreach (var (stat, value) in definitions)
            if (Program.Stats.Key(stat) is uint key) stats.Add((key, value));
        return stats.Count == 0 ? null : stats;
    }
}
