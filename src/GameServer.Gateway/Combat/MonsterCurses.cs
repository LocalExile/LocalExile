/// <summary>Recipient-owned curse slots. No module callbacks or catalogue mutations survive a reload.</summary>
public sealed class MonsterCurses
{
    public sealed record Curse(uint Caster, string Effect, ushort Skill, ushort Row, ushort Buff,
        double Applied, double Until, bool Aura, bool Mark, IReadOnlyDictionary<string, int> Stats);
    public readonly Dictionary<(uint Caster, string Effect), Curse> All = [];
    public readonly Dictionary<(uint Caster, string Effect), Curse> Presented = [];
    public readonly HashSet<string> PresentedStats = [];
    public int Version, PresentedVersion;
    Dictionary<string, int> stats = [];
    MonsterModEffects? basis, cached;
    public int this[string name] => stats.GetValueOrDefault(name);
    public IReadOnlyDictionary<string, int> Stats => stats;

    public void Put(Curse curse, int limit)
    {
        if (limit < 1) return;
        All[(curse.Caster, curse.Effect)] = curse;
        if (curse.Mark)
            foreach (var old in All.Values.Where(c => c.Mark && c.Effect != curse.Effect).ToArray()) All.Remove((old.Caster, old.Effect));
        var keep = All.Values.GroupBy(c => c.Effect).OrderByDescending(g => g.Any(c => c.Aura))
            .ThenByDescending(g => g.Max(c => c.Applied)).ThenByDescending(g => g.Max(c => c.Buff)).Take(limit).Select(g => g.Key).ToHashSet();
        foreach (var old in All.Values.Where(c => !keep.Contains(c.Effect)).ToArray()) All.Remove((old.Caster, old.Effect));
        Rebuild();
    }

    public void Remove(uint caster, string effect)
    { if (All.Remove((caster, effect))) Rebuild(); }

    public void Clear() { if (All.Count > 0) { All.Clear(); Rebuild(); } }

    public void Expire(double now)
    {
        bool changed = false;
        foreach (var c in All.Values.Where(c => c.Until <= now).ToArray()) changed |= All.Remove((c.Caster, c.Effect));
        if (changed) Rebuild();
    }

    void Rebuild()
    {
        stats = [];
        // The same curse from several casters never stacks; take the strongest application as a whole.
        foreach (var family in All.Values.GroupBy(c => c.Effect))
            foreach (var (key, value) in family.MaxBy(c => c.Stats.Sum(p => Math.Abs((long)p.Value)))!.Stats)
                stats[key] = stats.GetValueOrDefault(key) + value;
        basis = cached = null;
        Version++;
    }

    public MonsterModEffects Effects(MonsterModEffects original)
    {
        if (stats.Count == 0) return original;
        if (!ReferenceEquals(basis, original) || cached is null)
        { basis = original; cached = original.Combine(stats); }
        return cached;
    }
}

public static class CurseCatalog
{
    public sealed record Definition(ushort Buff, bool Mark = false);
    public static readonly IReadOnlyDictionary<string, Definition> All = new Dictionary<string, Definition>
    {
        ["TemporalChains"] = new(69), ["ElementalWeakness"] = new(70), ["Flammability"] = new(71),
        ["Frostbite"] = new(72), ["Conductivity"] = new(73), ["Enfeeble"] = new(76),
        ["Vulnerability"] = new(79), ["Punishment"] = new(81), ["Despair"] = new(1016),
        ["WarlordsMark"] = new(74, true), ["AssassinsMark"] = new(77, true), ["SnipersMark"] = new(78, true),
        ["PoachersMark"] = new(80, true), ["AlchemistsMark"] = new(2093, true),
    };
    public static bool Hex(string? effect) => effect is not null && All.TryGetValue(effect, out var curse) && !curse.Mark;
}
