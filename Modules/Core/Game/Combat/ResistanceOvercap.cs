

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
public static class ResistanceOvercap
{
    /// <summary>evasion_rating_increased_by_overcapped_cold_resistance: +1% increased evasion per 1% cold overcap.</summary>
    public const uint ColdEvasionFlag = Stat.EvasionRatingIncreasedByOvercappedColdResistance;

    /// <summary>critical_strike_chance_increased_by_overcapped_lightning_resistance: +1% increased critical strike
    /// chance (generic: attacks and spells) per 1% lightning overcap.</summary>
    public const uint LightningCritFlag = Stat.CriticalStrikeChanceIncreasedByOvercappedLightningResistance;

    /// <summary>cold_and_lightning_damage_resistance_%: the pair-resistance input stat Sea Legs also grants.</summary>
    public const uint ColdAndLightningResistance = Stat.ColdAndLightningDamageResistancePct;

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
    /// The one derived source for a pool build: the raw cold and lightning resistances and maxima from the aggregated
    /// bonus (passives, items and auras), with the flags' derived increases folded in. PoolsFor passes the Kitava
    /// penalty; CharacterStats.For stores the increases in Pools.</summary>
    public static CharacterStats.Bonus Applied(CharacterStats.Bonus bonus, IReadOnlyDictionary<uint, int> sources,
        int penalty, StatCatalog stats)
    {
        int StatOf(uint name) => name is var key && sources.TryGetValue(key, out int value) ? value : 0;
        int coldRaw = bonus.ColdResistance;
        int lightningRaw = bonus.LightningResistance;
        int fireMax = Math.Min(GameServer.Core.World.DamageModel.MaxResistCap, GameServer.Core.World.DamageModel.DefaultMaxResistance
            + bonus.FireMaxResistance);
        int coldMax = Math.Min(GameServer.Core.World.DamageModel.MaxResistCap, GameServer.Core.World.DamageModel.DefaultMaxResistance
            + bonus.ColdMaxResistance);
        int lightningMax = Math.Min(GameServer.Core.World.DamageModel.MaxResistCap, GameServer.Core.World.DamageModel.DefaultMaxResistance
            + bonus.LightningMaxResistance);
        if (StatOf(Stat.ElementalResistancesAreLimitedByHighestMaximumElementalResistance) != 0)
            fireMax = coldMax = lightningMax = Math.Max(fireMax, Math.Max(coldMax, lightningMax));
        bool tempered = StatOf(Stat.KeystoneTemperedByWar) != 0;
        int coldForOvercap = (tempered ? coldRaw / 2 : coldRaw) + penalty;
        int lightningForOvercap = (tempered ? lightningRaw / 2 : lightningRaw) + penalty;
        var derived = Increases(coldForOvercap, coldMax, Enabled(stats, sources, ColdEvasionFlag),
            lightningForOvercap, lightningMax, Enabled(stats, sources, LightningCritFlag));
        int manaDivisor = StatOf(Stat.ManaIncreasedPerXOvercappedLightningResistanceChoirOfTheStorm);
        int manaIncrease = manaDivisor > 0
            ? GameServer.Core.World.DamageModel.Overcap(lightningForOvercap, lightningMax) / manaDivisor : 0;
        int chaosIncrease = StatOf(Stat.EnergyShieldIncreasedByChaosResistance) == 0 ? 0
            : GameServer.Core.World.DamageModel.Capped(bonus.ChaosResistance + penalty,
                GameServer.Core.World.DamageModel.DefaultMaxResistance + bonus.ChaosMaxResistance);
        return bonus with
        {
            ManaPct = ActorScale.Saturate((long)bonus.ManaPct + manaIncrease),
            EnergyShieldPct = ActorScale.Saturate((long)bonus.EnergyShieldPct + chaosIncrease),
            ColdResistance = coldRaw - CharacterStats.BaseResistance,
            LightningResistance = lightningRaw - CharacterStats.BaseResistance,
            FireMaxResistance = fireMax - GameServer.Core.World.DamageModel.DefaultMaxResistance,
            ColdMaxResistance = coldMax - GameServer.Core.World.DamageModel.DefaultMaxResistance,
            LightningMaxResistance = lightningMax - GameServer.Core.World.DamageModel.DefaultMaxResistance,
            ColdOvercapEvasionPct = derived.EvasionPct, LightningOvercapCritPct = derived.CriticalStrikeChancePct
        };
    }

    public static bool IsInput(uint name) => name is ColdEvasionFlag or LightningCritFlag or ColdAndLightningResistance
        or Stat.FireAndColdDamageResistancePct or Stat.FireAndLightningDamageResistancePct
        or Stat.BaseColdDamageResistancePct or Stat.BaseLightningDamageResistancePct or Stat.BaseResistAllElementsPct
        or Stat.ElementalResistancesAreLimitedByHighestMaximumElementalResistance or Stat.BaseMaximumFireDamageResistancePct
        or Stat.BaseMaximumColdDamageResistancePct or Stat.BaseMaximumLightningDamageResistancePct
        or Stat.AdditionalMaximumAllResistancesPct or Stat.AdditionalMaximumAllElementalResistancesPct;

    /// <summary>Whether a flag stat is set in the passive and item sources.</summary>
    public static bool Enabled(StatCatalog stats, IReadOnlyDictionary<uint, int> sources, uint flag) =>
        sources.TryGetValue(flag, out int value) && value != 0;
}
