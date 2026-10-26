using GameServer.Core.World;

/// <summary>Unconditional support multipliers. Conditional/trigger/minion mechanics require their own execution paths.</summary>
static class GemSupportDamage
{
    static readonly HashSet<string> Unconditional =
    [
        "support_melee_physical_damage_+%_final", "support_weapon_elemental_damage_+%_final",
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

    public static DamagePacket Scale(DamagePacket hit, IEnumerable<(string Name, int Value)> stats,
        IReadOnlySet<int> types, bool afterConversion)
    {
        var modifiers = stats.ToList();
        return hit.Select((type, range) =>
        {
            double factor = 1;
            foreach (var (name, value) in modifiers)
            {
                if (!Unconditional.Contains(name)) continue;
                bool elemental = name.Contains("elemental", StringComparison.Ordinal);
                bool destinationType = elemental || name.Contains("_chaos_", StringComparison.Ordinal)
                    || name.Contains("_fire_", StringComparison.Ordinal) || name.Contains("_cold_", StringComparison.Ordinal)
                    || name.Contains("_lightning_", StringComparison.Ordinal);
                if (destinationType != afterConversion) continue;
                if (elemental && !DamageTypes.IsElemental(type)) continue;
                if (name.Contains("physical", StringComparison.Ordinal) && type != DamageType.Physical) continue;
                if (name.Contains("chaos", StringComparison.Ordinal) && type != DamageType.Chaos) continue;
                if (name.Contains("_fire_", StringComparison.Ordinal) && type != DamageType.Fire) continue;
                if (name.Contains("_cold_", StringComparison.Ordinal) && type != DamageType.Cold) continue;
                if (name.Contains("_lightning_", StringComparison.Ordinal) && type != DamageType.Lightning) continue;
                if (name.Contains("melee", StringComparison.Ordinal) && !types.Contains(19)) continue;
                if (name.Contains("spell", StringComparison.Ordinal) && !types.Contains(1)) continue;
                if (name.Contains("projectile", StringComparison.Ordinal) && !types.Contains(2)) continue;
                if (name.Contains("area", StringComparison.Ordinal) && !types.Contains(7)) continue;
                if (name.Contains("attack", StringComparison.Ordinal) && !types.Contains(0)) continue;
                factor *= Math.Max(0, 1 + value / 100.0);
            }
            if (afterConversion && (modifiers.Any(s => s.Name == "base_deal_no_damage" && s.Value != 0)
                || DamageTypes.IsElemental(type) && modifiers.Any(s => s.Name == "deal_no_elemental_damage" && s.Value != 0)
                || type == DamageType.Chaos && modifiers.Any(s => s.Name == "base_deal_no_chaos_damage" && s.Value != 0))) factor = 0;
            return range.Scale(factor);
        });
    }
}
