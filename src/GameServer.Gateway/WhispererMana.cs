/// <summary>Whisperer's raw mana inputs and hit-only resource rules. All amounts precede final pool rounding.</summary>
static class WhispererMana
{
    public const string PerDexterity = "X_mana_per_dexterity", NoIntelligence = "gain_no_maximum_mana_from_intelligence";
    public const string RagePer200 = "gain_x_rage_per_200_mana_spent";
    public static long AttributeManaTwice(int intelligence, int dexterity, bool noIntelligence, int perDexterity) =>
        (noIntelligence ? 0L : intelligence) + 2L * dexterity * perDexterity;

    public static int Cost(int supportedBase, Func<string, int> player) => (int)Math.Clamp(decimal.Floor(
        Math.Max(0, supportedBase) * Math.Max(0m, 100m - player("base_mana_cost_-%"))
        * Math.Max(0m, 100m + player("mana_cost_+%")) / 10000m), 0, int.MaxValue);

    public static uint FromMana(int physicalLifeDamage, uint availableMana, int percent) =>
        (uint)Math.Min(availableMana, (long)Math.Max(0, physicalLifeDamage) * Math.Clamp(percent, 0, 100) / 100);
}

/// <summary>Owned mana-spend progress and exact hit recoup. ReminderTextRecoup pins four game seconds.</summary>
sealed class WhispererManaState
{
    public const double RecoupSeconds = 4;
    public int SpentRemainder { get; private set; }
    readonly record struct Grant(double Until, decimal Rate, decimal Remaining);
    readonly List<Grant> recoup = [];
    double at;
    decimal pending;

    public void Observe(int ragePer200) { if (ragePer200 <= 0) SpentRemainder = 0; }
    public long Spent(uint amount, int ragePer200)
    {
        Observe(ragePer200);
        if (ragePer200 <= 0) return 0;
        long total = SpentRemainder + (long)amount;
        SpentRemainder = (int)(total % 200);
        return total / 200 * ragePer200;
    }
    public void Hit(double now, int damage, int percent)
    {
        Integrate(now);
        if (damage > 0 && percent > 0)
            recoup.Add(new(now + RecoupSeconds, damage * (decimal)percent / 400, damage * (decimal)percent / 100));
    }
    void Integrate(double now)
    {
        for (int i = 0; i < recoup.Count; i++)
        {
            var r = recoup[i];
            // Settle the exact remaining amount at expiry, instead of losing an integer boundary to double time.
            decimal earned = now >= r.Until ? r.Remaining
                : Math.Min(r.Remaining, (decimal)Math.Max(0, now - at) * r.Rate);
            pending += earned;
            recoup[i] = r with { Remaining = r.Remaining - earned };
        }
        at = Math.Max(now, at);
        recoup.RemoveAll(r => r.Until <= now);
    }
    // Keep sub-point recoup owned here in decimal. Handing repeated double fractions to another carry loses a point
    // at exact expiry; publishing whole earned points is the resource writer's existing visible granularity.
    public double Take(double now)
    {
        Integrate(now); decimal whole = decimal.Truncate(pending); pending -= whole; return (double)whole;
    }
    public void DiscardRecoveryFraction() => pending = 0;
    public void Clear() { SpentRemainder = 0; recoup.Clear(); at = 0; pending = 0; }
    public sealed record Carried(int Spent, decimal Pending, (double Seconds, decimal Rate, decimal Remaining)[] Recoup);
    public Carried Carry(double now)
    {
        Integrate(now);
        var value = new Carried(SpentRemainder, pending, [.. recoup.Select(r => (r.Until - now, r.Rate, r.Remaining))]);
        Clear(); return value;
    }
    public void Restore(Carried value, double now)
    {
        Clear(); SpentRemainder = value.Spent; pending = value.Pending; at = now;
        recoup.AddRange(value.Recoup.Select(r => new Grant(now + r.Seconds, r.Rate, r.Remaining)));
    }
}
