/// <summary>LocalExile incoming-contact model v1. Owned by one area connection; use under its Monsters lock.</summary>
public sealed class RecentEnemyHits
{
    public const uint Counter = Stat.NumberOfTimesHaveBeenHitByEnemyRecently;
    public const uint PerHit = Stat.ChanceToDoubleArmourEffectOnHitPctPerEnemyHitTakenRecentlyCapped;
    public const uint GenericChance = Stat.ChanceToDoubleArmourEffectOnHitPct;
    public const uint VirtualChance = Stat.VirtualChanceToDoubleArmourEffectOnHitPct;
    public const double RecentlySeconds = 4;
    readonly Queue<double> contacts = new();

    public int Count(double now)
    {
        while (contacts.TryPeek(out double at) && now - at >= RecentlySeconds) contacts.Dequeue();
        return contacts.Count;
    }

    /// <summary>A contact was recorded or the contacts cleared (the stats that count them follow).</summary>
    public Action? Changed;

    public void Record(double now) { Count(now); contacts.Enqueue(now); Changed?.Invoke(); }

    /// <summary>Seconds until the count next drops (the oldest contact leaves the window); null when none is counted.</summary>
    public double? NextChangeIn(double now) => Count(now) > 0 && contacts.TryPeek(out double at) ? at + RecentlySeconds - now : null;
    public void Clear()
    {
        if (contacts.Count == 0) return;
        contacts.Clear();
        Changed?.Invoke();
    }

    // The 30% cap applies to the per-hit contribution, before adding the independent generic chance.
    public static int Chance(int count, int perHit, int generic) =>
        (int)Math.Clamp((long)generic + Math.Min(30L, (long)Math.Max(0, count) * perHit), 0, 100);

    public readonly record struct Decision(int EffectiveArmour, int Chance, bool Doubled);

    // Resolve once per whole unblocked hit with a positive, non-immune armour-mitigated component.
    // Other hit origins can benefit without recording an enemy contact.
    public static Decision Resolve(int armour, int chance, bool relevant, Random random)
    {
        armour = Math.Max(0, armour);
        chance = Math.Clamp(chance, 0, 100);
        bool doubled = relevant && armour > 0 && chance > 0 && random.Next(100) < chance;
        return new((int)Math.Min(int.MaxValue, (long)armour * (doubled ? 2 : 1)), chance, doubled);
    }
}
