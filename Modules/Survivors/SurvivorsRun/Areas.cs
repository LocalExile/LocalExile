using static GameServer.Modules.Survivors.SurvivorsSkills;

namespace GameServer.Modules.Survivors;

/// <summary>
/// The areas a run can move through: every area with monster packs, at its own level, sorted by level. A town has no
/// packs and is therefore never picked; an area whose packs resolve to too few supported varieties (an incursion
/// room, a boss-only arena) is left out so a stage never lands somewhere with nothing to fight.
/// </summary>
internal static class SurvivorsAreas
{
    static readonly Lazy<IReadOnlyList<(string Id, int Level)>> Pool = new(Build);

    /// <summary>Areas with at least three spawnable varieties, at their level, sorted by level.</summary>
    internal static IReadOnlyList<(string Id, int Level)> For(ClientState conn) => Pool.Value;

    static IReadOnlyList<(string Id, int Level)> Build() =>
        [.. AreaTransitionCatalog.Areas
            .Select(area => (area.Id, Packs: Packs.For(area.Id)))
            .Where(entry => entry.Packs is { AreaLevel: > 0 } packs && Playable(packs))
            .Select(entry => (entry.Id, Level: entry.Packs!.AreaLevel))
            .OrderBy(area => area.Level).ThenBy(area => area.Id, StringComparer.Ordinal)];

    /// <summary>Whether an area can field a horde: at least three of its pack varieties resolve to supported monsters.</summary>
    static bool Playable(PackCatalog.AreaPacks area)
    {
        int count = 0;
        IEnumerable<int> rows = area.Scattered.SelectMany(s => s.Pack.Entries.Select(e => e.Variety))
            .Concat(area.Named.Values.SelectMany(p => p.Entries.Select(e => e.Variety)))
            .Distinct();
        foreach (int row in rows)
            if (Monsters.Find(row.ToString()) is { Supported: true } && ++count >= 3) return true;
        return false;
    }

    /// <summary>
    /// The next stage's area: a random one within two levels of the run clock's target (1 at the start, 20 by the bell),
    /// or the nearest level's. <paramref name="exclude"/> (the current area) is skipped when another choice exists, so
    /// a stage visibly changes place; an empty pool keeps the run in the opening area.
    /// </summary>
    internal static (string Id, int Level) Next(IReadOnlyList<(string Id, int Level)> pool, double progress,
        Random random, string? exclude = null)
    {
        if (pool.Count == 0) return (SurvivorsRunStartArea, 1);
        int target = 1 + (int)Math.Round(Math.Clamp(progress, 0, 1) * 19);
        var near = pool.Where(area => Math.Abs(area.Level - target) <= 2).ToList();
        var choices = near.Count > 0 ? near
            : pool.Where(area => Math.Abs(area.Level - target) == pool.Min(a => Math.Abs(a.Level - target))).ToList();
        var different = choices.Where(area => !string.Equals(area.Id, exclude, StringComparison.Ordinal)).ToList();
        var pick = different.Count > 0 ? different : choices;
        return pick[random.Next(pick.Count)];
    }

    const string SurvivorsRunStartArea = "1_1_1";
}
