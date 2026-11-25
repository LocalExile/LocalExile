using GameServer.Core.World;


/// <summary>Current (3.25+) Rage inputs and derived native stats, independent of any class selection.</summary>
public static class RageStats
{
    public const uint PhysicalAddedAsFirePerRage = Stat.PhysicalDamagePctToAddAsFirePerRage,
        BleedDotMultiplierPerRagePerEquippedAxe = Stat.BleedDotMultiplierPlusPerRageIfEquippedAxe,
        FireDotMultiplierPerRage = Stat.FireDotMultiplierPlusPerRage;

    public static RageState.Inputs Inputs(Func<uint, int> get) => new(
        MaximumBonus: get(Stat.MaximumRage), HalfMaximum: get(Stat.MaximumRageIsHalved) != 0,
        EffectPercent: get(Stat.RageEffectPlusPct), LossDelayMilliseconds: get(Stat.RageLossDelayMs),
        LossDelayRecoveryPercent: get(Stat.RageLossDelayRecoveryRatePlusPct),
        NoInherentLoss: get(Stat.NoInherentRageLoss) != 0 || get(Stat.ShouldNotLoseRage) != 0,
        CannotGain: get(Stat.CannotGainRage) != 0,
        CannotGainDuringSoulGainPrevention: get(Stat.CannotGainRageDuringSoulGainPrevention) != 0,
        MaximumOnHitGainChance: get(Stat.GainMaxRageOnRageGainFromHitPctChance),
        GainOnHit: get(Stat.GainXRageOnHit), GainOnAttackHit: get(Stat.GainXRageOnAttackHit),
        GainOnMeleeHit: get(Stat.GainXRageOnMeleeHit), GainOnAttackCritical: get(Stat.GainXRageOnAttackCrit),
        GainOnMeleeCritical: get(Stat.GainXRageOnMeleeCrit), GainWhenHit: get(Stat.GainXRageWhenHit),
        LossRatePercent: get(Stat.RageDecaySpeedPlusPct), RegenerationPerMinute: get(Stat.BaseRageRegenerationPerMinute));

    public static RageState.Inputs Inputs(Player player, Func<uint, int> get)
    {
        var inputs = Inputs(get);
        int divisor = get(Stat.Regenerate1RagePerXManaRegeneration);
        if (divisor <= 0 || get(Stat.MapPlayersNoRegenerationIncludingEs) != 0 || get(Stat.CannotLeechOrRegenerateMana) != 0)
            return inputs;
        return inputs with
        {
            NonDelayingRegenerationPerMinute = Math.Max(0, PanelStats.GrossManaRegenerationPerMinute(get, player.Pools.Mana) / divisor),
        };
    }

    /// <summary>
    /// Expands the verified item coefficients once from the authoritative effective Rage into the exact stats their
    /// existing consumers read. The axe-qualified source is already removed by StatConditions when no active axe is
    /// equipped; it is not multiplied by the number of axes.
    /// </summary>
    public static void ExpandItems(StatMap stats, int effectiveRage)
    {
        if (effectiveRage <= 0) return;
        Add(PhysicalAddedAsFirePerRage, Stat.PhysicalDamagePctToAddAsFire);
        Add(BleedDotMultiplierPerRagePerEquippedAxe, Stat.BleedingDotMultiplierPlus);
        Add(FireDotMultiplierPerRage, Stat.FireDotMultiplierPlus);
        return;

        void Add(uint coefficient, uint target)
        {
            int perRage = Game.RageStat(stats, coefficient);
            if (perRage == 0 || !(target is var key)) return;
            long contribution = (long)perRage * effectiveRage;
            stats[key] = (int)Math.Clamp((long)stats.GetValueOrDefault(key) + contribution, int.MinValue, int.MaxValue);
        }
    }

    // Preserve fractional ranges, but multiply before dividing: 100 * (1 + 15 / 100.0) can fall below 115
    // and the native panel then floors it to 114. Promote before adding, including saturated effective Rage.
    public static DamagePacket ApplyAttack(DamagePacket hit, Func<uint, int> get)
    {
        int percent = get(Stat.AttackDamagePlusPctFinalFromRage);
        if (percent == 0) return hit;
        double numerator = Math.Max(0, 100.0 + percent);
        if (numerator == 0) return DamagePacket.Empty;
        return hit.Select((_, range) => new(range.Min * numerator / 100.0, range.Max * numerator / 100.0));
    }

    public static IEnumerable<(uint Name, int Value)> Display(RageState.Value value, RageState.Inputs inputs)
    {
        yield return (Stat.CurrentRage, value.Current);
        yield return (Stat.VirtualMaximumRage, value.Maximum);
        yield return (Stat.EffectiveRageForPerRageEffects, value.Effective);
        yield return (Stat.AttackDamagePlusPctFinalFromRage, value.Effective);
        yield return (Stat.IsAtMaximumRage, value.Maximum > 0 && value.Current == value.Maximum ? 1 : 0);
        yield return (Stat.VirtualRageLossDelayMs, (int)Math.Min(int.MaxValue, inputs.LossDelaySeconds * 1000));
    }
}
