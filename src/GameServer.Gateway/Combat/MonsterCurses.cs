/// <summary>Recipient-owned curse slots. No module callbacks or catalogue mutations survive a reload.</summary>
public sealed class MonsterCurses(Action? changed = null)
{
    public sealed record Curse(uint Caster, string Effect, ushort Skill, ushort Row, ushort Buff,
        double Applied, double Until, bool Aura, bool Mark, IReadOnlyDictionary<uint, int> Stats)
    {
        public bool RemainsOnDeath { get; init; }
        public int CursedDamageTaken { get; init; }
    }
    public readonly Dictionary<(uint Caster, string Effect), Curse> All = [];
    public readonly Dictionary<(uint Caster, string Effect), Curse> Presented = [];
    public readonly HashSet<uint> PresentedStats = [];
    public int Version, PresentedVersion;
    Dictionary<uint, int> stats = [];
    bool hasCursedDamageTaken;
    public int this[uint stat] => stats.GetValueOrDefault(stat);
    public IReadOnlyDictionary<uint, int> Stats => stats;

    public bool HasMark(uint caster, double now)
    {
        foreach (var mark in All.Values)
            if (mark.Mark && mark.Caster == caster && mark.Until > now) return true;
        return false;
    }

    public int DamageTaken(double now)
    {
        if (!hasCursedDamageTaken) return 0;
        int pct = 0;
        foreach (var curse in All.Values)
            if (curse.Until > now) pct = Math.Max(pct, curse.CursedDamageTaken);
        return pct;
    }

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
        hasCursedDamageTaken = All.Values.Any(c => c.CursedDamageTaken > 0);
        // The same curse from several casters never stacks; take the strongest application as a whole.
        foreach (var family in All.Values.GroupBy(c => c.Effect))
            foreach (var (key, value) in family.MaxBy(c => c.Stats.Sum(p => Math.Abs((long)p.Value)))!.Stats)
                stats[key] = stats.GetValueOrDefault(key) + value;
        Version++;
        changed?.Invoke();
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
