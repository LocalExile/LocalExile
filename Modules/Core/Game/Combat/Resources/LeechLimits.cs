

/// <summary>
/// The character panel's leech limits, independently of active leech instances. The default percentages are
/// Character.ot's constants, also exported in pinned PoB 16de4b82's Data/Misc.lua and used by CalcSetup/CalcDefence.
/// The limits use maximum pools, including their reserved portions. This does not create or recover any leech.
/// </summary>
public static class LeechLimits
{
    public const int BaseInstancePercent = 10;
    public const int BaseLifeManaRatePercentPerMinute = 1200;
    public const int BaseEnergyShieldRatePercentPerMinute = 600;

    public sealed record Resource(string Pool, uint MaximumPool, bool CannotLeech, decimal InstancePermyriad,
        decimal RatePermyriadPerMinute)
    {
        public double MaximumPerInstance => MaximumPool * (double)InstancePermyriad / 10_000;
        public double MaximumPerSecond => MaximumPool * (double)RatePermyriadPerMinute / 600_000;
        public uint InstanceStat => Stat.Of($"virtual_maximum_{Pool}_leech_amount_per_leech_permyriad_max_{Pool}");
        public uint RateStat => Stat.Of($"virtual_maximum_{Pool}_leech_rate_permyriad_per_minute");
    }

    public sealed record Snapshot(bool CannotLeech, IReadOnlyList<Resource> Resources);

    /// <summary>
    /// Six absolute limits and their native permyriad companions, plus the shared cannot-leech flags that gate them.
    /// <paramref name="get"/> reads resolved sources without Character.ot's defaults.
    /// Increased maximum rate is distinct from leech recovery speed. Integer wire endpoints truncate nonnegative
    /// fractions; PoB retains fractional values, so this projection is not a measured official-server rounding rule.
    /// Conditional modifiers requiring other systems (reservation, recent savage hits, Defiance and party links),
    /// including the derived Wicked Ward virtual, are outside this calculation. Explicit raw Life maximum-rate
    /// doubling is supported independently of the keystone or item that might supply it.
    /// <paramref name="onLowLife"/> supplies the condition that prevents Life and Mana leech; pinned PoB's ModParser
    /// leaves ES leech available.
    /// </summary>
    public static List<(uint Stat, int Value)> Compute(Func<uint, int> get, uint maximumLife, uint maximumMana,
        uint maximumEnergyShield, bool onLowLife = false)
    {
        var limits = Resolve(get, maximumLife, maximumMana, maximumEnergyShield, onLowLife: onLowLife);
        var rows = new List<(uint Stat, int Value)> { (Stat.CannotLeech, limits.CannotLeech ? 1 : 0) };
        foreach (var resource in limits.Resources)
        {
            rows.Add((Stat.Of($"cannot_leech_{resource.Pool}"), resource.CannotLeech ? 1 : 0));
            rows.Add((resource.InstanceStat, Whole(resource.InstancePermyriad)));
            rows.Add((resource.RateStat, Whole(resource.RatePermyriadPerMinute)));
            rows.Add((Stat.Of($"maximum_{resource.Pool}_leech_amount_per_leech"), OfPool(resource.MaximumPool, resource.InstancePermyriad)));
            rows.Add((Stat.Of($"maximum_total_recovery_per_minute_from_{resource.Pool}_leech"), OfPool(resource.MaximumPool, resource.RatePermyriadPerMinute)));
        }
        return rows;
    }

    // Combat retains fractions; only Compute projects whole wire values. A small pool must not lose its recovery
    // merely because its per-instance amount displays below one whole resource point.
    public static Snapshot Resolve(Func<uint, int> get, uint maximumLife, uint maximumMana,
        uint maximumEnergyShield, Func<uint, int?>? replacement = null, bool onLowLife = false)
    {
        decimal Input(uint name, int baseline = 0) => replacement?.Invoke(name) is int set ? set : (decimal)baseline + get(name);
        bool Flag(uint name) => Input(name) != 0;
        bool ReplacedFlag(uint name, bool derived) => replacement?.Invoke(name) is int set ? set != 0 : derived;
        bool all = ReplacedFlag(Stat.CannotLeech, Flag(Stat.BaseCannotLeech) || Flag(Stat.CannotLeech) || Flag(Stat.MapPlayersCannotLeech));
        bool lowLifeLifeMana = onLowLife && Flag(Stat.CannotLeechWhenOnLowLife);
        var resources = new List<Resource>();
        foreach (var (pool, maximum) in new[] { ("life", maximumLife), ("mana", maximumMana), ("energy_shield", maximumEnergyShield) })
        {
            bool blocked = ReplacedFlag(Stat.Of($"cannot_leech_{pool}"), all || Flag(Stat.Of($"base_cannot_leech_{pool}"))
                || Flag(Stat.Of($"cannot_leech_{pool}")) || pool != "energy_shield" && lowLifeLifeMana
                || pool == "mana" && Flag(Stat.CannotLeechOrRegenerateMana));
            string maximumName = pool == "energy_shield" ? "energy_shield" : pool;
            uint instanceVirtual = Stat.Of($"virtual_maximum_{pool}_leech_amount_per_leech_permyriad_max_{maximumName}");
            uint rateVirtual = Stat.Of($"virtual_maximum_{pool}_leech_rate_permyriad_per_minute");
            decimal instance = Math.Max(0, Input(Stat.Of($"maximum_{pool}_leech_amount_per_leech_%_max_{maximumName}"), BaseInstancePercent))
                * Math.Max(0, 100 + Input(Stat.Of($"maximum_{pool}_leech_amount_per_leech_+%")));
            decimal rate = Math.Max(0, Input(Stat.Of($"maximum_{pool}_leech_rate_%_per_minute"),
                    pool == "energy_shield" ? BaseEnergyShieldRatePercentPerMinute : BaseLifeManaRatePercentPerMinute))
                * Math.Max(0, 100 + Input(Stat.Of($"maximum_{pool}_leech_rate_+%")) + Input(Stat.MaximumLeechRatePlusPct)
                    + Input(Stat.MapPlayerMaximumLeechRatePlusPct)
                    // The Elder ring's "#% increased Maximum total Life Recovery per second from Leech" is a twelfth.
                    + (pool == "life" ? decimal.Truncate(Input(Stat.MaximumLifeLeechRatePlus1PctPer12StatValue) / 12) : 0));
            if (pool == "energy_shield" && Flag(Stat.MaximumEnergyShieldLeechAmountPerLeechIsDoubled)) instance *= 2;
            if (pool == "life" && Flag(Stat.MaximumLifeLeechRatePctPerMinuteIsDoubled)) rate *= 2;
            if (pool == "life" && Flag(Stat.KeystoneEternalYouth)) rate *= .5m;
            if (pool == "energy_shield" && Flag(Stat.KeystoneGhostReaver)) rate *= 2;
            instance = Math.Max(0, replacement?.Invoke(instanceVirtual) ?? instance);
            rate = Math.Max(0, replacement?.Invoke(rateVirtual) ?? rate);
            if (blocked) (instance, rate) = (0, 0);
            resources.Add(new(pool, maximum, blocked, instance, rate));
        }
        return new(all, resources);
    }

    static int Whole(decimal value) => (int)Math.Clamp(decimal.Truncate(value), 0, int.MaxValue);

    // Compare before multiplication: valid uint pools and extreme signed stat inputs must never overflow decimal.
    static int OfPool(uint maximum, decimal permyriad) => maximum == 0 || permyriad <= 0 ? 0
        : permyriad >= (decimal)int.MaxValue * 10_000 / maximum ? int.MaxValue : Whole(maximum * permyriad / 10_000);
}
