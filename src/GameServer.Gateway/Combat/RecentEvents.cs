/// <summary>
/// The events that happened to a character in the last four seconds, for the "recently" stats (issue #504). PoE's
/// "recently" is a fixed four-second window, the same one <see cref="RecentEnemyHits"/> already uses for the hits
/// taken: a stat whose id says <c>if you have crit recently</c> holds while the last such event is inside it.
/// <para>
/// Owned by one area connection; use under its Monsters lock, like the rest of the per-area combat state. The keys are
/// <see cref="StatConditions"/>'s (<c>hit_enemy</c>, <c>killed_enemy</c>, …), so the stat layer can ask for exactly the
/// ones it names and answer <c>NotModelled</c> for anything it does not carry.
/// </para>
/// </summary>
public sealed class RecentEvents
{
    /// <summary>The window every "recently" stat uses, in seconds.</summary>
    public const double WindowSeconds = RecentEnemyHits.RecentlySeconds;
    public const double EightSecondWindow = 8;

    readonly Dictionary<string, double> last = new(StringComparer.Ordinal);
    readonly Dictionary<string, double> windows = new(StringComparer.Ordinal);
    readonly Dictionary<string, List<(double At, long Amount)>> amounts = new(StringComparer.Ordinal);

    // Amounts are recorded only at one authoritative commit per counted operation. Boolean observations may be
    // repeated by existing consumers without accidentally counting the same kill or payment twice.
    public void RecordAmount(string key, double now, long amount = 1)
    {
        if (amount <= 0) return;
        Record(key, now);
        if (!amounts.TryGetValue(key, out var entries)) amounts[key] = entries = [];
        entries.RemoveAll(e => !Recent(e.At, now, WindowSeconds));
        entries.Add((now, amount));
    }

    public Dictionary<string, long> Amounts(double now)
    {
        var totals = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var (key, entries) in amounts)
        {
            entries.RemoveAll(e => !Recent(e.At, now, WindowSeconds));
            long total = 0;
            foreach (var entry in entries)
                if (entry.At <= now) total = entry.Amount > long.MaxValue - total ? long.MaxValue : total + entry.Amount;
            totals[key] = total;
        }
        return totals;
    }

    /// <summary>The next time any currently recent event leaves the window; callers use it to expire derived caches.</summary>
    public double? NextExpiry(double now)
    {
        double? expiry = null;
        foreach (var (key, at) in last)
        {
            double candidate = at + Window(key);
            if (Recent(at, now, Window(key)) && (expiry is null || candidate < expiry)) expiry = candidate;
        }
        foreach (var entry in amounts.Values.SelectMany(e => e))
        {
            double candidate = entry.At + WindowSeconds;
            if (Recent(entry.At, now, WindowSeconds) && (expiry is null || candidate < expiry)) expiry = candidate;
        }
        return expiry;
    }

    /// <summary>Records that the event happened now. Most recent events use PoE's four-second window; a data id that
    /// explicitly says eight seconds supplies that duration instead.</summary>
    public void Record(string key, double now, double? seconds = null)
    {
        last[key] = now;
        if (seconds is double duration && duration > 0) windows[key] = duration;
        else windows.Remove(key);
    }

    /// <summary>A successful player hit commits its direction and critical outcome. The eight-second key is separate
    /// from ordinary four-second crit so an explicit eight-second modifier changes neither every recent stat nor the
    /// ordinary window.</summary>
    public void RecordHit(double now, bool critical)
    {
        Record(StatConditions.HitEnemy, now);
        RecordCriticalOutcome(now, critical);
    }

    public void RecordCriticalOutcome(double now, bool critical)
    {
        Record(critical ? StatConditions.Crit : StatConditions.NonCrit, now);
        if (critical) Record(StatConditions.Crit8Seconds, now, EightSecondWindow);
    }

    double Window(string key) => windows.GetValueOrDefault(key, WindowSeconds);
    // Checking both equivalent forms keeps exact deadlines stable whether the caller spelled them as at + window or
    // now - window, without a tolerance that would keep a just-expired count or cache input alive.
    static bool Recent(double at, double now, double window) => now < at + window && now - at < window;

    /// <summary>Whether the event happened within the window. Every call also drops the events that fell out of it, so
    /// the set stays the size of what actually happened in the last four seconds.</summary>
    public bool Happened(string key, double now) => last.TryGetValue(key, out double at) && Recent(at, now, Window(key));

    /// <summary>Every key that happened within the window, for a <c>StatConditions.Subject</c>. A key whose event never
    /// happened, or whose last one has aged out, is absent, which is what a "not X recently" stat reads as true.</summary>
    public IReadOnlySet<string> Snapshot(double now)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (key, at) in last)
            if (Recent(at, now, Window(key))) set.Add(key);
        return set;
    }

    /// <summary>Forgets every event (a new area, a death, a login).</summary>
    public void Clear() { last.Clear(); windows.Clear(); amounts.Clear(); }
}
