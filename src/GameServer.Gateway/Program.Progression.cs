using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using Progression = GameServer.Core.World.Progression;
using DamageModel = GameServer.Core.World.DamageModel;

partial class Program
{
    // The character's experience and passives from its save; returns its level. The saved experience decides the level: the
    // account's (the ticket's) is only a copy for the character list, which a failed update at a level-up leaves behind. A save
    // without experience (an older one) starts at the account's level. Passives beyond the level's points are dropped.
    static int LoadProgress(ClientState conn, int level)
    {
        var save = CharacterSaves.Load(CharacterName(conn));
        level = CharacterSaves.LevelOf(save, level);
        // Without the tree the saved passives can't be checked; dropping them would lose them at the next save.
        if (save?.Passives is { Count: > 0 } && !Passives.Loaded)
            throw new UnknownSavedItemException("allocated passives, but the passive tree (data/tables/PassiveSkills.json) is not loaded");
        // Gold is the account's (AccountStash.Gold): gold an older save kept with the character moves into the account, in one
        // transaction with the save letting go of it.
        if (save?.Gold is > 0 and uint oldGold)
        {
            uint now = Saves.Atomically(() =>
            {
                uint gold = PersonalStashOf(conn).AddGold(oldGold);
                CharacterSaves.TakeGold(CharacterName(conn));
                return gold;
            });
            Console.WriteLine($"[gateway] {CharacterName(conn)}: {oldGold} gold from the character save moved to the account, now {now}");
        }
        lock (conn.Monsters)
        {
            conn.Passives = [.. (save?.Passives ?? []).Where(n => Passives.Find(n) is { IsAscendancy: false })];
            conn.Waypoints.UnionWith(save?.Waypoints ?? []);
            conn.Visited.UnionWith(save?.Visited ?? []);
            conn.QuestFlags = [.. save?.QuestFlags ?? [], .. Quests.EveryCharacter];
            conn.ExtraPassivePoints = save?.ExtraPassivePoints ?? 0;
            conn.RefundPoints = save?.RefundPoints ?? 0;
            conn.Experience = save?.Experience ?? CharacterStats.ExperienceFor(level);
            TrimPassives(conn, level);
            conn.MasteryEffects = (save?.MasteryEffects ?? []).Where(kv => conn.Passives.Contains(kv.Key)
                && Passives.FindEffect(kv.Value) is { } effect && Passives.IsEffectFor(kv.Key, effect))
                .ToDictionary(kv => kv.Key, kv => kv.Value);
            conn.Pools = PoolsFor(conn, level);
            RestoreRememberedAuras(conn, save?.ActiveAuras ?? []);
            conn.ApplyMovementSpeed();
        }
        return level;
    }

    // The pools of the character's class at this level with its passives (call under the Monsters lock).
    // The character's stats from its allocated passives and the global mods of its active equipped items (ItemModStats),
    // by client stat key. Worn items whose requirements the class, the passives and the other active items do not meet are
    // inactive (ItemModStats.Inactive): remembered per character for the weapon profile and the gem skills.
    static Dictionary<uint, int> CharacterStatSources(ClientState conn, IEnumerable<ushort>? nodes = null, int? level = null)
    {
        var items = ItemsOf(conn);
        var passives = Passives.Stats(nodes ?? conn.Passives, conn.MasteryEffects);
        int Get(string name) => Stats.Key(name) is uint k ? passives.GetValueOrDefault(k) : 0;
        var c = Skills.ClassFor(conn.ClassRow);
        var (ps, pd, pi) = CharacterStats.Attributes(Get);
        int god = conn.God ? 1000 : 0;
        var worn = ItemModStats.EquipmentRows.SelectMany(items.Items)
            .Select(i => (i, t: Items.ByHash(i.Item.TypeHash)))
            .Select(x => x.t is { } t
                ? new ItemModStats.Worn(x.i.Id, x.i.Item, Items.Requirements(t).Str, Items.Requirements(t).Dex, Items.Requirements(t).Int,
                    ItemModStats.LevelRequirement(x.i.Item, t.DropLevel, Items.Mods))
                : new ItemModStats.Worn(x.i.Id, x.i.Item, 0, 0, 0, 0)).ToList();
        var before = InactiveItems.GetValueOrDefault(CharacterName(conn));
        var (inactive, active) = ItemModStats.ResolveEquipment(worn,
            (c.Strength + ps + god, c.Dexterity + pd + god, c.Intelligence + pi + god), level ?? conn.Pools.Level, Items.Mods, before);
        // Item activation depends on the item, not on socketed gems. Checking gems here used the previous
        // connection's/initial pools and disabled the entire item's stats and unrelated gems before pools existed.
        // Individual gem requirements are checked against the completed pools when socketing or casting.
        InactiveItems[CharacterName(conn)] = inactive;
        if (inactive.Count > 0 && (before is null || !before.SetEquals(inactive)))
            Console.WriteLine($"[gateway] {CharacterName(conn)}: inactive items (requirements not met): " +
                string.Join(", ", worn.Where(w => inactive.Contains(w.Id)).Select(w =>
                    $"{w.Id} {Items.ByHash(w.Item.TypeHash)?.Name} (str {w.Strength}, dex {w.Dexterity}, int {w.Intelligence}, level {w.Level})")));
        return ItemModStats.Plus(passives, ItemModStats.Global(active, Items.Mods, Stats));
    }

    static CharacterStats.Pools PoolsFor(ClientState conn, int level)
    {
        // The stat sources first: they decide which worn items are inactive, which the weapon profile reads.
        var sources = CharacterStatSources(conn, level: level);
        var weapons = WeaponProfileFor(conn);
        var bonus = PassiveBonus(sources, weapons);
        var gear = EquippedBaseDefences(conn);
        bonus = bonus with { Armour = bonus.Armour + gear.Armour, Evasion = bonus.Evasion + gear.Evasion,
            EnergyShield = bonus.EnergyShield + gear.EnergyShield };
        List<AuraCatalog.Aura> auras;
        lock (conn.Monsters) auras = [.. conn.Auras.Values];
        var aura = AuraCatalog.Bonus(auras, bonus.AuraEffectPct, Skills, Stats);
        bonus = bonus with
        {
            FireResistance = bonus.FireResistance + aura.FireResistance,
            ColdResistance = bonus.ColdResistance + aura.ColdResistance,
            LightningResistance = bonus.LightningResistance + aura.LightningResistance,
            ChaosResistance = bonus.ChaosResistance + aura.ChaosResistance,
            FireMaxResistance = bonus.FireMaxResistance + aura.FireMaxResistance,
            ColdMaxResistance = bonus.ColdMaxResistance + aura.ColdMaxResistance,
            LightningMaxResistance = bonus.LightningMaxResistance + aura.LightningMaxResistance,
            ChaosMaxResistance = bonus.ChaosMaxResistance + aura.ChaosMaxResistance,
            Accuracy = bonus.Accuracy + aura.Accuracy,
            Armour = bonus.Armour + aura.Armour,
            Evasion = bonus.Evasion + aura.Evasion,
            ArmourMorePct = aura.ArmourMorePct,
            EvasionMorePct = aura.EvasionMorePct,
            CriticalStrikeChancePct = bonus.CriticalStrikeChancePct + aura.CriticalStrikeChancePct,
            SpellCriticalStrikeChancePct = bonus.SpellCriticalStrikeChancePct + aura.SpellCriticalStrikeChancePct,
            AttackSpeedPct = bonus.AttackSpeedPct + aura.AttackSpeedPct,
            MovementVelocityPct = bonus.MovementVelocityPct + aura.MovementVelocityPct
        };
        if (conn.God)
            bonus = bonus with { Strength = bonus.Strength + 1000, Dexterity = bonus.Dexterity + 1000, Intelligence = bonus.Intelligence + 1000,
                Life = bonus.Life + 10_000, AttackSpeedPct = bonus.AttackSpeedPct + 1000 };
        var pools = CharacterStats.For(Skills.ClassFor(conn.ClassRow), level, bonus);
        pools = pools with
        {
            AttackBlockLucky = pools.AttackBlockLucky || conn.ForceAttackBlockLucky,
            SpellBlockLucky = pools.SpellBlockLucky || conn.ForceSpellBlockLucky,
            SpellSuppressionLucky = pools.SpellSuppressionLucky || conn.ForceSpellSuppressionLucky,
            SpellSuppressionUnlucky = pools.SpellSuppressionUnlucky || conn.ForceSpellSuppressionUnlucky,
            // WeaponTypes.dat gives the main hand's base critical chance (permyriad, e.g. 830 = 8.30%); unarmed is 0.
            AttackCriticalStrikeChance = (int)Math.Round(
                DamageModel.CriticalStrikeChance(weapons.Critical, pools.CriticalStrikeChancePct) * 100),
            SpellCriticalStrikeChance = (int)Math.Round(
                DamageModel.CriticalStrikeChance(DamageModel.BaseSpellCriticalStrikeChancePerMyriad,
                    pools.SpellCriticalStrikeChancePct) * 100)
        };
        int shieldBlock = weapons.Shield ? ShieldBaseBlock(conn) : 0;
        int rawAttack = pools.BlockChanceUncapped + shieldBlock;
        double blockFactor = (1 + bonus.BlockChanceInc / 100.0) * (1 + bonus.BlockChanceMore / 100.0);
        int totalAttack = (int)Math.Round(Math.Max(0, rawAttack) * blockFactor);
        int totalSpell = (int)Math.Round(Math.Max(0, pools.SpellBlockChance) * blockFactor);
        pools = pools with
        {
            BlockChanceBase = shieldBlock,
            BlockChanceUncapped = Math.Max(0, totalAttack),
            BlockChance = Math.Min(Math.Max(0, totalAttack), pools.BlockChanceMax),
            SpellBlockChance = Math.Min(Math.Max(0, totalSpell), pools.SpellBlockChanceMax)
        };
        // "+1% Chance to Block Spell Damage per 5% Chance to Block Attack Damage": the attack value is the final capped
        // chance, including the shield's base. The effect stacks additively and is capped by the spell block maximum.
        if (pools.SpellBlockPer5AttackBlock != 0 && pools.BlockChance > 0)
            pools = pools with
            {
                SpellBlockChance = Math.Min(pools.SpellBlockChance + pools.BlockChance / 5 * pools.SpellBlockPer5AttackBlock,
                    pools.SpellBlockChanceMax)
            };
        // Versatile Combatant: +2% spell block for each 1% of attack block above its (keystone-reduced) maximum.
        if (pools.VersatileCombatant)
        {
            int overcap = Math.Max(0, pools.BlockChanceUncapped - pools.BlockChanceMax);
            if (overcap > 0)
                pools = pools with
                {
                    SpellBlockChance = Math.Min(pools.SpellBlockChance + 2 * overcap, pools.SpellBlockChanceMax)
                };
        }
        return pools;
    }

    // What the allocated passives add to the pools: attributes (base_* and additional_*, all attributes and the pairs),
    // flat and increased life, mana and accuracy, flat and increased armour and evasion, energy shield and melee physical
    // damage, the base resistances (base_resist_all_elements_% applies to fire, cold and lightning), the shield-specific
    // block nodes when a shield is equipped, critical strike chance and multiplier, and reservation efficiency and aura
    // effect.
    static CharacterStats.Bonus PassiveBonus(Dictionary<uint, int> stats, WeaponProfile weapons)
    {
        int Get(string name) => Stats.Key(name) is uint k ? stats.GetValueOrDefault(k) : 0;
        int allRes = Get("base_resist_all_elements_%");
        // The game's block stats are conditional on the equipped weapons:
        // - dual_wield_or_shield_block_%: dual wielding or a shield;
        // - block_while_dual_wielding_% / _claws: dual wielding (the latter with two claws);
        // - staff_block_% / additional_staff_block_%: a staff;
        // - shield_block_% / block_chance_%_while_holding_shield: a shield.
        int blockWhileDualOrShield = weapons is { Shield: true } or { DualWield: true } ? Get("dual_wield_or_shield_block_%") : 0;
        int shieldBlock = weapons.Shield ? Get("shield_block_%") + Get("block_chance_%_while_holding_shield") : 0;
        int staffBlock = weapons.Staff ? Get("staff_block_%") + Get("additional_staff_block_%") : 0;
        int dualBlock = weapons.DualWield
            ? Get("block_while_dual_wielding_%") + (weapons.DualClaws ? Get("block_while_dual_wielding_claws_%") : 0) + 20
            : 0; // 20 is the inherent dual-wield block chance (PoB Misc.lua)
        int bowBlock = weapons.Bow ? Get("with_bow_additional_block_%") : 0;
        // Spell block has the matching weapon-specific stats; the staff-or-shield stat is shared.
        int staffOrShieldSpell = weapons is { Staff: true } or { Shield: true } ? Get("spell_block_chance_%_while_holding_staff_or_shield") : 0;
        int shieldSpellBlock = weapons.Shield ? Get("shield_spell_block_%") + Get("block_spells_chance_%_while_holding_shield") : 0;
        int staffSpellBlock = weapons.Staff ? Get("spell_block_with_staff_%") : 0;
        int dualSpellBlock = weapons.DualWield ? Get("spell_block_while_dual_wielding_%") : 0;
        int bowSpellBlock = weapons.Bow ? Get("spell_block_with_bow_%") : 0;
        // Attack speed passives, conditional on the equipped weapon shape as in PoB's ModFlags: generic, melee,
        // one-/two-handed melee, all one-handed, the weapon families, shields and dual wielding. Minion, totem and
        // timed/conditional stats are not player attack speed here.
        int attackSpeed = Get("attack_speed_+%") + Get("attack_and_cast_speed_+%")
            + (weapons.Melee ? Get("melee_attack_speed_+%") : 0)
            + (weapons.OneHandMelee ? Get("one_handed_melee_attack_speed_+%") : 0)
            + (weapons.TwoHandMelee ? Get("two_handed_melee_attack_speed_+%") : 0)
            + (weapons.OneHand ? Get("one_handed_attack_speed_+%") : 0)
            + (weapons.Unarmed ? Get("unarmed_melee_attack_speed_+%") : 0)
            + (weapons.Shield ? Get("attack_speed_+%_while_holding_shield") + Get("shield_attack_speed_+%") : 0)
            + (weapons.DualWield ? Get("attack_speed_while_dual_wielding_+%") : 0)
            + (weapons.Axe ? Get("axe_attack_speed_+%") : 0)
            + (weapons.Sword ? Get("sword_attack_speed_+%") : 0)
            + (weapons.Mace ? Get("mace_attack_speed_+%") : 0)
            + (weapons.Claw ? Get("claw_attack_speed_+%") : 0)
            + (weapons.Dagger ? Get("dagger_attack_speed_+%") : 0)
            + (weapons.Wand ? Get("wand_attack_speed_+%") : 0)
            + (weapons.Bow ? Get("bow_attack_speed_+%") : 0)
            + (weapons.Staff ? Get("staff_attack_speed_+%") : 0);
        // PoB Misc.lua: 10% more attack speed while dual wielding; unarmed attacks count as dual wielding. The main hand's
        // local attack speed scales the weapon's own rate before the global increases, so it multiplies like a more.
        int attackSpeedMore = (int)Math.Round(((weapons.DualWield || weapons.Unarmed ? 1.10 : 1.0) * (1 + weapons.LocalAttackSpeedPct / 100.0) - 1) * 100);
        // Unconditional critical strike chance and multiplier passives: generic, attack/melee/weapon-shape and spell.
        // Timed or enemy-state conditions are not modelled.
        int criticalChance = Get("critical_strike_chance_+%") + Get("attack_critical_strike_chance_+%")
            + (weapons.Melee ? Get("melee_critical_strike_chance_+%") : 0)
            + (weapons.OneHandMelee ? Get("one_handed_melee_critical_strike_chance_+%") : 0)
            + (weapons.TwoHandMelee ? Get("two_handed_melee_critical_strike_chance_+%") : 0)
            + (weapons.DualWield ? Get("critical_strike_chance_while_dual_wielding_+%") : 0)
            + (weapons.Shield ? Get("critical_strike_chance_while_wielding_shield_+%") : 0)
            + (weapons.Axe ? Get("axe_critical_strike_chance_+%") : 0)
            + (weapons.Sword ? Get("sword_critical_strike_chance_+%") : 0)
            + (weapons.Mace ? Get("mace_critical_strike_chance_+%") : 0)
            + (weapons.Claw ? Get("claw_critical_strike_chance_+%") : 0)
            + (weapons.Dagger ? Get("dagger_critical_strike_chance_+%") : 0)
            + (weapons.Wand ? Get("wand_critical_strike_chance_+%") : 0)
            + (weapons.Bow ? Get("bow_critical_strike_chance_+%") + Get("global_critical_strike_chance_+%_while_holding_bow") : 0)
            + (weapons.Staff ? Get("staff_critical_strike_chance_+%") + Get("global_critical_strike_chance_+%_while_holding_staff") : 0);
        int spellCriticalChance = Get("critical_strike_chance_+%") + Get("spell_critical_strike_chance_+%");
        int criticalMultiplier = Get("base_critical_strike_multiplier_+")
            + (weapons.Melee ? Get("melee_critical_strike_multiplier_+") + Get("melee_weapon_critical_strike_multiplier_+") : 0)
            + (weapons.OneHandMelee ? Get("one_handed_melee_critical_strike_multiplier_+") : 0)
            + (weapons.TwoHandMelee ? Get("two_handed_melee_critical_strike_multiplier_+") : 0)
            + (weapons.DualWield ? Get("critical_strike_multiplier_while_dual_wielding_+") : 0)
            + (weapons.Axe ? Get("axe_critical_strike_multiplier_+") : 0)
            + (weapons.Sword ? Get("sword_critical_strike_multiplier_+") : 0)
            + (weapons.Mace ? Get("mace_critical_strike_multiplier_+") : 0)
            + (weapons.Claw ? Get("claw_critical_strike_multiplier_+") : 0)
            + (weapons.Dagger ? Get("dagger_critical_strike_multiplier_+") + Get("critical_strike_multiplier_with_dagger_+") : 0)
            + (weapons.Wand ? Get("wand_critical_strike_multiplier_+") : 0)
            + (weapons.Bow ? Get("bow_critical_strike_multiplier_+") + Get("global_critical_strike_multiplier_+_while_holding_bow") : 0)
            + (weapons.Staff ? Get("staff_critical_strike_multiplier_+") + Get("global_critical_strike_multiplier_+_while_holding_staff") : 0);
        int spellCriticalMultiplier = Get("base_critical_strike_multiplier_+")
            + Get("base_spell_critical_strike_multiplier_+") + Get("spell_critical_strike_multiplier_+");
        // Accuracy passives: generic flat and increased, the weapon-specific flat and increased stats, and the Accuracy
        // Mastery's conversions. Conditional stats (nearby ally, marked enemy, low life, frenzy charges, unique/close
        // enemies) are not modelled here. Unarmed attacks count as dual wielding, as they do for attack speed.
        int accuracyFlat = Get("accuracy_rating") + weapons.LocalAccuracy
            + (weapons.Sword ? Get("sword_accuracy_rating") : 0)
            + (weapons.Axe ? Get("axe_accuracy_rating") : 0)
            + (weapons.Mace ? Get("mace_accuracy_rating") : 0)
            + (weapons.Claw ? Get("claw_accuracy_rating") : 0)
            + (weapons.Dagger ? Get("dagger_accuracy_rating") : 0)
            + (weapons.Wand ? Get("wand_accuracy_rating") : 0)
            + (weapons.Staff ? Get("staff_accuracy_rating") : 0)
            + (weapons.Bow ? Get("bow_accuracy_rating") : 0);
        int accuracyPct = Get("accuracy_rating_+%")
            + ((weapons.DualWield || weapons.Unarmed) ? Get("accuracy_rating_while_dual_wielding_+%") : 0)
            + (weapons.OneHandMelee ? Get("one_handed_melee_accuracy_rating_+%") : 0)
            + (weapons.TwoHandMelee ? Get("two_handed_melee_accuracy_rating_+%") : 0)
            + (weapons.Axe ? Get("axe_accuracy_rating_+%") : 0)
            + (weapons.Sword ? Get("sword_accuracy_rating_+%") : 0)
            + (weapons.Mace ? Get("mace_accuracy_rating_+%") : 0)
            + (weapons.Claw ? Get("claw_accuracy_rating_+%") : 0)
            + (weapons.Dagger ? Get("dagger_accuracy_rating_+%") : 0)
            + (weapons.Wand ? Get("wand_accuracy_rating_+%") : 0)
            + (weapons.Staff ? Get("staff_accuracy_rating_+%") : 0)
            + (weapons.Bow ? Get("bow_accuracy_rating_+%") : 0);
        int allMax = Get("additional_maximum_all_resistances_%"), allElemMax = Get("additional_maximum_all_elemental_resistances_%");
        // Reservation efficiency: generic efficiency applies to both pools; the mana/life-specific stats only to theirs.
        // Aura effect is the generic non-curse/aura effect; the Reservation mastery's self-aura effect is included.
        int manaReservation = Get("base_mana_reservation_efficiency_+%") + Get("base_reservation_efficiency_+%")
            + Get("mana_reservation_efficiency_+%") + Get("reservation_efficiency_+%");
        int lifeReservation = Get("base_life_reservation_efficiency_+%") + Get("base_reservation_efficiency_+%")
            + Get("life_reservation_efficiency_+%") + Get("reservation_efficiency_+%");
        int auraEffect = Get("non_curse_aura_effect_+%") + Get("aura_effect_+%")
            + Get("aura_effect_on_self_from_your_skills_+%");
        int auraArea = Get("base_aura_area_of_effect_+%");
        // Defences: the tree's flat armour/evasion and their increased modifiers. "Defences" and the combined armour and
        // evasion increase apply to both ratings, as in PoB's calcLib.mod.
        int defencesPct = Get("global_defences_+%") + Get("combined_global_defences_+%")
            + Get("evasion_and_physical_damage_reduction_rating_+%")
            + Get("combined_armour_and_evasion_rating_+%");
        int armourPct = Get("physical_damage_reduction_rating_+%") + defencesPct;
        int evasionPct = Get("evasion_rating_+%") + defencesPct;
        var (strength, dexterity, intelligence) = CharacterStats.Attributes(Get);
        return new CharacterStats.Bonus(
            Strength: strength, Dexterity: dexterity, Intelligence: intelligence,
            Life: Get("base_maximum_life"), LifePct: Get("maximum_life_+%"), Mana: Get("base_maximum_mana"), ManaPct: Get("maximum_mana_+%"),
            Armour: Get("base_physical_damage_reduction_rating"), ArmourPct: armourPct,
            Evasion: Get("base_evasion_rating"), EvasionPct: evasionPct,
            SharedDefencesPct: defencesPct, IronReflexes: Get("keystone_iron_reflexes") != 0,
            EnergyShield: Get("base_maximum_energy_shield"),
            EnergyShieldPct: Get("maximum_energy_shield_+%") + Get("global_defences_+%") + Get("combined_global_defences_+%"),
            MeleePhysicalDamagePct: Get("melee_physical_damage_+%"),
            FireResistance: allRes + Get("base_fire_damage_resistance_%"),
            ColdResistance: allRes + Get("base_cold_damage_resistance_%"),
            LightningResistance: allRes + Get("base_lightning_damage_resistance_%"),
            ChaosResistance: Get("base_chaos_damage_resistance_%"),
            FireMaxResistance: allMax + allElemMax + Get("base_maximum_fire_damage_resistance_%"),
            ColdMaxResistance: allMax + allElemMax + Get("base_maximum_cold_damage_resistance_%"),
            LightningMaxResistance: allMax + allElemMax + Get("base_maximum_lightning_damage_resistance_%"),
            ChaosMaxResistance: allMax + Get("base_maximum_chaos_damage_resistance_%"),
            SpellSuppressionChance: Get("base_spell_suppression_chance_%"),
            SpellSuppressionEffect: Get("base_spell_damage_%_suppressed"),
            SpellSuppressionLucky: Get("spell_suppression_chance_is_lucky") != 0,
            SpellSuppressionUnlucky: Get("spell_suppression_chance_is_unlucky") != 0,
            SpellSuppressionWhileFullLife: Get("spell_suppression_chance_%_while_on_full_life"),
            SpellSuppressionWhileMoving: Get("spell_suppression_chance_%_while_moving"),
            SpellSuppressionIfHitRecently: Get("spell_suppression_chance_%_if_enemy_hit_recently"),
            MovementVelocityPct: Get("base_movement_velocity_+%"),
            BlockChance: Get("additional_block_%") + Get("attack_block_%") + Get("monster_base_block_%")
                + blockWhileDualOrShield + shieldBlock + staffBlock + dualBlock + bowBlock,
            SpellBlockChance: Get("base_spell_block_%") + staffOrShieldSpell + shieldSpellBlock + staffSpellBlock + dualSpellBlock + bowSpellBlock,
            BlockChanceMax: Get("maximum_block_%"), SpellBlockChanceMax: Get("base_maximum_spell_block_%"),
            BlockChanceInc: Get("block_chance_+%"), BlockChanceMore: Get("block_chance_+%_final"),
            SpellBlockPer5AttackBlock: Get("spell_block_%_per_5_attack_block"),
            LifeGainedOnBlock: Get("life_gained_on_block"), ManaGainedOnBlock: Get("mana_gained_on_block"),
            VersatileCombatant: Get("keystone_versatile_combatant") != 0,
            AttackBlockLucky: Get("base_block_luck") != 0 || Get("attack_block_luck") != 0 || Get("base_attack_block_luck") != 0,
            SpellBlockLucky: Get("base_block_luck") != 0 || Get("spell_block_luck") != 0 || Get("base_spell_block_luck") != 0,
            AttackSpeedPct: attackSpeed, AttackSpeedMorePct: attackSpeedMore,
            CriticalStrikeChancePct: criticalChance, SpellCriticalStrikeChancePct: spellCriticalChance,
            CriticalStrikeMultiplier: criticalMultiplier, SpellCriticalStrikeMultiplier: spellCriticalMultiplier,
            Accuracy: accuracyFlat, AccuracyPct: accuracyPct,
            AccuracyPerLevel: Get("accuracy_rating_per_level"),
            DexterityAccuracyTriple: Get("dexterity_accuracy_bonus_grants_accuracy_rating_+3_per_dexterity_instead") != 0,
            AccuracyEqualsIntelligence: Get("gain_accuracy_rating_equal_to_intelligence") != 0,
            ManaReservationEfficiencyPct: manaReservation, LifeReservationEfficiencyPct: lifeReservation,
            AuraEffectPct: auraEffect, AuraAreaPct: auraArea);
    }

    // The character's passive skill points (one per level after the first, plus the extra points from
    // /add_passive_points; the client computes the same from the level in 0x33 and its extra_skill_points field), the
    // allocated nodes and the selected mastery effects. The client allows that many nodes plus the points the allocated
    // nodes grant themselves.
    static (ushort Points, ushort RefundPoints, List<ushort> Nodes, Dictionary<ushort, ushort> Masteries) PassiveState(ClientState conn)
    {
        lock (conn.Monsters)
            return ((ushort)Math.Min(ushort.MaxValue, Progression.PassivePoints(conn.Pools.Level) + conn.ExtraPassivePoints),
                conn.RefundPoints, [.. conn.Passives], new(conn.MasteryEffects));
    }

    // Drops the last allocated passives beyond the level's points (call under the Monsters lock).
    static void TrimPassives(ClientState conn, int level)
    {
        int points = Progression.PassivePoints(level) + conn.ExtraPassivePoints;
        while (conn.Passives.Count > 0 && conn.Passives.Count > points + Passives.PointsGranted(conn.Passives))
            conn.Passives.RemoveAt(conn.Passives.Count - 1);
    }

    // The character's passive skill tree (0x33): its level (from which the client counts the points), its class (the
    // tree's start) and the allocated nodes.
    static async Task SendPassives(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
    {
        var (bytes, label) = PassivesMessage(conn);
        await Send(remote, stream, ciphers, bytes, label);
    }

    static (byte[] Bytes, string Label) PassivesMessage(ClientState conn)
    {
        var (points, refundPoints, nodes, masteries) = PassiveState(conn);
        int unspent = points + Passives.PointsGranted(nodes) - nodes.Count;
        return (InstanceEntities.CharacterPassives((byte)conn.Pools.Level, (byte)Skills.ClassFor(conn.ClassRow).IntegerId, nodes,
                questPoints: conn.ExtraPassivePoints, refundPoints: refundPoints, masteryEffects: masteries),
            $"0x0033 passive skill tree: level {conn.Pools.Level}, {points} skill point(s) ({conn.ExtraPassivePoints} extra), " +
            $"{refundPoints} refund point(s), {nodes.Count} allocated, {unspent} unspent");
    }

    // Allocates (or, with allocate false, refunds) passive nodes: checked against the tree's nodes and the character's
    // points, then saved, applied to the stats and sent back (0x3d). The client asks only for nodes connected to its tree.
    // A gold respec (goldRespec) refunds only allocated nodes and charges their cost (PassiveTree.GoldRespecCost); a
    // refund-point refund (refundPoints) spends one saved point per node. /reset_passives sets neither.
    static async Task<string?> ChangePassives(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        IReadOnlyList<ushort> nodes, bool allocate, string why, ushort? masteryEffect = null, bool goldRespec = false,
        bool refundPoints = false)
    {
        string? error = null;
        List<ushort> now;
        Dictionary<ushort, ushort> effectsNow;
        string? effectLabel = null;
        long cost = 0;
        uint gold = 0;
        ushort refundsNow = 0;
        int refundsSpent = 0;
        lock (conn.Monsters)
        {
            var next = new List<ushort>(conn.Passives);
            var effects = new Dictionary<ushort, ushort>(conn.MasteryEffects);
            foreach (var n in nodes)
            {
                if (Passives.Find(n) is not { } node) { error = $"unknown passive {n}"; break; }
                if (node.IsAscendancy) { error = $"{node.Name} ({n}) is an ascendancy passive"; break; }
                if (masteryEffect is { } raw)
                {
                    if (!allocate) { error = "a mastery effect cannot be refunded on its own"; break; }
                    var effect = Passives.EffectsFor(n).FirstOrDefault(e => e.Hash == raw || e.Row == raw);
                    if (effect is null) { error = $"{node.Name} ({n}) has no mastery effect {raw}"; break; }
                    if (!next.Contains(n)) next.Add(n);
                    effects[n] = effect.Hash;
                    effectLabel = $"{effect.Id} ({effect.Hash})";
                }
                else if (allocate) { if (!next.Contains(n)) next.Add(n); }
                else if ((goldRespec || refundPoints) && !next.Contains(n)) { error = $"{node.Name} ({n}) is not allocated"; break; }
                else
                {
                    next.Remove(n);
                    effects.Remove(n);
                    if (refundPoints) refundsSpent++;
                }
            }
            if (error is null && next.Count > Progression.PassivePoints(conn.Pools.Level) + conn.ExtraPassivePoints + Passives.PointsGranted(next))
                error = $"not enough passive skill points ({Progression.PassivePoints(conn.Pools.Level) + conn.ExtraPassivePoints} at level {conn.Pools.Level}, {next.Count} wanted)";
            if (error is null && refundPoints && refundsSpent > conn.RefundPoints)
                error = $"not enough refund points ({refundsSpent} needed, {conn.RefundPoints} held)";
            if (goldRespec) cost = Passives.GoldRespecCost(nodes, conn.Pools.Level);
            now = next;
            effectsNow = effects;
            refundsNow = (ushort)(conn.RefundPoints - (refundPoints && error is null ? refundsSpent : 0));
        }
        // Saved before it applies, in one transaction: the gold (paid last, from the account's gold: nothing is taken when the
        // change is refused), the passives, their mastery effects and the refund points.
        if (error is null)
            error = Saves.Atomically(() =>
            {
                if (goldRespec)
                {
                    if (PersonalStashOf(conn).TrySpendGold(cost) is not uint left) return $"not enough gold ({cost} needed, {PersonalStashOf(conn).Gold} held)";
                    gold = left;
                }
                CharacterSaves.SetPassives(CharacterName(conn), now, effectsNow, refundPoints ? refundsNow : null);
                return null;
            });
        lock (conn.Monsters)
        {
            if (error is null)
            {
                conn.Passives = now;
                conn.MasteryEffects = effectsNow;
                conn.RefundPoints = refundsNow;
                conn.Pools = PoolsFor(conn, conn.Pools.Level);
                conn.ApplyMovementSpeed();
                conn.PlayerLife = Math.Min(conn.PlayerLife, conn.PlayerMaxLife);
                conn.PlayerMana = Math.Min(conn.PlayerMana, (uint)UnreservedMana(conn));
            }
            now = [.. conn.Passives];
            effectsNow = new(conn.MasteryEffects);
            refundsNow = conn.RefundPoints;
        }
        Console.WriteLine($"[gateway] {remote} passives {(allocate ? "allocate" : "refund")} [{string.Join(", ", nodes.Select(n => $"{n} {Passives.Find(n)?.Name}"))}]" +
            (effectLabel is null ? "" : $" mastery {effectLabel}") + $" ({why}): " +
            (error ?? $"{now.Count} allocated, life {conn.Pools.Life}, mana {conn.Pools.Mana}" +
                (goldRespec ? $", {cost} gold paid, {gold} left" : "") +
                (refundPoints ? $", {refundsSpent} refund point(s) spent, {refundsNow} left" : "")));
        if (error is null)
        {
            if (goldRespec)
            {
                await Send(remote, stream, ciphers, InstanceEntities.Gold(gold), $"0x02a9 gold: {gold} (respec, {cost} paid)");
            }
            await SendCharacterStats(remote, stream, ciphers, conn, "passives");
            await SendPlayerMana(remote, stream, ciphers, conn, $"0x01b1 player mana {conn.PlayerMana} (passives)");
        }
        // Sent either way: on a refusal it puts the client's tree back to the server's.
        await SendPassives(remote, stream, ciphers, conn);
        return error;
    }

    // Experience from a kill: added (up to the last level), sent as the Player component's experience (0x2ba; the client
    // derives the level and plays the level up from it), saved, and at a new level the level's stats, life and passive
    // points (ApplyLevel) and the account's level (Auth set_level, for the character list).
    static async Task GainExperience(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, uint gain, string why)
    {
        // Added to the character's total (an older connection's or another task's gain is never written over), kept in memory
        // and saved every few seconds; a new level is saved at once.
        uint start;
        int oldLevel;
        lock (conn.Monsters) (start, oldLevel) = (conn.Experience, conn.Pools.Level);
        var (before, after) = CharacterSaves.AddExperience(CharacterName(conn), start, xp => Progression.AddExperience(xp, gain, CharacterStats.LevelStarts));
        if (after == before) return;
        lock (conn.Monsters) conn.Experience = Math.Max(conn.Experience, after);
        int level = Progression.LevelFor(after, CharacterStats.LevelStarts);
        await Send(remote, stream, ciphers, InstanceEntities.PlayerExperienceUpdate(PlayerId, after),
            $"0x02ba player Player: experience {before} + {after - before} = {after} (level {level}; {why})");
        if (level <= oldLevel) return;
        CharacterSaves.SaveExperience(CharacterName(conn));   // a new level is saved at once, before the account hears of it
        var r = await InstanceControl.CallAsync(new ControlRequest { Op = "set_level", Ticket = conn.Ticket, Level = level });
        if (!r.Ok) Console.WriteLine($"[gateway] {remote} level {level} not saved to the account: {r.Error}");
        await ApplyLevel(remote, stream, ciphers, conn, level);
    }

    // A kill's experience goes to the gems too: every one socketed in an equipped item, in either weapon set, gains 10% of the
    // player's gain (support gems too, a gem in an inactive item, at its last level or over its cap nothing), up to the
    // threshold of the first level whose requirement the character does not meet — the gem's level stalls there, as the
    // game's does (the client would level the gem to whatever its experience says, so the experience stops just below that
    // threshold; a gem already past it, after /set_level down, keeps its level and experience and is disabled instead) and
    // the last level's threshold. Written into the gem item inside the socket list — the equipped
    // item's own component has no skill gems. Sent as the socket's equipment row 0x00bf item change, the same item id removed
    // and added again (the replace idiom: an addition of a known id alone is dropped as a duplicate, so the client would keep
    // the stale gem), so the client's gem XP bar and tooltip follow; the next save keeps it.
    static async Task GainGemExperience(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, uint gain)
    {
        int share = (int)Math.Min(gain / 10, int.MaxValue);
        if (share == 0) return;
        var items = ItemsOf(conn);
        var changed = items.ChangeSocketedGems((itemId, gem) =>
        {
            if (IsInactive(conn, itemId) || Items.ByHash(gem.TypeHash) is not { } baseType || Items.Gem(baseType) is not { } type) return null;
            // -1 is a corrupted gem over its cap (GemType.Level), and at its last level's threshold a gem gains no more.
            int lastLevel = Math.Min(Skills.MaxLevelAt(type.Variant(gem.GemVariant).Effect, conn.Pools.Level), type.MaxLevel);
            int cap = lastLevel < type.MaxLevel ? type.ExperienceFor(lastLevel + 1) - 1 : type.ExperienceFor(type.MaxLevel);
            if (gem.GemExperience < 0 || gem.GemExperience >= cap) return null;
            return gem with { GemExperience = (int)Math.Min((long)gem.GemExperience + share, cap) };
        });
        bool leveled = false;
        foreach (var (before, after) in changed.SelectMany(c => c.Gems))
        {
            if (Items.GemLevel(after) > Items.GemLevel(before))
            {
                leveled = true;
                Console.WriteLine($"[gateway] {remote} {CharacterName(conn)}'s {Items.GemName(after)} levels to {Items.GemLevel(after)}");
            }
            else
                Console.WriteLine($"[gateway] {remote} {CharacterName(conn)}'s {Items.GemName(after)} gains experience " +
                    $"+{after.GemExperience - before.GemExperience} ({after.GemExperience}, level {Items.GemLevel(after)})");
        }
        // One change per row, each item in it once with all its gems' experience.
        foreach (var row in changed.GroupBy(c => c.Row))
            await Send(remote, stream, ciphers, InstanceItems.InventoryChange(PlayerInventories.InventoryId(row.Key), items.NextRevision(row.Key),
                [.. row.Select(c => c.Item.Id)], [.. row.Select(c => c.Item)]),
                $"0x00bf inventory {row.Key}: gem experience +{share} ({string.Join(", ", row.Select(c => $"{c.Item.Id:x} {c.Gems.Count} gem(s)"))})");
        // A gem's level's costs, damage and reservation live with the skills (the 0x20 Actor update), not the item.
        if (leveled) await SendSkills(remote, stream, ciphers, conn);
    }
}
