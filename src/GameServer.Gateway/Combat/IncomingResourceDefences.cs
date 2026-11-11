/// <summary>Resource routing after mitigation. Ward consumes the reduced hit before typed ES bypass.</summary>
public static class IncomingResourceDefences
{
    public const string TakenStat = "harbinger_channel_buff_damage_taken_+%_final";
    public const string UnhingedSaneDamageFinal = "physical_and_chaos_damage_taken_+%_final_while_not_unhinged";
    public const string BypassStat = "base_non_chaos_damage_bypass_energy_shield_%";

    // The Eater of Worlds body-armour implicits: "1% less Damage Taken per X Strength / Dexterity / Intelligence".
    public const string LessPerStrength = "body_armour_implicit_damage_taken_-1%_final_per_X_strength";
    public const string LessPerDexterity = "body_armour_implicit_damage_taken_-1%_final_per_X_dexterity";
    public const string LessPerIntelligence = "body_armour_implicit_damage_taken_-1%_final_per_X_intelligence";

    /// <summary>The final "less Damage Taken" share from the attribute implicits: each stat's attribute total divided by
    /// its X, added together and capped at 100%. The attribute totals include the item's own attributes.</summary>
    public static int LessTakenPct(CharacterStats.Pools pools, Func<string, int> get) =>
        Per(pools.Strength, get(LessPerStrength)) + Per(pools.Dexterity, get(LessPerDexterity))
        + Per(pools.Intelligence, get(LessPerIntelligence));

    static int Per(int attribute, int per) => per > 0 ? Math.Clamp(attribute / per, 0, 100) : 0;

    public static int Taken(int damage, Func<string, int> get, int lessPct = 0) =>
        (int)Math.Clamp(Math.Max(0L, damage) * Math.Max(0L, 100L + get(TakenStat))
            * Math.Max(0L, 100L - Math.Clamp(lessPct, 0, 100)) / 10000, 0, int.MaxValue);

    public static int Damage(EnergyShield shield, int damage, int bypassPercent, double now)
    {
        int bypass = (int)(Math.Max(0L, damage) * Math.Clamp(bypassPercent, 0, 100) / 100);
        return bypass + shield.Damage(Math.Max(0, damage) - bypass, false, now);
    }

    public static int TakenOverTime(double damage, Func<string, int> get, ref double carry, int lessPct = 0,
        int typedIncreasedPct = 0, int reductionPct = 0, int finalTakenPct = 0)
    {
        double earned = Math.Max(0, damage)
            * Math.Max(0, 1 + (get("damage_taken_+%") + typedIncreasedPct) / 100.0)
            * Math.Max(0, 1 - Math.Clamp(reductionPct, 0, 100) / 100.0)
            * Math.Max(0, 1 + get(TakenStat) / 100.0)
            * Math.Max(0, 1 + finalTakenPct / 100.0)
            * Math.Max(0, 1 - Math.Clamp(lessPct, 0, 100) / 100.0) + carry;
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
