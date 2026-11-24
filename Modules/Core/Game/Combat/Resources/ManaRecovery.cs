/// <summary>Stat-driven Mana costs.</summary>
public static class ManaRecoveryRules
{
    public const uint PerDexterity = Stat.XManaPerDexterity;
    public const uint RagePer200 = Stat.GainXRagePer200ManaSpent;
    public static int Cost(int supportedBase, Func<uint, int> player, bool? channelled = null, double more = 1) =>
        Cost(supportedBase, player, channelled, more, applyManaEfficiency: true);

    public static int Cost(int supportedBase, Func<uint, int> player, bool? channelled, double more, bool applyManaEfficiency)
    {
        decimal scaled = ScaledCost(supportedBase, player, more);
        // Resource conversion precedes resource-specific efficiency. Mana efficiency cannot reduce a Life cost.
        if (applyManaEfficiency && !ConvertsManaToLife(player) && player(ItemChargeCosts.ManaEfficiency) is int efficiency and not 0)
            scaled = ApplyManaEfficiency(scaled, efficiency);
        // Flat total-cost modifiers apply after support multipliers and percentage modifiers. The skill's type,
        // rather than whether the actor is presently channelling, selects the channel-specific term.
        return (int)Math.Clamp(scaled + FlatManaCost(player, channelled), 0m, int.MaxValue);
    }

    static decimal ScaledCost(int supportedBase, Func<uint, int> player, double more)
    {
        decimal scaled = decimal.Floor(Math.Max(0, supportedBase) * Math.Max(0m, 100m - player(Stat.BaseManaCostMinusPct))
            * Math.Max(0m, 100m + player(Stat.ManaCostPlusPct) + player(Stat.BaseCostPlusPct)) / 10000m);
        scaled = Math.Clamp(scaled, 0m, int.MaxValue);
        if (more != 1)
            scaled = (decimal)Math.Clamp(Math.Round((double)scaled * more), 0, int.MaxValue);
        int inspiration = player(Stat.SupportInspirationManaCostPlusPctFinal);
        if (inspiration != 0)
            scaled = decimal.Round(scaled * Math.Max(0m, 1m + inspiration / 100m), 0, MidpointRounding.AwayFromZero);
        return scaled;
    }

    static decimal ApplyManaEfficiency(decimal scaled, int efficiency)
    {
        decimal denominator = 100m + efficiency;
        return denominator <= 0 ? scaled == 0 ? 0 : int.MaxValue
            : decimal.Round(scaled * 100 / denominator, 0, MidpointRounding.AwayFromZero);
    }

    /// <summary>Splits an attack skill's effective post-modifier Mana cost into a Life share and remaining Mana share
    /// (Cannibalistic Rite's <c>base_attack_skill_cost_life_instead_of_mana_%</c>). Negative flat cost modifiers can
    /// reduce the effective cost to zero (charging 0 Life and 0 Mana); otherwise the pre-efficiency effective cost is
    /// partitioned conservatively (life + pre-efficiency mana == total), with Life-specific cost multipliers applied
    /// only to the Life share and Mana-specific efficiency applied only to the Mana share.</summary>
    public static (int ManaCost, int PreEfficiencyManaCost, int LifeCost) SplitCostToLife(
        int supportedBase, Func<uint, int> player, bool? channelled, double more, int splitPercent)
    {
        int split = Math.Clamp(splitPercent, 0, 100);
        decimal scaled = ScaledCost(supportedBase, player, more);
        long flat = FlatManaCost(player, channelled);
        int preEfficiencyTotal = (int)Math.Clamp(scaled + flat, 0m, int.MaxValue);
        if (preEfficiencyTotal <= 0) return (0, 0, 0);

        int rawLife = (int)decimal.Round(preEfficiencyTotal * (decimal)split / 100m, 0, MidpointRounding.AwayFromZero);
        int preEfficiencyMana = preEfficiencyTotal - rawLife;

        // BaseCostPlusPct was already applied in ScaledCost; only Life-specific percentage modifiers rescale rawLife.
        int lifeSpecificPct = player(Stat.BaseLifeCostPlusPct) + player(Stat.LifeCostPlusPct);
        int lifeCost = lifeSpecificPct != 0
            ? (int)Math.Clamp(decimal.Floor(rawLife * Math.Max(0m, 100m + lifeSpecificPct) / 100m), 0m, int.MaxValue)
            : rawLife;

        int manaCost = preEfficiencyMana;
        if (preEfficiencyMana > 0 && player(ItemChargeCosts.ManaEfficiency) is int efficiency and not 0)
        {
            if (flat == 0)
                manaCost = (int)Math.Clamp(ApplyManaEfficiency(preEfficiencyMana, efficiency), 0m, int.MaxValue);
            else
            {
                decimal scaledLife = decimal.Round(scaled * split / 100m, 0, MidpointRounding.AwayFromZero);
                decimal scaledMana = scaled - scaledLife;
                long flatLife = (long)decimal.Round(flat * (decimal)split / 100m, 0, MidpointRounding.AwayFromZero);
                long flatMana = flat - flatLife;
                manaCost = (int)Math.Clamp(ApplyManaEfficiency(scaledMana, efficiency) + flatMana, 0m, int.MaxValue);
            }
        }
        return (manaCost, preEfficiencyMana, lifeCost);
    }

    public static long FlatManaCost(Func<uint, int> player, bool? channelled)
    {
        long flat = player(Stat.SkillManaCostPlus);
        if (channelled is bool channel)
            flat += player(channel ? Stat.SkillManaCostPlusWithChannellingSkills
                : Stat.SkillManaCostPlusWithNonChannellingSkills);
        return flat;
    }

    public static bool ConvertsManaToLife(Func<uint, int> stat) => stat(Stat.KeystoneBloodMagic) != 0
        || stat(Lifetap.CostLifeStat) != 0 || stat(Lifetap.AlternateCostLifeStat) != 0;

    public static int LifeCost(long nativeLife, Func<uint, int> stat) => LifeCost(nativeLife, stat, 1);

    internal static int LifeCost(long nativeLife, Func<uint, int> stat, double costMore) =>
        (int)Math.Clamp(decimal.Floor(nativeLife * Math.Max(0m, 100m + stat(Stat.BaseCostPlusPct)
            + stat(Stat.BaseLifeCostPlusPct) + stat(Stat.LifeCostPlusPct)) * (decimal)costMore / 100m), 0, int.MaxValue);
}

/// <summary>Owned mana-spend progress and exact hit recoup. ReminderTextRecoup pins four game seconds, which "Recoup
/// Effects instead occur over 3 seconds" shortens to three.</summary>
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
    /// <param name="seconds">The window the hit's recoup is paid over: <see cref="RecoupSeconds"/>, or three with
    /// "Recoup Effects instead occur over 3 seconds" (<see cref="Game.ManaRecoupSeconds"/>). The total is the same;
    /// only the rate changes.</param>
    public void Hit(double now, int damage, int percent, double seconds)
    {
        Integrate(now);
        if (damage > 0 && percent > 0 && seconds > 0)
            recoup.Add(new(now + seconds, damage * (decimal)percent / 100 / (decimal)seconds, damage * (decimal)percent / 100));
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
