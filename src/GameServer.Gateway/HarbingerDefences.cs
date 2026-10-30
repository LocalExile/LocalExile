/// <summary>Extra base ES from final pools; eligible non-chaos bypass after Ward and before ES.</summary>
static class HarbingerDefences
{
    public static int Taken(int damage, Func<string, int> get) =>
        (int)Math.Clamp((long)Math.Max(0, damage)
            * Math.Max(0L, 100L + get("harbinger_channel_buff_damage_taken_+%_final")) / 100, 0, int.MaxValue);

    public static uint Maximum(uint ordinary, uint life, uint mana, int increased, int lifePercent, int manaPercent)
    {
        decimal extra = ((decimal)life * lifePercent + (decimal)mana * manaPercent) / 100;
        return (uint)Math.Clamp(ordinary + decimal.Truncate(extra * Math.Max(0m, 100m + increased) / 100),
            0, int.MaxValue);
    }

    public static int Damage(EnergyShield shield, int damage, int bypassPercent, double now)
    {
        int bypass = (int)((long)Math.Max(0, damage) * Math.Clamp(bypassPercent, 0, 100) / 100);
        return bypass + shield.Damage(damage - bypass, false, now);
    }

    public static int DamageOverTime(EnergyShield shield, int damage, int bypassPercent, double now,
        ref double bypassCarry)
    {
        bypassCarry += Math.Max(0, damage) * Math.Clamp(bypassPercent, 0, 100) / 100.0;
        int bypass = (int)Math.Floor(bypassCarry + 1e-9);
        bypassCarry = Math.Max(0, bypassCarry - bypass);
        return bypass + shield.Damage(damage - bypass, false, now);
    }
}
