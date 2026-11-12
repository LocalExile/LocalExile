/// <summary>Stat-driven Mana costs.</summary>
public static class ManaRecoveryRules
{
    public const string PerDexterity = "X_mana_per_dexterity";
    public const string RagePer200 = "gain_x_rage_per_200_mana_spent";
    public static int Cost(int supportedBase, Func<string, int> player, bool? channelled = null, double more = 1) =>
        Cost(supportedBase, player, channelled, more, applyManaEfficiency: true);

    public static int Cost(int supportedBase, Func<string, int> player, bool? channelled, double more, bool applyManaEfficiency)
    {
        decimal scaled = decimal.Floor(Math.Max(0, supportedBase) * Math.Max(0m, 100m - player("base_mana_cost_-%"))
            * Math.Max(0m, 100m + player("mana_cost_+%") + player("base_cost_+%")) / 10000m);
        scaled = Math.Clamp(scaled, 0m, int.MaxValue);
        if (more != 1)
            scaled = (decimal)Math.Clamp(Math.Round((double)scaled * more), 0, int.MaxValue);
        int inspiration = player("support_inspiration_mana_cost_+%_final");
        if (inspiration != 0)
            scaled = decimal.Round(scaled * Math.Max(0m, 1m + inspiration / 100m), 0, MidpointRounding.AwayFromZero);
        // Resource conversion precedes resource-specific efficiency. Mana efficiency cannot reduce a Life cost.
        bool convertsToLife = player("keystone_blood_magic") != 0 || player(Lifetap.CostLifeStat) != 0
            || player(Lifetap.AlternateCostLifeStat) != 0;
        if (applyManaEfficiency && !convertsToLife && player(ItemChargeCosts.ManaEfficiency) is int efficiency and not 0)
        {
            decimal denominator = 100m + efficiency;
            scaled = denominator <= 0 ? scaled == 0 ? 0 : int.MaxValue
                : decimal.Round(scaled * 100 / denominator, 0, MidpointRounding.AwayFromZero);
        }
        // Flat total-cost modifiers apply after support multipliers and percentage modifiers. The skill's type,
        // rather than whether the actor is presently channelling, selects the channel-specific term.
        long flat = player("skill_mana_cost_+");
        if (channelled is bool channel)
            flat += player(channel ? "skill_mana_cost_+_with_channelling_skills"
                : "skill_mana_cost_+_with_non_channelling_skills");
        return (int)Math.Clamp(scaled + flat, 0m, int.MaxValue);
    }
}

/// <summary>Owned mana-spend progress and exact hit recoup. ReminderTextRecoup pins four game seconds.</summary>
public sealed class ManaRecoupState
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
    public Carried Capture(double now)
    {
        Integrate(now);
        return new(SpentRemainder, pending, [.. recoup.Select(r => (r.Until - now, r.Rate, r.Remaining))]);
    }
    public Carried Carry(double now) { var value = Capture(now); Clear(); return value; }
    public void Restore(Carried value, double now)
    {
        Clear(); SpentRemainder = value.Spent; pending = value.Pending; at = now;
        recoup.AddRange(value.Recoup.Select(r => new Grant(now + r.Seconds, r.Rate, r.Remaining)));
    }
}
