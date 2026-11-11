/// <summary>
/// The auras the server models: their BuffDefinitions rows and their effects on the player. Reservation itself is
/// data-driven (SkillCatalog.Reservation). Enemy auras, herald procs, reservation minions and persistent spell buffs
/// have separate executors; their HUD state does not turn them into ordinary player-stat auras.
/// </summary>
static class AuraCatalog
{
    /// <summary>One active reservation skill: the skill id, its GrantedEffects row and id, its buff instance, and its gem's
    /// level and quality.</summary>
    public sealed record Aura(ushort SkillId, int Effect, string EffectId, string Name, ushort Buff, ushort BuffRow,
        int ManaFlat, int ManaPercent, int LifeFlat = 0, int LifePercent = 0, int Level = 1, int Quality = 0, ushort? SourceBuff = null,
        WornGrantedSkills.Grant? WornGrant = null, IReadOnlyList<(uint Key, int Value)>? SkillStats = null,
        int SupportEffectPct = 0, bool AffectsSelf = true, string? Stance = null, int? ExternalEffectPct = null,
        bool ReservesLife = false, int ReservationEfficiencyPct = 0)
    {
        public object? StanceOwner { get; init; }
    }

    // The source supplies the ground art; the received buff supplies the HUD icon and active-skill association.
    // Distinct ids let both survive area restoration and end together without counting the aura's bonuses twice.
    public static IEnumerable<byte[]> StartPackets(Aura aura, uint player, ushort? action)
    {
        if (aura.SourceBuff is ushort source)
            yield return GameServer.Core.Protocol.Instance.InstanceEntities.AddBuff(player, source, aura.BuffRow,
                player, aura.SkillId, auraSource: true, displayState: 0);
        // An enemy hex aura has a source on its caster, never a received self curse.
        if (!CurseCatalog.Hex(aura.EffectId))
            yield return GameServer.Core.Protocol.Instance.InstanceEntities.AddBuff(player, aura.Buff, aura.BuffRow,
                player, aura.SkillId, activeSkill: action is ushort hash ? (aura.SkillId, hash) : null,
                // TempestShieldBuff's native behaviour reads the art variation and alternate-shield flag
                // from the first two list entries without checking its length (0x1417a4380).
                list: aura.EffectId switch
                {
                    "TempestShield" => [0u, 0u],
                    // ChronomancerBuff likewise reads its ring size and sample interval unchecked.
                    // A 42-entry ring holds 41 samples spanning the server's four-second history.
                    "TemporalRift" => [42u, 100u],
                    _ => null,
                });
    }

    public static IEnumerable<byte[]> EndPackets(Aura aura, uint player)
    {
        yield return GameServer.Core.Protocol.Instance.InstanceEntities.RemoveBuff(player, aura.Buff);
        if (aura.SourceBuff is ushort source)
            yield return GameServer.Core.Protocol.Instance.InstanceEntities.RemoveBuff(player, source);
    }

    /// <summary>The BuffDefinitions row that draws each aura's visual and buff icon.</summary>
    private static readonly Dictionary<string, ushort> BuffRows = new()
    {
        ["PurityOfElements"] = 144,
        ["PurityOfFire"] = 145,
        ["PurityOfIce"] = 146,
        ["PurityOfLightning"] = 147,
        ["Clarity"] = 155,
        ["Vitality"] = 153,
        ["Precision"] = 1293,
        ["Haste"] = 152,
        ["Determination"] = 142,
        ["Grace"] = 143,
        ["Discipline"] = 148,
        ["Anger"] = 149,
        ["Hatred"] = 150,
        ["Wrath"] = 151,
        ["Zealotry"] = 1272,
        ["Malevolence"] = 1274,
        ["Envy"] = 828,
        ["BloodStance"] = 1308,
        ["SandStance"] = 1309,
        // Pride's buff is on nearby enemies; the same row also draws the player's aura icon (BuffVisuals 1098).
        ["Pride"] = PrideBuffRow,
        ["TemporalChains"] = 69,
        ["ElementalWeakness"] = 70, ["Flammability"] = 71, ["Frostbite"] = 72,
        ["Conductivity"] = 73, ["Enfeeble"] = 76, ["Vulnerability"] = 79,
        ["Punishment"] = 81, ["Despair"] = 1016,
        // The native stance icon is shared; the two skills' effects remain independent.
        ["FleshAndStone"] = FleshAndStoneBloodRow,
        ["BloodAndSand"] = FleshAndStoneBloodRow,
        ["ArcticArmour"] = 427,
        ["TempestShield"] = 112,
        ["HeraldOfAsh"] = 344,
        ["HeraldOfIce"] = 345,
        ["HeraldOfThunder"] = 346,
        ["HeraldOfPurity"] = 1173,
        ["HeraldOfAgony"] = 1177,
        ["SummonSkitterbots"] = 1390,
        ["TemporalRift"] = 1915,
    };

    /// <summary>Flesh and Stone's Blood Stance BuffDefinitions row: enemies nearby take more Physical Damage.</summary>
    public const ushort FleshAndStoneBloodRow = 1308;

    /// <summary>The Sand Stance BuffDefinitions row.</summary>
    public const ushort FleshAndStoneSandRow = 1309;

    /// <summary>Flesh and Stone's stances, the two the skill alternates between (its own description).</summary>
    public static string OtherStance(string? stance) => stance == "Sand" ? "Blood" : "Sand";

    /// <summary>The BuffDefinitions row that draws one activation of an aura: Flesh and Stone's row follows its stance.</summary>
    public static ushort BuffRow(string effectId, string? stance) =>
        IsStance(effectId) && stance == "Sand" ? FleshAndStoneSandRow : BuffRow(effectId);

    public static bool IsStance(string effectId) => Family(effectId) is "BloodAndSand" or "FleshAndStone";
    public static bool Shared(string id) => Family(id) is "PurityOfElements" or "PurityOfFire" or "PurityOfIce"
        or "PurityOfLightning" or "Clarity" or "Vitality" or "Precision" or "Haste" or "Determination" or "Grace"
        or "Discipline" or "Anger" or "Hatred" or "Wrath" or "Zealotry" or "Malevolence" or "Envy";

    public static double DistanceEffect(double distance, double radius) => radius > 0 ? Math.Clamp(1 - distance / radius, 0, 1) : 0;

    /// <summary>Pride's BuffDefinitions row: the enemy debuff, also the player's aura icon.</summary>
    public const ushort PrideBuffRow = 1331;

    /// <summary>Every aura's base radius in cells (Path of Building; the data has none).</summary>
    public const int BaseRadius = 40;

    public static string Family(string effectId) => effectId switch
    {
        "GraceHardMode" => "Grace", "DeterminationHardMode" => "Determination", _ => effectId,
    };

    static string StatFamily(Aura aura) =>
        StanceCatalog.IsStance(aura.EffectId) ? StanceCatalog.BloodAndSand : Family(aura.EffectId);

    public static bool Supported(string effectId) => BuffRows.ContainsKey(Family(effectId));

    public static ushort BuffRow(string effectId) => BuffRows[Family(effectId)];

    /// <summary>The character stat that exempts a supported aura family from Mana reservation. The shipped rows use the
    /// lower snake-case effect family (for example EnvyNoReservationUnique__1 carries envy_reserves_no_mana).</summary>
    public static string NoManaReservationStat(string effectId)
    {
        string family = Family(effectId);
        var stat = new System.Text.StringBuilder(family.Length + 16);
        for (int i = 0; i < family.Length; i++)
        {
            char c = family[i];
            if (i > 0 && char.IsUpper(c)) stat.Append('_');
            stat.Append(char.ToLowerInvariant(c));
        }
        return stat.Append("_reserves_no_mana").ToString();
    }

    /// <summary>Pride's "more Physical Damage taken" at its minimum and maximum, scaled by aura effect.</summary>
    public static (int Min, int Max) PridePhysicalDamageTaken(Aura aura, int auraEffectPct, SkillCatalog skills,
        StatCatalog stats) =>
        (Value(skills, stats, aura, "physical_damage_aura_nearby_enemies_physical_damage_taken_+%", auraEffectPct + aura.SupportEffectPct),
         Value(skills, stats, aura, "physical_damage_aura_nearby_enemies_physical_damage_taken_+%_max", auraEffectPct + aura.SupportEffectPct));

    /// <summary>Pride's ramp: at least <paramref name="min"/> more Physical Damage, reaching
    /// <paramref name="max"/> after 4 seconds in the aura (linear).</summary>
    public static double PridePhysicalMore(int min, int max, double secondsInAura) =>
        min + Math.Max(0, max - min) * Math.Clamp(secondsInAura / 4.0, 0, 1);

    /// <summary>Blood Stance's more Physical Hit Damage taken at distance zero, scaled by aura effect.</summary>
    public static int FleshAndStonePhysicalTaken(Aura aura, int auraEffectPct, SkillCatalog skills, StatCatalog stats) =>
        Value(skills, stats, aura, "flesh_stone_blood_stance_enemies_physical_damage_taken_when_hit_+%_final_from_player_distance",
            auraEffectPct + aura.SupportEffectPct);

    /// <summary>Flesh and Stone's Sand Stance: the share of an enemy hit's damage the player does not take at distance 0.
    /// The hit executor scales it with the attacker's distance.</summary>
    public static int FleshAndStoneDamageReduction(Aura aura, int auraEffectPct, SkillCatalog skills, StatCatalog stats) =>
        Value(skills, stats, aura, "flesh_stone_sand_stance_damage_taken_+%_final_from_distance_from_enemy_hits", 0);

    public static IReadOnlyList<Aura> Restore(IEnumerable<string> saved, IEnumerable<Aura> available, CharacterStats.Pools pools, bool inTown = false)
    {
        if (inTown) return [];
        var gems = available.Where(a => Supported(a.EffectId)).GroupBy(RestoreFamily)
            .ToDictionary(g => g.Key, g => g.First());
        var restored = new List<Aura>();
        foreach (string effect in saved.Select(RestoreFamily).Distinct())
            if (gems.TryGetValue(effect, out var aura))
                if (RequiredMana(pools, restored.Append(aura)) <= pools.Mana
                    && RequiredLife(pools, restored.Append(aura)) < pools.Life) restored.Add(aura);
        return restored;
    }

    static string RestoreFamily(Aura aura) => RestoreFamily(aura.EffectId);
    static string RestoreFamily(string effectId) =>
        StanceCatalog.IsStance(effectId) ? StanceCatalog.SourceFamily(effectId) : Family(effectId);

    /// <summary>
    /// The mana reserved by the active auras: flat reservations plus the percent reservations (permyriad) of the
    /// character's maximum mana, each reduced by mana reservation efficiency (PoB CalcPerform: divide by
    /// 1 + efficiency / 100; the final amount is rounded up to whole mana).
    /// </summary>
    public static int ReservedMana(CharacterStats.Pools pools, IEnumerable<Aura> auras)
        => Math.Min((int)pools.Mana, RequiredMana(pools, auras));

    public static int RequiredMana(CharacterStats.Pools pools, IEnumerable<Aura> auras)
        => Required(pools.Mana, pools.ManaReservationEfficiencyPct, auras.Where(a => !a.ReservesLife), false);

    public static int RequiredLife(CharacterStats.Pools pools, IEnumerable<Aura> auras)
        => Required(pools.Life, pools.LifeReservationEfficiencyPct, auras.Where(UsesLife), true);

    internal static int Required(uint maximum, int efficiencyPct, IEnumerable<Aura> auras)
        => Required(maximum, efficiencyPct, auras, false);

    internal static int Required(uint maximum, int efficiencyPct, IEnumerable<Aura> auras, bool life)
    {
        double flat = 0, percent = 0;
        foreach (var aura in auras)
        {
            double efficiency = Math.Max(-99L, (long)efficiencyPct + aura.ReservationEfficiencyPct) / 100.0 + 1;
            var (flatCost, percentCost) = life
                ? ExplicitLife(aura) ? (aura.LifeFlat, aura.LifePercent) : (aura.ManaFlat, aura.ManaPercent)
                : (aura.ManaFlat, aura.ManaPercent);
            flat += Math.Round(flatCost / efficiency, MidpointRounding.AwayFromZero);
            // PoB rounds each skill's reserved percent to two decimals before summing it.
            percent += Math.Round(percentCost / 100.0 / efficiency, 2, MidpointRounding.AwayFromZero);
        }
        double reserved = flat + maximum * percent / 100.0;
        return (int)Math.Clamp(Math.Ceiling(reserved), 0, int.MaxValue);
    }

    static bool ExplicitLife(Aura aura) => aura.LifeFlat != 0 || aura.LifePercent != 0;
    static bool UsesLife(Aura aura) => ExplicitLife(aura) || aura.ReservesLife;

    public static int UnreservedMana(CharacterStats.Pools pools, IEnumerable<Aura> auras) =>
        Math.Max(0, (int)pools.Mana - ReservedMana(pools, auras));

    /// <summary>The player stat bonuses from the active auras, scaled by generic aura effect.</summary>
    public static CharacterStats.Bonus Bonus(IEnumerable<Aura> auras, int auraEffectPct, SkillCatalog skills,
        StatCatalog stats)
    {
        int fire = 0, cold = 0, lightning = 0, chaos = 0;
        int fireMax = 0, coldMax = 0, lightningMax = 0, chaosMax = 0;
        int accuracy = 0, criticalChance = 0, attackSpeed = 0, movementSpeed = 0;
        int armour = 0, evasion = 0, energyShield = 0, armourMore = 0, evasionMore = 0, spellCrit = 0;
        foreach (var group in auras.GroupBy(StatFamily))
        {
            var aura = group.First();
            int Strongest(string name) => group.Max(a => Value(skills, stats, a, name, auraEffectPct + a.SupportEffectPct));
            switch (Family(aura.EffectId))
            {
                case "Determination":
                    armour += Strongest("base_physical_damage_reduction_rating");
                    armourMore += Strongest("determination_aura_armour_+%_final");
                    break;
                case "Grace":
                    evasion += Strongest("base_evasion_rating");
                    evasionMore += Strongest("grace_aura_evasion_rating_+%_final");
                    break;
                case "Zealotry":
                    spellCrit += Strongest("spell_critical_strike_chance_+%");
                    break;
                case "Discipline":
                    // Flat energy shield before the increases (PoB: a BASE mod); RawStats only publishes the raw stat.
                    energyShield += Strongest("base_maximum_energy_shield");
                    break;
                case "PurityOfElements":
                    int all = Strongest("base_resist_all_elements_%");
                    fire += all;
                    cold += all;
                    lightning += all;
                    break;
                case "PurityOfFire":
                    fire += Strongest("base_fire_damage_resistance_%");
                    fireMax += Strongest("base_maximum_fire_damage_resistance_%");
                    break;
                case "PurityOfIce":
                    cold += Strongest("base_cold_damage_resistance_%");
                    coldMax += Strongest("base_maximum_cold_damage_resistance_%");
                    break;
                case "PurityOfLightning":
                    lightning += Strongest("base_lightning_damage_resistance_%");
                    lightningMax += Strongest("base_maximum_lightning_damage_resistance_%");
                    break;
                case "Precision":
                    accuracy += Strongest("accuracy_rating");
                    int crit = Strongest("skill_buff_grants_critical_strike_chance_+%");
                    criticalChance += crit;
                    break;
                case "Haste":
                    attackSpeed += Strongest("attack_speed_+%_granted_from_skill");
                    movementSpeed += Strongest("base_movement_velocity_+%");
                    break;
            }
        }
        return new CharacterStats.Bonus(FireResistance: fire, ColdResistance: cold, LightningResistance: lightning,
            ChaosResistance: chaos, FireMaxResistance: fireMax, ColdMaxResistance: coldMax,
            LightningMaxResistance: lightningMax, ChaosMaxResistance: chaosMax,
            Accuracy: accuracy, CriticalStrikeChancePct: criticalChance, SpellCriticalStrikeChancePct: criticalChance + spellCrit,
            AttackSpeedPct: attackSpeed, MovementVelocityPct: movementSpeed, Armour: armour, Evasion: evasion,
            ArmourMorePct: armourMore, EvasionMorePct: evasionMore, EnergyShield: energyShield);
    }

    /// <summary>The raw keys behind Purity's resistance and maximum bonuses, for per-key dev replacements.
    /// Keep these separate from RawStats: resistance presentation already reads the completed pools.</summary>
    public static IEnumerable<(uint Key, int Value)> ResistanceStats(IEnumerable<Aura> auras, int auraEffectPct,
        SkillCatalog skills, StatCatalog stats)
    {
        foreach (var group in auras.GroupBy(StatFamily))
        {
            var aura = group.First();
            string? element = aura.EffectId switch
            {
                "PurityOfFire" => "fire", "PurityOfIce" => "cold", "PurityOfLightning" => "lightning", _ => null,
            };
            string[] names = aura.EffectId == "PurityOfElements" ? ["base_resist_all_elements_%"]
                : element is not null ? [$"base_{element}_damage_resistance_%", $"base_maximum_{element}_damage_resistance_%"]
                : [];
            foreach (string name in names)
                if (stats.Key(name) is uint key)
                    yield return (key, group.Max(a => Value(skills, stats, a, name, auraEffectPct + a.SupportEffectPct)));
        }
    }

    /// <summary>Watcher's Eye's "Damage Penetrates X% Lightning Resistance while affected by Wrath" and its Anger and
    /// Hatred forms, as the base penetration stat while that aura runs. The character's own stat, so aura effect does
    /// not scale it.</summary>
    public static IEnumerable<(uint Key, int Value)> WhileAffectedPenetration(IEnumerable<Aura> auras,
        IReadOnlyDictionary<uint, int> character, StatCatalog stats)
    {
        foreach (var aura in auras.DistinctBy(a => Family(a.EffectId)))
            if (aura.AffectsSelf && aura.EffectId switch { "Anger" => "fire", "Hatred" => "cold", "Wrath" => "lightning", _ => null } is { } element
                && stats.Key($"reduce_enemy_{element}_resistance_%_while_affected_by_{aura.EffectId.ToLowerInvariant()}") is uint from
                && stats.Key($"base_reduce_enemy_{element}_resistance_%") is uint to && character.GetValueOrDefault(from) is var value and not 0)
                yield return (to, value);
    }

    /// <summary>The flat stat bonuses from the active auras (regeneration and Haste's cast speed), scaled by aura effect.</summary>
    public static IEnumerable<(uint Key, int Value)> RawStats(IEnumerable<Aura> auras, int auraEffectPct,
        SkillCatalog skills, StatCatalog stats)
    {
        var result = new Dictionary<uint, int>();
        foreach (var group in auras.GroupBy(StatFamily))
        {
            var family = new Dictionary<uint, int>();
            foreach (var aura in group)
            {
                switch (aura.EffectId)
                {
                    case "PurityOfElements":
                        foreach (string ailment in new[] { "ignite", "freeze", "chill", "shock", "scorch", "brittle", "sap" })
                            if (stats.Key($"base_immune_to_{ailment}") is uint immune) family[immune] = 1;
                        break;
                    case "ArcticArmour":
                        Add("base_immune_to_freeze");
                        break;
                    case "TemporalRift":
                        Add("base_cooldown_speed_+%");
                        if (stats.Key("unaffected_by_temporal_chains") is uint temporal) family[temporal] = 1;
                        break;
                    case "SummonSkitterbots":
                        Add("skitterbots_trap_mine_damage_+%_final");
                        break;
                    case "TempestShield":
                        Add("shield_spell_block_%");
                        if (stats.Key("base_immune_to_shock") is uint shock) family[shock] = 1;
                        break;
                    case "HeraldOfAsh":
                        Add("physical_damage_%_to_add_as_fire");
                        break;
                    case "HeraldOfIce":
                        Add("attack_minimum_added_cold_damage"); Add("attack_maximum_added_cold_damage");
                        Add("spell_minimum_added_cold_damage"); Add("spell_maximum_added_cold_damage");
                        break;
                    case "HeraldOfThunder":
                        Add("attack_minimum_added_lightning_damage"); Add("attack_maximum_added_lightning_damage");
                        Add("spell_minimum_added_lightning_damage"); Add("spell_maximum_added_lightning_damage");
                        break;
                    case "HeraldOfPurity":
                        Add("herald_of_purity_physical_damage_+%_final");
                        break;
                    case "HeraldOfAgony":
                        Add("base_chance_to_poison_on_hit_%", "skill_buff_grants_chance_to_poison_%");
                        Add("herald_of_agony_poison_damage_+%_final");
                        break;
                    case "Anger":
                        Add("attack_minimum_added_fire_damage"); Add("attack_maximum_added_fire_damage");
                        Add("spell_minimum_added_fire_damage"); Add("spell_maximum_added_fire_damage");
                        break;
                    case "Hatred":
                        Add("physical_damage_%_to_add_as_cold");
                        break;
                    case "Wrath":
                        Add("attack_minimum_added_lightning_damage"); Add("attack_maximum_added_lightning_damage");
                        Add("wrath_aura_spell_lightning_damage_+%_final");
                        break;
                    case "Envy":
                        Add("attack_minimum_added_chaos_damage"); Add("attack_maximum_added_chaos_damage");
                        Add("spell_minimum_added_chaos_damage"); Add("spell_maximum_added_chaos_damage");
                        break;
                    case "Zealotry":
                        Add("spell_damage_aura_spell_damage_+%_final");
                        Add("spell_critical_strike_chance_+%");
                        Add("create_consecrated_ground_on_hit_%_vs_rare_or_unique_enemy");
                        break;
                    case "Precision":
                        Add("critical_strike_chance_+%", "skill_buff_grants_critical_strike_chance_+%");
                        break;
                    case "Malevolence":
                        Add("delirium_aura_damage_over_time_+%_final");
                        Add("skill_effect_duration_+%", "delirium_skill_effect_duration_+%");
                        break;
                    case "Discipline":
                        Add("base_maximum_energy_shield");
                        Add("energy_shield_recharge_rate_+%");
                        break;
                    case "Clarity":
                        Add("base_mana_regeneration_rate_per_minute");
                        break;
                    case "Vitality":
                        Add("base_life_regeneration_rate_per_minute");
                        break;
                    case "Haste":
                        Add("cast_speed_+%", "cast_speed_+%_granted_from_skill");
                        break;
                    case "BloodAndSand" or "BloodStance" or "SandStance" or "FleshAndStone":
                        if (stats.Key(aura.Stance == "Sand" ? "in_sand_stance" : "in_blood_stance") is uint stanceKey)
                            result[stanceKey] = 1;
                        break;
                }

            // Only size inputs compose here; this does not activate or add any aura.
            var effectStats = EffectStats(aura, skills);
            foreach (string name in ActorScale.Inputs)
            {
                if (stats.Key(name) is not uint key) continue;
                var source = effectStats.FirstOrDefault(s => s.Item1 == key);
                if (source.Item1 != key) continue; // Preserve present zero limits as well as nonzero size sources.
                int value = Value(skills, stats, aura, name, auraEffectPct + aura.SupportEffectPct);
                result[key] = ActorScale.Add(key, result.GetValueOrDefault(key), value);
            }

                void Add(string name, string? source = null)
                {
                    if (stats.Key(name) is not uint key) return;
                    int value = Value(skills, stats, aura, source ?? name, auraEffectPct + aura.SupportEffectPct);
                    family[key] = family.TryGetValue(key, out int previous) ? Math.Max(previous, value) : value;
                }
            }
            foreach (var (key, value) in family) result[key] = ActorScale.Add(key, result.GetValueOrDefault(key), value);
        }
        return result.Select(kv => (kv.Key, kv.Value));
    }

    private static int Value(SkillCatalog skills, StatCatalog stats, Aura aura, string name, int auraEffectPct)
    {
        if (stats.Key(name) is not uint key) return 0;
        auraEffectPct = aura.ExternalEffectPct ?? auraEffectPct;
        if (!skills.IsAura(aura.Effect)) auraEffectPct = 0;
        int value = EffectStats(aura, skills).FirstOrDefault(kv => kv.Item1 == key).Item2;
        // PoB ModStore.ScaleAddList truncates these integer defence-aura stats before character-wide scaling.
        // Decimal preserves the exact hundredths from the integer aura-effect percentage.
        if (Family(aura.EffectId) is "Discipline" or "Determination" or "Grace")
            return (int)Math.Clamp(decimal.Truncate(value * Math.Max(0m, 100m + auraEffectPct) / 100m), int.MinValue, int.MaxValue);
        return (int)Math.Round(value * Math.Max(0, 100 + auraEffectPct) / 100.0, MidpointRounding.AwayFromZero);
    }

    static IReadOnlyList<(uint Key, int Value)> EffectStats(Aura aura, SkillCatalog skills) =>
        aura.SkillStats ?? skills.EffectStats(aura.Effect, aura.Level, aura.Quality);
}
