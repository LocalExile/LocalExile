/// <summary>Item stats whose effect is other stats', under a second id.</summary>
public static class ItemStatAliases
{
    internal static readonly uint[] RingFinalStats =
        [Stat.LocalRingNovaSpellsAreaOfEffectPlusPctFinal, Stat.LocalRingImpaledDebuffDurationPlusPctFinal];
    static readonly (uint Input, uint[] Outputs)[] Rules =
    [
        // "Adds # to # Cold Damage to Spells and Attacks": the attack and the spell added damage, not the global one,
        // which secondary damage, heralds and Madness explosions also read.
        (Stat.SpellAndAttackMinimumAddedColdDamage, [Stat.AttackMinimumAddedColdDamage, Stat.SpellMinimumAddedColdDamage]),
        (Stat.SpellAndAttackMaximumAddedColdDamage, [Stat.AttackMaximumAddedColdDamage, Stat.SpellMaximumAddedColdDamage]),
        (Stat.SpellAndAttackMinimumAddedFireDamage, [Stat.AttackMinimumAddedFireDamage, Stat.SpellMinimumAddedFireDamage]),
        (Stat.SpellAndAttackMaximumAddedFireDamage, [Stat.AttackMaximumAddedFireDamage, Stat.SpellMaximumAddedFireDamage]),
        (Stat.SpellAndAttackMinimumAddedLightningDamage,
            [Stat.AttackMinimumAddedLightningDamage, Stat.SpellMinimumAddedLightningDamage]),
        (Stat.SpellAndAttackMaximumAddedLightningDamage,
            [Stat.AttackMaximumAddedLightningDamage, Stat.SpellMaximumAddedLightningDamage]),
        (Stat.DamagePlusPctVsBlindedEnemies, [PlayerHitDamage.VsBlindedDamageStat]),
        (Stat.ImmuneToBurningShocksAndChilledGround,
            [Stat.UnaffectedByBurningGround, Stat.UnaffectedByChilledGround, Stat.UnaffectedByShockedGround]),
    ];

    static readonly (uint Input, uint Resource, uint Resistance)[] ResourceResistanceRules =
    [
        (Stat.MaximumLifePlusPctAndFireResistanceMinusPct, Stat.MaximumLifePlusPct, Stat.BaseFireDamageResistancePct),
        (Stat.MaximumManaPlusPctAndColdResistanceMinusPct, Stat.MaximumManaPlusPct, Stat.BaseColdDamageResistancePct),
        (Stat.MaximumEnergyShieldPlusPctAndLightningResistanceMinusPct, Stat.MaximumEnergyShieldPlusPct,
            Stat.BaseLightningDamageResistancePct),
    ];

    // Apply once to a fresh qualified source sum, before pools, skill snapshots and panels consume it.
    public static void Apply(Dictionary<uint, int> sources)
    {
        void Add(uint key, long amount) =>
            sources[key] = ActorScale.Saturate(sources.GetValueOrDefault(key) + amount);
        foreach (var (input, outputs) in Rules)
            if (sources.TryGetValue(input, out int value) && value != 0)
                foreach (uint output in outputs)
                    Add(output, value);
        foreach (var (input, resource, resistance) in ResourceResistanceRules)
            if (sources.TryGetValue(input, out int resourceValue) && resourceValue != 0)
            {
                Add(resource, resourceValue);
                Add(resistance, -(long)resourceValue);
            }
    }
}
