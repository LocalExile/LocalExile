using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using Progression = GameServer.Core.World.Progression;
using DamageModel = GameServer.Core.World.DamageModel;
using DamagePacket = GameServer.Core.World.DamagePacket;

partial class Program
{
    // The player's granted skills: the default attack with the unarmed weapon's stats and every stat the server set on the
    // player since (a skill's stats include the character's; the client reads the skill's). A gem whose requirements the
    // character does not meet makes its item inactive (CharacterStatSources) like a worn item the character is too low for,
    // so GemSkillEffects leaves its skill out: the client has nothing to cast. A use that arrives anyway (a stale client) is
    // refused (GemDisabled).
    static IReadOnlyList<GrantedSkill> PlayerSkills(ClientState conn)
    {
        var player = FinalPlayerStats(conn).ToList();
        return [.. Skills.PlayerDefaults(player), .. GemSkills(conn.Character, player)];
    }

    // The player's stats as the server sets them: what /set_stat set, then the attack's final values computed from them
    // (the client derives nothing itself: 11-skills.md §4).
    // FinalPlayerStats as a full 0x1b3, which only sets the stats it lists: every stat sent before that is gone now (a
    // stat only an unequipped item or a refunded passive gave) goes with 0, so the client does not keep its old value.
    static List<(uint, int)> PlayerStatsWithCleared(ClientState conn)
    {
        var stats = FinalPlayerStats(conn).ToList();
        var now = stats.Select(s => s.Item1).ToHashSet();
        lock (conn.Monsters)
        {
            stats.AddRange(conn.SentStatKeys.Where(k => !now.Contains(k)).Select(k => (k, 0)));
            conn.SentStatKeys = now;
        }
        return stats;
    }

    static IEnumerable<(uint, int)> FinalPlayerStats(ClientState conn)
    {
        var stats = CharacterPlayerStats(conn);
        // Quivers are gated by main_hand_weapon_type on the client, independently of the model's weapon stance. Only the
        // bow's value is known, so other weapons send nothing (a value /set_stat set stays); taking the bow off clears the
        // 9 sent before through PlayerStatsWithCleared.
        var mainHand = ActiveMainHand(conn);
        if (Stats.Key("main_hand_weapon_type") is uint weaponTypeKey && WeaponTypeStats.BowType(mainHand?.Type?.Class) is int bowType)
            stats.TryAdd(weaponTypeKey, bowType);
        // Both hands' weapon stats. A dual-wielded attack swings the off hand as well, and the client sets that strike's
        // weapon up from the off hand's own final stats (it derives nothing itself, 11-skills.md §4).
        foreach (var (k, v) in WeaponStats(conn, WeaponOf(conn, mainHand), stats)) stats[k] = v;
        foreach (var (k, v) in CosmeticStats(conn)) stats.TryAdd(k, v);
        return stats.Select(kv => (kv.Key, kv.Value));
    }

    // The main hand's final weapon stats and, when the off hand holds a weapon, the off hand's too (off_hand_… instead of
    // main_hand_…). Until issue #167 the Gateway sent no off-hand stat at all, so the second weapon of a dual-wielded attack
    // had no attack time, damage or reach on the client; the off-hand animation of a Holy Strike on the ground then ran
    // with a null animation set and the next action crashed (0x141c988c1, AnimationController+0x180 = 0). Frida 2026-10-25:
    // while it creates the attack action the client reads off_hand_minimum/maximum_attack_distance.
    // The off hand's attack speed is the main hand's final value without the main weapon's own local attack speed (which
    // CharacterStats folds into it): the off-hand weapon's local attack speed is already in its attack time (WeaponIn).
    static List<(uint, int)> WeaponStats(ClientState conn, SkillCatalog.Unarmed weapon, IReadOnlyDictionary<uint, int> stats)
    {
        var conversion = DamageStats.Conversion(stats, Stats, attack: true);
        var list = weapon.Stats(stats, AttackHit(weapon, stats).Convert(conversion)).ToList();
        if (OffhandWeaponFor(conn) is { } off)
            list.AddRange(off.OffStats(stats, AttackHit(off, stats).Convert(conversion), WeaponProfileFor(conn).LocalAttackSpeedPct));
        return list;
    }

    // What /set_stat set, then the class's attributes and pools and the passives' stats for the stats it did not set.
    static Dictionary<uint, int> CharacterPlayerStats(ClientState conn)
    {
        Dictionary<uint, int> stats;
        lock (conn.Monsters) stats = new(conn.PlayerStats);
        foreach (var (k, v) in BaseStats(conn)) stats.TryAdd(k, v);
        // The passives' conditional attack speed is already one final value in Pools; /set_stat's raw attack-speed stats
        // stay additive. main_hand_attack_speed_+% is the total the client swings with, so Unarmed reads only it.
        int devAttackSpeed = 0;
        lock (conn.Monsters)
            foreach (uint key in StatKeys.AttackSpeedInputs)
                devAttackSpeed += conn.PlayerStats.GetValueOrDefault(key);
        stats[StatKeys.MainHandAttackSpeedPct] = conn.Pools.AttackSpeedPct + devAttackSpeed;
        // The character panel's sums of base forms (PanelStats); a stat /set_stat set keeps its value.
        HashSet<uint> devSet;
        lock (conn.Monsters) devSet = [.. conn.PlayerStats.Keys];
        int Get(string name) => Stats.Key(name) is uint k ? stats.GetValueOrDefault(k) : 0;
        var (mainLocal, offLocal) = HandLocalStats(conn);
        foreach (var (stat, value) in PanelStats.Compute(Get, n => mainLocal.GetValueOrDefault(n), n => offLocal.GetValueOrDefault(n))
            .Concat(PanelStats.FromPools(Get, conn.Pools))
            .Concat(PanelStats.Recovery(Get, conn.Pools.Life, conn.Pools.Mana))
            .Concat(new[] { ("energy_shield_recovery_per_minute", (int)Math.Round(EnergyShield.PredictionPerMinute(conn.Pools.EnergyShield, Get, conn.EnergyShield.Recharging))),
                ("energy_shield_recharge_rate_per_minute", (int)Math.Round(EnergyShield.RechargePerMinute(conn.Pools.EnergyShield, Get))),
                ("energy_shield_recharge_active", 0) })
            .Concat(ChargePanelStats.Compute(Get)))
            if (Stats.Key(stat) is uint key && !devSet.Contains(key)) stats[key] = value;
        // The Offence tab's per-hand damage rows (issue #75): the hit the default attack actually swings with, so the panel
        // agrees with the damage the server applies. Only the main hand: the server attacks with it. Those rows are gated by
        // skill_uses_main_hand, which nothing sent before, so the numbers alone would leave them blank.
        if (Stats.Key("skill_uses_main_hand") is uint usesMainHand && !devSet.Contains(usesMainHand)) stats[usesMainHand] = 1;
        var attack = AttackHit(WeaponFor(conn), stats);
        foreach (var (stat, value) in PanelStats.HandDamage(attack, "main_hand"))
            if (Stats.Key(stat) is uint key && !devSet.Contains(key)) stats[key] = value;
        // The conditions as they are now; the player tick sends the chance again when they change.
        int suppression = Suppression(conn).Chance;
        foreach (uint key in SuppressionChanceKeys) stats[key] = suppression;
        return stats;
    }

    // Conversion for one hit: the player's stats plus the skill's own stats (base/skill conversion). Player stats win on
    // a key collision; the normal case is that the skill adds stats the player does not have.
    static DamagePacket AttackHit(SkillCatalog.Unarmed weapon, IReadOnlyDictionary<uint, int> stats) =>
        DamageStats.Added(weapon.HitDamage(stats), name => Stats.Key(name) is uint key ? stats.GetValueOrDefault(key) : 0, spell: false);

    static double AddedEffectiveness(int effect, int level) => Skills.DamageEffectiveness(effect, level) is int value and > 0 ? value / 10000.0 : 1;

    static GameServer.Core.World.DamageConversion ConversionFor(ClientState conn, int effectRow, int level, bool attack)
    {
        var stats = CharacterPlayerStats(conn);
        if (effectRow >= 0)
            foreach (var (key, value) in Skills.EffectStats(effectRow, level)) stats.TryAdd(key, value);
        return DamageStats.Conversion(stats, Stats, attack);
    }

    // The character's own stats: the class's attributes and the pools (ClassAttributes), then every other stat of the
    // allocated passives, summed (increased life and the attributes are already in the pools; the rest, such as increased
    // spell damage, the client only shows).
    static IEnumerable<(uint, int)> BaseStats(ClientState conn)
    {
        var seen = new HashSet<uint>();
        foreach (var (k, v) in ClassAttributes(conn.ClassRow, conn.Pools)) if (seen.Add(k)) yield return (k, v);
        List<ushort> nodes;
        lock (conn.Monsters) nodes = [.. conn.Passives];
        var extra = CharacterStatSources(conn, nodes);
        if (conn.God)
        {
            foreach (var (stat, value) in GodStats)
                if (Stats.Key(stat) is uint key) extra[key] = extra.GetValueOrDefault(key) + value;
            // the final value the client shows: 100% of maximum life per second, per minute
            if (Stats.Key("life_regeneration_rate_per_minute") is uint lr) extra[lr] = (int)Math.Min(int.MaxValue, (long)conn.Pools.Life * GodLifeRegenPctPerSecond / 100 * 60);
        }
        List<AuraCatalog.Aura> auras;
        lock (conn.Monsters) auras = [.. conn.Auras.Values];
        foreach (var (k, v) in AuraCatalog.RawStats(auras, conn.Pools.AuraEffectPct, Skills, Stats))
            extra[k] = extra.GetValueOrDefault(k) + v;
        foreach (var (k, v) in extra) if (seen.Add(k)) yield return (k, v);
    }

    // The class's attributes (Characters.dat BaseStrength / BaseDexterity / BaseIntelligence) as final stats: strength,
    // dexterity and intelligence for the character panel, and virtual_*_for_requirements, which item requirements are
    // checked against (0x1402ce5f0 via the Player cache 0x141e80340, keys 0x3d1b-0x3d1d; global_no_attribute_requirements
    // skips the check, global_item_attribute_requirements_+% scales it).
    // With the level-based pools (CharacterStats): level, maximum_life / maximum_mana (and their base_ forms),
    // accuracy_rating, physical_damage_reduction_rating (armour) and evasion_rating (base_evasion_rating 15), as final
    // values, and the attributes' increases: armour/evasion_rating_+%, maximum_energy_shield_+% (intelligence) and
    // melee_physical_damage_+% (strength). The Offence panel's accuracy row reads main_hand_accuracy_rating
    // (CharacterPanelStats MH_Accuracy); its Chance to
    // Hit rows read main_hand_chance_to_hit_% and main_hand_chance_to_hit_evasive_monsters_% directly. The reference is a
    // monster of the character's level (the normal and high-dexterity evasion rows of DefaultMonsterStats).
    static IEnumerable<(uint, int)> ClassAttributes(int classRow, CharacterStats.Pools pools)
    {
        int referenceEvasion = Monsters.BaseEvasion(pools.Level) ?? 0;
        int referenceEvasiveEvasion = Monsters.BaseEvasiveEvasion(pools.Level) ?? 0;
        int chanceToHit = DamageModel.AccuracyHitChance(referenceEvasion, pools.Accuracy);
        int evasiveChanceToHit = DamageModel.AccuracyHitChance(referenceEvasiveEvasion, pools.Accuracy);
        foreach (var (stat, value) in new[] { ("level", pools.Level), ("base_maximum_life", (int)pools.Life), ("maximum_life", (int)pools.Life),
            ("base_maximum_mana", (int)pools.Mana), ("maximum_mana", (int)pools.Mana), ("accuracy_rating", pools.Accuracy),
            ("main_hand_accuracy_rating", pools.Accuracy), ("off_hand_accuracy_rating", pools.Accuracy),
            ("main_hand_chance_to_hit_%", chanceToHit), ("off_hand_chance_to_hit_%", chanceToHit),
            ("main_hand_chance_to_hit_evasive_monsters_%", evasiveChanceToHit),
            ("off_hand_chance_to_hit_evasive_monsters_%", evasiveChanceToHit),
            ("character_sheet_reference_evasion", referenceEvasion),
            ("character_sheet_reference_high_dex_evasion", referenceEvasiveEvasion),
            ("physical_damage_reduction_rating", pools.Armour), ("physical_damage_reduction_rating_+%", pools.ArmourPct),
            ("base_evasion_rating", CharacterStats.BaseEvasion), ("evasion_rating", pools.Evasion), ("evasion_rating_+%", pools.EvasionPct),
            ("maximum_energy_shield", (int)pools.EnergyShield), ("maximum_energy_shield_+%", pools.EnergyShieldPct), ("melee_physical_damage_+%", pools.MeleePhysicalDamagePct),
            ("movement_velocity_+%", pools.MovementVelocityPct),
            ("combined_base_block_%", pools.BlockChanceBase), ("attack_block_%", pools.BlockChance),
            // The character panel prints attack block as "capped% (uncapped%)"; the client does not derive the second value.
            ("uncapped_attack_block_%", pools.BlockChanceUncapped),
            // A separate spell block row is only drawn when display_spell_block_chance is non-zero.
            ("spell_block_%", pools.SpellBlockChance), ("display_spell_block_chance", pools.SpellBlockChance),
            ("base_spell_block_%", 0),
            // Resistance display: the client shows fire_damage_resistance_% as the capped value and
            // uncapped_fire_damage_resistance_% (and the other three) in parentheses as the overcap. The passives' base
            // resistances are already summed into the pools, so zero the all-elements input to avoid counting it twice. The
            // maximum-resistance passives stay in BaseStats: the client adds them to its own 75 base. Do not send maximum_*
            // here; those keys are additive maximum-resistance modifiers, so sending 75 would count the base twice.
            ("base_resist_all_elements_%", 0),
            ("base_fire_damage_resistance_%", pools.FireResistance),
            ("fire_damage_resistance_%", DamageModel.Capped(pools.FireResistance, pools.FireMaxResistance)),
            ("uncapped_fire_damage_resistance_%", pools.FireResistance),
            ("base_cold_damage_resistance_%", pools.ColdResistance),
            ("cold_damage_resistance_%", DamageModel.Capped(pools.ColdResistance, pools.ColdMaxResistance)),
            ("uncapped_cold_damage_resistance_%", pools.ColdResistance),
            ("base_lightning_damage_resistance_%", pools.LightningResistance),
            ("lightning_damage_resistance_%", DamageModel.Capped(pools.LightningResistance, pools.LightningMaxResistance)),
            ("uncapped_lightning_damage_resistance_%", pools.LightningResistance),
            ("base_chaos_damage_resistance_%", pools.ChaosResistance),
            ("chaos_damage_resistance_%", DamageModel.Capped(pools.ChaosResistance, pools.ChaosMaxResistance)),
            ("uncapped_chaos_damage_resistance_%", pools.ChaosResistance),
            // The character panel reads the final suppression values, not only the raw passive input.
            ("spell_suppression_chance_%", pools.SpellSuppressionChance),
            ("base_spell_suppression_chance_%", pools.SpellSuppressionChance),
            ("combined_spell_suppression_chance_%", pools.SpellSuppressionChance),
            ("spell_damage_%_suppressed", pools.SpellSuppressionEffect),
            ("base_spell_damage_%_suppressed", pools.SpellSuppressionEffect),
            // The Offence panel's crit rows read these final values (permyriad for chance, percent for multiplier).
            ("main_hand_critical_strike_chance", pools.AttackCriticalStrikeChance),
            ("main_hand_critical_strike_multiplier", pools.CriticalStrikeMultiplier),
            ("spell_critical_strike_chance", pools.SpellCriticalStrikeChance),
            ("spell_critical_strike_multiplier", pools.SpellCriticalStrikeMultiplier) })
            if (Stats.Key(stat) is uint key) yield return (key, value);
        // The class's attributes with the passives' (CharacterStats.For); a pools record made without a class (0) falls back
        // to the class's own.
        var c = Skills.ClassFor(classRow);
        var (str, dex, intel) = pools.Strength + pools.Dexterity + pools.Intelligence > 0
            ? (pools.Strength, pools.Dexterity, pools.Intelligence) : (c.Strength, c.Dexterity, c.Intelligence);
        foreach (var (stat, value) in new[] { ("strength", str), ("dexterity", dex), ("intelligence", intel),
            ("virtual_strength_for_requirements", str), ("virtual_dexterity_for_requirements", dex),
            ("virtual_intelligence_for_requirements", intel) })
            if (Stats.Key(stat) is uint key) yield return (key, value);
    }

    // The skills of the gems socketed in the equipped items: each gem's GrantedEffects row at the gem's level with its own stats
    // (SkillCatalog.EffectStats: damage, cast time, mana cost) after the player's, with ids after the default attack's that
    // no basic action uses (SkillCatalog.SkillIds).
    static IEnumerable<GrantedSkill> GemSkills(string? character, IEnumerable<(uint, int)>? player = null)
    {
        var playerStats = new Dictionary<uint, int>();
        foreach (var (key, value) in player ?? []) playerStats[key] = value;
        var skillCosmetics = CharacterSaves.SkillCosmetics(string.IsNullOrEmpty(character) ? "Exile" : character);
        foreach (var (id, effect, name, level, quality, gem) in GemSkillEffects(character))
        {
            var stats = new Dictionary<uint, int>();
            foreach (var (k, v) in playerStats) stats[k] = v;
            var own = Skills.EffectStats(effect, level, quality).ToDictionary(kv => kv.Item1, kv => kv.Item2);
            foreach (var (k, v) in own) stats[k] = v;
            if (Skills.IsSpell(effect) && !Skills.IsAttack(effect) && Stats.Key("spell_critical_strike_chance") is uint critKey)
                stats[critKey] = SpellCriticalStrike.Chance(Skills.SpellCritChance(effect, level), Stats, playerStats, own);
            // The skill effects worn in the gem's Cosmetics tab slots: their stats (skill_art_variation …) on this skill only.
            foreach (var (key, worn) in skillCosmetics)
                if (CharacterSaves.SkillCosmeticGem(key) == gem && Mtx.Find(worn.Record) is { } record && MtxEffects.Of(record.MtxTypeHash) is { } fx)
                    foreach (var (k, v) in fx.Stats) stats[k] = v;
            // Increased cast speed shortens the cast the client plays (the final values: 11-skills.md §4).
            if (CastSpeedPct(stats) is int castSpeed and not 0 && Skills.CastMs(effect) is int castMs and > 0)
            {
                int ms = (int)Math.Round(castMs / (1 + castSpeed / 100.0));
                if (Stats.Key("virtual_base_spell_cast_time_ms") is uint vk) stats[vk] = ms;
                if (Stats.Key("hundred_times_casts_per_second") is uint hk) stats[hk] = (int)Math.Round(100_000.0 / Math.Max(1, ms));
            }
            // A spell's tooltip DPS is its own hit (SpellDamage) × casts per second, not the default attack's values that
            // came with the player's stats.
            int Get(string name) => Stats.Key(name) is uint k ? stats.GetValueOrDefault(k) : 0;
            // Cyclone's client effect reads the final radius, not the increased-area input. Match AreaSkillHit.
            if (SkillArea.IsCyclone(Skills.EffectId(effect)))
            {
                int baseRadius = SkillArea.BaseRadius(Skills.EffectId(effect), Get);
                int increased = SkillArea.Increased(Get, spell: false);
                int radiusPercent = SkillArea.RadiusPercent(Get, spell: false);
                int radius = SkillArea.Radius(baseRadius, Get, spell: false);
                if (Stats.Key("active_skill_base_area_of_effect_radius") is uint baseKey) stats[baseKey] = baseRadius;
                if (Stats.Key("active_skill_area_of_effect_radius") is uint radiusKey)
                    stats[radiusKey] = radius;
                // Channelled Cyclone's conditional-radius path reads these derived stats instead of 0x4876.
                // Compute them before writing skill_area_of_effect_+%, or the base increase would count twice.
                if (Stats.Key("total_skill_area_of_effect_radius_+%_including_final") is uint scaleKey) stats[scaleKey] = radiusPercent;
                if (Stats.Key("skill_area_of_effect_+%") is uint areaKey) stats[areaKey] = increased;
                Console.WriteLine($"[gateway] Cyclone area: base {baseRadius}, radius {radius}, increased area {increased}%, radius scale {radiusPercent}%");
            }
            if (SpellDamage.Hit(Get, AddedEffectiveness(effect, level)) is { Max: > 0 } hit && Get("hundred_times_casts_per_second") is int cps and > 0)
            {
                var (perUse, perSecond) = SpellDamage.Display(hit, cps / 100.0);
                stats[StatKeys.DisplayHundredTimesDamagePerSkillUse] = perUse;
                stats[StatKeys.HundredTimesDamagePerSecond] = perSecond;
            }
            if (Skills.FindByRow(id, effect, level, [.. stats.Select(kv => (kv.Key, kv.Value))]) is { } skill)
            {
                Console.WriteLine($"[gateway] gem skill 0x{skill.Id:x4} {name} level {level} quality {quality} (effect {effect}): GEPL {skill.EffectsPerLevelRow}, " +
                    $"GESSPL {skill.StatSetsPerLevelRow}, " + string.Join(", ", own.Select(kv => $"{Stats.Name(kv.Key)} {kv.Value}")));
                yield return skill;
            }
        }
    }

    // What a skill bar slot holds, as saved (CharacterSaves): a gem or the default attack by GrantedEffects Id (skill ids
    // change with the socketed gems), a basic action as "Action/<ActionTypes Id>" ("Action/Move": "Move only" on a mouse
    // button), "" for a cleared slot. Null for an id the server does not know.
    static string? SkillBarEntry(ClientState conn, ushort skillId) =>
        skillId == 0 ? "" : SkillEffectId(conn, skillId) ?? (Skills.ActionName(skillId) is { } a ? "Action/" + a : null);

    // The saved skill bar as the player's current skill ids, sent with S→C 0x53. The default attack goes on slot 0 (left
    // mouse button) only when nothing at all was saved for that slot; nothing is sent without a saved bar, so the client
    // keeps its own default.
    static async Task SendSkillBar(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
    {
        FillSkillBar(conn);
        if (CharacterSaves.Load(CharacterName(conn))?.SkillBar is not { Count: > 0 } bar) return;
        var ids = new ushort[InstanceProtocol.SkillBarSlots];
        var byEffect = GemSkillEffects(conn.Character).Select(g => (g.Id, Effect: Skills.EffectId(g.Effect)))
            .Append((Id: SkillCatalog.FirstSkillId, Effect: SkillEffectId(conn, SkillCatalog.FirstSkillId)))
            .Where(e => e.Effect is not null).GroupBy(e => e.Effect!).ToDictionary(g => g.Key, g => g.First().Id);
        foreach (var (slot, entry) in bar)
        {
            if (slot < 0 || slot >= ids.Length) continue;
            if (entry.StartsWith("Action/") && Skills.ActionSkillId(entry["Action/".Length..]) is ushort action) ids[slot] = action;
            else if (byEffect.TryGetValue(entry, out ushort id)) ids[slot] = id;
        }
        if (!bar.ContainsKey(0)) ids[0] = SkillCatalog.FirstSkillId;
        await Send(remote, stream, ciphers, InstanceProtocol.SkillBar(ids),
            $"0x0053 skill bar: {string.Join(", ", ids.Select((id, i) => id == 0 ? null : $"slot {i} 0x{id:x4}").OfType<string>())}");
    }

    // Puts the skills that are nowhere on the saved bar into its empty slots (SkillBarLayout), leaving every other slot as
    // it is: the default attack on the left mouse button of a new bar, each gem skill of the equipment on the right mouse
    // button or the next empty slot. True when the bar changed (saved).
    static bool FillSkillBar(ClientState conn)
    {
        string character = CharacterName(conn);
        var bar = CharacterSaves.Load(character)?.SkillBar ?? [];
        string attack = SkillEffectId(conn, SkillCatalog.FirstSkillId)!;
        var gems = GemSkillEffects(conn.Character).Select(g => Skills.EffectId(g.Effect)).OfType<string>().ToList();
        var added = SkillBarLayout.Fill(bar, attack, gems, entry => entry == attack || entry.StartsWith("Action/") || gems.Contains(entry));
        foreach (var (slot, entry) in added) CharacterSaves.SetSkillBar(character, (byte)slot, entry);
        if (added.Count > 0)
            Console.WriteLine($"[gateway] {character} skill bar: {string.Join(", ", added.Select(a => $"{a.Entry} on slot {a.Slot}"))}");
        return added.Count > 0;
    }

    // The GrantedEffects Id behind a skill id of the player (the default attack or a gem skill), or null.
    static string? SkillEffectId(ClientState conn, ushort skillId) =>
        skillId == SkillCatalog.FirstSkillId ? Environment.GetEnvironmentVariable("Game_PLAYER_ATTACK") ?? "PlayerMelee"
        : GemSkillEffects(conn.Character).FirstOrDefault(g => g.Id == skillId) is { Name: not null } g ? Skills.EffectId(g.Effect) : null;

    // Why the held item may not go into an equipment row: the character is below its level requirement
    // (ItemModStats.LevelRequirement). The client also checks it (0x1421eca50) against the level we send in the item data,
    // but the server does not trust that.
    static string? LevelTooLow(ClientState conn, byte row, Item? held)
    {
        if (held is null || !(ItemModStats.EquipmentRows.Contains(row) || row is 14 or 15)) return null;
        if (Items.ByHash(held.TypeHash) is not { } t) return null;
        int needs = ItemModStats.LevelRequirement(held, t.DropLevel, Items.Mods);
        return conn.Pools.Level < needs ? $"{t.Name} requires level {needs} (character is {conn.Pools.Level})" : null;
    }

    // Why a gem may not go into a socket: the character's level is below the gem's level requirement (GrantedEffectStatSetsPerLevel
    // PlayerLevelReq at the gem's level) or an attribute requirement is not met. The client checks both (0x14246fff0 for the
    // level, 0x14246fc40 per attribute) and refuses the move itself, but the server does not trust that.
    static string? GemRequirementsUnmet(ClientState conn, Item? gem)
    {
        if (gem is null || Items.ByHash(gem.TypeHash) is not { } baseType || Items.Gem(baseType) is not { } type) return null;
        int level = Items.GemLevel(gem);
        int effect = type.Variant(gem.GemVariant).Effect;
        if (effect < 0) return null;
        string name = Items.GemName(gem);
        if (Skills.PlayerLevelReq(effect, level) is int needs and > 0 && conn.Pools.Level < needs)
            return $"{name} at level {level} requires level {needs} (character is {conn.Pools.Level})";
        // A gem's attributes come from its percentage shares and the level requirement, at the gem's level clamped to its cap
        // (a corrupted level 21 gem keeps level 20's requirement; 0x14246fc40). The client checks them against
        // virtual_*_for_requirements, which are the attributes unless something overrides them (/set_stat).
        var (str, dex, intel) = GemAttributeRequirements(gem, type, effect);
        foreach (var (need, attr, has) in new[] { (str, "strength", RequirementAttribute(conn, "strength", conn.Pools.Strength)),
            (dex, "dexterity", RequirementAttribute(conn, "dexterity", conn.Pools.Dexterity)),
            (intel, "intelligence", RequirementAttribute(conn, "intelligence", conn.Pools.Intelligence)) })
            if (need > 0 && has < need)
                return $"{name} at level {level} requires {need} {attr} (character has {has})";
        return null;
    }

    // A gem's attribute requirements at its level: each SkillGems percentage through SkillCatalog.GemAttributeRequirement.
    static (int Str, int Dex, int Int) GemAttributeRequirements(Item gem, ItemCatalog.GemType type, int effect)
    {
        int level = Math.Clamp(Items.GemLevel(gem), 1, Math.Max(1, type.MaxLevel));
        int needs = Skills.PlayerLevelReq(effect, level);
        return (SkillCatalog.GemAttributeRequirement(type.StrengthPercent, needs, type.IsSupport),
            SkillCatalog.GemAttributeRequirement(type.DexterityPercent, needs, type.IsSupport),
            SkillCatalog.GemAttributeRequirement(type.IntelligencePercent, needs, type.IsSupport));
    }

    // The value the client's requirement checks compare (virtual_*_for_requirements, 13-items.md §2d): what /set_stat set,
    // else the character's attribute.
    static int RequirementAttribute(ClientState conn, string attribute, int value)
    {
        lock (conn.Monsters)
            return Stats.Key($"virtual_{attribute}_for_requirements") is uint key && conn.PlayerStats.TryGetValue(key, out int set) ? set : value;
    }

    // Why a gem skill may not be used: the character's level or an attribute is below the gem's requirement of its own level.
    // The client reds the gem but still sends the use, so this is the backstop for a client whose item is not inactive yet.
    // Null when no gem granting the effect is below its requirement.
    static string? GemDisabled(ClientState conn, int effect)
    {
        foreach (var place in PlayerInventories.For(CharacterName(conn), Items).SocketedGemPlaces())
            if (Items.GemEffects(place.Gem).Contains(effect) && GemRequirementsUnmet(conn, place.Gem) is { } missing)
                return missing;
        return null;
    }

    // A worn item whose requirements are not met (CharacterStatSources): its mods and gems do nothing.
    static bool IsInactive(ClientState conn, uint itemId) => InactiveItems.GetValueOrDefault(CharacterName(conn))?.Contains(itemId) == true;

    static string CharacterName(ClientState conn) => string.IsNullOrEmpty(conn.Character) ? "Exile" : conn.Character;

    // The skills of the active gems socketed in the equipped items in skill-list order: skill id, GrantedEffects row, gem
    // name, the gem's own level (from its experience; the experience stalls at the character's level requirement instead,
    // GainGemExperience) and quality. A transfigured or Trarthan gem grants its variant's skill, a Vaal gem its Vaal skill and
    // its base skill (ItemCatalog.GemEffects). Each skill keeps its id while its gem stays socketed (GemSkillIds): the client
    // binds skill bar keys by skill id. A gem whose requirements the character does not meet makes its item inactive
    // (CharacterStatSources), so it grants nothing here; a use that still arrives is refused (GemDisabled).
    static IEnumerable<(ushort Id, int Effect, string Name, int Level, int Quality, uint Gem)> GemSkillEffects(string? character)
    {
        string name = string.IsNullOrEmpty(character) ? "Exile" : character;
        var inactive = InactiveItems.GetValueOrDefault(name);
        var gems = PlayerInventories.For(name, Items).SocketedGemPlaces()
            .SelectMany(g => Items.GemEffects(g.Gem).Select(effect => (g.ItemId, g.Socket, g.Gem, Effect: effect))).ToList();
        var ids = GemSkillIdsOf.GetOrAdd(name, _ => new GemSkillIds())
            .Assign([.. gems.Select(g => new GemSkillIds.Gem(g.ItemId, g.Socket, g.Effect))], GemSkillIdPool.Value);
        // Gems in an inactive item grant nothing, but keep their id.
        for (int i = 0; i < gems.Count; i++)
            if (ids[i] is ushort id && inactive?.Contains(gems[i].ItemId) != true)
                yield return (id, gems[i].Effect, Items.GemName(gems[i].Gem), Items.GemLevel(gems[i].Gem), gems[i].Gem.Quality, gems[i].Gem.TypeHash);
    }

    static double Dist((int X, int Y) a, (int X, int Y) b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    // A new level from /set_level: the level's starting experience, then the level change (ApplyLevel).
    static async Task SetLevel(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, int level)
    {
        lock (conn.Monsters) conn.Experience = CharacterStats.ExperienceFor(level);
        CharacterSaves.SetExperience(CharacterName(conn), conn.Experience);
        await Send(remote, stream, ciphers, InstanceEntities.PlayerExperienceUpdate(PlayerId, conn.Experience),
            $"0x02ba player Player: experience {conn.Experience} (level {level})");
        await ApplyLevel(remote, stream, ciphers, conn, level);
    }

    // A new level in place, after the Player component's experience (0x2ba; the client derives the level from it and runs
    // its level change): the level's stats (0x1b3: level, life, mana, accuracy, ...), the skills with them (0x2ba Actor),
    // full life, as a level up gives, and the passive skill points (0x3d). Passives beyond a lower level's points are
    // taken back, last allocated first.
    static async Task ApplyLevel(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, int level)
    {
        int before = conn.Pools.Level;
        List<ushort>? trimmed = null;
        Dictionary<ushort, ushort> effects;
        lock (conn.Monsters)
        {
            int had = conn.Passives.Count;
            TrimPassives(conn, level);
            if (conn.Passives.Count != had) trimmed = [.. conn.Passives];
            effects = conn.MasteryEffects.Where(kv => conn.Passives.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value);
            conn.Pools = PoolsFor(conn, level);
            conn.ApplyMovementSpeed();
        }
        // Only a lower level (/set_level) drops passives; otherwise the saved ones stay as they are.
        if (trimmed is not null) CharacterSaves.SetPassives(CharacterName(conn), trimmed, effects);
        Console.WriteLine($"[gateway] {remote} {CharacterName(conn)}: level {before} → {level} (life {conn.Pools.Life}, mana {conn.Pools.Mana}, {Progression.PassivePoints(level)} passive point(s))");
        await SendCharacterStats(remote, stream, ciphers, conn, $"level {level}");
        int mana = UnreservedMana(conn);
        lock (conn.Monsters) { (conn.PlayerLife, conn.PlayerMana) = (conn.PlayerMaxLife, (uint)mana); conn.EnergyShield.SetMaximum(conn.Pools.EnergyShield, true); }
        await Send(remote, stream, ciphers, InstanceProtocol.LifeUpdate(PlayerId, conn.PlayerMaxLife), $"0x01b1 player life {conn.PlayerMaxLife}");
        await SendPlayerMana(remote, stream, ciphers, conn, $"0x01b1 player mana {conn.PlayerMana}");
        await SendPassives(remote, stream, ciphers, conn);
    }

    // The player's stats (0x1b3) and the skills computed with them (0x2ba Actor), after the level or the passives changed.
    static async Task SendCharacterStats(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, string why)
    {
        await Send(remote, stream, ciphers, InstanceEntities.StatsUpdate(PlayerId, PlayerStatsWithCleared(conn)), $"0x01b3 player stats ({why})");
        await SendSkills(remote, stream, ciphers, conn);
        await SendPlayerEnergyShield(remote, stream, ciphers, conn);
    }

    // The player's skills again, and the removal of the ones the client still has whose gems left the equipment, in one
    // Actor 0x20 update (PlayerSkillsChange): the 0x40 list neither removes skills nor tells the character panel.
    static async Task SendSkills(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
    {
        var skills = PlayerSkills(conn);
        var gone = conn.ClientSkillIds.Except(skills.Select(s => s.Id)).Order().ToList();
        var added = skills.Select(s => s.Id).Where(id => !conn.ClientSkillIds.Contains(id)).ToList();
        conn.ClientSkillIds = [.. skills.Select(s => s.Id)];
        await Send(remote, stream, ciphers, InstanceEntities.PlayerSkillsChange(PlayerId, skills, gone),
            $"0x02ba player Actor: {skills.Count} skill(s) with {skills.Sum(k => k.Stats?.Count ?? 0)} stats" +
            (gone.Count > 0 ? $", remove skill(s) {string.Join(", ", gone.Select(id => $"0x{id:x4}"))}" : ""));
        if (gone.Count > 0) await EndSkillEffects(remote, stream, ciphers, conn, gone);
        IReadOnlyList<AuraCatalog.Aura> resumed = [];
        lock (conn.Monsters)
            if (conn.PlayerLife > 0 && !InTown(conn))
                resumed = RestoreRememberedAuras(conn, CharacterSaves.Load(CharacterName(conn))?.ActiveAuras ?? []);
        if (resumed.Count > 0)
        {
            foreach (var aura in resumed)
                foreach (var packet in AuraBuffs(aura))
                    await Send(remote, stream, ciphers, packet, $"aura {aura.EffectId} resumes after skill refresh");
            await SendCharacterStats(remote, stream, ciphers, conn, "remembered auras resumed");
            await SendPlayerMana(remote, stream, ciphers, conn, "remembered aura reservation");
        }

        // The update can take a bound skill off the skill bar (live 2026-10-21: Haste's slot emptied when its aura went on,
        // and came back with the area change's 0x53), and a gem socketed again does not get its slot back by itself, so the
        // saved bar goes out again. Not when a skill the saved bar never had was added: the client binds a new skill to a free
        // slot itself (C→S 0x4e), and the bar sent now would clear it there. A new gem skill gets an empty slot of the saved
        // bar first (FillSkillBar: the right mouse button, then Q W E R T …), so it goes out with the bar.
        FillSkillBar(conn);
        var saved = CharacterSaves.Load(CharacterName(conn))?.SkillBar?.Values;
        if (added.All(id => SkillEffectId(conn, id) is { } effect && saved?.Contains(effect) == true))
            await SendSkillBar(remote, stream, ciphers, conn);
    }

    // Skills the player no longer has (their gems left the equipment): their auras and Righteous Fire end with them, and the
    // reserved mana comes back.
    static async Task EndSkillEffects(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, IReadOnlyCollection<ushort> gone)
    {
        List<AuraCatalog.Aura> ended;
        lock (conn.Monsters)
        {
            ended = [.. conn.Auras.Values.Where(a => gone.Contains(a.SkillId))];
            foreach (var aura in ended) conn.Auras.Remove(aura.SkillId);
            if (ended.Count > 0)
            {
                conn.Pools = PoolsFor(conn, conn.Pools.Level);
                conn.ApplyMovementSpeed();
            }
        }
        foreach (var aura in ended)
        {
            foreach (var packet in AuraCatalog.EndPackets(aura, PlayerId))
                await Send(remote, stream, ciphers, packet, $"0x01c2 player aura {aura.Name} ends (its gem left)");
        }
        if (ended.Count > 0)
        {
            await SendCharacterStats(remote, stream, ciphers, conn, $"aura {string.Join(", ", ended.Select(a => a.Name))} off (gem left)");
            await SendPlayerMana(remote, stream, ciphers, conn, $"0x01b1 player mana {conn.PlayerMana}/{conn.Pools.Mana}, {ReservedMana(conn)} reserved");
        }
        if (conn.RighteousFire is { } rf && gone.Contains(rf.Skill)) await EndRighteousFire(remote, stream, ciphers, conn, "its gem left");
    }
}
