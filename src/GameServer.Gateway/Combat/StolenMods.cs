/// <summary>
/// Headhunter's "When you Kill a Rare monster, you gain its Modifiers" (Agents/docs/protocol/13-items.md §2e), by the
/// project's own rule rather than the mod's 20 seconds: the rare's affixes, not its rarity's own mods, become character
/// stats. Each different modifier is one stack with its own <see cref="Seconds"/>; getting it again resets its time and adds
/// nothing. One stolen_mods_buff shows them: the number of stacks, and the time of the one that lasts longest.
/// Stats that act on a monster but not on a player here (no consumer on the character) do nothing, as any unread stat from
/// gear does. One per connection: like a flask's effect, they end with an area change or death.
/// </summary>
sealed class StolenMods
{
    /// <summary>BuffDefinitions row of <c>stolen_mods_buff</c> ("Headhunter", no stats of its own, cancelled on death).</summary>
    public const ushort BuffRow = 269;

    /// <summary>On Headhunter's GainRareMonsterModsOnKillUniqueBelt7_: having it is what steals.</summary>
    public const string SourceStat = "gain_rare_monster_mods_on_kill_ms";

    /// <summary>How long each modifier lasts.</summary>
    public const double Seconds = 60;

    sealed record Stack(IReadOnlyList<(string Stat, int Value)> Stats, double Ends);

    readonly Dictionary<ushort, Stack> _stacks = [];
    readonly Lock _lock = new();
    bool _changed;

    /// <summary>A slain monster's affixes: each one's stats by Stats.dat Id, by the affix's Mods.dat hash.</summary>
    public void Steal(MonsterModSet mods, ModCatalog catalog, double now)
    {
        lock (_lock)
            foreach (var affix in mods.Affixes)
                if (catalog.ByHash(affix.Hash) is { } mod)
                {
                    _stacks[affix.Hash] = new([.. mod.Stats.Zip(affix.Values, (stat, value) => (stat.Id, value))], now + Seconds);
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

    /// <summary>Ends the stacks due by <paramref name="now"/>, all of them on <paramref name="dead"/>. Null when nothing
    /// changed since the last step; else what the buff should say now (no stacks: no buff).</summary>
    public (int Stacks, double Remaining)? Step(double now, bool dead)
    {
        lock (_lock)
        {
            foreach (var (hash, stack) in _stacks.ToList())
                if (dead || stack.Ends <= now)
                {
                    _stacks.Remove(hash);
                    _changed = true;
                }
            if (!_changed) return null;
            _changed = false;
            return (_stacks.Count, _stacks.Count == 0 ? 0 : _stacks.Values.Max(s => s.Ends) - now);
        }
    }
}
