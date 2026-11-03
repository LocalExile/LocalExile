/// <summary>Resource routing after mitigation. Ward consumes the reduced hit before typed ES bypass.</summary>
static class IncomingResourceDefences
{
    internal const string TakenStat = "harbinger_channel_buff_damage_taken_+%_final";
    internal const string BypassStat = "base_non_chaos_damage_bypass_energy_shield_%";

    internal static int Taken(int damage, Func<string, int> get) =>
        (int)Math.Clamp(Math.Max(0L, damage) * Math.Max(0L, 100L + get(TakenStat)) / 100, 0, int.MaxValue);

    internal static int Damage(EnergyShield shield, int damage, int bypassPercent, double now)
    {
        int bypass = (int)(Math.Max(0L, damage) * Math.Clamp(bypassPercent, 0, 100) / 100);
        return bypass + shield.Damage(Math.Max(0, damage) - bypass, false, now);
    }

    internal static int TakenOverTime(int damage, Func<string, int> get, ref double carry)
    {
        double earned = Math.Max(0, damage) * Math.Max(0, 1 + get(TakenStat) / 100.0) + carry;
        int whole = (int)Math.Min(int.MaxValue, Math.Floor(earned + 1e-9));
        carry = Math.Max(0, earned - whole);
        return whole;
    }

    internal static int DamageOverTime(EnergyShield shield, int damage, int bypassPercent, double now,
        ref double carry)
    {
        carry += Math.Max(0, damage) * Math.Clamp(bypassPercent, 0, 100) / 100.0;
        int bypass = (int)Math.Floor(carry + 1e-9);
        carry = Math.Max(0, carry - bypass);
        return bypass + shield.Damage(Math.Max(0, damage) - bypass, false, now);
    }

    internal static uint Maximum(uint ordinary, uint life, uint mana, int increased, int lifePercent, int manaPercent)
    {
        decimal extra = ((decimal)life * lifePercent + (decimal)mana * manaPercent) / 100;
        return (uint)Math.Clamp(ordinary + decimal.Truncate(extra * Math.Max(0m, 100m + increased) / 100),
            0, int.MaxValue);
    }
}
