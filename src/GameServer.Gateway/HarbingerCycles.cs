/// <summary>One companion's spell timing. The caller supplies a paused monotonic clock and serializes access.
/// Initial cooldowns begin at spawn. Cooldown recovers during casting, and, when requested by the skill, stops
/// only during that spell's active buff. No owner cooldown/duration modifiers or inferred AIS conditions are added.</summary>
sealed class HarbingerCycles
{
    public sealed record Cast(HarbingerCompanionCatalog.BuffSkill Skill, double StartedAt, double CompletesAt);
    public sealed record Buff(HarbingerCompanionCatalog.BuffSkill Skill, double StartedAt, double EndsAt);
    public sealed record Change(IReadOnlyList<Cast> Started, IReadOnlyList<Buff> Completed)
    {
        public static readonly Change Empty = new([], []);
    }

    sealed class Spell(HarbingerCompanionCatalog.BuffSkill skill, double cooldown, double readyAt, int order)
    {
        public HarbingerCompanionCatalog.BuffSkill Skill { get; } = skill;
        public double Cooldown { get; } = cooldown;
        public double Remaining = cooldown;
        public double ReadyAt = readyAt;
        public int Order { get; } = order;
    }

    const double Precision = 1e-9;
    readonly Spell[] _spells;
    readonly Dictionary<int, Buff> _active = [];
    double _lastAt, _lastStartedAt = double.NegativeInfinity;
    bool _cleared;
    public Cast? CurrentCast { get; private set; }
    public bool IsCasting => CurrentCast is not null;

    public HarbingerCycles(IReadOnlyList<HarbingerCompanionCatalog.BuffSkill> skills, double startedAt,
        int ownCooldownRecoveryPct)
    {
        ArgumentNullException.ThrowIfNull(skills);
        if (!double.IsFinite(startedAt)) throw new ArgumentOutOfRangeException(nameof(startedAt));
        if (skills.Any(s => s is null || s.CastMs < 0 || s.DurationMs < 0 || s.CooldownMs < 0)
            || skills.Select(s => s.Effect).Distinct().Count() != skills.Count)
            throw new ArgumentException("Buff skills must be distinct and have nonnegative timing", nameof(skills));
        _lastAt = startedAt;
        double recovery = 1 + ownCooldownRecoveryPct / 100.0;
        _spells = [.. skills.Select((skill, i) =>
        {
            double cooldown = skill.CooldownMs == 0 ? 0 : recovery <= 0 ? double.PositiveInfinity
                : skill.CooldownMs / 1000.0 / recovery;
            return new Spell(skill, cooldown, startedAt + cooldown, i);
        })];
    }

    /// <summary>Observe time and start at most one new cast at now. A delayed completion retains its scheduled
    /// deadline, even if that buff has expired by now; publish only intervals returned by Active(now).</summary>
    public Change Advance(double now, bool canStart)
    {
        if (!double.IsFinite(now) || now < _lastAt) throw new ArgumentOutOfRangeException(nameof(now));
        if (_cleared) { _lastAt = now; return Change.Empty; }
        var completed = new List<Buff>();
        if (CurrentCast is { } pending && pending.CompletesAt <= now + Precision)
        {
            Recover(_lastAt, pending.CompletesAt);
            completed.Add(Complete(pending));
            Recover(pending.CompletesAt, now);
        }
        else Recover(_lastAt, now);
        _lastAt = now;
        foreach (int effect in _active.Where(a => a.Value.EndsAt <= now + Precision).Select(a => a.Key).ToArray()) _active.Remove(effect);
        if (!canStart || CurrentCast is not null || now <= _lastStartedAt)
            return new([], completed.ToArray());
        var ready = _spells.Where(s => s.Remaining <= Precision).OrderBy(s => s.ReadyAt).ThenBy(s => s.Order).FirstOrDefault();
        if (ready is null) return new([], completed.ToArray());
        var cast = new Cast(ready.Skill, now, now + ready.Skill.CastMs / 1000.0);
        CurrentCast = cast;
        _lastStartedAt = now;
        ready.Remaining = ready.Cooldown;
        ready.ReadyAt = now + ready.Cooldown;
        // Zero-time skills may complete in this observation, but never cause an unbounded catch-up/start loop.
        if (cast.CompletesAt <= now) completed.Add(Complete(cast));
        return new([cast], completed.ToArray());
    }

    public IReadOnlyList<Buff> Active(double now)
    {
        if (!double.IsFinite(now)) throw new ArgumentOutOfRangeException(nameof(now));
        return [.. _active.Values.Where(b => b.StartedAt <= now + Precision && b.EndsAt > now + Precision).OrderBy(b => b.Skill.Effect)];
    }

    /// <summary>Cancel this summon permanently, including pending casts and buffs. A new summon gets a new cycle.</summary>
    public void Clear()
    {
        _cleared = true;
        CurrentCast = null;
        _active.Clear();
    }

    Buff Complete(Cast cast)
    {
        var buff = new Buff(cast.Skill, cast.CompletesAt, cast.CompletesAt + cast.Skill.DurationMs / 1000.0);
        _active[cast.Skill.Effect] = buff;
        CurrentCast = null;
        return buff;
    }

    void Recover(double from, double to)
    {
        if (to <= from) return;
        foreach (var spell in _spells)
        {
            if (!spell.Skill.CooldownNotRecoverDuringActive || !_active.TryGetValue(spell.Skill.Effect, out var buff)
                || buff.EndsAt <= from || buff.StartedAt >= to)
                RecoverWindow(spell, from, to);
            else
            {
                RecoverWindow(spell, from, Math.Min(to, buff.StartedAt));
                RecoverWindow(spell, Math.Max(from, buff.EndsAt), to);
            }
        }
    }

    static void RecoverWindow(Spell spell, double from, double to)
    {
        if (to <= from || spell.Remaining <= 0) return;
        double elapsed = to - from;
        if (elapsed + Precision >= spell.Remaining)
        {
            spell.ReadyAt = Math.Min(to, from + spell.Remaining);
            spell.Remaining = 0;
        }
        else spell.Remaining -= elapsed;
    }
}
