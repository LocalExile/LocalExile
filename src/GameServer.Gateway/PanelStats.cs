using GameServer.Core.World;

/// <summary>
/// The character panel's "sum" stats (issue #75): virtual stats that the server works out from the base forms that
/// passives, items and auras give, because the client derives none of them (11-skills.md §4). A passive grants
/// <c>base_stun_recovery_+%</c>, the panel's Stun Recovery row reads <c>stun_recovery_+%</c>. Each display stat is its
/// own value plus every unconditional source below: the base form, and the combined stats that apply to it too (Avoid
/// Elemental Ailments counts for each avoid row, "increased Duration of Elemental Ailments" for each duration).
/// Main hand / off hand rows also add that weapon's local stats (its own chance to bleed, penetration, life gain on hit).
/// Conditional stats (while, if, per charge, against …) are not counted. Flags (cannot be, immune to) are 1 when any
/// source is set. The pools' own stats (life, mana, armour, evasion, attributes, resistances, suppression, block,
/// movement and attack speed, crit) are not here: <see cref="CharacterStats"/> computes those, and
/// <see cref="FromPools"/> adds the rows that combine them with a stat.
/// </summary>
static class PanelStats
{
    /// <summary>A source stat, scaled: × <paramref name="Mul"/> ÷ <paramref name="Div"/> (a "-%" stat counts negative;
    /// a 1/100 000 leech counts ÷ 10 as permyriad).</summary>
    public readonly record struct Src(string Name, int Mul = 1, int Div = 1)
    {
        public static implicit operator Src(string name) => new(name);
    }

    /// <summary>A display stat and what it sums; <paramref name="Local"/> are the weapon's own stats (hand rows).</summary>
    public sealed record Sum(string Display, Src[] Sources, bool Flag = false, Src[]? Local = null, Hand Hand = Hand.None);

    public enum Hand { None, Main, Off }

    const string ElementalAilmentDuration = "base_elemental_status_ailment_duration_+%";
    const string AilmentDuration = "base_all_ailment_duration_+%";
    const string NonDamagingAilmentEffect = "non_damaging_ailment_effect_+%";
    const string AvoidElementalAilments = "avoid_all_elemental_status_%";
    const string SelfElementalAilmentDuration = "self_elemental_status_duration_-%";
    const string ChargeDuration = "charge_duration_+%", BaseChargeDuration = "base_charge_duration_+%";
    const string LeechSpeed = "life_mana_es_leech_speed_+%";
    const string ElementalPenetration = "reduce_enemy_elemental_resistance_%", PenetrateElemental = "base_penetrate_elemental_resistances_%";
    const string IgniteChance = "base_chance_to_ignite_%", FreezeShockIgniteChance = "chance_to_freeze_shock_ignite_%";

    public static readonly Sum[] Sums =
    [
        // Offence
        new("cast_speed_+%", ["base_cast_speed_+%", "attack_and_cast_speed_+%"]),
        new("global_chance_to_knockback_%", ["base_global_chance_to_knockback_%"]),
        new("virtual_knockback_distance_+%", ["knockback_distance_+%"]),
        new("stun_duration_+%", ["base_stun_duration_+%"]),
        new("virtual_chance_to_blind_on_hit_%", ["global_chance_to_blind_on_hit_%"]),
        new("virtual_ignite_duration_+%", ["ignite_duration_+%", ElementalAilmentDuration, AilmentDuration,
            "fire_ailment_duration_+%", "skill_effect_and_damaging_ailment_duration_+%"]),
        new("virtual_scorch_duration_+%", [ElementalAilmentDuration, AilmentDuration, "fire_ailment_duration_+%"]),
        new("virtual_scorch_effect_+%", ["scorch_effect_+%", NonDamagingAilmentEffect]),
        new("virtual_chill_duration_+%", ["chill_duration_+%", "chill_and_freeze_duration_+%", ElementalAilmentDuration,
            AilmentDuration, "cold_ailment_duration_+%"]),
        new("virtual_chill_effect_+%", ["chill_effect_+%", "cold_ailment_effect_+%", NonDamagingAilmentEffect]),
        new("chance_to_freeze_%", ["base_chance_to_freeze_%", FreezeShockIgniteChance]),
        new("virtual_freeze_duration_+%", ["freeze_duration_+%", "chill_and_freeze_duration_+%", ElementalAilmentDuration,
            AilmentDuration, "cold_ailment_duration_+%"]),
        new("virtual_frostburn_duration_+%", [ElementalAilmentDuration, AilmentDuration, "cold_ailment_duration_+%"]),
        new("virtual_frostburn_effect_+%", ["cold_ailment_effect_+%", NonDamagingAilmentEffect]),
        new("chance_to_shock_%", ["base_chance_to_shock_%", FreezeShockIgniteChance]),
        new("virtual_shock_duration_+%", ["shock_duration_+%", ElementalAilmentDuration, AilmentDuration,
            "lightning_ailment_duration_+%"]),
        new("virtual_shock_effect_+%", ["shock_effect_+%", "lightning_ailment_effect_+%", NonDamagingAilmentEffect]),
        new("virtual_sap_duration_+%", [ElementalAilmentDuration, AilmentDuration, "lightning_ailment_duration_+%"]),
        new("virtual_sap_effect_+%", ["lightning_ailment_effect_+%", NonDamagingAilmentEffect]),
        // Spells and secondary damage (no weapon)
        new("virtual_total_spell_chance_to_ignite_on_hit_%", [IgniteChance, FreezeShockIgniteChance]),
        new("virtual_total_secondary_chance_to_ignite_on_hit_%", [IgniteChance, FreezeShockIgniteChance]),
        new("virtual_total_spell_chance_to_poison_on_hit_%", ["base_chance_to_poison_on_hit_%", "spells_chance_to_poison_on_hit_%"]),
        new("virtual_total_secondary_chance_to_poison_on_hit_%", ["base_chance_to_poison_on_hit_%"]),
        new("virtual_total_spell_impale_on_hit_%_chance", ["spells_impale_on_hit_%_chance", "impale_on_hit_%_chance"]),
        new("impale_on_hit_%_chance", ["attacks_impale_on_hit_%_chance"]),
        new("reduce_enemy_fire_resistance_%", ["base_reduce_enemy_fire_resistance_%", ElementalPenetration, PenetrateElemental]),
        new("reduce_enemy_cold_resistance_%", ["base_reduce_enemy_cold_resistance_%", ElementalPenetration, PenetrateElemental]),
        new("reduce_enemy_lightning_resistance_%", ["base_reduce_enemy_lightning_resistance_%", ElementalPenetration, PenetrateElemental]),
        // Damage immunities (the Defence tab's "Immune to X" rows). A source sets the base form; the elemental
        // immunity covers fire, cold and lightning, and chaos keeps its own older name (chaos_immunity). The display
        // stat itself counts too (Compute adds get(display)), so a mod that sets fire_damage_immunity directly works.
        new("fire_damage_immunity", ["base_fire_immunity", "elemental_damage_immunity"], Flag: true),
        new("cold_damage_immunity", ["base_cold_immunity", "elemental_damage_immunity"], Flag: true),
        new("lightning_damage_immunity", ["base_lightning_immunity", "elemental_damage_immunity"], Flag: true),
        new("chaos_damage_immunity", ["chaos_immunity"], Flag: true),
        new("physical_immunity", [], Flag: true),
        // Main hand and off hand: the global stats and that weapon's own
        .. Hands(h => new($"{h}_chance_to_blind_on_hit_%", ["global_chance_to_blind_on_hit_%", "attacks_chance_to_blind_on_hit_%"],
            Local: ["local_chance_to_blind_on_hit_%"])),
        .. Hands(h => new($"virtual_total_{h}_chance_to_bleed_on_hit_%", ["bleed_on_hit_with_attacks_%"],
            Local: ["local_chance_to_bleed_on_hit_%"])),
        .. Hands(h => new($"virtual_total_{h}_chance_to_ignite_on_hit_%", [IgniteChance, FreezeShockIgniteChance])),
        .. Hands(h => new($"virtual_total_{h}_chance_to_poison_on_hit_%",
            ["base_chance_to_poison_on_hit_%", "chance_to_poison_on_hit_with_attacks_%"], Local: ["local_poison_on_hit_%"])),
        .. Hands(h => new($"virtual_total_{h}_impale_on_hit_%_chance", ["attacks_impale_on_hit_%_chance", "impale_on_hit_%_chance"],
            Local: ["local_attacks_impale_on_hit_%_chance"])),
        .. Hands(h => new($"{h}_attack_damage_penetrates_fire_resist_%", ["base_reduce_enemy_fire_resistance_%", ElementalPenetration,
            PenetrateElemental], Local: ["local_fire_penetration_%", "local_elemental_penetration_%"])),
        .. Hands(h => new($"{h}_attack_damage_penetrates_cold_resist_%", ["base_reduce_enemy_cold_resistance_%", ElementalPenetration,
            PenetrateElemental], Local: ["local_cold_penetration_%", "local_elemental_penetration_%"])),
        .. Hands(h => new($"{h}_attack_damage_penetrates_lightning_resist_%", ["base_reduce_enemy_lightning_resistance_%",
            ElementalPenetration, PenetrateElemental], Local: ["local_lightning_penetration_%", "local_elemental_penetration_%"])),
        .. Hands(h => new($"{h}_attack_damage_penetrates_chaos_resist_%", ["reduce_enemy_chaos_resistance_%"],
            Local: ["local_chaos_penetration_%"])),
        .. Hands(h => new($"{h}_stun_threshold_reduction_+%", ["base_stun_threshold_reduction_+%"],
            Local: ["local_stun_threshold_reduction_+%"])),
        .. Hands(h => new($"{h}_chance_to_knockback_%", ["base_global_chance_to_knockback_%"])),
        .. Hands(h => new($"{h}_knockback_on_crit", ["global_knockback_on_crit"], Flag: true)),
        .. Hands(h => new($"{h}_hit_causes_monster_flee_%", ["global_hit_causes_monster_flee_%"])),
        .. Hands(h => new($"{h}_reduce_enemy_block_%", ["global_reduce_enemy_block_%"])),
        .. Hands(h => new($"{h}_double_damage_to_chilled_enemies", [], Flag: true, Local: ["local_double_damage_to_chilled_enemies"])),
        .. Hands(h => new($"{h}_local_life_gain_per_target", ["base_life_gain_per_target"], Local: ["local_life_gain_per_target"])),
        .. Hands(h => new($"{h}_local_mana_gain_per_target", ["mana_gain_per_target"], Local: ["local_mana_gain_per_target"])),
        .. Hands(h => new($"{h}_life_leech_from_physical_attack_damage_permyriad", ["life_leech_from_physical_attack_damage_permyriad"],
            Local: ["local_life_leech_from_physical_damage_permyriad"])),
        .. Hands(h => new($"{h}_mana_leech_from_physical_attack_damage_permyriad", ["mana_leech_from_physical_attack_damage_permyriad"],
            Local: ["local_mana_leech_from_physical_damage_permyriad"])),
        // Defence
        new("virtual_energy_shield_delay_-%", ["energy_shield_delay_-%"]),
        new("virtual_block_recovery_+%", ["block_recovery_+%"]),
        new("stun_recovery_+%", ["base_stun_recovery_+%"]),
        new("avoid_stun_%", ["base_avoid_stun_%"]),
        new("cannot_be_stunned", ["base_cannot_be_stunned"], Flag: true),
        new("extra_damage_taken_from_crit_+%", [new("base_self_critical_strike_multiplier_-%", Mul: -1)]),
        new("avoid_bleed_%", ["base_avoid_bleed_%"]),
        new("cannot_gain_bleeding", ["base_cannot_gain_bleeding"], Flag: true),
        new("virtual_immune_to_bleeding", ["immune_to_bleeding"], Flag: true),
        new("combined_bleeding_duration_on_self_+%", ["self_bleed_duration_+%"]),
        .. Ailment("ignite", "ignited"), .. Ailment("chill", "chilled"), .. Ailment("freeze", "frozen"), .. Ailment("shock", "shocked"),
        new("avoid_poison_%", ["base_avoid_poison_%"]),
        new("virtual_cannot_be_poisoned", ["cannot_be_poisoned"], Flag: true),
        new("virtual_immune_to_poison", ["immune_to_poison"], Flag: true),
        new("combined_poison_duration_on_self_+%", ["self_poison_duration_+%"]),
        .. new[] { "physical", "fire", "cold", "lightning", "chaos" }.SelectMany(type => new Sum[]
        {
            new($"minimum_{type}_damage_to_return_to_melee_attacker", [$"{type}_damage_to_return_to_melee_attacker"]),
            new($"maximum_{type}_damage_to_return_to_melee_attacker", [$"{type}_damage_to_return_to_melee_attacker"]),
        }),
        new("intermediary_combined_action_speed_+%", [new("action_speed_-%", Mul: -1)]),
        // Misc
        new("ascendance", ["base_ascendance"]),
        new("skill_area_of_effect_+%", ["base_skill_area_of_effect_+%"]),
        new("virtual_curse_effect_+%", ["curse_effect_+%"]),
        new("virtual_flask_duration_+%", ["flask_duration_+%"]),
        .. Leech("life"), .. Leech("mana"),
        .. new[] { "spell", "fire", "cold", "lightning" }.Select(type => new Sum(
            $"virtual_energy_shield_leech_from_{type}_damage_permyriad", [$"base_energy_shield_leech_from_{type}_damage_permyriad"])),
        new("virtual_energy_shield_leech_speed_+%", ["energy_shield_leech_speed_+%", LeechSpeed]),
        new("life_gained_on_spell_hit", ["base_life_gained_on_spell_hit"]),
        new("virtual_energy_shield_gain_per_target", ["energy_shield_gain_per_target"]),
        new("life_gained_on_enemy_death", ["base_life_gained_on_enemy_death"]),
        new("mana_gained_on_enemy_death", ["base_mana_gained_on_enemy_death"]),
        new("energy_shield_gained_on_enemy_death", ["base_energy_shield_gained_on_enemy_death"]),
        new("virtual_minion_damage_+%", ["minion_damage_+%"]),
        new("projectile_speed_+%", ["base_projectile_speed_+%"]),
        new("display_skill_increased_item_rarity_+%", ["base_item_found_rarity_+%", "item_found_rarity_+%"]),
        new("display_skill_increased_item_quantity_+%", ["base_item_found_quantity_+%", "item_found_quantity_+%"]),
        new("virtual_trap_throwing_speed_+%", ["trap_throwing_speed_+%", "trap_and_mine_throwing_speed_+%"]),
        new("virtual_mine_throwing_speed_+%", ["mine_laying_speed_+%", "trap_and_mine_throwing_speed_+%"]),
        new("virtual_cooldown_speed_+%", ["base_cooldown_speed_+%"]),
        // Charges
        new("virtual_endurance_charge_duration_+%", ["endurance_charge_duration_+%", ChargeDuration, BaseChargeDuration]),
        new("virtual_frenzy_charge_duration_+%", ["base_frenzy_charge_duration_+%", ChargeDuration, BaseChargeDuration]),
        new("virtual_power_charge_duration_+%", ["power_charge_duration_+%", ChargeDuration, BaseChargeDuration]),
        new("attack_speed_+%_per_frenzy_charge", ["base_attack_speed_+%_per_frenzy_charge"]),
        new("cast_speed_+%_per_frenzy_charge", ["base_cast_speed_+%_per_frenzy_charge"]),
    ];

    // A row for each hand: main_hand_… with the main hand weapon's local stats, off_hand_… with the off hand's.
    static Sum[] Hands(Func<string, Sum> row) =>
        [row("main_hand") with { Hand = Hand.Main }, row("off_hand") with { Hand = Hand.Off }];

    // An elemental ailment's defence rows: avoid (with Avoid Elemental Ailments), the duration on self (with the elemental
    // ailment duration on self), cannot be (flag) and immune (flag; "Immune to Ignite and Shock" counts for both).
    static Sum[] Ailment(string ailment, string past) =>
    [
        new($"avoid_{ailment}_%", [$"base_avoid_{ailment}_%", AvoidElementalAilments]),
        new($"self_{ailment}_duration_-%", [$"base_self_{ailment}_duration_-%", SelfElementalAilmentDuration]),
        new($"cannot_be_{past}", [$"base_cannot_be_{past}"], Flag: true),
        new($"immune_to_{ailment}", ailment is "ignite" or "shock"
            ? [$"base_immune_to_{ailment}", "immune_to_ignite_and_shock"] : [$"base_immune_to_{ailment}"], Flag: true),
    ];

    // Life or mana leech per damage type (fire, cold and lightning also take the elemental leech; life also has the
    // 1/100 000 form some item mods use), from any attack damage, and the leech recovery speed.
    static Sum[] Leech(string pool) =>
    [
        .. new[] { "spell", "physical", "fire", "cold", "lightning", "chaos" }.Select(type => new Sum(
            $"{pool}_leech_from_{type}_damage_permyriad",
            [
                $"base_{pool}_leech_from_{type}_damage_permyriad",
                .. type is "fire" or "cold" or "lightning" ? new Src[] { $"base_{pool}_leech_from_elemental_damage_permyriad" } : [],
                .. pool == "life" && type != "spell" ? new Src[] { new($"horrible_ugly_life_leech_from_{type}_damage_perhundredthousand", Div: 10) } : [],
            ])),
        new($"virtual_{pool}_leech_from_attack_damage_permyriad", [$"base_{pool}_leech_from_attack_damage_permyriad"]),
        new($"virtual_{pool}_leech_speed_+%", [$"{pool}_leech_speed_+%", LeechSpeed]),
    ];

    /// <summary>Every display stat with a non-zero value, from the stats as the server holds them (<paramref name="get"/>:
    /// by Stats.dat Id, 0 when absent) and each hand's local weapon stats.</summary>
    public static List<(string Stat, int Value)> Compute(Func<string, int> get,
        Func<string, int>? mainHand = null, Func<string, int>? offHand = null)
    {
        static long Total(IEnumerable<Src>? sources, Func<string, int>? get) =>
            get is null || sources is null ? 0 : sources.Sum(s => (long)get(s.Name) * s.Mul / s.Div);
        var result = new List<(string, int)>();
        foreach (var sum in Sums)
        {
            var local = sum.Hand switch { Hand.Main => mainHand, Hand.Off => offHand, _ => null };
            long value = get(sum.Display) + Total(sum.Sources, get) + Total(sum.Local, local);
            if (sum.Flag) value = value > 0 ? 1 : 0;
            if (value != 0) result.Add((sum.Display, (int)Math.Clamp(value, int.MinValue, int.MaxValue)));
        }
        return result;
    }

    /// <summary>
    /// The Offence tab's per-hand damage rows (issue #75): the hit the server actually swings with, per damage type and
    /// in total. The client shows whole numbers, so each range is rounded down. An empty packet adds no rows, which
    /// leaves those rows blank, as the game does for an attack that cannot happen.
    /// </summary>
    public static List<(string Stat, int Value)> HandDamage(DamagePacket packet, string hand)
    {
        var result = new List<(string, int)>();
        if (packet.IsZero) return result;
        static int Whole(double value) => (int)Math.Floor(Math.Max(0, value));
        result.Add(($"{hand}_minimum_total_damage", Whole(packet.MinTotal)));
        result.Add(($"{hand}_maximum_total_damage", Whole(packet.MaxTotal)));
        foreach (var (type, range) in packet.Components)
        {
            string? slug = type switch
            {
                DamageType.Physical => "physical",
                DamageType.Fire => "fire",
                DamageType.Cold => "cold",
                DamageType.Lightning => "lightning",
                DamageType.Chaos => "chaos",
                _ => null,
            };
            if (slug is null) continue;
            result.Add(($"{hand}_minimum_{slug}_damage", Whole(range.Min)));
            result.Add(($"{hand}_maximum_{slug}_damage", Whole(range.Max)));
        }
        return result;
    }

    /// <summary>
    /// The rows that combine a pool with a stat: block against projectiles (attack block + <c>additional_block_chance_against_projectiles_%</c>,
    /// spell block + <c>base_spell_projectile_block_%</c>, each capped at its maximum; the spell row shows when
    /// <c>spell_block_differs_against_projectiles</c> is set) and chaos resistance against damage over time (chaos
    /// resistance + <c>additional_chaos_resistance_against_damage_over_time_%</c>, capped at the chaos maximum).
    /// </summary>
    public static List<(string Stat, int Value)> FromPools(Func<string, int> get, CharacterStats.Pools pools)
    {
        var result = new List<(string, int)>();
        int projectileBlock = get("additional_block_chance_against_projectiles_%");
        if (projectileBlock != 0)
            result.Add(("projectile_attack_block_%", Math.Clamp(pools.BlockChanceUncapped + projectileBlock, 0, pools.BlockChanceMax)));
        int spellProjectileBlock = get("base_spell_projectile_block_%");
        if (spellProjectileBlock != 0)
        {
            result.Add(("spell_projectile_block_%", Math.Clamp(pools.SpellBlockChance + spellProjectileBlock, 0, pools.SpellBlockChanceMax)));
            result.Add(("spell_block_differs_against_projectiles", 1));
        }
        int chaosDot = get("additional_chaos_resistance_against_damage_over_time_%");
        if (chaosDot != 0)
        {
            int uncapped = pools.ChaosResistance + chaosDot;
            result.Add(("uncapped_chaos_damage_over_time_resistance_%", uncapped));
            result.Add(("chaos_damage_over_time_resistance_%", DamageModel.Capped(uncapped, pools.ChaosMaxResistance)));
        }
        return result;
    }

    /// <summary>Character.ot's <c>mana_regeneration_rate_per_minute_%</c>: 105, i.e. 1.75% of maximum mana per second.</summary>
    public const int BaseManaRegenPctPerMinute = 105;

    /// <summary>
    /// The Defence tab's regeneration rows (issue #75), as the per-minute totals the client divides by 60: life
    /// (<see cref="LifePerMinute"/>) and mana (<see cref="ManaPerMinute"/>). The same formulas feed the player tick, so
    /// the panel shows the regeneration the server actually applies. A character with no life regeneration gets no life
    /// row; mana always has its base rate. The client also needs life_recovery_per_minute / mana_recovery_per_minute
    /// for its own pool stepping: the Life constructor (0x141e678d0) binds these keys to the pools, and 0x141e6b320
    /// applies the rate / 60 times the client tick duration. The display totals alone do not drive that stepping.
    /// </summary>
    public static List<(string Stat, int Value)> Recovery(Func<string, int> get, uint maxLife, uint maxMana)
    {
        var result = new List<(string, int)>();
        double life = LifePerMinute(get, maxLife);
        if (life != 0)
        {
            result.Add(("total_life_recovery_per_minute_from_regeneration", (int)Math.Round(life)));
            result.Add(("life_recovery_per_minute", (int)Math.Round(life)));
        }
        int mana = (int)Math.Round(ManaPerMinute(get, maxMana));
        result.Add(("total_mana_recovery_per_minute_from_regeneration", mana));
        result.Add(("mana_recovery_per_minute", mana));
        return result;
    }

    /// <summary><c>base_life_regeneration_rate_per_minute</c> plus <c>life_regeneration_rate_per_minute_%</c> of maximum life.</summary>
    public static double LifePerMinute(Func<string, int> get, uint maxLife) =>
        get("base_life_regeneration_rate_per_minute") + maxLife * get("life_regeneration_rate_per_minute_%") / 100.0;

    /// <summary>
    /// Character.ot's <see cref="BaseManaRegenPctPerMinute"/> plus <c>mana_regeneration_rate_per_minute_%</c> of maximum
    /// mana and the flat <c>base_mana_regeneration_rate_per_minute</c> (Clarity), all raised by <c>mana_regeneration_rate_+%</c>.
    /// </summary>
    public static double ManaPerMinute(Func<string, int> get, uint maxMana) =>
        (maxMana * (BaseManaRegenPctPerMinute + get("mana_regeneration_rate_per_minute_%")) / 100.0
         + get("base_mana_regeneration_rate_per_minute")) * (1 + get("mana_regeneration_rate_+%") / 100.0);
}
