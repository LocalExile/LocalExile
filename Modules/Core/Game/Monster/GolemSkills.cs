using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

public static partial class Game
{
    static MonsterModSet GolemModifiers(GolemState.Golem golem, IReadOnlyDictionary<uint, int> player)
    {
        var values = new Dictionary<string, int>();
        foreach (var (key, value) in player)
            if (Stats.Name(key) is { } name && name.StartsWith("minion_", StringComparison.Ordinal)) values[name[7..]] = value;
        foreach (var (key, value) in golem.Own)
        {
            string name = Stats.Name(key) ?? "";
            if (name.StartsWith("minion_", StringComparison.Ordinal)) name = name[7..];
            // SupportedSkill already projects its global added damage into the attack and spell forms.
            if (name.StartsWith("global_", StringComparison.Ordinal) && name.Contains("_added_", StringComparison.Ordinal)
                && name.EndsWith("_damage", StringComparison.Ordinal)) continue;
            values[name] = values.GetValueOrDefault(name) + value;
        }
        foreach (var (name, value) in golem.Auras.Concat(golem.Utility)) values[name] = values.GetValueOrDefault(name) + value;
        foreach (var (name, value) in golem.Gear) values[name] = values.GetValueOrDefault(name) + value;
        int Get(string n) => GolemStat(player, n);
        if (!golem.Definition.IsGolem)
        {
            values["global_minimum_added_physical_damage"] = values.GetValueOrDefault("global_minimum_added_physical_damage")
                + Get("non_golem_minion_global_minimum_added_physical_damage");
            values["global_maximum_added_physical_damage"] = values.GetValueOrDefault("global_maximum_added_physical_damage")
                + Get("non_golem_minion_global_maximum_added_physical_damage");
            values["attack_speed_+%"] = values.GetValueOrDefault("attack_speed_+%") + values.GetValueOrDefault("attack_and_cast_speed_+%");
            values["base_movement_velocity_+%"] = values.GetValueOrDefault("movement_speed_+%") + values.GetValueOrDefault("base_movement_velocity_+%");
            values.Remove("life_regeneration_rate_per_minute_%");
            return MonsterModSet.None with { Effects = new(values) };
        }
        string family = golem.Definition.Kind == "Carrion" ? "bone" : golem.Definition.Kind.ToLowerInvariant();
        values["damage_+%"] = values.GetValueOrDefault("damage_+%") + Get("golem_damage_+%") + Get(family + "_golem_damage_+%")
            + Get("golem_damage_+%_per_active_golem_type") * Math.Max(1, Get("virtual_current_number_of_golem_types"))
            + Get("golem_damage_+%_per_active_golem") * Get("virtual_total_number_of_active_golems");
        values["attack_and_cast_speed_+%"] = values.GetValueOrDefault("attack_and_cast_speed_+%") + Get("golem_attack_and_cast_speed_+%");
        values["attack_speed_+%"] = values.GetValueOrDefault("attack_speed_+%") + values.GetValueOrDefault("attack_and_cast_speed_+%");
        values["base_movement_velocity_+%"] = values.GetValueOrDefault("movement_speed_+%") + values.GetValueOrDefault("base_movement_velocity_+%") + Get("golem_movement_speed_+%");
        values["base_resist_all_elements_%"] = values.GetValueOrDefault("base_resist_all_elements_%") + Get(family + "_golem_elemental_resistances_%");
        // Recovery is stepped once by the owner controller, not again by RegenerateMonstersLocked.
        values.Remove("life_regeneration_rate_per_minute_%");
        return MonsterModSet.None with { Effects = new(values) };
    }

    static uint GolemMaximumLife(MonsterCatalog.Variety variety, byte level, GolemState.Golem golem, IReadOnlyDictionary<uint, int> player)
    {
        int Own(string name) => GolemStat(golem.Own, name);
        double increased = GolemStat(player, "minion_maximum_life_+%") + Own("minion_maximum_life_+%")
            + (golem.Definition.IsGolem ? GolemStat(player, "golem_maximum_life_+%") : 0);
        double more = Math.Max(0, 1 + Own("active_skill_minion_life_+%_final") / 100.0)
            * Math.Max(0, 1 + Own("support_minion_maximum_life_+%_final") / 100.0)
            * Math.Max(0, 1 + Own("support_minion_damage_minion_life_+%_final") / 100.0)
            * (golem.Definition.IsGolem ? Math.Max(0, 1 + GolemTether(player, "life") / 100.0) : 1);
        if (golem.Definition.Kind is "Flame" or "Ice" or "Lightning" && GolemStat(player, "elemental_golems_maximum_life_is_doubled") > 0) more *= 2;
        if (golem.Definition.Kind != "Reaper") more *= Math.Max(0, 1 + GolemStat(player, "active_skill_non_reaper_minion_maximum_life_+%_final") / 100d);
        increased += golem.Gear.GetValueOrDefault("maximum_life_+%");
        return (uint)Math.Clamp(((Monsters.BaseLife(variety, level) ?? 100) + golem.Gear.GetValueOrDefault("base_maximum_life"))
            * Math.Max(.01, 1 + increased / 100) * more, 1, int.MaxValue);
    }

    static double GolemShield(GolemState.Golem golem, IReadOnlyDictionary<uint, int> player) => golem.Monster is null ? 0
        : Math.Max(0, GolemStat(player, "minion_maximum_life_%_to_add_as_maximum_energy_shield") / 100.0 * golem.Monster.MaxLife
            + golem.Auras.GetValueOrDefault("base_maximum_energy_shield"));

    static int SkeletonShieldChaos(ClientState conn, GolemState.Golem fighter, IReadOnlyDictionary<uint, int> player)
    {
        if (fighter.Definition.Kind != "Skeleton") return 0;
        int percent = GolemStat(player, "your_skeletons_gain_added_chaos_damage_equal_to_%_of_energy_shield_on_shield");
        if (percent <= 0 || ActiveOffHand(conn) is not { Type: { Class: "Shield" } shield } hand) return 0;
        var items = ItemsFor(conn);
        if (items.Defences(shield) is not { } defences) return 0;
        int localShield = ItemDefences.Calculate(hand.Item, defences, items.Mods).EnergyShield;
        return (int)Math.Min(int.MaxValue, (long)localShield * percent / 100);
    }

    public static IReadOnlyList<GolemState.Ability> GolemAbilities(ClientState conn, MonsterCatalog.Variety variety, byte level,
        GolemState.Golem golem, IReadOnlyDictionary<uint, int> player)
    {
        var catalog = SkillsFor(conn); var result = new List<GolemState.Ability>();
        var mods = GolemModifiers(golem, player).Effects;
        int shieldChaos = SkeletonShieldChaos(conn, golem, player);
        int Own(string name) => GolemStat(golem.Own, name);
        foreach (var (effect, index) in (variety.GrantedEffects ?? []).Select((e, i) => (e, i)))
        {
            var (skillLevel, raw, multiplier, crit) = GolemData.Skill(catalog, effect, level);
            int Raw(string name) => GolemStat(raw, name);
            bool spell = catalog.IsSpell(effect) && !catalog.IsAttack(effect);
            double seconds = (spell || Raw("cast_time_overrides_attack_duration") != 0) && catalog.CastTimeMs(effect) > 0
                ? catalog.CastTimeMs(effect) / 1000.0 : (golem.Weapon?.AttackMs ?? variety.AttackMs) / 1000.0;
            double speed = 1 + (spell ? mods["cast_speed_+%"] + mods["attack_and_cast_speed_+%"] : mods["attack_speed_+%"] + Raw("attack_speed_+%")) / 100.0;
            speed *= Math.Max(.01, 1 + Raw(spell ? "active_skill_cast_speed_+%_final" : "active_skill_attack_speed_+%_final") / 100.0);
            if (!spell) speed *= Math.Max(.01, 1 + catalog.AttackSpeedMultiplier(effect, skillLevel) / 100.0);
            seconds = Math.Max(.1, seconds / Math.Max(.1, speed));
            var damage = DamagePacket.Empty;
            if (spell)
                foreach (var type in DamageTypes.ConversionOrder)
                {
                    string slug = type.ToString().ToLowerInvariant();
                    int min = Raw($"spell_minimum_base_{slug}_damage"), max = Raw($"spell_maximum_base_{slug}_damage");
                    if (max > 0) damage = damage.Add(type, new(min, max));
                }
            else if (catalog.IsAttack(effect) && Raw("base_deal_no_damage") == 0)
            {
                var (min, max) = Monsters.BaseDamage(variety, level) ?? (1, 1);
                damage = (golem.Weapon is { } equipped ? equipped.LocalDamage ?? DamagePacket.Of(DamageType.Physical, equipped.MinDamage, equipped.MaxDamage)
                    : DamagePacket.Of(DamageType.Physical, min, max)).Scale(multiplier);
            }
            if (effect == 5268 && damage.MaxTotal <= 0)
            {
                var (min, max) = Monsters.BaseDamage(variety, level) ?? (1, 1);
                damage = DamagePacket.Of(DamageType.Physical, min, max).Scale(multiplier);
            }
            if (shieldChaos > 0 && damage.MaxTotal > 0 && Raw("base_deal_no_damage") == 0)
            {
                double added = shieldChaos * AddedEffectiveness(conn, effect, skillLevel);
                damage = damage.Add(DamageType.Chaos, new(added, added));
            }
            foreach (var type in DamageTypes.ConversionOrder)
            {
                string slug = type.ToString().ToLowerInvariant();
                int min = mods[$"global_minimum_added_{slug}_damage"] + mods[$"{(spell ? "spell" : "attack")}_minimum_added_{slug}_damage"];
                int max = mods[$"global_maximum_added_{slug}_damage"] + mods[$"{(spell ? "spell" : "attack")}_maximum_added_{slug}_damage"];
                if (max != 0) damage = damage.Add(type, new(min, max));
            }
            var conversion = new DamageConversion();
            foreach (var type in DamageTypes.ConversionOrder.Where(t => t != DamageType.Physical))
            {
                string slug = type.ToString().ToLowerInvariant();
                conversion.AddSkill(DamageType.Physical, type, Raw($"base_physical_damage_%_to_convert_to_{slug}") + Raw($"skill_physical_damage_%_to_convert_to_{slug}"));
                conversion.AddGlobal(DamageType.Physical, type, mods[$"physical_damage_%_to_convert_to_{slug}"]);
                conversion.AddGain(DamageType.Physical, type, mods[$"physical_damage_%_to_add_as_{slug}"]);
            }
            var rawDamage = damage;
            double more = Math.Max(0, 1 + Own("active_skill_minion_damage_+%_final") / 100.0)
                * Math.Max(0, 1 + Own("support_minion_damage_+%_final") / 100.0)
                * Math.Max(0, 1 + Own("minion_damage_+%_final") / 100.0)
                * Math.Max(0, 1 + Raw("active_skill_damage_+%_final") / 100.0)
                * (golem.Definition.IsGolem ? Math.Max(0, 1 + GolemTether(player, "damage") / 100.0) : 1);
            if (golem.Definition.Kind != "Reaper") more *= Math.Max(0, 1 + GolemStat(player, "active_skill_non_reaper_minion_damage_+%_final") / 100d);
            var types = new HashSet<int>();
            if (spell) types.Add(1); else { types.Add(0); types.Add(19); }
            if (Raw("is_area_damage") != 0) types.Add(7);
            if (Raw("base_is_projectile") != 0) types.Add(2);
            var supportStats = golem.Own.Select(p => (Name: Stats.Name(p.Key) ?? "", p.Value)).ToArray();
            var modified = DamagePacket.Empty;
            foreach (var path in conversion.Trace(damage))
            {
                if (GemSupportDamage.Prohibited(path.Type, supportStats)) continue;
                double increase = mods["damage_+%"] + (spell ? mods["spell_damage_+%"] : mods["attack_damage_+%"])
                    + (Raw("is_area_damage") != 0 ? mods["area_damage_+%"] : 0);
                foreach (var type in DamageTypes.ConversionOrder)
                    if ((path.History & DamageTypes.HistoryOf(type)) != 0) increase += mods[type.ToString().ToLowerInvariant() + "_damage_+%"];
                if ((path.History & DamageTypeHistory.Elemental) != 0) increase += mods["elemental_damage_+%"];
                var (general, typed) = GemSupportDamage.Multipliers(path.History, supportStats, types);
                double physicalMore = (path.History & DamageTypeHistory.Physical) != 0
                    ? Math.Max(0, 1 + Own("active_skill_minion_physical_damage_+%_final") / 100.0) : 1;
                double auraMore = spell ? Math.Max(0, 1 + mods["spell_damage_aura_spell_damage_+%_final"] / 100.0) : 1;
                if (spell && (path.History & DamageTypeHistory.Lightning) != 0)
                    auraMore *= Math.Max(0, 1 + mods["wrath_aura_spell_lightning_damage_+%_final"] / 100.0);
                // Elemental Army: "Minions deal 10% more Elemental Damage" (the support's other half is the exposure).
                double elementalMore = (path.History & DamageTypeHistory.Elemental) != 0
                    ? Math.Max(0, 1 + Own("support_minion_totem_resistance_elemental_damage_+%_final") / 100.0) : 1;
                modified = modified.Add(path.Type, path.Range.Scale(Math.Max(0, 1 + increase / 100) * more * physicalMore * auraMore * general * typed * elementalMore));
            }
            damage = modified;
            string id = catalog.EffectId(effect) ?? "";
            double range = spell ? Math.Max(20, 70 * (1 + Raw("spell_maximum_action_distance_+%") / 100.0)) : variety.AttackRange;
            if (!spell && golem.Weapon is { } weapon) range = weapon.Melee ? weapon.Range : 70;
            if (id.Contains("Whirling", StringComparison.Ordinal) || id.Contains("Cyclone", StringComparison.Ordinal) || id.Contains("Leap", StringComparison.Ordinal)) range = 45;
            double radius = Raw("active_skill_base_area_of_effect_radius");
            if (radius <= 0) radius = id.Contains("Slam", StringComparison.Ordinal) ? 15 : 10;
            radius *= (1 + Raw("active_skill_area_of_effect_radius_+%_final") / 100.0)
                * Math.Sqrt(Math.Max(0, 1 + (mods["base_skill_area_of_effect_+%"] + Raw("base_skill_area_of_effect_+%")) / 100.0));
            ushort skill = (ushort)(0x4000 + index);
            var attack = new MonsterAttack(skill, seconds, range, damage) { Spell = spell, Ranged = spell, Scripted = true };
            var projectile = catalog.MonsterProjectile(effect);
            // Ice spear / Flame spray are named active skills rather than monster_projectile_spell rows.
            projectile ??= GolemProjectile(effect, catalog);
            if (!spell && golem.Weapon is { Melee: false } ranged)
                projectile ??= new(ranged.WieldingWand ? OrdinaryRangedAttack.Wand : OrdinaryRangedAttack.Arrow, 750, false, 2);
            var contact = projectile is null ? null : MonsterReleaseFor(conn, variety, effect, level, projectile);
            double cd = catalog.CooldownMs(effect, skillLevel) / 1000.0 / Math.Max(.01, 1 +
                (GolemStat(player, "minion_cooldown_recovery_+%") + (golem.Definition.IsGolem ? GolemStat(player, "golem_skill_cooldown_recovery_+%") : 0) + Own("minion_cooldown_recovery_+%")) / 100.0);
            var ability = new GolemState.Ability(effect, skill, id, attack, raw, radius, cd, Math.Max(1, catalog.CooldownUses(effect, skillLevel)),
                Math.Clamp(contact?.Fraction ?? .5, .05, 1), projectile, crit);
            result.Add(ability);
            int AilmentStat(string name) => mods[name] + Raw(name) + (name == "critical_ailment_dot_multiplier_+" ? 50 : 0);
            string hand = spell ? "spell" : "main_hand";
            var rows = AilmentPanelStats.Compute(new(hand, spell, !spell && projectile is null), rawDamage.Scale(more), conversion, AilmentStat);
            var ignite = MonsterIgnites.Settings.From(rows, hand, AilmentStat);
            var bleed = spell ? MonsterIgnites.Settings.None : BleedSettings(rows, hand, AilmentStat, golem.Weapon?.LocalSources, projectile is null, projectile is not null);
            double bleedMore = Math.Max(0, 1 + Own("active_skill_minion_bleeding_damage_+%_final") / 100d);
            bleed = bleed with { Min = bleed.Min * bleedMore, Max = bleed.Max * bleedMore, CriticalMin = bleed.CriticalMin * bleedMore, CriticalMax = bleed.CriticalMax * bleedMore };
            var poison = MonsterPoisons.Settings.From(rows, hand, AilmentStat, golem.Weapon?.LocalSources);
            int chance = AilmentStat("base_chance_to_poison_on_hit_%") + AilmentStat(spell ? "spells_chance_to_poison_on_hit_%" : "chance_to_poison_on_hit_with_attacks_%") + poison.WeaponChance;
            MinionAilmentProfiles.Add(ability, new(ignite, bleed, poison, chance));
        }
        return result;
    }

    static int GolemTether(IReadOnlyDictionary<uint, int> player, string kind)
    {
        uint? derived = Stats.Key($"golem_{kind}_+%_final_from_unique_primordial_tether");
        return derived is uint key && player.ContainsKey(key) ? player[key]
            : GolemStat(player, $"unique_primordial_tether_golem_{kind}_+%_final");
    }

    static SkillCatalog.MonsterProjectileInfo? GolemProjectile(int effect, SkillCatalog skills) => effect switch
    {
        2378 => new("Metadata/Projectiles/FireElementalSpray", 750, false, 2),
        5025 or 5026 => new("Metadata/Projectiles/IceElementalProjectile", 750, false, 2),
        _ => null,
    };

    public static IReadOnlyList<GrantedSkill> GolemNativeSkills(ClientState conn, byte level, IReadOnlyList<GolemState.Ability> abilities)
    {
        var result = new List<GrantedSkill>();
        foreach (var ability in abilities)
        {
            var row = GolemData.Skill(SkillsFor(conn), ability.Effect, level);
            var stats = new Dictionary<uint, int>(row.Stats);
            foreach (var (key, value) in ability.Attack.InPlaceSkillStats()) stats[key] = value;
            foreach (var (type, range) in ability.Attack.Damage.Components)
            {
                string kind = ability.Attack.Spell ? "spell" : "attack", slug = type.ToString().ToLowerInvariant();
                if (Stats.Key($"{kind}_minimum_base_{slug}_damage") is uint min) stats[min] = (int)range.Min;
                if (Stats.Key($"{kind}_maximum_base_{slug}_damage") is uint max) stats[max] = (int)range.Max;
            }
            if (SkillsFor(conn).FindByRow(ability.Skill, ability.Effect, row.Level, [.. stats.Select(p => (p.Key, p.Value))]) is { } skill)
                result.Add(skill);
        }
        return result;
    }

    static void RefreshGolemLocked(ClientState conn, GolemState.Golem golem, IReadOnlyDictionary<uint, int> player)
    {
        golem.Auras = MinionAuraStats(conn, golem);
        var hash = new HashCode();
        hash.Add(CombatLifetime);
        hash.Add(SkeletonShieldChaos(conn, golem, player));
        foreach (var p in player.Where(p => (Stats.Name(p.Key) ?? "").Contains("minion", StringComparison.Ordinal)
            || (Stats.Name(p.Key) ?? "").Contains("golem", StringComparison.Ordinal)).OrderBy(p => p.Key)) hash.Add(p);
        foreach (var p in golem.Own.OrderBy(p => p.Key)) hash.Add(p);
        foreach (var p in golem.Auras.OrderBy(p => p.Key)) hash.Add(p);
        foreach (var p in golem.Utility.OrderBy(p => p.Key)) hash.Add(p);
        foreach (var p in golem.Gear.OrderBy(p => p.Key)) hash.Add(p);
        int input = hash.ToHashCode();
        if (input == golem.InputHash) return;
        golem.InputHash = input;
        golem.Player = new(player);
        var m = golem.Monster;
        uint maximum = GolemMaximumLife(m.Variety!, m.Level, golem, player);
        if (maximum != m.MaxLife)
        {
            m.Life = (uint)Math.Clamp(m.Life / (double)m.MaxLife * maximum, 1, maximum); m.MaxLife = maximum;
            conn.Minions.OutputQueue.Add(new(InstanceEntities.StatsUpdate(golem.Id, [(StatKeys.MaximumLife, (int)maximum)]), "golem maximum life", golem.Id, m, WorldId: golem.Id));
            conn.Minions.OutputQueue.Add(new([], "golem life", golem.Id, m, PlayerMinions.OutputKind.Life));
        }
        m.Mods = GolemModifiers(golem, player);
        golem.MaximumShield = GolemShield(golem, player); golem.Shield = Math.Min(golem.Shield, golem.MaximumShield);
        golem.Abilities = GolemAbilities(conn, m.Variety!, m.Level, golem, player);
        golem.Skills = GolemNativeSkills(conn, m.Level, golem.Abilities);
        conn.Minions.OutputQueue.Add(new(InstanceEntities.PlayerSkillsChange(golem.Id, golem.Skills, []), "golem abilities updated", golem.Id, m, WorldId: golem.Id));
        conn.Minions.OutputQueue.Add(new(InstanceEntities.StatsUpdate(golem.Id,
            [(StatKeys.MovementVelocityPct, m.Mods.Effects.MovementPct)]), "golem speed", golem.Id, m, WorldId: golem.Id));
        if (conn.MinionOrigins.TryGetValue(golem.Id, out var origin))
            conn.MinionOrigins[golem.Id] = origin with { Object = origin.Object with { Skills = golem.Skills } };
    }
}
