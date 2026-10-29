using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using Progression = GameServer.Core.World.Progression;
using DamageModel = GameServer.Core.World.DamageModel;

partial class Program
{
    // Called before inventory loading or any save normalization on a real connection.
    internal static void CheckAscendancyClientCompatibility(ClientState conn) =>
        AscendancyClientCompatibility.RequirePlayable(CharacterSaves.Load(CharacterName(conn))?.MainAscendancy?.Selection);

    // Read-only admission checks, shared with LoadProgress: no inventory loading, normalization or gold migration.
    static void ValidateSavedProgress(ClientState conn, CharacterSaves.Save? save)
    {
        // Without the tree the saved passives can't be checked; dropping them would lose them at the next save.
        if (save?.Passives is { Count: > 0 } && !PassivesFor(conn).Loaded)
            throw new UnknownSavedItemException("allocated passives, but the passive tree (data/tables/PassiveSkills.json) is not loaded");
        var ordinary = save?.Passives ?? [];
        var ascendancy = save?.MainAscendancy ?? new();
        if (ordinary.Any(n => PassivesFor(conn).Find(n) is not { IsAscendancy: false }))
            throw new UnknownSavedItemException("unknown or ascendancy nodes in the ordinary passive allocation; the save was retained");
        AscendancyRules.Validate((byte)SkillsFor(conn).ClassFor(conn.ClassRow).IntegerId, ascendancy);
        ValidateOrdinaryPassives(conn, ordinary, ascendancy);
    }

    // The character's experience and passives from its save; returns its level. The saved experience decides the level: the
    // account's (the ticket's) is only a copy for the character list, which a failed update at a level-up leaves behind. A save
    // without experience (an older one) starts at the account's level. Passives beyond the level's points are dropped.
    static int LoadProgress(ClientState conn, int level)
    {
        var save = CharacterSaves.Load(CharacterName(conn));
        level = CharacterSaves.LevelOf(save, level);
        ValidateSavedProgress(conn, save);
        var ordinary = save?.Passives ?? [];
        var ascendancy = save?.MainAscendancy ?? new();
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
            conn.MainAscendancy = ascendancy;
            conn.Passives = [.. ordinary];
            conn.Waypoints.UnionWith(save?.Waypoints ?? []);
            conn.Visited.UnionWith(save?.Visited ?? []);
            conn.QuestFlags = [.. save?.QuestFlags ?? [], .. Quests.EveryCharacter];
            conn.ExtraPassivePoints = save?.ExtraPassivePoints ?? 0;
            conn.RefundPoints = save?.RefundPoints ?? 0;
            conn.Experience = save?.Experience ?? CharacterStats.ExperienceFor(level);
            TrimPassives(conn, level);
            conn.MasteryEffects = (save?.MasteryEffects ?? []).Where(kv => conn.Passives.Contains(kv.Key)
                && PassivesFor(conn).FindEffect(kv.Value) is { } effect && PassivesFor(conn).IsEffectFor(kv.Key, effect))
                .ToDictionary(kv => kv.Key, kv => kv.Value);
            conn.SavedPassiveProgress = new(conn.MainAscendancy, conn.Passives, conn.MasteryEffects, conn.RefundPoints);
            // Persist the pre-existing ordinary level/mastery normalization once. The compare protects a newer connection.
            if (!conn.SavedPassiveProgress.Matches(save ?? new CharacterSaves.Save([])))
                CharacterSaves.SetMainAscendancy(CharacterName(conn), conn.MainAscendancy, conn.Passives, conn.MasteryEffects,
                    expected: CharacterSaves.PassiveProgressSnapshot.From(save));
            conn.Pools = PoolsFor(conn, level);
            RestoreRememberedAuras(conn, save?.ActiveAuras ?? []);
            conn.ApplyMovementSpeed();
        }
        return level;
    }

    // The character's stats from its allocated passives and the global mods of its active equipped items (ItemModStats),
    // by client stat key. Worn items whose requirements the class, the passives and the other active items do not meet are
    // inactive (ItemModStats.Inactive): remembered per character for the weapon profile and the gem skills.
    static (Dictionary<uint, int> Stats, AttributeModifiers Attributes) CharacterStatSources(ClientState conn,
        IEnumerable<ushort>? nodes = null, int? level = null)
    {
        var before = InactiveItems.GetValueOrDefault(CharacterName(conn));
        var (sources, attributes, inactive, worn) = EquipmentStatSources(conn, ItemsOf(conn).EquipmentSnapshot(), nodes, level);
        InactiveItems[CharacterName(conn)] = inactive;
        if (inactive.Count > 0 && (before is null || !before.SetEquals(inactive)))
            Console.WriteLine($"[gateway] {CharacterName(conn)}: inactive items (requirements not met): " +
                string.Join(", ", worn.Where(w => inactive.Contains(w.Id)).Select(w =>
                    $"{w.Id} {ItemsFor(conn).ByHash(w.Item.TypeHash)?.Name} (str {w.Strength}, dex {w.Dexterity}, int {w.Intelligence}, level {w.Level})")));
        return (WithAntiquarian(conn, WithFortification(conn, WithCharges(conn, sources))), attributes);
    }

    // Shared equipment/passive resolver. The requirement snapshot uses this without publishing caches or pools.
    static (Dictionary<uint, int> Sources, AttributeModifiers Attributes, HashSet<uint> Inactive,
        List<ItemModStats.Worn> Worn) EquipmentStatSources(
        ClientState conn, IReadOnlyList<(byte Row, InventoryItem Item)> equipment,
        IEnumerable<ushort>? nodes = null, int? level = null)
    {
        // A level change can trim the list while /set_stat builds pools outside the combat lock.
        // Mastery dictionaries are replaced as a whole, so retaining their current reference is safe.
        IReadOnlyDictionary<ushort, ushort> masteries;
        lock (conn.Monsters)
        {
            nodes = (nodes ?? conn.Passives).Concat(conn.MainAscendancy.Allocated).ToArray();
            masteries = conn.MasteryEffects;
        }
        var c = SkillsFor(conn).ClassFor(conn.ClassRow);
        Dictionary<uint, int> dev;
        lock (conn.Monsters) dev = new(conn.PlayerStats);
        var worn = equipment.Select(e => e.Item).ToArray();
        var before = InactiveItems.GetValueOrDefault(CharacterName(conn));
        // The sum below is a pure function of these inputs (items are immutable records), and the combat paths ask for
        // it many times a second (every monster hit): reuse the last result while none of them changed.
        var sourcesKey = new StatSourcesKey([.. nodes], masteries, worn, dev, conn.ClassRow, conn.God, level ?? conn.Pools.Level, before);
        if (conn.StatSourcesCache is { } cached && cached.Key.Matches(sourcesKey))
            return (new(cached.Sources), cached.Attributes, new(cached.Inactive), cached.Worn);
        var passives = PassivesFor(conn).Stats(nodes, masteries);
        // Only attributes need a wide source view. Keep each raw key until overrides have replaced its entire total.
        var passiveAttributes = new Dictionary<string, long>();
        foreach (ushort node in nodes)
        {
            var effects = masteries.TryGetValue(node, out ushort effect) ? PassivesFor(conn).FindEffect(effect)?.Stats : null;
            foreach (var (key, value) in (PassivesFor(conn).Find(node)?.Stats ?? []).Concat(effects ?? []))
            {
                string name = Stats.Name(key);
                if (AttributeModifiers.IsInput(name))
                    passiveAttributes[name] = passiveAttributes.GetValueOrDefault(name) + value;
            }
        }
        Dictionary<string, long> RawAttributes(IEnumerable<Item> active)
        {
            var raw = ItemModStats.RawAttributes(active, ItemsFor(conn).Mods);
            foreach (var (name, value) in passiveAttributes) raw[name] = raw.GetValueOrDefault(name) + value;
            return raw;
        }
        int god = conn.God ? 1000 : 0;
        var wornItems = worn
            .Select(i => (i, t: ItemsFor(conn).ByHash(i.Item.TypeHash)))
            .Select(x => x.t is { } t
                ? new ItemModStats.Worn(x.i.Id, x.i.Item, ItemsFor(conn).Requirements(t).Str, ItemsFor(conn).Requirements(t).Dex, ItemsFor(conn).Requirements(t).Int,
                    ItemModStats.LevelRequirement(x.i.Item, t.DropLevel, ItemsFor(conn).Mods))
                : new ItemModStats.Worn(x.i.Id, x.i.Item, 0, 0, 0, 0)).ToList();
        var (inactive, active) = ItemModStats.ResolveEquipment(wornItems, level ?? conn.Pools.Level,
            active => AttributeInputs(RawAttributes(active.Select(w => w.Item)), dev)
                .Apply(c.Strength + god, c.Dexterity + god, c.Intelligence + god), before);
        // Item activation depends on equipment requirements, never the gems inside it.
        var rawAttributes = RawAttributes(active);
        var sources = ItemModStats.Plus(passives, ItemModStats.Global(active, ItemsFor(conn).Mods, Stats));
        // Clamp only this outgoing integer view; the authoritative attribute inputs remain wide beside it.
        foreach (var (name, value) in rawAttributes)
            if (Stats.Key(name) is uint key) sources[key] = AttributeModifiers.Signed(value);
        var attributes = AttributeInputs(rawAttributes, dev);
        conn.StatSourcesCache = new(sourcesKey with { Before = inactive }, new(sources), attributes, inactive, wornItems);
        return (sources, attributes, inactive, wornItems);
    }

    /// <summary>The inputs of CharacterStatSources' passive and equipment sum, compared by value (items by reference).</summary>
    internal sealed record StatSourcesKey(ushort[] Nodes, IReadOnlyDictionary<ushort, ushort> Masteries,
        GameServer.Core.Protocol.Instance.InventoryItem[] Worn, Dictionary<uint, int> Dev, int ClassRow, bool God, int Level, IReadOnlySet<uint>? Before)
    {
        public bool Matches(StatSourcesKey o) =>
            ClassRow == o.ClassRow && God == o.God && Level == o.Level && ReferenceEquals(Masteries, o.Masteries)
            && Nodes.AsSpan().SequenceEqual(o.Nodes)
            && Worn.Length == o.Worn.Length && Worn.Zip(o.Worn).All(p => p.First.Id == p.Second.Id && ReferenceEquals(p.First.Item, p.Second.Item))
            && Dev.Count == o.Dev.Count && Dev.All(kv => o.Dev.TryGetValue(kv.Key, out int v) && v == kv.Value)
            && (Before ?? new HashSet<uint>()).SetEquals(o.Before ?? new HashSet<uint>());
    }

    internal sealed record StatSourcesResult(StatSourcesKey Key, Dictionary<uint, int> Sources, AttributeModifiers Attributes,
        HashSet<uint> Inactive, List<ItemModStats.Worn> Worn);

    // Only raw attribute keys are inputs. A final pool/panel attribute must never feed the flat bucket on a rebuild.
    static AttributeModifiers AttributeInputs(IReadOnlyDictionary<string, long> raw, IReadOnlyDictionary<uint, int> dev) =>
        AttributeModifiers.FromWide(name => Stats.Key(name) is uint key && dev.TryGetValue(key, out int value)
            ? value : raw.GetValueOrDefault(name));

    internal static CharacterStats.Pools PoolsFor(ClientState conn, int level)
    {
        lock (conn.Monsters)
        {
            using var snapshot = FortificationOf(conn).Capture(conn.Clock.Elapsed.TotalSeconds);
            return BuildPoolsFor(conn, level);
        }
    }

    static CharacterStats.Pools BuildPoolsFor(ClientState conn, int level)
    {
        // The stat sources first: they decide which worn items are inactive, which the weapon profile reads.
        var snapshot = CharacterStatSources(conn, level: level);
        var sources = snapshot.Stats;
        // Detach forbidden self auras before any pool bonus is sampled. No restoration here: it needs completed pools.
        SuspendRestrictedOwnAuras(conn, new(sources));
        var weapons = WeaponProfileFor(conn);
        var activeWeapon = WeaponProfileFor(conn, activeSet: true);
        var bonus = PassiveBonus(sources, weapons, snapshot.Attributes);
        int Input(string name) => Stats.Key(name) is uint key
            ? conn.PlayerStats.TryGetValue(key, out int set) ? set : sources.GetValueOrDefault(key) : 0;
        var gear = EquippedBaseDefences(conn, Input);
        bonus = bonus with { Armour = bonus.Armour + gear.Armour, Evasion = bonus.Evasion + gear.Evasion,
            EnergyShield = bonus.EnergyShield + gear.EnergyShield, Ward = bonus.Ward + gear.Ward };
        List<AuraCatalog.Aura> auras;
        lock (conn.Monsters) auras = [.. conn.Auras.Values];
        var aura = AuraCatalog.Bonus(auras, bonus.AuraEffectPct, SkillsFor(conn), Stats);
        // Match CharacterPlayerStats: a dev value replaces the complete raw passive/equipment/aura input.
        // Read raw replacements once, preserving independent mana and incoming-hit consumers.
        Dictionary<uint, int> devStats;
        lock (conn.Monsters) devStats = new(conn.PlayerStats);
        // Final native Strength remains a presentation override; use the production attribute calculation here.
        if (Stats.Key(StrengthMana.PerFourStrength) is uint manaPerStrength
            && devStats.TryGetValue(manaPerStrength, out int perFourStrength))
            bonus = bonus with { ManaPerFourStrength = perFourStrength };
        if (Stats.Key(PlayerHitMitigation.ArmourAppliesToChaos) is uint chaosArmour
            && devStats.TryGetValue(chaosArmour, out int chaosArmourOverride))
            bonus = bonus with { ArmourAppliesToChaosDamage = chaosArmourOverride != 0 };
        if (Stats.Key(PowerOfPurpose.RawFlag) is uint mentalConditioning
            && devStats.TryGetValue(mentalConditioning, out int flag))
            bonus = bonus with { MentalConditioning = flag != 0 };
        long RawCriticalSum(string name)
        {
            if (Stats.Key(name) is not uint key) return 0;
            if (devStats.TryGetValue(key, out int value)) return value;
            return (long)sources.GetValueOrDefault(key) + (name switch
            {
                "critical_strike_chance_+%" => aura.CriticalStrikeChancePct,
                "spell_critical_strike_chance_+%" => aura.SpellCriticalStrikeChancePct - aura.CriticalStrikeChancePct,
                _ => 0,
            });
        }
        int RawCritical(string name) => (int)Math.Clamp(RawCriticalSum(name), int.MinValue, int.MaxValue);
        long attackIncrease = AttackBaseCriticalStrike.IncreasedSum(activeWeapon, RawCriticalSum);
        long spellIncrease = RawCriticalSum("critical_strike_chance_+%") + RawCriticalSum("spell_critical_strike_chance_+%");
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
            CriticalStrikeChancePct = attackIncrease,
            SpellCriticalStrikeChancePct = spellIncrease,
            AttackSpeedPct = bonus.AttackSpeedPct + aura.AttackSpeedPct,
            MovementVelocityPct = bonus.MovementVelocityPct + aura.MovementVelocityPct
        };
        // Running utility flasks and Mageblood's permanent ones combined before one strongest-per-effect pass: the same
        // effect never counts twice.
        int MagebloodGet(string name) => Stats.Key(name) is uint k ? sources.GetValueOrDefault(k) : 0;
        bonus = UtilityFlasks.Apply(bonus, UtilityStats(conn, MagebloodGet));
        if (conn.God)
            bonus = bonus with { Strength = bonus.Strength + 1000, Dexterity = bonus.Dexterity + 1000, Intelligence = bonus.Intelligence + 1000,
                Life = bonus.Life + 10_000, AttackSpeedPct = bonus.AttackSpeedPct + 1000 };
        // The overcapped-resistance flags' consumers (ResistanceOvercap), derived once per pool build from the final
        // raw inputs: the resistances after the Kitava penalty and the per-type maxima, with an explicit dev stat
        // (/set_stat) replacing its own contribution. Pools stores the derived increases, so the pools, the panel and
        // the spell consumers read them from one place and nothing counts them twice.
        // The bonus already includes Purity. Replacing one raw key must subtract its aura contribution too,
        // including an explicit zero maximum, without removing other resistance keys or altering presentation.
        var resistanceSources = new Dictionary<uint, int>(sources);
        foreach (var (key, value) in AuraCatalog.ResistanceStats(auras, bonus.AuraEffectPct, SkillsFor(conn), Stats))
            resistanceSources[key] = resistanceSources.GetValueOrDefault(key) + value;
        bonus = ResistanceOvercap.Applied(bonus, resistanceSources, devStats, ResistancePenalty(conn), Stats);
        var pools = CharacterStats.For(SkillsFor(conn).ClassFor(conn.ClassRow), level, bonus);
        // The pure base critical stats (/set_stat too) sit outside the passive increases: the weapon override replaces
        // the armed main hand's base and the unarmed base replaces the empty one, before the global increases.
        pools = pools with
        {
            AttackBlockLucky = pools.AttackBlockLucky || conn.ForceAttackBlockLucky,
            SpellBlockLucky = pools.SpellBlockLucky || conn.ForceSpellBlockLucky,
            SpellSuppressionLucky = pools.SpellSuppressionLucky || conn.ForceSpellSuppressionLucky,
            SpellSuppressionUnlucky = pools.SpellSuppressionUnlucky || conn.ForceSpellSuppressionUnlucky,
            // WeaponTypes.dat gives the main hand's base critical chance (permyriad, e.g. 830 = 8.30%); unarmed is 0.
            // A pure base stat takes its place before the global increases: the weapon override (Slayer Overwhelm,
            // Blind Prophet's Sightless) replaces the armed weapon's base, already with its local mods, and unarmed
            // melee uses its own Behemoth base. Neither base is a spell value; the spell route stays on SpellCriticalStrike.
            AttackCriticalStrikeChance = AttackBaseCriticalStrike.Chance(!activeWeapon.Unarmed,
                activeWeapon.Critical, pools.CriticalStrikeChancePct, RawCritical),
            SpellCriticalStrikeChance = SpellCriticalStrike.Chance(DamageModel.BaseSpellCriticalStrikeChancePerMyriad,
                RawCritical, playerIncrease: pools.SpellCriticalStrikeChancePct)
        };
        // Only the active usable shield contributes. The alternate set and unmet requirements cannot grant block.
        int shieldBlock = ActiveOffHand(conn) is { Type.Class: "Shield" } shield
            ? Input("block_chance_from_equipped_shield_is_%") is > 0 and var replacement
                ? replacement : ItemsFor(conn).ShieldBlock(shield.Type) : 0;
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
        conn.Ward.SetMaximum(conn.PlayerLife > 0 ? pools.Ward : 0);
        conn.AppliedAdrenaline = conn.Antiquarian.Adrenaline(conn.Clock.Elapsed.TotalSeconds);
        ConfigureFortificationRecovery(conn, pools.Life);
        return pools with { FortificationRevision = FortificationOf(conn).Revision };
    }

    // What the allocated passives add to the pools: attributes (base_* and additional_*, all attributes and the pairs),
    // flat and increased life, mana and accuracy, flat and increased armour and evasion, energy shield and melee physical
    // damage, the base resistances (base_resist_all_elements_% applies to fire, cold and lightning), the shield-specific
    // block nodes when a shield is equipped, critical strike chance and multiplier, and reservation efficiency and aura
    // effect.
    static CharacterStats.Bonus PassiveBonus(Dictionary<uint, int> stats, WeaponProfile weapons, AttributeModifiers attributes)
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
        long criticalChance = AttackBaseCriticalStrike.IncreasedSum(weapons, name => Get(name));
        long spellCriticalChance = (long)Get("critical_strike_chance_+%") + Get("spell_critical_strike_chance_+%");
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
        // Mastery's conversions, plus its two target-conditional "more" effects (below; TargetAccuracy applies them to
        // the hit's own target at the roll). Other conditional accuracy stays unmodelled. Unarmed attacks count as dual
        // wielding, as they do for attack speed.
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
        // The Accuracy Mastery's two target-conditional effects (11-skills.md panels): final "more" accuracy for a hit's
        // own target, which TargetAccuracy applies at the roll. Kept out of the additive pools, so the panel and
        // /accuracy keep showing only the unconditional rating and never claim a target condition statically.
        int accuracyMoreVsUnique = Get("accuracy_rating_+%_final_vs_unique_enemies_from_mastery");
        int accuracyMoreCloseRange = Get("accuracy_rating_+%_final_vs_enemies_in_close_range_from_mastery");
        int allMax = Get("additional_maximum_all_resistances_%"), allElemMax = Get("additional_maximum_all_elemental_resistances_%");
        // The cold-and-lightning pair-resistance stat (Sea Legs' +40%): one input stat that both elements' pools take.
        int pairRes = Get(ResistanceOvercap.ColdAndLightningResistance);
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
        long attackBlockLuck = (long)Get("base_block_luck") + Get("attack_block_luck") + Get("base_attack_block_luck");
        long spellBlockLuck = (long)Get("base_block_luck") + Get("spell_block_luck") + Get("base_spell_block_luck");
        return new CharacterStats.Bonus(
            Attributes: attributes, Ward: Get("base_ward"), WardPct: Get("ward_+%") + Get("global_defences_+%") + Get("combined_global_defences_+%"),
            Life: Get("base_maximum_life"), LifePct: Get("maximum_life_+%"), Mana: Get("base_maximum_mana"), ManaPct: Get("maximum_mana_+%"),
            ManaPerFourStrength: Get(StrengthMana.PerFourStrength),
            Armour: Get("base_physical_damage_reduction_rating"), ArmourPct: armourPct,
            Evasion: Get("base_evasion_rating"), EvasionPct: evasionPct,
            SharedDefencesPct: defencesPct, IronReflexes: Get("keystone_iron_reflexes") != 0,
            MentalConditioning: Get(PowerOfPurpose.RawFlag) != 0,
            ChaosInoculation: Get("keystone_chaos_inoculation") != 0,
            ArmourAppliesToChaosDamage: Get(PlayerHitMitigation.ArmourAppliesToChaos) != 0,
            EnergyShield: Get("base_maximum_energy_shield"),
            EnergyShieldPct: Get("maximum_energy_shield_+%") + Get("global_defences_+%") + Get("combined_global_defences_+%"),
            MeleePhysicalDamagePct: Get("melee_physical_damage_+%"),
            FireResistance: allRes + Get("base_fire_damage_resistance_%"),
            ColdResistance: allRes + Get("base_cold_damage_resistance_%") + pairRes,
            LightningResistance: allRes + Get("base_lightning_damage_resistance_%") + pairRes,
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
            AttackBlockLucky: attackBlockLuck > 0, AttackBlockUnlucky: attackBlockLuck < 0,
            SpellBlockLucky: spellBlockLuck > 0, SpellBlockUnlucky: spellBlockLuck < 0,
            AttackSpeedPct: attackSpeed, AttackSpeedMorePct: attackSpeedMore,
            CriticalStrikeChancePct: criticalChance, SpellCriticalStrikeChancePct: spellCriticalChance,
            CriticalStrikeMultiplier: criticalMultiplier, SpellCriticalStrikeMultiplier: spellCriticalMultiplier,
            Accuracy: accuracyFlat, AccuracyPct: accuracyPct,
            AccuracyMoreVsUniquePct: accuracyMoreVsUnique,
            AccuracyMoreInCloseRangePct: accuracyMoreCloseRange,
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
        int points = Progression.PassivePoints(level) + conn.ExtraPassivePoints + AscendancyOrdinaryPoints(conn);
        TrimOrdinaryPassives(conn, conn.Passives, points);
    }

    static void ValidateOrdinaryPassives(ClientState conn, IEnumerable<ushort> nodes, AscendancyProgress ascendancy) =>
        OrdinaryPassives.Validate((byte)SkillsFor(conn).ClassFor(conn.ClassRow).IntegerId,
            ascendancy.Selection?.Family ?? AscendancyFamily.Normal, nodes, ascendancy.Allocated);

    // A batch may list its leaf before its prerequisite. Removing the last allocation alone could orphan that leaf.
    // Keep the historical last-first preference, but only remove a node whose remaining tree is still connected.
    static void TrimOrdinaryPassives(ClientState conn, List<ushort> nodes, int points,
        AscendancyProgress? ascendancy = null)
    {
        ascendancy ??= conn.MainAscendancy;
        ValidateOrdinaryPassives(conn, nodes, ascendancy);
        while (nodes.Count > points + PassivesFor(conn).PointsGranted(nodes))
        {
            int removable = -1;
            for (int i = nodes.Count - 1; i >= 0; i--)
            {
                try
                {
                    ValidateOrdinaryPassives(conn, nodes.Where((_, index) => index != i), ascendancy);
                    removable = i;
                    break;
                }
                catch (AscendancyRuleException) { }
            }
            if (removable < 0) throw new AscendancyRuleException("Ordinary passives cannot be reduced to the level's point budget");
            nodes.RemoveAt(removable);
        }
    }

    // The character's passive skill tree (0x33): its level (from which the client counts the points), its class (the
    // tree's start) and the allocated nodes.
    static async Task SendPassives(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
    {
        // A queued publication must not restore a tree captured before a later allocation/refund or level change.
        await Send(remote, stream, ciphers, [], "0x0033 current passive skill tree",
            atGate: () => PassivesMessage(conn).Bytes);
    }

    static (byte[] Bytes, string Label) PassivesMessage(ClientState conn)
    {
        lock (conn.Monsters)
        {
            var (points, refundPoints, nodes, masteries) = PassiveState(conn);
            int unspent = points + PassivesFor(conn).PointsGranted(nodes) + AscendancyOrdinaryPoints(conn) - nodes.Count;
            return (InstanceEntities.CharacterPassives((byte)conn.Pools.Level, PackedClass(conn), [.. nodes, .. conn.MainAscendancy.Allocated],
                    questPoints: conn.ExtraPassivePoints, refundPoints: refundPoints, ascendancyPoints: (ushort)conn.MainAscendancy.EarnedPoints, masteryEffects: masteries),
                $"0x0033 passive skill tree: level {conn.Pools.Level}, {points} skill point(s) ({conn.ExtraPassivePoints} extra), " +
                $"{refundPoints} refund point(s), {nodes.Count} allocated, {unspent} unspent; " +
                $"ascendancy {conn.MainAscendancy.Selection?.Family}/{conn.MainAscendancy.Selection?.Id}, {conn.MainAscendancy.EarnedPoints} earned, {conn.MainAscendancy.Allocated.Count} nodes");
        }
    }

    // The native family selector is a world stat, independently of the character's stored league.
    internal static IReadOnlyDictionary<uint, int>? LeagueStatsFor(AscendancyProgress progress) =>
        progress.Selection?.Family == AscendancyFamily.Phrecia ? new Dictionary<uint, int> { [21146] = 1 } : null;

    static int AscendancyOrdinaryPoints(ClientState conn) =>
        AscendancyRules.OrdinaryPointsGranted((byte)SkillsFor(conn).ClassFor(conn.ClassRow).IntegerId, conn.MainAscendancy);

    static byte PackedClass(ClientState conn)
    {
        byte baseClass = (byte)SkillsFor(conn).ClassFor(conn.ClassRow).IntegerId;
        byte number = AscendancyRules.Validate(baseClass, conn.MainAscendancy)?.ClassNo ?? 0;
        return CharacterClassByte.Encode(baseClass, number);
    }

    // Allocates (or, with allocate false, refunds) passive nodes: checked against the tree's nodes and the character's
    // points and complete ordinary/ascendancy connectivity, then saved, applied to the stats and sent back (0x33).
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
        AscendancyProgress ascendancyNow;
        CharacterSaves.PassiveProgressSnapshot? expectedProgress;
        // Validate, save and publish under one connection lock; level changes cannot split these phases.
        // All network sends remain outside it (connection lock -> database, never connection lock -> cipher gate).
        lock (conn.Monsters)
        {
            var next = new List<ushort>(conn.Passives);
            var effects = new Dictionary<ushort, ushort>(conn.MasteryEffects);
            byte baseClass = (byte)SkillsFor(conn).ClassFor(conn.ClassRow).IntegerId;
            var ascendancy = conn.MainAscendancy;
            expectedProgress = conn.SavedPassiveProgress;
            var ascendancyNodes = new List<ushort>();
            foreach (var n in nodes)
            {
                if (PassivesFor(conn).Find(n) is not { } node) { error = $"unknown passive {n}"; break; }
                if (node.IsAscendancy)
                {
                    if (goldRespec || !allocate && !refundPoints) { error = "Ascendancy passives require refund points, not gold or a free reset"; break; }
                    if (masteryEffect is not null) { error = "An ascendancy passive is not an ordinary mastery"; break; }
                    ascendancyNodes.Add(n);
                    continue;
                }
                if (masteryEffect is { } raw)
                {
                    if (!allocate) { error = "a mastery effect cannot be refunded on its own"; break; }
                    var effect = PassivesFor(conn).EffectsFor(n).FirstOrDefault(e => e.Hash == raw || e.Row == raw);
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
            int ordinaryBudget = Progression.PassivePoints(conn.Pools.Level) + conn.ExtraPassivePoints + PassivesFor(conn).PointsGranted(next);
            if (error is null && ascendancyNodes.Count > 0)
            {
                try
                {
                    if (allocate)
                        ascendancy = AscendancyRules.Allocate(baseClass, ascendancy, ascendancyNodes);
                    else
                    {
                        var result = AscendancyRules.Refund(baseClass, ascendancy, ascendancyNodes, next.Count, ordinaryBudget);
                        ascendancy = result.Progress;
                        if (refundPoints) refundsSpent += result.Cost;
                    }
                }
                catch (AscendancyRuleException e) { error = e.Message; }
            }
            if (error is null)
            {
                try { ValidateOrdinaryPassives(conn, next, ascendancy); }
                catch (AscendancyRuleException e) { error = e.Message; }
            }
            if (error is null && next.Count > ordinaryBudget + AscendancyRules.OrdinaryPointsGranted(baseClass, ascendancy))
                error = $"not enough passive skill points ({Progression.PassivePoints(conn.Pools.Level) + conn.ExtraPassivePoints} at level {conn.Pools.Level}, {next.Count} wanted)";
            if (error is null && refundPoints && refundsSpent > conn.RefundPoints)
                error = $"not enough refund points ({refundsSpent} needed, {conn.RefundPoints} held)";
            if (goldRespec) cost = PassivesFor(conn).GoldRespecCost(nodes, conn.Pools.Level);
            now = next;
            ascendancyNow = ascendancy;
            effectsNow = effects;
            refundsNow = (ushort)(conn.RefundPoints - (refundPoints && error is null ? refundsSpent : 0));
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
                    CharacterSaves.SetMainAscendancy(CharacterName(conn), ascendancyNow, now, effectsNow, refundPoints ? refundsNow : null,
                        expected: expectedProgress);
                    return null;
                });
            if (error is null)
            {
                conn.Passives = now;
                conn.MainAscendancy = ascendancyNow;
                conn.MasteryEffects = effectsNow;
                conn.RefundPoints = refundsNow;
                conn.SavedPassiveProgress = new(conn.MainAscendancy, now, effectsNow, refundsNow);
                conn.Pools = PoolsFor(conn, conn.Pools.Level);
                conn.ApplyMovementSpeed();
                conn.PlayerLife = Math.Min(conn.PlayerLife, conn.PlayerMaxLife);
                conn.PlayerMana = Math.Min(conn.PlayerMana, (uint)UnreservedMana(conn));
            }
            now = [.. conn.Passives];
            effectsNow = new(conn.MasteryEffects);
            refundsNow = conn.RefundPoints;
        }
        Console.WriteLine($"[gateway] {remote} passives {(allocate ? "allocate" : "refund")} [{string.Join(", ", nodes.Select(n => $"{n} {PassivesFor(conn).Find(n)?.Name}"))}]" +
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
    // points from current experience and the account's level (Auth set_level, for the character list).
    internal static async Task GainExperience(string remote, NetworkStream stream, ConnectionCiphers ciphers,
        ClientState conn, uint gain, string why, Func<ControlRequest, Task<ControlResponse>>? control = null)
    {
        if (RecordExperienceGain(conn, gain) is { } change)
            await CompleteExperienceGain(remote, stream, ciphers, conn, change, why, control);
    }

    // A kill's experience goes to the gems too: every one socketed in an equipped item, in either weapon set, gains 10% of the
    // player's gain (support gems too, a gem in an inactive item, at its last level or over its cap nothing), up to the
    // threshold of the first level whose requirement the character does not meet â€” the gem's level stalls there, as the
    // game's does (the client would level the gem to whatever its experience says, so the experience stops just below that
    // threshold; a gem already past it, after /set_level down, keeps its level and experience and is disabled instead) and
    // the last level's threshold. Written into the gem item inside the socket list â€” the equipped
    // item's own component has no skill gems. Sent as the socket's equipment row 0x00bf item change, the same item id removed
    // and added again (the replace idiom: an addition of a known id alone is dropped as a duplicate, so the client would keep
    // the stale gem), so the client's gem XP bar and tooltip follow; the next save keeps it.
    static async Task GainGemExperience(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, uint gain)
    {
        var (packets, leveled) = CommitGemExperience(conn, gain);
        foreach (var packet in packets) await Send(remote, stream, ciphers, packet, "gem experience");
        if (leveled) await SendSkills(remote, stream, ciphers, conn);
    }

    static (List<byte[]> Packets, bool Leveled) CommitGemExperience(ClientState conn, uint gain)
    {
        lock (conn.Monsters)
        {
            if (HardcoreProgressBlocked(conn)) return ([], false);
            int share = conn.Ruthless ? RuthlessGameplay.GemExperience(gain, RuthlessSettings.Current.GemExperienceMultiplier,
                ref conn.RuthlessGemExperienceCarry) : (int)Math.Min(gain / 10, int.MaxValue);
            if (share == 0) return ([], false);
            var items = ItemsOf(conn);
            var changed = items.ChangeSocketedGems((itemId, gem) =>
            {
                if (IsInactive(conn, itemId) || ItemsFor(conn).ByHash(gem.TypeHash) is not { } baseType || ItemsFor(conn).Gem(baseType) is not { } type) return null;
                // -1 is a corrupted gem over its cap (GemType.Level), and at its last level's threshold a gem gains no more.
                int lastLevel = Math.Min(SkillsFor(conn).MaxLevelAt(type.Variant(gem.GemVariant).Effect, conn.Pools.Level), type.MaxLevel);
                int cap = lastLevel < type.MaxLevel ? type.ExperienceFor(lastLevel + 1) - 1 : type.ExperienceFor(type.MaxLevel);
                if (gem.GemExperience < 0 || gem.GemExperience >= cap) return null;
                return gem with { GemExperience = (int)Math.Min((long)gem.GemExperience + share, cap) };
            });
            bool leveled = false;
            foreach (var (before, after) in changed.SelectMany(c => c.Gems))
            {
                if (ItemsFor(conn).GemLevel(after) > ItemsFor(conn).GemLevel(before))
                {
                    leveled = true;
                    Console.WriteLine($"[gateway] {CharacterName(conn)}'s {ItemsFor(conn).GemName(after)} levels to {ItemsFor(conn).GemLevel(after)}");
                }
                else
                    Console.WriteLine($"[gateway] {CharacterName(conn)}'s {ItemsFor(conn).GemName(after)} gains experience " +
                        $"+{after.GemExperience - before.GemExperience} ({after.GemExperience}, level {ItemsFor(conn).GemLevel(after)})");
            }
            var packets = changed.GroupBy(c => c.Row).Select(row =>
                InstanceItems.InventoryChange(PlayerInventories.InventoryId(row.Key), items.NextRevision(row.Key),
                    [.. row.Select(c => c.Item.Id)], [.. row.Select(c => c.Item)])).ToList();
            return (packets, leveled);
        }
    }
}
