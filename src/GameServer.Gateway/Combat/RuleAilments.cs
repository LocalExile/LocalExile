using GameServer.Core.World;

/// <summary>
/// Timed ailments of kinds the rules define (a kind is a number the rules give it; the framework names none). Each
/// instance keeps its own magnitude and end; the strongest of a kind applies, whoever added it, and an instance that is
/// weaker and also ends sooner is dropped, as it can never be the strongest again. One store per monster (shared by
/// every connection in its instance) and one per player. Its own lock guards it; times are on its owner's pauseable
/// clock, and <see cref="SetExpiryRate"/> retimes them as the other ailments are.
/// </summary>
public sealed class RuleAilments
{
    public sealed record Instance(int Kind, double From, double Until, double Magnitude);

    readonly List<Instance> instances = [];
    // What was last presented of each kind (Present): its strongest magnitude, truncated.
    readonly Dictionary<int, int> shown = [];
    double expiryRate = 1;
    // instances.Count, published for Magnitude's lock-free empty check.
    int count;

    /// <summary>Nothing runs and nothing is shown.</summary>
    public bool Idle { get { lock (instances) return instances.Count == 0 && shown.Count == 0; } }

    /// <summary>The strongest running magnitude of <paramref name="kind"/> at <paramref name="now"/> (0 without one, or
    /// without a time).</summary>
    public double Magnitude(int kind, double now)
    {
        // Read on every hit (resistances, critical chance); most stores are empty. A stale count only delays by one read.
        if (Volatile.Read(ref count) == 0) return 0;
        lock (instances)
        {
            double strongest = 0;
            foreach (var e in instances)
                if (e.Kind == kind && e.From <= now && now < e.Until) strongest = Math.Max(strongest, e.Magnitude);
            return strongest;
        }
    }

    /// <summary>Adds an instance starting at <paramref name="now"/> that lasts <paramref name="seconds"/> (already
    /// through the rules' duration modifiers) at the current expiry rate. Returns it, or null when its magnitude or
    /// duration is not positive.</summary>
    public Instance? Add(int kind, double now, double seconds, double magnitude)
    {
        if (!(magnitude > 0) || !(seconds > 0) || double.IsNaN(now)) return null;
        lock (instances)
        {
            var added = new Instance(kind, now, now + seconds / expiryRate, magnitude);
            instances.RemoveAll(e => e.Until <= now || e.Kind == kind && e.Magnitude <= magnitude && e.Until <= added.Until);
            instances.Add(added);
            count = instances.Count;
            return added;
        }
    }

    /// <summary>The owner's effects now expire at <paramref name="rate"/> (at least a quarter of the normal speed).</summary>
    public void SetExpiryRate(double now, double rate)
    {
        lock (instances)
        {
            rate = Math.Max(.25, rate);
            for (int i = 0; i < instances.Count; i++)
                instances[i] = instances[i] with { Until = RecipientEffectTime.RetimedDeadline(instances[i].Until, now, expiryRate, rate) };
            expiryRate = rate;
        }
    }

    /// <summary>Ends every instance; the next <see cref="Present"/> reports the kinds that were shown as gone.</summary>
    public void Clear() { lock (instances) { instances.Clear(); count = 0; } }

    /// <summary>The kinds whose strongest magnitude (truncated) at <paramref name="now"/> differs from what was last
    /// presented, with the old and new values (0: none), in kind order; expired instances are dropped.</summary>
    public List<(int Kind, int Was, int Now)> Present(double now)
    {
        lock (instances)
        {
            instances.RemoveAll(e => e.Until <= now);
            count = instances.Count;
            var changes = new List<(int, int, int)>();
            foreach (int kind in instances.Select(e => e.Kind).Concat(shown.Keys).Distinct().Order().ToList())
            {
                int magnitude = (int)Magnitude(kind, now);
                int was = shown.GetValueOrDefault(kind);
                if (magnitude == was) continue;
                if (magnitude > 0) shown[kind] = magnitude;
                else shown.Remove(kind);
                changes.Add((kind, was, magnitude));
            }
            return changes;
        }
    }
}
