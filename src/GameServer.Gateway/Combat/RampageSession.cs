/// <summary>Combat streak and pending tier crossings. Explicit policy v1: one stack per accepted owner melee
/// contact and one per owner kill, including both on a melee killing blow. A counter alone is not full Rampage.</summary>
public sealed class RampageSession
{
    public const uint Source = Stat.MeleeHitsGrantRampageStacks;
    /// <summary>The other way to start a streak: kills grant the stacks instead of melee hits.</summary>
    public const uint KillSource = Stat.PlayerGainRampageStacks;
    public static readonly int[] Thresholds = [15, 30, 45, 70, 100, 135, 175, 220, 270, 325, 385, 450, 520];
    readonly RampageState state = new(Thresholds);
    readonly HashSet<object> creditedKills = new(ReferenceEqualityComparer.Instance);
    readonly Queue<int> pendingTiers = new();
    // The same crossings feed the "on Rampage threshold" item mods; the tier executor keeps its own queue.
    readonly Queue<int> pendingThresholds = new();
    public IReadOnlyList<int> PendingTiers => pendingTiers.ToArray();
    /// <summary>The streak grew, was cleared or was carried in (the stat steps that read it run again).</summary>
    public Action? Changed;

    public RampageState.Value Current => state.Current;
    public int KillsInStreak { get; private set; }

    public RampageState.Carried Carry(double now)
    {
        pendingTiers.Clear();
        pendingThresholds.Clear();
        var carried = state.Carry(now);
        return carried with { KillsInStreak = carried.Remaining > 0 ? KillsInStreak : 0 };
    }
    public void Restore(RampageState.Carried carried, double now)
    {
        pendingTiers.Clear();
        pendingThresholds.Clear();
        state.Restore(carried, now);
        KillsInStreak = Current.Active ? carried.KillsInStreak : 0;
        Changed?.Invoke();
    }
    public int[] TakeTiers()
    {
        int[] result = pendingTiers.ToArray(); pendingTiers.Clear(); return result;
    }
    public int[] TakeThresholds()
    {
        int[] result = pendingThresholds.ToArray(); pendingThresholds.Clear(); return result;
    }

    public RampageState.Value Observe(double now, bool enabled, bool alive, bool active)
    {
        var before = Current;
        state.Advance(now);
        if (!enabled || !alive || !active) { Clear(); return Current; }
        if (before.Active && !Current.Active)
        {
            pendingTiers.Clear();
            pendingThresholds.Clear();
            KillsInStreak = 0;
        }
        return Current;
    }

    public bool MeleeHit(double now, bool enabled, bool alive, bool active, bool melee, bool hostile)
    {
        Observe(now, enabled, alive, active);
        if (!enabled || !alive || !active || !melee || !hostile) return false;
        return Gain(now);
    }

    public bool Kill(double now, object monster, bool enabled, bool alive, bool active, bool hostile)
    {
        Observe(now, enabled, alive, active);
        if (!enabled || !alive || !active || !hostile || creditedKills.Contains(monster) || !Gain(now)) return false;
        creditedKills.Add(monster);
        KillsInStreak++;
        return true;
    }

    bool Gain(double now)
    {
        var change = state.GainQualified(now);
        if (!change.Accepted) return false;
        Changed?.Invoke();
        foreach (int tier in change.CrossedThresholds) { pendingTiers.Enqueue(tier); pendingThresholds.Enqueue(tier); }
        return true;
    }

    public void Clear()
    {
        KillsInStreak = 0;
        if (state.Clear()) Changed?.Invoke();
        pendingTiers.Clear(); pendingThresholds.Clear();
        // Keep credited kills across source changes/expiry: delayed notices cannot grant them again.
    }
}
