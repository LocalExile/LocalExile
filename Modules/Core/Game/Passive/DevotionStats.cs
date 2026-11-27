

/// <summary>
/// Converts the per-10-Devotion stats on Militant Faith (and any other item with such a stat) into the normal stats they
/// modify. The timeless tree already grants <c>base_devotion</c> from the allocated nodes in the jewel's radius; a stat
/// named <c>..._per_10_devotion</c> means "value per whole 10 Devotion", so 145 Devotion gives 14 steps (the same floor
/// as Path of Building's PerStat modifier).
///
/// The target is the client's normal stat for the same effect. All elemental resistance uses
/// <c>base_resist_all_elements_%</c>, which the resistance pool adds to fire, cold and lightning (not chaos). A target
/// that no other server code reads is still mapped here so the source is complete; the generated support list marks it
/// inert until a consumer exists.
/// </summary>
public static class DevotionStats
{
    public const uint Devotion = Stat.BaseDevotion;

    sealed record Target(uint Stat, int Factor = 1);

    static readonly IReadOnlyDictionary<uint, Target[]> Targets = new Dictionary<uint, Target[]>
    {
        [Stat.TotemDamagePlusPctPer10Devotion] = [new(Stat.TotemDamagePlusPct)],
        [Stat.SigilDamagePlusPctPer10Devotion] = [new(Stat.SigilDamagePlusPct)],
        [Stat.ChannelledSkillDamagePlusPctPer10Devotion] = [new(Stat.ChannelledSkillDamagePlusPct)],
        [Stat.AreaDamagePlusPctPer10Devotion] = [new(Stat.AreaDamagePlusPct)],
        [Stat.ElementalDamagePlusPctPer10Devotion] = [new(Stat.ElementalDamagePlusPct)],
        [Stat.ElementalResistancePctPer10Devotion] = [new(Stat.BaseResistAllElementsPct)],
        [Stat.NonDamagingAilmentEffectPlusPctPer10Devotion] = [new(Stat.NonDamagingAilmentEffectPlusPct)],
        [Stat.SelfElementalStatusDurationMinusPctPer10Devotion] = [new(Stat.SelfElementalStatusDurationMinusPct)],
        [Stat.SelfCurseDurationPlusPctPer10Devotion] = [new(Stat.SelfCurseDurationPlusPct)],
        [Stat.MinionAttackAndCastSpeedPlusPctPer10Devotion] = [new(Stat.MinionAttackAndCastSpeedPlusPct)],
        [Stat.MinionAccuracyRatingPer10Devotion] = [new(Stat.MinionAccuracyRating)],
        [Stat.ManaRegenerationRatePerMinutePer10Devotion] = [new(Stat.BaseManaRegenerationRatePerMinute)],
        [Stat.ManaCostPlusPctPer10Devotion] = [new(Stat.ManaCostPlusPct)],
        [Stat.BaseManaCostEfficiencyPlusPctPer10Devotion] = [new(Stat.BaseManaCostEfficiencyPlusPct)],
        [Stat.NonCurseAuraEffectPlusPctPer10Devotion] = [new(Stat.NonCurseAuraEffectPlusPct)],
        [Stat.ShieldDefencesPlusPctPer10Devotion] = [new(Stat.CombinedShieldDefencesPlusPct)],
    };

    static readonly (uint Input, uint Output)[] ThresholdTargets =
    [
        (Stat.PhysicalDamagePctToConvertToFireAtDevotionThreshold, Stat.PhysicalDamagePctToConvertToFire),
        (Stat.PhysicalDamagePctToConvertToColdAtDevotionThreshold, Stat.PhysicalDamagePctToConvertToCold),
        (Stat.PhysicalDamagePctToConvertToLightningAtDevotionThreshold, Stat.PhysicalDamagePctToConvertToLightning),
        (Stat.ManaPctToAddAsEnergyShieldAtDevotionThreshold, Stat.ManaPctToAddAsEnergyShield),
        (Stat.MinimumEnduranceChargesAtDevotionThreshold, Stat.MinimumEnduranceCharges),
        (Stat.MinimumFrenzyChargesAtDevotionThreshold, Stat.MinimumFrenzyCharges),
        (Stat.MinimumPowerChargesAtDevotionThreshold, Stat.MinimumPowerCharges),
        (Stat.PhysicalDamageReductionPctAtDevotionThreshold, Stat.AdditionalPhysicalDamageReductionPct),
        (Stat.AdditionalMaximumAllResistancesPctAtDevotionThreshold, Stat.AdditionalMaximumAllResistancesPct),
        (Stat.InflictFireExposureOnHitPctChanceAtDevotionThreshold, Stat.BaseInflictFireExposureOnHitPctChance),
        (Stat.InflictColdExposureOnHitPctChanceAtDevotionThreshold, Stat.BaseInflictColdExposureOnHitPctChance),
        (Stat.InflictLightningExposureOnHitPctChanceAtDevotionThreshold, Stat.BaseInflictLightningExposureOnHitPctChance),
    ];

    static readonly uint[] ElementalAilmentImmunity =
    [
        Stat.ImmuneToIgnite, Stat.ImmuneToChill, Stat.ImmuneToFreeze, Stat.ImmuneToShock,
        Stat.ImmuneToScorch, Stat.ImmuneToSap, Stat.CannotGainBrittle,
    ];

    /// <summary>Replaces every per-10-Devotion stat in <paramref name="sources"/> with its normal stat(s) times the
    /// number of whole 10 Devotion steps. The raw per-Devotion key is removed even at zero Devotion, so it cannot leak
    /// into a panel as a flat stat.</summary>
    public static void Apply(Dictionary<uint, int> sources, StatCatalog stats) => Apply(sources, stats, null);

    public static void Apply(Dictionary<uint, int> sources, StatCatalog stats,
        IReadOnlyDictionary<string, bool>? conditions)
    {
        int devotion = sources.GetValueOrDefault(Devotion);
        int steps = Math.Max(0, devotion) / 10;
        foreach (var (perStat, targets) in Targets)
        {
            if (!sources.Remove(perStat, out int value)) continue;
            if (steps == 0) continue;
            foreach (var target in targets)
                if (target.Stat is var targetKey)
                    sources[targetKey] = ActorScale.Add(targetKey, sources.GetValueOrDefault(targetKey),
                        value * steps * target.Factor);
        }

        bool reachedThreshold = devotion >= 150;
        void Add(uint key, int amount) =>
            sources[key] = ActorScale.Add(key, sources.GetValueOrDefault(key), amount);
        foreach (var (input, output) in ThresholdTargets)
            if (sources.Remove(input, out int value) && reachedThreshold)
                Add(output, value);

        if (sources.Remove(Stat.GainArcaneSurgeOnHitAtDevotionThreshold, out int surge) && reachedThreshold && surge > 0)
            Add(Stat.GainArcaneSurgeOnHitPctChance, 100);
        if (sources.Remove(Stat.ImmuneToElementalAilmentsWhileOnConsecratedGroundAtDevotionThreshold, out int immune)
            && reachedThreshold && immune > 0 && conditions?.GetValueOrDefault("_while_on_consecrated_ground") == true)
            foreach (uint key in ElementalAilmentImmunity)
                Add(key, 1);
    }
}
