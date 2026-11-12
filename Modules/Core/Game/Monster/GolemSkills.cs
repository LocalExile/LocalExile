using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

public static partial class Game
{
    public static bool GolemSummonedInPastEightSeconds(GolemState.Golem golem, double at) =>
        golem.Definition.IsGolem && at >= golem.SummonedAt && at - golem.SummonedAt < 8;

    public static int GolemRecentDamageIncrease(GolemState.Golem golem, double at) =>
        GolemSummonedInPastEightSeconds(golem, at)
            ? GolemStat(golem.Player, "golem_damage_+%_if_summoned_in_past_8_seconds") : 0;

    public readonly record struct GolemOwnerResistances(int Fire, int Cold, int Lightning, int Chaos);

    static GolemOwnerResistances GolemOwnerResistancesFor(ClientState conn)
    {
        int Capped(DamageType type) => DamageModel.Capped(Resistance(conn, type), MaxResistance(conn, type));
        return new(Capped(DamageType.Fire), Capped(DamageType.Cold), Capped(DamageType.Lightning), Capped(DamageType.Chaos));
    }

    public static MonsterModSet GolemModifiers(ClientState conn, GolemState.Golem golem,
        IReadOnlyDictionary<uint, int> player, double at = double.NegativeInfinity,
        GolemOwnerResistances? ownerResistances = null)
    {
        var values = new Dictionary<string, int>();
        foreach (var (key, value) in player)
            if (Stats.Name(key) is { } name)
            {
                if (name.StartsWith("minion_", StringComparison.Ordinal))
                {
                    name = name[7..] switch
                    {
                        "elemental_resistance_%" => "base_resist_all_elements_%",
                        "fire_damage_resistance_%" => "base_fire_damage_resistance_%",
                        "cold_damage_resistance_%" => "base_cold_damage_resistance_%",
                        "lightning_damage_resistance_%" => "base_lightning_damage_resistance_%",
                        "chaos_resistance_%" => "base_chaos_damage_resistance_%",
                        var plain => plain,
                    };
                }
                else if (name is not ("energy_shield_+%_final_for_minion" or "minions_have_no_armour_or_energy_shield"))
                    continue;
                values[name] = ActorScale.Saturate((long)values.GetValueOrDefault(name) + value);
            }
        foreach (var (key, value) in golem.Own)
        {
            string name = Stats.Name(key) ?? "";
            if (name.StartsWith("minion_", StringComparison.Ordinal)) name = name[7..];
            // SupportedSkill already projects its global added damage into the attack and spell forms.
            if (name.StartsWith("global_", StringComparison.Ordinal) && name.Contains("_added_", StringComparison.Ordinal)
                && name.EndsWith("_damage", StringComparison.Ordinal)) continue;
            values[name] = values.GetValueOrDefault(name) + value;
        }
        foreach (var (name, value) in golem.Auras) values[name] = values.GetValueOrDefault(name) + value;
        foreach (var (name, value) in golem.Gear) values[name] = values.GetValueOrDefault(name) + value;
        int Get(string n) => GolemStat(player, n);
        // Ancient Skull's real MinionCriticalStrikeChanceMaximumPowerChargeUnique__1 row uses the owner's maximum Power
        // Charges, not the charges held and not a minion's ability scheduling count. Charges.LimitsOf is the same
        // maximum kernel as the owner's charge session; its maximum already includes Character.ot's base three.
        int maximumPowerChargeCrit = ActorScale.Saturate((long)Get("minion_critical_strike_chance_+%_per_maximum_power_charge")
            * OwnerMaximumPowerCharges(player));
        if (maximumPowerChargeCrit != 0)
            values["critical_strike_chance_+%"] = ActorScale.Saturate((long)values.GetValueOrDefault("critical_strike_chance_+%")
                + maximumPowerChargeCrit);
        // Speaker's Wreath's real rows are per complete 50 of the owner's final Dexterity. This is the same
        // authoritative attribute snapshot the character pools use (class, equipment, flat and percentage sources),
        // never the minion's Dexterity. The attack row intentionally does not enter cast speed.
        int dexGroups = Math.Max(0, Get("dexterity")) / 50;
        int dexAttackSpeed = ActorScale.Saturate((long)dexGroups * Get("minion_attack_speed_+%_per_50_dex"));
        int dexMovementSpeed = ActorScale.Saturate((long)dexGroups * Get("minion_movement_speed_+%_per_50_dex"));
        values["attack_speed_+%"] = ActorScale.Saturate((long)values.GetValueOrDefault("attack_speed_+%") + dexAttackSpeed);
        values["base_movement_velocity_+%"] = ActorScale.Saturate((long)values.GetValueOrDefault("base_movement_velocity_+%")
            + dexMovementSpeed);
        int inheritedCoefficient = Get("minions_gain_x_percent_of_your_resistances");
        if (inheritedCoefficient != 0)
        {
            var owner = ownerResistances ?? GolemOwnerResistancesFor(conn);
            void Inherit(string type, int resistance)
            {
                string key = $"base_{type}_damage_resistance_%";
                long inherited = (long)Math.Floor((double)resistance * inheritedCoefficient / 100d);
                values[key] = ActorScale.Saturate((long)values.GetValueOrDefault(key) + inherited);
            }
            Inherit("fire", owner.Fire); Inherit("cold", owner.Cold);
            Inherit("lightning", owner.Lightning); Inherit("chaos", owner.Chaos);
        }
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
        int recent = GolemSummonedInPastEightSeconds(golem, at)
            ? Get("golem_damage_+%_if_summoned_in_past_8_seconds") : 0;
        values["damage_+%"] = values.GetValueOrDefault("damage_+%") + Get("golem_damage_+%") + Get(family + "_golem_damage_+%")
            + Get("golem_damage_+%_per_active_golem_type") * Math.Max(1, Get("virtual_current_number_of_golem_types"))
            + Get("golem_damage_+%_per_active_golem") * Get("virtual_total_number_of_active_golems") + recent;
        values["attack_and_cast_speed_+%"] = values.GetValueOrDefault("attack_and_cast_speed_+%") + Get("golem_attack_and_cast_speed_+%");
        values["attack_speed_+%"] = values.GetValueOrDefault("attack_speed_+%") + values.GetValueOrDefault("attack_and_cast_speed_+%");
        values["base_movement_velocity_+%"] = values.GetValueOrDefault("movement_speed_+%") + values.GetValueOrDefault("base_movement_velocity_+%") + Get("golem_movement_speed_+%");
        values["base_resist_all_elements_%"] = values.GetValueOrDefault("base_resist_all_elements_%") + Get(family + "_golem_elemental_resistances_%");
        // Recovery is stepped once by the owner controller, not again by RegenerateMonstersLocked.
        values.Remove("life_regeneration_rate_per_minute_%");
        return MonsterModSet.None with { Effects = new(values) };
    }

    static int OwnerMaximumPowerCharges(IReadOnlyDictionary<uint, int> player) =>
        Charges.LimitsOf(ChargeType.Power, name => GolemStat(player, name)).Maximum;

    public static uint NativeEnergyShield(double value) => value <= 0 ? 0
        : value >= int.MaxValue ? int.MaxValue : (uint)Math.Floor(value);

    static uint SnapshotMaximumEnergyShield(MonsterObject monster) =>
        Stats.Key("maximum_energy_shield") is uint key
            ? (uint)Math.Max(0, monster.Stats?.FirstOrDefault(stat => stat.Key == key).Value ?? 0) : 0;

    internal static void PublishEnergyShieldLocked(ClientState conn, GolemState.Golem golem)
    {
        if (!conn.MinionOrigins.TryGetValue(golem.Id, out var origin)) return;
        var current = golem.Shield;
        var maximum = golem.MaximumShield;
        var previousCurrent = origin.Object.EnergyShield;
        var previousMaximum = SnapshotMaximumEnergyShield(origin.Object);
        if (current == previousCurrent && maximum == previousMaximum) return;
        var maximumKey = Stats.Key("maximum_energy_shield");
        IReadOnlyList<(uint Key, int Value)>? stats = origin.Object.Stats;
        if (maximum != previousMaximum)
        {
            var rebuilt = stats?.Where(stat => stat.Key != maximumKey).ToList() ?? [];
            if (maximumKey is uint key) rebuilt.Add((key, (int)maximum));
            stats = rebuilt.Count == 0 ? null : rebuilt;
        }
        var synced = origin.Object with { EnergyShield = current, Stats = stats };
        conn.MinionOrigins[golem.Id] = origin with { Object = synced };
        if (!conn.Minions.OutputQueue.Any(output => output.Kind == PlayerMinions.OutputKind.EnergyShield
                && output.TargetId == golem.Id))
            conn.Minions.OutputQueue.Add(new([], "golem energy shield", golem.Id, golem.Monster, PlayerMinions.OutputKind.EnergyShield));
    }

    public static byte[]? GolemEnergyShieldPresentation(ClientState conn, PlayerMinions.Output output) =>
        GolemEnergyShieldRecords(conn, output) is { } records ? [.. records.SelectMany(p => p)] : null;

    static byte[][]? GolemEnergyShieldRecords(ClientState conn, PlayerMinions.Output output)
    {
        if (output.TargetId is not uint id || output.Target is null) return null;
        GolemState.Golem? golem = conn.Golems.All.GetValueOrDefault(id)
            ?? conn.Reservations.Fighters.GetValueOrDefault(id)
            ?? conn.Summons.Fighters.GetValueOrDefault(id);
        if (golem is null || !ReferenceEquals(golem.Monster, output.Target) || !GolemCurrent(conn, golem)) return null;
        var maximum = golem.MaximumShield;
        var current = Math.Min(maximum, golem.Shield);
        var maximumStat = Stats.Key("maximum_energy_shield") is uint key
            ? InstanceEntities.StatsUpdate(id, [(key, (int)maximum)]) : [];
        return maximumStat.Length == 0 ? [InstanceProtocol.LifeUpdate(id, current, pool: 2)]
            : [maximumStat, InstanceProtocol.LifeUpdate(id, current, pool: 2)];
    }

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
        GolemState.Golem golem, IReadOnlyDictionary<uint, int> player, double? at = null,
        GolemOwnerResistances? ownerResistances = null)
    {
        var catalog = SkillsFor(conn); var result = new List<GolemState.Ability>();
        double now = at ?? AreaTime(conn);
        var mods = GolemModifiers(conn, golem, player, now, ownerResistances).Effects;
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

    static void RefreshGolemLocked(ClientState conn, GolemState.Golem golem, IReadOnlyDictionary<uint, int> player, double? at = null)
    {
        double now = at ?? AreaTime(conn);
        golem.Auras = MinionAuraStats(conn, golem);
        var hash = new HashCode();
        hash.Add(CombatLifetime);
        hash.Add(GolemSummonedInPastEightSeconds(golem, now));
        hash.Add(SkeletonShieldChaos(conn, golem, player));
        foreach (var p in player.Where(p => (Stats.Name(p.Key) ?? "").Contains("minion", StringComparison.Ordinal)
            || (Stats.Name(p.Key) ?? "").Contains("golem", StringComparison.Ordinal)).OrderBy(p => p.Key)) hash.Add(p);
        if (Stats.Key("dexterity") is uint dexKey) hash.Add((dexKey, player.GetValueOrDefault(dexKey)));
        hash.Add(OwnerMaximumPowerCharges(player));
        int inheritedCoefficient = GolemStat(player, "minions_gain_x_percent_of_your_resistances");
        GolemOwnerResistances? ownerResistances = inheritedCoefficient == 0 ? null : GolemOwnerResistancesFor(conn);
        if (ownerResistances is { } resistanceHash) hash.Add(resistanceHash);
        foreach (var p in golem.Own.OrderBy(p => p.Key)) hash.Add(p);
        foreach (var p in golem.Auras.OrderBy(p => p.Key)) hash.Add(p);
        foreach (var p in golem.Gear.OrderBy(p => p.Key)) hash.Add(p);
        int input = hash.ToHashCode();
        if (input == golem.InputHash) return;
        golem.InputHash = input;
        golem.Player = new(player);
        var m = golem.Monster;
        var mods = GolemModifiers(conn, golem, player, now, ownerResistances);
        m.Mods = mods;
        var pools = GolemPools.Calculate(Monsters, m.Variety!, m.Level, golem, player, mods.Effects);
        if (pools.Fatal)
        {
            m.MaxLife = 1; m.Life = 1;
            golem.ShieldPool.SetMaximum(0);
            golem.ShieldPool.Empty();
            golem.Abilities = []; golem.Skills = [];
            golem.OwnerBuff.Clear();
            foreach (var hazard in golem.Hazards)
                if (hazard.Object is uint art) conn.Minions.OutputQueue.Add(new(InstanceProtocol.RemoveObject(art), null, WorldId: art, RemovesObject: true));
            golem.Hazards.Clear(); golem.Pulses.Clear();
            PublishEnergyShieldLocked(conn, golem);
            conn.Minions.OutputQueue.Add(new(InstanceEntities.StatsUpdate(golem.Id,
                [(StatKeys.MaximumLife, 1)]), "golem maximum life (fatal conversion transport)", golem.Id, m, WorldId: golem.Id));
            GolemDieLocked(conn, golem, now);
            if (conn.MinionOrigins.TryGetValue(golem.Id, out var fatalOrigin))
                conn.MinionOrigins[golem.Id] = fatalOrigin with
                {
                    Object = SyncLifeMaximum(fatalOrigin.Object, m, 1) with { Skills = [] },
                };
            return;
        }

        uint maximum = pools.MaximumLife;
        uint nextLife = (uint)Math.Clamp(m.Life, 1, maximum);
        bool maximumChanged = maximum != m.MaxLife;
        bool lifeChanged = nextLife != m.Life;
        m.MaxLife = maximum; m.Life = nextLife;
        if (maximumChanged)
            conn.Minions.OutputQueue.Add(new(InstanceEntities.StatsUpdate(golem.Id, [(StatKeys.MaximumLife, (int)maximum)]),
                "golem maximum life", golem.Id, m));
        if (maximumChanged || lifeChanged)
            conn.Minions.OutputQueue.Add(new([], "golem life", golem.Id, m, PlayerMinions.OutputKind.Life));
        if (conn.MinionOrigins.TryGetValue(golem.Id, out var origin))
            conn.MinionOrigins[golem.Id] = origin with { Object = SyncLifeMaximum(origin.Object, m, maximum) };
        golem.ShieldPool.SetMaximum(NativeEnergyShield(pools.MaximumEnergyShield));
        PublishEnergyShieldLocked(conn, golem);
        golem.Abilities = GolemAbilities(conn, m.Variety!, m.Level, golem, player, now, ownerResistances);
        golem.Skills = GolemNativeSkills(conn, m.Level, golem.Abilities);
        conn.Minions.OutputQueue.Add(new(InstanceEntities.PlayerSkillsChange(golem.Id, golem.Skills, []), "golem abilities updated", golem.Id, m, WorldId: golem.Id));
        conn.Minions.OutputQueue.Add(new(InstanceEntities.StatsUpdate(golem.Id,
            [(StatKeys.MovementVelocityPct, m.Mods.Effects.MovementPct)]), "golem speed", golem.Id, m, WorldId: golem.Id));
        if (conn.MinionOrigins.TryGetValue(golem.Id, out origin))
            conn.MinionOrigins[golem.Id] = origin with { Object = origin.Object with { Skills = golem.Skills } };
    }

    static MonsterObject SyncLifeMaximum(MonsterObject origin, SpawnedMonster monster, uint maximum)
    {
        IReadOnlyList<(uint Key, int Value)>? stats = origin.Stats;
        int baseLife = monster.Variety is { } variety ? Monsters.BaseLife(variety, monster.Level) ?? 100 : 100;
        if (maximum != baseLife)
            stats = [.. (stats ?? []).Where(stat => stat.Key != StatKeys.MaximumLife), (StatKeys.MaximumLife, (int)maximum)];
        else if (stats is not null)
        {
            var rebuilt = stats.Where(stat => stat.Key != StatKeys.MaximumLife).ToList();
            stats = rebuilt.Count == 0 ? null : rebuilt;
        }
        return origin with { Life = maximum, Stats = stats };
    }
}
