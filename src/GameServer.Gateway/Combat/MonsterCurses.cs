/// <summary>Recipient-owned curse slots. No module callbacks or catalogue mutations survive a reload.</summary>
public sealed class MonsterCurses(Action? changed = null)
{
    public sealed record Curse(uint Caster, string Effect, ushort Skill, ushort Row, ushort Buff,
        double Applied, double Until, bool Aura, bool Mark, IReadOnlyDictionary<uint, int> Stats)
    {
        public bool RemainsOnDeath { get; init; }
        public int CursedDamageTaken { get; init; }
        public int TransferChance { get; init; }
        public bool PreventsCriticalStrikes { get; init; }
        public double Rate { get; init; } = 1;
        public bool UsesBaseExpiryRate { get; init; }
        public double AmplifiesAt { get; init; } = double.PositiveInfinity;
        public IReadOnlyDictionary<uint, int>? AmplifiedStats { get; init; }
        public int ExpandedCapacity { get; init; }
    }
    public readonly Dictionary<(uint Caster, string Effect), Curse> All = [];
    public readonly Dictionary<(uint Caster, string Effect), Curse> Presented = [];
    public readonly HashSet<uint> PresentedStats = [];
    public int Version, PresentedVersion;
    Dictionary<uint, int> stats = [];
    bool hasCursedDamageTaken, hasCriticalPrevention, hasExpandedCurse;

    public bool HasActive(double now)
    {
        foreach (var curse in All.Values)
            if (curse.Until > now) return true;
        return false;
    }

    public bool PreventsCritical(double now)
    {
        if (!hasCriticalPrevention) return false;
        foreach (var curse in All.Values)
            if (curse.PreventsCriticalStrikes && !(curse.Until <= now)) return true;
        return false;
    }
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
        if (limit < 1 && curse.ExpandedCapacity < 1) return;
        All[(curse.Caster, curse.Effect)] = curse;
        if (curse.Mark)
            foreach (var old in All.Values.Where(c => c.Mark && c.Effect != curse.Effect).ToArray()) All.Remove((old.Caster, old.Effect));
        if (All.Count > limit)
        {
            var ordinary = PreferredEffects(All.Values.Where(c => c.ExpandedCapacity == 0), limit);
            foreach (var old in All.Values.Where(c => c.ExpandedCapacity == 0 && !ordinary.Contains(c.Effect)).ToArray())
                All.Remove((old.Caster, old.Effect));

            // The extra slots belong to the emitter that granted them. Ordinary spells still use the caster's limit.
            if (hasExpandedCurse || curse.ExpandedCapacity > 0)
                foreach (var emitter in All.Values.Where(c => c.ExpandedCapacity > 0).GroupBy(c => c.Caster).ToArray())
                {
                    int specialSlots = Math.Max(0, emitter.Max(c => c.ExpandedCapacity) - ordinary.Count);
                    var keep = PreferredEffects(emitter.Where(c => !ordinary.Contains(c.Effect)), specialSlots);
                    foreach (var old in emitter)
                        if (!ordinary.Contains(old.Effect) && !keep.Contains(old.Effect)) All.Remove((old.Caster, old.Effect));
                }
        }
        Rebuild();
    }

    static HashSet<string> PreferredEffects(IEnumerable<Curse> curses, int capacity) =>
        curses.GroupBy(c => c.Effect).OrderByDescending(g => g.Any(c => c.Aura))
            .ThenByDescending(g => g.Max(c => c.Applied)).ThenByDescending(g => g.Max(c => c.Buff))
            .Take(capacity).Select(g => g.Key).ToHashSet();

    public void Remove(uint caster, string effect)
    { if (All.Remove((caster, effect))) Rebuild(); }

    public void Clear() { if (All.Count > 0) { All.Clear(); Rebuild(); } }

    public void Expire(double now)
    {
        bool changed = false;
        foreach (var c in All.Values.Where(c => c.Until <= now || c.AmplifiesAt <= now).ToArray())
        {
            var key = (c.Caster, c.Effect);
            if (c.Until <= now) changed |= All.Remove(key);
            else if (c.AmplifiedStats is { } amplified)
            {
                All[key] = c with { Stats = amplified, AmplifiesAt = double.PositiveInfinity, AmplifiedStats = null };
                changed = true;
            }
        }
        if (changed) Rebuild();
    }

    // The recipient's other effects can change its clock after a curse has already started. Retiming its two
    // boundaries settles the elapsed portion at the old speed. The source chooses which rate a curse follows.
    public void Retime(double now, double effectRate, double baseRate)
    {
        double Rate(Curse curse) => curse.UsesBaseExpiryRate ? baseRate : effectRate;
        foreach (var curse in All.Values.Where(c => double.IsFinite(c.Until) && c.Rate != Rate(c)).ToArray())
        {
            double rate = Rate(curse);
            All[(curse.Caster, curse.Effect)] = curse with
            {
                Rate = rate,
                Until = RecipientEffectTime.RetimedDeadline(curse.Until, now, curse.Rate, rate),
                AmplifiesAt = RecipientEffectTime.RetimedDeadline(curse.AmplifiesAt, now, curse.Rate, rate),
            };
        }
    }

    void Rebuild()
    {
        stats = [];
        hasCursedDamageTaken = hasCriticalPrevention = hasExpandedCurse = false;
        foreach (var curse in All.Values)
        {
            hasCursedDamageTaken |= curse.CursedDamageTaken > 0;
            hasCriticalPrevention |= curse.PreventsCriticalStrikes;
            hasExpandedCurse |= curse.ExpandedCapacity > 0;
        }
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
