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
sealed class RecentEvents
{
    /// <summary>The window every "recently" stat uses, in seconds.</summary>
    public const double WindowSeconds = RecentEnemyHits.RecentlySeconds;

    readonly Dictionary<string, double> last = new(StringComparer.Ordinal);

    /// <summary>The next time any currently recent event leaves the window; callers use it to expire derived caches.</summary>
    public double? NextExpiry(double now)
    {
        double? expiry = null;
        foreach (double at in last.Values)
        {
            double candidate = at + WindowSeconds;
            if (candidate > now && (expiry is null || candidate < expiry)) expiry = candidate;
        }
        return expiry;
    }

    /// <summary>Records that the event happened now.</summary>
    public void Record(string key, double now) => last[key] = now;

    /// <summary>Whether the event happened within the window. Every call also drops the events that fell out of it, so
    /// the set stays the size of what actually happened in the last four seconds.</summary>
    public bool Happened(string key, double now) => last.TryGetValue(key, out double at) && now - at < WindowSeconds;

    /// <summary>Every key that happened within the window, for a <c>StatConditions.Subject</c>. A key whose event never
    /// happened, or whose last one has aged out, is absent, which is what a "not X recently" stat reads as true.</summary>
    public IReadOnlySet<string> Snapshot(double now)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (key, at) in last)
            if (now - at < WindowSeconds) set.Add(key);
        return set;
    }

    /// <summary>Forgets every event (a new area, a death, a login).</summary>
    public void Clear() => last.Clear();
}
