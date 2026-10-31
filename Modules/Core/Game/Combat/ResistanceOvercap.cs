

/// <summary>
/// The generic consumers of the "increased by overcapped resistance" flags. Path of Building commit 16de4b8:
/// CalcDefence.lua computes <c>ResistOverCap = m_max(0, total - max)</c> per resistance (the uncapped value above
/// its maximum, whose absolute cap is 90) and, while the flag is set, adds it once as an increased modifier —
/// <c>EvasionRatingIncreasedByOvercappedColdRes</c> to Evasion (INC), CalcOffence.lua lines 745-748 add
/// <c>LightningResistOverCap</c> to CritChance (INC, attacks and spells alike). The flags are ordinary stats here
/// (Stats.dat rows 5319/5320, keys 5320/5321): any source that sets them — a passive node such as the Surfcaster's
/// Sea Legs (graph 61696, with its +40% cold_and_lightning_damage_resistance_%), an item mod or /set_stat — turns
/// the consumer on; /set_stat 0 turns it off again. This class is the derived source both the pools and the spell
/// consumers read, so nothing counts the modifier twice.
/// </summary>
static class ResistanceOvercap
{
    /// <summary>evasion_rating_increased_by_overcapped_cold_resistance: +1% increased evasion per 1% cold overcap.</summary>
    public const string ColdEvasionFlag = "evasion_rating_increased_by_overcapped_cold_resistance";

    /// <summary>critical_strike_chance_increased_by_overcapped_lightning_resistance: +1% increased critical strike
    /// chance (generic: attacks and spells) per 1% lightning overcap.</summary>
    public const string LightningCritFlag = "critical_strike_chance_increased_by_overcapped_lightning_resistance";

    /// <summary>cold_and_lightning_damage_resistance_%: the pair-resistance input stat Sea Legs also grants.</summary>
    public const string ColdAndLightningResistance = "cold_and_lightning_damage_resistance_%";

    /// <summary>The increased values the flags grant while set, additive with the other increased modifiers (not a
    /// final multiplier): 1% per 1% of overcap. A missing flag leaves its value at 0.</summary>
    public readonly record struct Derived(int EvasionPct, int CriticalStrikeChancePct)
    {
        public static readonly Derived None = new();
    }

    /// <summary>The two flags' derived increases for these raw inputs (uncapped resistance after the campaign
    /// penalty, per-type maximum); <see cref="GameServer.Core.World.DamageModel.Overcap"/> caps the maximum at 90.</summary>
    public static Derived Increases(int coldUncapped, int coldMaximum, bool evasionFlag,
        int lightningUncapped, int lightningMaximum, bool criticalFlag) => new(
        EvasionPct: evasionFlag ? GameServer.Core.World.DamageModel.Overcap(coldUncapped, coldMaximum) : 0,
        CriticalStrikeChancePct: criticalFlag ? GameServer.Core.World.DamageModel.Overcap(lightningUncapped, lightningMaximum) : 0);

    /// <summary>
    /// The one derived source for a pool build: re-derives the raw cold and lightning resistances and maxima from the
    /// aggregated bonus (passives, items and auras) with an explicit dev value replacing its own stat's contribution —
    /// /set_stat sets a stat, it does not add to the aggregate — and folds the flags' derived increases in.
    /// Sources must include the aura's raw resistance/max keys already folded into bonus, so a replacement subtracts
    /// the complete contribution. PoolsFor passes the Kitava penalty; CharacterStats.For stores the increases in Pools.</summary>
    public static CharacterStats.Bonus Applied(CharacterStats.Bonus bonus, IReadOnlyDictionary<uint, int> sources,
        IReadOnlyDictionary<uint, int> devStats, int penalty, StatCatalog stats)
    {
        int StatOf(string name) => stats.Key(name) is uint key && sources.TryGetValue(key, out int value) ? value : 0;
        int Effective(string name) => stats.Key(name) is uint key && devStats.TryGetValue(key, out int dev) ? dev : StatOf(name);
        int Replacement(string name) => Effective(name) - StatOf(name);
        int commonResistance = Replacement("base_resist_all_elements_%") + Replacement(ColdAndLightningResistance);
        int coldRaw = bonus.ColdResistance + commonResistance + Replacement("base_cold_damage_resistance_%");
        int lightningRaw = bonus.LightningResistance + commonResistance + Replacement("base_lightning_damage_resistance_%");
        int commonMaximum = Replacement("additional_maximum_all_resistances_%")
            + Replacement("additional_maximum_all_elemental_resistances_%");
        int coldMax = Math.Min(GameServer.Core.World.DamageModel.MaxResistCap, GameServer.Core.World.DamageModel.DefaultMaxResistance
            + bonus.ColdMaxResistance + commonMaximum + Replacement("base_maximum_cold_damage_resistance_%"));
        int lightningMax = Math.Min(GameServer.Core.World.DamageModel.MaxResistCap, GameServer.Core.World.DamageModel.DefaultMaxResistance
            + bonus.LightningMaxResistance + commonMaximum + Replacement("base_maximum_lightning_damage_resistance_%"));
        var derived = Increases(coldRaw + penalty, coldMax, Enabled(stats, sources, devStats, ColdEvasionFlag),
            lightningRaw + penalty, lightningMax, Enabled(stats, sources, devStats, LightningCritFlag));
        return bonus with
        {
            ColdResistance = coldRaw - CharacterStats.BaseResistance,
            LightningResistance = lightningRaw - CharacterStats.BaseResistance,
            ColdMaxResistance = coldMax - GameServer.Core.World.DamageModel.DefaultMaxResistance,
            LightningMaxResistance = lightningMax - GameServer.Core.World.DamageModel.DefaultMaxResistance,
            ColdOvercapEvasionPct = derived.EvasionPct, LightningOvercapCritPct = derived.CriticalStrikeChancePct
        };
    }

    public static bool IsInput(string name) => name is ColdEvasionFlag or LightningCritFlag or ColdAndLightningResistance
        or "base_cold_damage_resistance_%" or "base_lightning_damage_resistance_%" or "base_resist_all_elements_%"
        or "base_maximum_cold_damage_resistance_%" or "base_maximum_lightning_damage_resistance_%"
        or "additional_maximum_all_resistances_%" or "additional_maximum_all_elemental_resistances_%";

    /// <summary>Whether a flag stat is set: an explicit dev value (including 0, which turns the consumer off) wins
    /// over the passive and item sources, as the explicit value wins for every stat the server sends.</summary>
    public static bool Enabled(StatCatalog stats, IReadOnlyDictionary<uint, int> sources,
        IReadOnlyDictionary<uint, int> devStats, string flag)
    {
        if (stats.Key(flag) is not uint key) return false;
        if (devStats.TryGetValue(key, out int dev)) return dev != 0;
        return sources.TryGetValue(key, out int value) && value != 0;
    }
}
