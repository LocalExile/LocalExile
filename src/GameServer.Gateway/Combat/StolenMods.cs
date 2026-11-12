using GameServer.Core.Protocol.Instance;

/// <summary>Each stolen affix contributes its stats once. The belt and jewel have independent buff timers.</summary>
public sealed class StolenMods
{
    /// <summary>BuffDefinitions row of <c>stolen_mods_buff</c> ("Headhunter", no stats of its own, cancelled on death).</summary>
    public const ushort BuffRow = 269;
    public const ushort InspiredLearningBuffRow = 874;

    /// <summary>On Headhunter's GainRareMonsterModsOnKillUniqueBelt7_: having it is what steals.</summary>
    public const string SourceStat = "gain_rare_monster_mods_on_kill_ms";

    public const string RandomSourceStat = "gain_X_random_rare_monster_mods_on_kill";

    /// <summary>How long each Headhunter modifier lasts.</summary>
    public const double Seconds = 60;

    sealed record Stack(IReadOnlyList<(string Stat, int Value)> Stats, double BeltEnd, double JewelEnd);
    public readonly record struct Buff(int Count, double Remaining);

    readonly Dictionary<ushort, Stack> _stacks = [];
    readonly Lock _lock = new();
    bool _changed;

    /// <summary>A slain monster's affixes: each one's stats by Stats.dat Id, by the affix's Mods.dat hash.</summary>
    public void Steal(MonsterModSet mods, ModCatalog catalog, double now) =>
        Add(Eligible(mods, catalog), catalog, now + Seconds, belt: true);

    public void StealRandom(MonsterModSet mods, ModCatalog catalog, int count, double now, Random random)
    {
        if (count <= 0) return;
        var eligible = Eligible(mods, catalog).ToArray();
        random.Shuffle(eligible);
        Add(eligible.Take(count), catalog, now + 20, belt: false);
    }

    static IEnumerable<ItemMod> Eligible(MonsterModSet mods, ModCatalog catalog) =>
        mods.Affixes.Where(affix => catalog.ByHash(affix.Hash) is
            { Name: not ("Fractured" or "Inner Treasure" or "Soul Conduit") }).DistinctBy(affix => affix.Hash);

    void Add(IEnumerable<ItemMod> affixes, ModCatalog catalog, double ends, bool belt)
    {
        lock (_lock)
            foreach (var affix in affixes)
            {
                if (catalog.ByHash(affix.Hash) is not { } mod) continue;
                var old = _stacks.GetValueOrDefault(affix.Hash);
                double beltEnd = old?.BeltEnd ?? 0, jewelEnd = old?.JewelEnd ?? 0;
                var stats = old is not null && Math.Max(beltEnd, jewelEnd) > ends ? old.Stats
                    : mod.Stats.Zip(affix.Values, (stat, value) => (stat.Id, value)).ToArray();
                _stacks[affix.Hash] = new(stats, belt ? Math.Max(beltEnd, ends) : beltEnd,
                    belt ? jewelEnd : Math.Max(jewelEnd, ends));
                _changed = true;
            }
    }

    /// <summary>What the stacks add, summed.</summary>
    public IReadOnlyDictionary<string, int> Stats()
    {
        lock (_lock)
            return _stacks.Values.SelectMany(s => s.Stats).GroupBy(s => s.Stat).ToDictionary(g => g.Key, g => g.Sum(s => s.Value));
    }

    public int Count
    {
        get { lock (_lock) return _stacks.Count; }
    }

    public (int Stacks, double Remaining)? Step(double now, bool dead) => Step(now, dead, out _, out _);

    public (int Stacks, double Remaining)? Step(double now, bool dead, out Buff belt, out Buff jewel)
    {
        lock (_lock)
        {
            belt = jewel = default;
            foreach (var (hash, stack) in _stacks.ToArray())
            {
                double beltEnd = dead || stack.BeltEnd <= now ? 0 : stack.BeltEnd;
                double jewelEnd = dead || stack.JewelEnd <= now ? 0 : stack.JewelEnd;
                if (beltEnd != stack.BeltEnd || jewelEnd != stack.JewelEnd)
                {
                    if (beltEnd == 0 && jewelEnd == 0) _stacks.Remove(hash);
                    else _stacks[hash] = stack with { BeltEnd = beltEnd, JewelEnd = jewelEnd };
                    _changed = true;
                }
                if (beltEnd > 0) belt = new(belt.Count + 1, Math.Max(belt.Remaining, beltEnd - now));
                if (jewelEnd > 0) jewel = new(jewel.Count + 1, Math.Max(jewel.Remaining, jewelEnd - now));
            }
            if (!_changed) return null;
            _changed = false;
            return (_stacks.Count, Math.Max(belt.Remaining, jewel.Remaining));
        }
    }
}
