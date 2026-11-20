/// <summary>Resource routing after mitigation. Ward consumes the reduced hit before typed ES bypass.</summary>
public static class IncomingResourceDefences
{
    public const string TakenStat = "harbinger_channel_buff_damage_taken_+%_final";
    public const string ConditionalTakenStat = "damage_taken_+%_final";
    public const string UnhingedSaneDamageFinal = "physical_and_chaos_damage_taken_+%_final_while_not_unhinged";
    public const string BypassStat = "base_non_chaos_damage_bypass_energy_shield_%";

    // The Eater of Worlds body-armour implicits: "1% less Damage Taken per X Strength / Dexterity / Intelligence".
    public const string LessPerStrength = "body_armour_implicit_damage_taken_-1%_final_per_X_strength";
    public const string LessPerDexterity = "body_armour_implicit_damage_taken_-1%_final_per_X_dexterity";
    public const string LessPerIntelligence = "body_armour_implicit_damage_taken_-1%_final_per_X_intelligence";

    // Final more/less damage-taken stats modules handed over, in the order they were handed over.
    static volatile string[] _finalTaken = [];
    static readonly object FinalTakenLock = new();

    /// <summary>A hand-over for modules: each stat in <paramref name="stats"/> multiplies incoming hit and
    /// over-time damage by max(0, 1 + value / 100), a "more" or "less" damage-taken modifier. The framework names
    /// none; with none handed over, or none on the character, the factor is 1. Disposing the result (the module's
    /// lifetime) takes back the stats it added.</summary>
    public static IDisposable HandOverFinalTaken(IReadOnlyCollection<string> stats)
    {
        string[] added;
        lock (FinalTakenLock)
        {
            added = [.. stats.Distinct().Except(_finalTaken)];
            if (added.Length > 0) _finalTaken = [.. _finalTaken, .. added];
        }
        return new FinalTakenHandOver(added);
    }

    sealed class FinalTakenHandOver(string[] stats) : IDisposable
    {
        int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0 || stats.Length == 0) return;
            lock (FinalTakenLock)
                _finalTaken = [.. _finalTaken.Except(stats)];
        }
    }

    /// <summary>The final "less Damage Taken" share from the attribute implicits: each stat's attribute total divided by
    /// its X, added together and capped at 100%. The attribute totals include the item's own attributes.</summary>
    public static int LessTakenPct(CharacterStats.Pools pools, Func<string, int> get) =>
        Per(pools.Strength, get(LessPerStrength)) + Per(pools.Dexterity, get(LessPerDexterity))
        + Per(pools.Intelligence, get(LessPerIntelligence));

    static int Per(int attribute, int per) => per > 0 ? Math.Clamp(attribute / per, 0, 100) : 0;

    /// <summary>The combined final damage-taken multiplier from <see cref="TakenStat"/>,
    /// <see cref="ConditionalTakenStat"/>, attribute implicits (<see cref="LessTakenPct"/>) and any handed-over
    /// final taken stats (<see cref="HandOverFinalTaken"/>).</summary>
    public static double FinalTakenFactor(CharacterStats.Pools pools, Func<string, int> get) =>
        FinalTakenFactor(get, LessTakenPct(pools, get));

    /// <inheritdoc cref="FinalTakenFactor(CharacterStats.Pools, Func{string, int})"/>
    public static double FinalTakenFactor(Func<string, int> get, int lessPct = 0)
    {
        double factor = Math.Max(0, 1 + get(TakenStat) / 100.0)
            * Math.Max(0, 1 + get(ConditionalTakenStat) / 100.0)
            * Math.Max(0, 1 - Math.Clamp(lessPct, 0, 100) / 100.0);
        string[] extra = _finalTaken;
        for (int i = 0; i < extra.Length; i++)
        {
            int pct = get(extra[i]);
            if (pct != 0) factor *= Math.Max(0, 1 + pct / 100.0);
        }
        return factor;
    }

    public static int Taken(int damage, Func<string, int> get, int lessPct = 0)
    {
        decimal value = Math.Max(0, damage)
            * Math.Max(0m, 1 + get(TakenStat) / 100m)
            * Math.Max(0m, 1 + get(ConditionalTakenStat) / 100m)
            * (1 - Math.Clamp(lessPct, 0, 100) / 100m);
        string[] extra = _finalTaken;
        for (int i = 0; i < extra.Length; i++)
        {
            int pct = get(extra[i]);
            if (pct != 0) value *= Math.Max(0m, 1 + pct / 100m);
        }
        return (int)Math.Clamp(value, 0, int.MaxValue);
    }

    public static int Damage(EnergyShield shield, int damage, int bypassPercent, double now)
    {
        int bypass = (int)(Math.Max(0L, damage) * Math.Clamp(bypassPercent, 0, 100) / 100);
        return bypass + shield.Damage(Math.Max(0, damage) - bypass, false, now);
    }

    public static int TakenOverTime(double damage, Func<string, int> get, ref double carry, int lessPct = 0,
        int typedIncreasedPct = 0, int reductionPct = 0, int finalTakenPct = 0)
    {
        double earned = Math.Max(0, damage)
            * Math.Max(0, 1 + ((long)get("base_damage_taken_+%") + typedIncreasedPct) / 100.0)
            * Math.Max(0, 1 - Math.Clamp(reductionPct, 0, 100) / 100.0)
            * FinalTakenFactor(get, lessPct)
            * Math.Max(0, 1 + finalTakenPct / 100.0) + carry;
        int whole = (int)Math.Min(int.MaxValue, Math.Floor(earned + 1e-9));
        carry = Math.Max(0, earned - whole);
        return whole;
    }

    public static int DamageOverTime(EnergyShield shield, int damage, int bypassPercent, double now,
        ref double carry)
    {
        carry += Math.Max(0, damage) * Math.Clamp(bypassPercent, 0, 100) / 100.0;
        int bypass = (int)Math.Floor(carry + 1e-9);
        carry = Math.Max(0, carry - bypass);
        return bypass + shield.Damage(Math.Max(0, damage) - bypass, false, now);
    }

    public static uint Maximum(uint ordinary, uint life, uint mana, int increased, int lifePercent, int manaPercent)
    {
        decimal extra = ((decimal)life * lifePercent + (decimal)mana * manaPercent) / 100;
        return (uint)Math.Clamp(ordinary + decimal.Truncate(extra * Math.Max(0m, 100m + increased) / 100),
            0, int.MaxValue);
    }
}
