/// <summary>LocalExile incoming-contact model v1. Owned by one area connection; use under its Monsters lock.</summary>
sealed class RecentEnemyHits
{
    public const string Counter = "number_of_times_have_been_hit_by_enemy_recently";
    public const string PerHit = "chance_to_double_armour_effect_on_hit_%_per_enemy_hit_taken_recently_capped";
    public const string GenericChance = "chance_to_double_armour_effect_on_hit_%";
    public const string VirtualChance = "virtual_chance_to_double_armour_effect_on_hit_%";
    public const double RecentlySeconds = 4;
    readonly Queue<double> contacts = new();

    public int Count(double now)
    {
        while (contacts.TryPeek(out double at) && now - at >= RecentlySeconds) contacts.Dequeue();
        return contacts.Count;
    }

    public void Record(double now) { Count(now); contacts.Enqueue(now); }
    public void Clear() => contacts.Clear();

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
