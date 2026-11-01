using GameServer.Core.World;

/// <summary>Qualified support multipliers, with explicit owner conditions for Energy Leech.
/// Other conditional/trigger/minion mechanics require their own execution paths.</summary>
static class GemSupportDamage
{
    static readonly HashSet<string> Unconditional =
    [
        "support_melee_physical_damage_+%_final", "support_weapon_elemental_damage_+%_final",
        "delve_mod_attack_damage_+%_final", "delve_mod_spell_damage_+%_final",
        "support_area_concentrate_area_damage_+%_final", "support_multiple_projectile_damage_+%_final",
        "support_lesser_multiple_projectile_damage_+%_final", "support_chain_hit_damage_+%_final",
        "support_split_projectile_damage_+%_final", "support_fork_projectile_damage_+%_final",
        "support_multiple_attack_damage_+%_final", "support_projectile_attack_physical_damage_+%_final",
        "support_echo_damage_+%_final", "support_slower_projectiles_damage_+%_final",
        "support_elemental_proliferation_damage_+%_final", "support_controlled_destruction_spell_damage_+%_final",
        "support_void_manipulation_chaos_damage_+%_final", "support_reduced_duration_damage_+%_final",
        "support_gem_elemental_damage_+%_final", "support_pierce_projectile_damage_+%_final",
        "support_attack_skills_elemental_damage_+%_final", "support_chance_to_ignite_fire_damage_+%_final",
        "support_efficacy_spell_damage_+%_final", "support_maim_chance_physical_damage_+%_final",
        "support_brutality_physical_damage_+%_final", "support_parallel_projectiles_damage_+%_final",
        "support_spell_cascade_damage_+%_final", "support_greater_volley_projectile_damage_+%_final",
        "support_pulverise_melee_area_damage_+%_final", "support_fortify_melee_damage_+%_final",
        "support_barrage_damage_+%_final", "support_cruelty_hit_damage_+%_final",
        "support_vicious_projectiles_physical_damage_+%_final", "support_vicious_projectiles_chaos_damage_+%_final"
    ];

    /// <summary>Each matching support modifier applies once to the portion's complete visited-type set.
    /// Retain the existing two product groups for floating projection, while qualifying both against full history.</summary>
    public static (double GeneralOrPhysical, double ElementalOrChaos) Multipliers(DamageTypeHistory history,
        IEnumerable<(string Name, int Value)> stats,
        IReadOnlySet<int> types, bool fullEnergyShield = false, bool leechingEnergyShield = false)
    {
        double general = 1, typed = 1;
        foreach (var (name, value) in stats)
            if ((Unconditional.Contains(name)
                || fullEnergyShield && name == "support_energy_shield_leech_damage_+%_on_full_energy_shield_final"
                || leechingEnergyShield && name == "support_energy_shield_leech_damage_+%_while_leeching_energy_shield_final")
                && Applies(name, history, types))
            {
                double factor = Math.Max(0, 1 + value / 100.0);
                if (name.Contains("elemental", StringComparison.Ordinal) || name.Contains("_chaos_", StringComparison.Ordinal)
                    || name.Contains("_fire_", StringComparison.Ordinal) || name.Contains("_cold_", StringComparison.Ordinal)
                    || name.Contains("_lightning_", StringComparison.Ordinal)) typed *= factor;
                else general *= factor;
            }
        return (general, typed);
    }

    public static bool Prohibited(DamageType finalType, IEnumerable<(string Name, int Value)> stats) =>
        stats.Any(s => s.Value != 0 && (s.Name == "base_deal_no_damage"
            || DamageTypes.IsElemental(finalType) && s.Name == "deal_no_elemental_damage"
            || finalType == DamageType.Chaos && s.Name == "base_deal_no_chaos_damage"));

    static bool Applies(string name, DamageTypeHistory history, IReadOnlySet<int> types)
    {
        if (name.Contains("elemental", StringComparison.Ordinal) && (history & DamageTypeHistory.Elemental) == 0) return false;
        if (name.Contains("physical", StringComparison.Ordinal) && (history & DamageTypeHistory.Physical) == 0) return false;
        if (name.Contains("chaos", StringComparison.Ordinal) && (history & DamageTypeHistory.Chaos) == 0) return false;
        if (name.Contains("_fire_", StringComparison.Ordinal) && (history & DamageTypeHistory.Fire) == 0) return false;
        if (name.Contains("_cold_", StringComparison.Ordinal) && (history & DamageTypeHistory.Cold) == 0) return false;
        if (name.Contains("_lightning_", StringComparison.Ordinal) && (history & DamageTypeHistory.Lightning) == 0) return false;
        if (name.Contains("melee", StringComparison.Ordinal) && !types.Contains(19)) return false;
        if (name.Contains("spell", StringComparison.Ordinal) && !types.Contains(1)) return false;
        if (name.Contains("projectile", StringComparison.Ordinal) && !types.Contains(2)) return false;
        if (name.Contains("area", StringComparison.Ordinal) && !types.Contains(7)) return false;
        if (name.Contains("attack", StringComparison.Ordinal) && !types.Contains(0)) return false;
        return true;
    }

}
