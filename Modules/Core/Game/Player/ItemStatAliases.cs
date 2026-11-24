/// <summary>Item stats whose effect is other stats', under a second id.</summary>
public static class ItemStatAliases
{
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
    ];

    // Apply once to a fresh qualified source sum, before pools, skill snapshots and panels consume it.
    public static void Apply(Dictionary<uint, int> sources)
    {
        foreach (var (input, outputs) in Rules)
            if (sources.TryGetValue(input, out int value) && value != 0)
                foreach (uint output in outputs)
                    sources[output] = ActorScale.Saturate((long)sources.GetValueOrDefault(output) + value);
    }
}
