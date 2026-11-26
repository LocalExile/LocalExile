/// <summary>
/// The events that happened to a character in the last four seconds, for the "recently" stats (issue #504). PoE's
/// "recently" is a fixed four-second window, the same one <see cref="RecentEnemyHits"/> already uses for the hits
/// taken: a stat whose id says <c>if you have crit recently</c> holds while the last such event is inside it.
/// <para>
/// Owned by one area connection. Frame work and stat queries can overlap. The keys are <see cref="StatConditions"/>'s
/// (<c>hit_enemy</c>, <c>killed_enemy</c>, …), so the stat layer can ask for exactly the ones it names.
/// </para>
/// </summary>
public sealed class RecentEvents
{
    /// <summary>The window every "recently" stat uses, in seconds.</summary>
    public const double WindowSeconds = RecentEnemyHits.RecentlySeconds;
    public const double EightSecondWindow = 8;

    // Keep the event and its window together during each read and write. Notify stat consumers after releasing the gate.
    readonly object gate = new();
    readonly Dictionary<string, double> last = new(StringComparer.Ordinal);
    readonly Dictionary<string, double> windows = new(StringComparer.Ordinal);
    readonly Dictionary<string, List<(double At, long Amount)>> amounts = new(StringComparer.Ordinal);
    Queue<double>? attackCriticals;
    List<(int Effect, double At)>? nonInstantSpells;

    public void RecordNonInstantSpell(int effect, double now)
    {
        lock (gate)
        {
            var casts = nonInstantSpells ??= [];
            int expired = 0;
            while (expired < casts.Count && !Recent(casts[expired].At, now, EightSecondWindow)) expired++;
            if (expired != 0) casts.RemoveRange(0, expired);
            casts.Add((effect, now));
        }
    }

    /// <summary>Count accepted casts in the requested windows and report their next relevant expiry.</summary>
    public (int Distinct, int Total, double? Expiry) NonInstantSpells(double now, bool needDistinct, bool needTotal)
    {
        lock (gate)
        {
            if (nonInstantSpells is not { Count: > 0 } casts) return default;
            HashSet<int>? distinct = needDistinct ? [] : null;
            int total = 0;
            double expiry = double.PositiveInfinity;
            for (int index = casts.Count - 1; index >= 0; index--)
            {
                var (effect, at) = casts[index];
                if (at > now || !Recent(at, now, EightSecondWindow)) continue;
                if (needTotal)
                {
                    total++;
                    expiry = Math.Min(expiry, at + EightSecondWindow);
                }
                if (distinct is not null && Recent(at, now, WindowSeconds) && distinct.Add(effect))
                    expiry = Math.Min(expiry, at + WindowSeconds);
            }
            return (distinct?.Count ?? 0, total, double.IsFinite(expiry) ? expiry : null);
        }
    }

    // Amounts are recorded only at one authoritative commit per counted operation. Boolean observations may be
    // repeated by existing consumers without accidentally counting the same kill or payment twice.
    public void RecordAmount(string key, double now, long amount = 1)
    {
        if (amount <= 0) return;
        bool changed;
        lock (gate)
        {
            changed = RecordLocked(key, now, null);
            if (!amounts.TryGetValue(key, out var entries)) amounts[key] = entries = [];
            entries.RemoveAll(e => !Recent(e.At, now, WindowSeconds));
            entries.Add((now, amount));
        }
        if (changed) Changed?.Invoke();
        AmountChanged?.Invoke(key);
    }

    /// <summary>What happened changed: an event that had not happened recently did, or all were forgotten. (An event
    /// repeated within its window changes nothing a boolean condition reads until it ages out, which
    /// <see cref="NextExpiry"/> tells.)</summary>
    public Action? Changed;

    /// <summary>A counted recent amount increased for the given key; consumers only dirty cached stats that actually
    /// read that key's count.</summary>
    public Action<string>? AmountChanged;

    public long Amount(string key, double now)
    {
        lock (gate) return amounts.TryGetValue(key, out var entries) ? SumAmount(entries, now) : 0;
    }

    static long SumAmount(List<(double At, long Amount)> entries, double now)
    {
        long total = 0;
        foreach (var (at, amount) in entries)
            if (at <= now && Recent(at, now, WindowSeconds))
                total = amount > long.MaxValue - total ? long.MaxValue : total + amount;
        return total;
    }

    public Dictionary<string, long> Amounts(double now)
    {
        var totals = new Dictionary<string, long>(StringComparer.Ordinal);
        lock (gate)
        {
            foreach (var (key, entries) in amounts)
            {
                entries.RemoveAll(e => !Recent(e.At, now, WindowSeconds));
                totals[key] = SumAmount(entries, now);
            }
        }
        return totals;
    }

    /// <summary>The next time any currently recent event leaves the window; callers use it to expire derived caches.</summary>
    public double? NextExpiry(double now)
    {
        double? expiry = null;
        lock (gate)
        {
            foreach (var (key, at) in last)
            {
                double window = Window(key);
                double candidate = at + window;
                if (Recent(at, now, window) && (expiry is null || candidate < expiry)) expiry = candidate;
            }
            foreach (var entries in amounts.Values)
            foreach (var entry in entries)
            {
                double candidate = entry.At + WindowSeconds;
                if (Recent(entry.At, now, WindowSeconds) && (expiry is null || candidate < expiry)) expiry = candidate;
            }
        }
        return expiry;
    }

    /// <summary>Records that the event happened now. Most recent events use PoE's four-second window; a data id that
    /// explicitly says eight seconds supplies that duration instead.</summary>
    public void Record(string key, double now, double? seconds = null)
    {
        bool changed;
        lock (gate) changed = RecordLocked(key, now, seconds);
        if (changed) Changed?.Invoke();
    }

    bool RecordLocked(string key, double now, double? seconds)
    {
        bool happened = HappenedLocked(key, now);
        last[key] = now;
        if (seconds is double duration && duration > 0) windows[key] = duration;
        else windows.Remove(key);
        return !happened;
    }

    /// <summary>A successful player hit commits its direction and critical outcome. The eight-second key is separate
    /// from ordinary four-second crit so an explicit eight-second modifier changes neither every recent stat nor the
    /// ordinary window.</summary>
    public void RecordHit(double now, bool critical) => RecordHit(now, critical, attack: false);

    public void RecordHit(double now, bool critical, bool attack)
    {
        Record(StatConditions.HitEnemy, now);
        RecordCriticalOutcome(now, critical, attack);
    }

    public void RecordCriticalOutcome(double now, bool critical) => RecordCriticalOutcome(now, critical, attack: false);

    public void RecordCriticalOutcome(double now, bool critical, bool attack)
    {
        Record(critical ? StatConditions.Crit : StatConditions.NonCrit, now);
        if (!critical) return;
        Record(StatConditions.Crit8Seconds, now, EightSecondWindow);
        if (attack)
        {
            lock (gate)
            {
                TrimAttackCriticals(now);
                (attackCriticals ??= new()).Enqueue(now);
            }
        }
    }

    public int RecentAttackCriticals(double now)
    {
        lock (gate)
        {
            TrimAttackCriticals(now);
            return attackCriticals?.Count ?? 0;
        }
    }

    void TrimAttackCriticals(double now)
    {
        while (attackCriticals is not null && attackCriticals.TryPeek(out double at) && !Recent(at, now, WindowSeconds))
            attackCriticals.Dequeue();
    }

    // Elemental Overload's window is per skill (poewiki /wiki/Elemental_Overload): the skill that dealt the critical
    // strike, and all its uses including proxies, gain the bonus for eight seconds. Keyed by the skill's effect id, so a
    // totem's or a trap's critical strike opens the window for the player's skill of the same effect. Minions never call
    // this. The activating critical strike does not benefit: its damage is built before the crit is recorded here.
    readonly Dictionary<string, double> skillCrit = new(StringComparer.Ordinal);

    public void RecordSkillCrit(string effect, double now, bool critical)
    {
        if (!critical || effect.Length == 0) return;
        lock (gate) skillCrit[effect] = now;
    }

    public bool SkillCritRecently(string effect, double now)
    {
        if (effect.Length == 0) return false;
        lock (gate) return skillCrit.TryGetValue(effect, out double at) && Recent(at, now, EightSecondWindow);
    }

    double Window(string key) => windows.GetValueOrDefault(key, WindowSeconds);
    // Checking both equivalent forms keeps exact deadlines stable whether the caller spelled them as at + window or
    // now - window, without a tolerance that would keep a just-expired count or cache input alive.
    static bool Recent(double at, double now, double window) => now < at + window && now - at < window;

    /// <summary>Whether the event happened within its window.</summary>
    public bool Happened(string key, double now)
    {
        lock (gate) return HappenedLocked(key, now);
    }

    bool HappenedLocked(string key, double now) => last.TryGetValue(key, out double at) && Recent(at, now, Window(key));

    public (double From, double Until)? LastWindow(string key)
    {
        lock (gate) return last.TryGetValue(key, out double at) ? (at, at + Window(key)) : null;
    }

    /// <summary>Every key that happened within the window, for a <c>StatConditions.Subject</c>. A key whose event never
    /// happened, or whose last one has aged out, is absent, which is what a "not X recently" stat reads as true.</summary>
    public IReadOnlySet<string> Snapshot(double now)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        lock (gate)
        {
            foreach (var (key, at) in last)
                if (Recent(at, now, Window(key))) set.Add(key);
        }
        return set;
    }

    /// <summary>Removes the pending event and its window data when an admitted skill consumes it.</summary>
    public void Forget(string key)
    {
        bool removed;
        lock (gate)
        {
            removed = last.Remove(key);
            windows.Remove(key);
            amounts.Remove(key);
        }
        if (removed) Changed?.Invoke();
    }

    /// <summary>Forgets every event (a new area, a death, a login).</summary>
    public void Clear()
    {
        lock (gate)
        {
            last.Clear();
            windows.Clear();
            amounts.Clear();
            attackCriticals?.Clear();
            nonInstantSpells?.Clear();
        }
        Changed?.Invoke();
    }
}
