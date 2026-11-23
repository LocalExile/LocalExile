using GameServer.Core.World;

/// <summary>Qualified support multipliers, with explicit owner conditions for Energy Leech.
/// Other conditional/trigger/minion mechanics require their own execution paths.</summary>
public static class GemSupportDamage
{
    static readonly HashSet<uint> Unconditional =
    [
        Stat.SupportMeleePhysicalDamagePlusPctFinal, Stat.SupportWeaponElementalDamagePlusPctFinal,
        Stat.DelveModAttackDamagePlusPctFinal, Stat.DelveModSpellDamagePlusPctFinal,
        Stat.SupportAreaConcentrateAreaDamagePlusPctFinal, Stat.SupportMultipleProjectileDamagePlusPctFinal,
        Stat.SupportLesserMultipleProjectileDamagePlusPctFinal, Stat.SupportChainHitDamagePlusPctFinal,
        Stat.SupportSplitProjectileDamagePlusPctFinal, Stat.SupportForkProjectileDamagePlusPctFinal,
        Stat.SupportMultipleAttackDamagePlusPctFinal, Stat.SupportProjectileAttackPhysicalDamagePlusPctFinal,
        Stat.SupportEchoDamagePlusPctFinal, Stat.SupportSlowerProjectilesDamagePlusPctFinal,
        Stat.SupportElementalProliferationDamagePlusPctFinal, Stat.SupportControlledDestructionSpellDamagePlusPctFinal,
        Stat.SupportVoidManipulationChaosDamagePlusPctFinal, Stat.SupportReducedDurationDamagePlusPctFinal,
        Stat.SupportGemElementalDamagePlusPctFinal, Stat.SupportPierceProjectileDamagePlusPctFinal,
        Stat.SupportBetterAilmentsHitDamagePlusPctFinal, Stat.SupportSpiritStrikeDamagePlusPctFinal,
        Stat.SupportStormBarrierDamagePlusPctFinal, Stat.SupportPureShockDamagePlusPctFinal,
        Stat.SupportBonechillColdDamagePlusPctFinal, Stat.SupportBloodThirstDamagePlusPctFinal,
        Stat.SupportAttackSkillsElementalDamagePlusPctFinal, Stat.SupportChanceToIgniteFireDamagePlusPctFinal,
        Stat.SupportEfficacySpellDamagePlusPctFinal, Stat.SupportMaimChancePhysicalDamagePlusPctFinal,
        Stat.SupportBrutalityPhysicalDamagePlusPctFinal, Stat.SupportParallelProjectilesDamagePlusPctFinal,
        Stat.SupportSpellCascadeDamagePlusPctFinal, Stat.SupportGreaterVolleyProjectileDamagePlusPctFinal,
        Stat.SupportPulveriseMeleeAreaDamagePlusPctFinal, Stat.SupportFortifyMeleeDamagePlusPctFinal,
        Stat.SupportBarrageDamagePlusPctFinal, Stat.SupportCrueltyHitDamagePlusPctFinal,
        Stat.SupportViciousProjectilesPhysicalDamagePlusPctFinal, Stat.SupportViciousProjectilesChaosDamagePlusPctFinal,
        Stat.SupportCastWhileChannellingTriggeredSkillDamagePlusPctFinal, Stat.SupportCastOnCritSpellDamagePlusPctFinal, Stat.CastOnDamageTakenDamagePlusPctFinal, Stat.IncAoePlusMoreAreaDamagePlusPctFinal, Stat.DoedreAuraDamagePlusPctFinal, Stat.BloodSandStanceMeleeSkillsAreaDamagePlusPctFinal,
        Stat.SupportCastOnMeleeKillSpellDamagePlusPctFinal,
        // The trap, mine and totem supports' own more/less damage: Trap, Trap and Mine Damage, Cluster Traps,
        // Multiple Traps, Blastchain Mine, High-Impact Mine, Locus Mine, Multiple Totems and Focused Ballista.
        Stat.SupportTrapDamagePlusPctFinal, Stat.SupportTrapAndMineDamagePlusPctFinal,
        Stat.SupportClustertrapDamagePlusPctFinal, Stat.SupportMultithrowDamagePlusPctFinal,
        Stat.SupportGemMineDamagePlusPctFinal, Stat.SupportRemoteMine2DamagePlusPctFinal,
        Stat.SupportLocusMineDamagePlusPctFinal, Stat.SupportAdditionalTotemDamagePlusPctFinal,
        Stat.SupportFocusedBallistaTotemDamagePlusPctFinal, Stat.SupportBrandDamagePlusPctFinal
    ];

    /// <summary>Each matching support modifier applies once to the portion's complete visited-type set.
    /// Retain the existing two product groups for floating projection, while qualifying both against full history.</summary>
    public static (double GeneralOrPhysical, double ElementalOrChaos) Multipliers(DamageTypeHistory history,
        IEnumerable<(uint Id, int Value)> stats,
        IReadOnlySet<int> types, bool fullEnergyShield = false, bool leechingEnergyShield = false)
    {
        double general = 1, typed = 1;
        foreach (var (id, value) in stats)
            if ((Unconditional.Contains(id)
                || fullEnergyShield && id == Stat.SupportEnergyShieldLeechDamagePlusPctOnFullEnergyShieldFinal
                || leechingEnergyShield && id == Stat.SupportEnergyShieldLeechDamagePlusPctWhileLeechingEnergyShieldFinal)
                && Program.Stats.Name(id) is var name && Applies(name, history, types))
            {
                double factor = Math.Max(0, 1 + value / 100.0);
                if (name.Contains("elemental", StringComparison.Ordinal) || name.Contains("_chaos_", StringComparison.Ordinal)
                    || name.Contains("_fire_", StringComparison.Ordinal) || name.Contains("_cold_", StringComparison.Ordinal)
                    || name.Contains("_lightning_", StringComparison.Ordinal)) typed *= factor;
                else general *= factor;
            }
        return (general, typed);
    }

    public static bool Prohibited(DamageType finalType, IEnumerable<(uint Id, int Value)> stats) =>
        DamageStats.Prohibited(finalType, id => stats.Any(s => s.Value != 0 && s.Id == id) ? 1 : 0);

    static bool Applies(string name, DamageTypeHistory history, IReadOnlySet<int> types)
    {
        if (name.Contains("elemental", StringComparison.Ordinal) && (history & DamageTypeHistory.Elemental) == 0) return false;
        if (name.Contains("physical", StringComparison.Ordinal) && (history & DamageTypeHistory.Physical) == 0) return false;
        if (name.Contains("chaos", StringComparison.Ordinal) && (history & DamageTypeHistory.Chaos) == 0) return false;
        if (name.Contains("_fire_", StringComparison.Ordinal) && (history & DamageTypeHistory.Fire) == 0) return false;
        if (name.Contains("_cold_", StringComparison.Ordinal) && (history & DamageTypeHistory.Cold) == 0) return false;
        if (name.Contains("_lightning_", StringComparison.Ordinal) && (history & DamageTypeHistory.Lightning) == 0) return false;
        // Cast on Melee Kill's more damage is the triggered spell's: "melee" names its trigger, not the hit.
        if (name.Contains("melee", StringComparison.Ordinal) && name != "support_cast_on_melee_kill_spell_damage_+%_final"
            && !types.Contains(19)) return false;
        if (name.Contains("spell", StringComparison.Ordinal) && !types.Contains(1)) return false;
        if (name.Contains("projectile", StringComparison.Ordinal) && !types.Contains(2)) return false;
        if (name.Contains("area", StringComparison.Ordinal) && !types.Contains(7)) return false;
        if (name.Contains("attack", StringComparison.Ordinal) && !types.Contains(0)) return false;
        return true;
    }

}
