using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using DamageModel = GameServer.Core.World.DamageModel;
using DamagePacket = GameServer.Core.World.DamagePacket;
using DamageOverTime = GameServer.Core.World.DamageOverTime;
using DamageType = GameServer.Core.World.DamageType;
using DamageTypes = GameServer.Core.World.DamageTypes;

public static partial class Game
{
    /// <summary>Fills this type's game data at startup (the world loader, before the Gateway listens).</summary>
    public static void LoadMonsterModsData()
    {
        MonsterModRolls = new MonsterModCatalog(Items.Mods);
        LoadMonsterChargesData();
        LoadMonsterAurasData();
    }

    static bool MonsterOnLowLife(SpawnedMonster monster) => !monster.Dead && monster.Life > 0
        && (ulong)monster.Life * 2 <= monster.MaxLife;
    // Monster-domain Mods.dat rows, their names and effects; loaded with the first magic or rare monster.
    public static MonsterModCatalog MonsterModRolls = default!;

    // Per instance, the mods its magic monsters share: "all monsters in an instance that are the same or very similar
    // will have the same magic monster modifier" (3.19.0 patch notes). Very similar: the same name (The Coast's
    // Drowned are several MonsterVarieties rows), as long as the affix can roll on it (MonsterModCatalog.SharedMagic).
    public static readonly Store<InstanceState, Dictionary<string, MonsterModSet>> MagicModsOf = new();

    /// <summary>The mods a new monster gets: <paramref name="kept"/> (a monster of a kept instance, a /mob_mods
    /// choice) when it has any, else freshly rolled for its rarity. Area packs pass <paramref name="alone"/> false;
    /// their magic monsters share one roll per monster name in the instance.</summary>
    public static MonsterModSet MonsterModsFor(Player player, MonsterCatalog.Variety variety, byte level, MonsterRarity rarity,
        MonsterModSet? kept, bool alone) =>
        MonsterModsFor(player, variety, level, rarity, kept, alone, extraMagicMods: 0);

    /// <summary>Overload allowing an area/cell to grant additional modifiers to magic monsters
    /// (<c>map_magic_monster_num_additional_modifiers</c>).</summary>
    public static MonsterModSet MonsterModsFor(Player player, MonsterCatalog.Variety variety, byte level, MonsterRarity rarity,
        MonsterModSet? kept, bool alone, int extraMagicMods)
    {
        if (kept is { Mods.Count: > 0 } || kept is not null && rarity == MonsterRarity.Normal) return kept;
        var rolls = MonsterModRolls;
        int magicExtra = rarity == MonsterRarity.Magic
            ? Math.Max(0, extraMagicMods + (MapModsOf(player)?.Get(Stat.MapMagicMonsterNumAdditionalModifiers) ?? 0))
            : 0;
        if (rarity != MonsterRarity.Magic) return rolls.Roll(variety, level, rarity, AreaRng(player), alone);
        if (magicExtra == 0)
        {
            if (alone) return rolls.Roll(variety, level, rarity, AreaRng(player), alone);
            var shared = MagicModsOf.GetValue(player.Instance, _ => []);
            lock (shared) return rolls.SharedMagic(shared, variety, level, AreaRng(player));
        }
        int want = 1 + magicExtra;
        if (alone) return RollMagicWithExtra(rolls, variety, level, want, alone: true, AreaRng(player));
        var sharedExtra = MagicModsOf.GetValue(player.Instance, _ => []);
        string key = $"{variety.Name}|+{magicExtra}";
        lock (sharedExtra)
        {
            var tags = rolls.TagsOf(variety, MonsterRarity.Magic, alone: false);
            bool Fits(MonsterModSet s) => s.Affixes.All(a => rolls.Mods.ByHash(a.Hash) is { } m && m.Level <= level && m.WeightFor(tags) > 0);
            if (sharedExtra.TryGetValue(key, out var existing) && Fits(existing)) return existing;
            string scopedKey = key + "|" + string.Join(",", tags.Order(StringComparer.Ordinal));
            if (!sharedExtra.TryGetValue(scopedKey, out existing))
                sharedExtra[scopedKey] = existing = RollMagicWithExtra(rolls, variety, level, want, alone: false, AreaRng(player));
            sharedExtra.TryAdd(key, existing);
            return existing;
        }
    }

    static MonsterModSet RollMagicWithExtra(MonsterModCatalog rolls, MonsterCatalog.Variety variety, int level, int want, bool alone, Random rng)
    {
        var set = rolls.Roll(variety, level, MonsterRarity.Magic, rng, alone, count: want);
        var currentAffixes = set.Affixes.ToList();
        if (currentAffixes.Count >= want) return set;
        // Rarity.dat caps magic prefix+suffix at 1+1=2; when a map/cell grants +2 additional magic mods (want=3),
        // draw remaining distinct-family mods from the monster's pool so all requested affixes apply.
        var tags = rolls.TagsOf(variety, MonsterRarity.Magic, alone);
        var pool = rolls.Pool(tags, level, rolls.RollUnsupported);
        var chosen = currentAffixes.Select(a => rolls.Mods.ByHash(a.Hash)).OfType<ModCatalog.Mod>().ToList();
        var all = set.Mods.ToList();
        while (chosen.Count < want)
        {
            var allowed = pool.Where(p => !chosen.Any(c => c.Hash == p.Mod.Hash || c.Families.Intersect(p.Mod.Families).Any())).ToList();
            if (allowed.Count == 0) break;
            long total = allowed.Sum(p => (long)p.Weight);
            if (total <= 0) break;
            long pick = rng.NextInt64(total);
            var picked = allowed[^1].Mod;
            foreach (var (mod, weight) in allowed)
            {
                if (pick < weight) { picked = mod; break; }
                pick -= weight;
            }
            chosen.Add(picked);
            all.Add(picked.Roll(ModSection.Explicit, rng));
        }
        return rolls.Create(all, set.NameWords, MonsterRarity.Magic, level);
    }

    /// <summary>The stats the client needs for the mods (it applies no modifier of its own to a monster, 14 §2): the
    /// rarity's life stats and the final maximum_life its life bars read, movement_velocity_+% for its walk (the server
    /// walks it at the same speed), actor_scale_+% for its size and life_regeneration_rate_per_minute_%.</summary>
    public static IEnumerable<(uint Key, int Value)> MonsterModClientStats(MonsterModEffects e, uint maxLife, int baseLife)
    {
        if (e[Stat.MonsterLifePlusPctFinalFromRarity] is int rarity and not 0) yield return (Stat.MonsterLifePlusPctFinalFromRarity, rarity);
        if (e[MonsterModCatalog.RarityTableLifeStat] is int table and not 0 && MonsterModCatalog.RarityTableLifeStat is var tk)
            yield return (tk, table);
        if (maxLife != baseLife) yield return (Stat.MaximumLife, (int)maxLife);
        if (e.MovementPct != 0) yield return (Stat.MovementVelocityPlusPct, e.MovementPct);
        if (e.ActorScalePct != 0 && Stat.ActorScalePlusPct is var scale) yield return (scale, e.ActorScalePct);
        if (e[Stat.LifeRegenerationRatePerMinutePct] is int regen and not 0 && Stat.LifeRegenerationRatePerMinutePct is var rk)
            yield return (rk, regen);
    }

    /// <summary>The monster's own part of its hit on the player, after the roll and before the player's defences: its
    /// critical strike (base 5% for attacks or skill SpellCritChance, scaled by critical_strike_chance_+% and power
    /// charges; × Monster.ot's 130% multiplier less the player's <paramref name="selfCriticalReduction"/>), Frenzy
    /// charge more damage (+4% per charge), and Executioner's missing-life scaling. Call in the area's frame.</summary>
    public static DamagePacket MonsterModHit(SpawnedMonster m, DamagePacket rolled, uint playerLife, uint playerMaxLife, Random random,
        out bool critical, Func<int>? selfCriticalReduction = null, double now = double.NaN, bool cannotCrit = false,
        int additionalCritical = 0, Func<int>? extraCriticalMultiplier = null, int baseCritPermyriad = -1)
    {
        var e = m.Effects;
        // additionalCritical: the target's Brittle (PlayerBrittleCriticalChance), in hundredths as the mods' chance.
        int basePermyriad = baseCritPermyriad >= 0 ? baseCritPermyriad : MonsterBaseCritChancePermyriad;
        int baseCrit = e.CriticalChanceWith(basePermyriad);
        int chance = baseCrit + additionalCritical;
        critical = !cannotCrit && chance > 0 && !(m.HasCurses && m.Curses.PreventsCritical(now))
            && random.Next(10_000) < chance;
        int extraMult = critical
            ? Math.Max(0, (MonsterModEffects.CriticalMultiplier - 100) + e[Stat.BaseCriticalStrikeMultiplierPlus] + (extraCriticalMultiplier?.Invoke() ?? 0))
            : 0;
        int selfReduction = critical ? Math.Clamp(selfCriticalReduction?.Invoke() ?? 0, 0, 100) : 0;
        double factor = critical ? (100 + extraMult * (100 - selfReduction) / 100.0) / 100.0 : 1;
        factor *= MonsterFrenzyDamageFactor(m);
        factor *= Math.Max(0, 1 + e[m.Rarity is MonsterRarity.Rare or MonsterRarity.Unique
            ? Stat.EnfeebleDamagePlusPctVsRareOrUniqueFinal : Stat.EnfeebleDamagePlusPctFinal] / 100.0);
        int executioner = e.ExecutionerPct(playerLife, playerMaxLife);
        int spiderDamage = m.SpiderWebs.Damage(now);
        int lowLife = MonsterOnLowLife(m) ? e[Stat.DamagePlusPctWhenOnLowLife] : 0;
        if (executioner > 0 || spiderDamage != 0 || lowLife != 0)
        {
            int increased = e[Stat.DamagePlusPct] + e[Stat.PhysicalDamagePlusPct];
            factor *= 100d + increased > 0 ? Math.Max(0, 100d + increased + executioner + spiderDamage + lowLife) / (100d + increased) : 0;
        }
        return factor == 1 ? rolled : rolled.Scale(factor);
    }

    /// <summary>
    /// The damage a hit of the player deals to a monster, rolled: each type less the resistance the monster's mods give
    /// (capped at Monster.ot's 75%), the physical part less its physical damage reduction, then its damage taken
    /// (Berserking's reduction, Spell Resistant's less spell damage). Blocking is rolled before (<see cref="MonsterBlocks"/>).
    /// Base and mod resistance compose before the cap; penetration subtracts once after that cap.
    /// </summary>
    public static int MonsterTakes(SpawnedMonster m, DamagePacket hit, Random random, bool spell,
        Penetration.PerType pen = default, double now = double.NaN) => MonsterTakesRolled(m, hit.Roll(random), spell, pen, now, random);

    /// <summary>A monster's resistance to one type of the player's hit: base and mod resistance compose before
    /// Monster.ot's 75% cap; penetration subtracts once after that cap, the cold one against a chilled monster at
    /// <paramref name="now"/> too (without a time it is not chilled).</summary>
    public static DamageModel.ResistanceValue MonsterHitResistance(SpawnedMonster m, DamageType type,
        Penetration.PerType pen, double now = double.NaN)
    {
        var resistance = HitMitigation.Resistance(Monsters, m.Variety, m.Level, type);
        // Doryani's Prototype ("Nearby Enemies have Lightning Resistance equal to yours"): while a wearer keeps his own
        // resistance on the monster (EnemyDebuffsTick's zone), the whole composed value is replaced with it — negative
        // and zero included — and only the cap and penetration still apply. Zero-alloc when no source values are set;
        // multiple wearers deterministically take the lowest resistance.
        if (type == DamageType.Lightning && m.HasSourceValues)
        {
            int? lowest = null;
            foreach (var (_, encoded) in m.SourceValues(LightningResistEqualsYours))
            {
                int v = encoded - DoryaniResistOffset;
                if (lowest is null || v < lowest.Value) lowest = v;
            }
            if (lowest is int ownResist)
                return resistance with { Value = ownResist, Maximum = m.Effects.MaximumResistance(type), Penetration = pen.Of(type) };
        }
        return resistance with { Value = resistance.Value + m.Effects.Resistance(type) + MonsterExposure(m, type, now)
                + ScorchedResistance(m, type, now)
                + (type == DamageType.Physical ? 0 : m.SpiderWebs.Resistance(now)),
            Maximum = m.Effects.MaximumResistance(type), Penetration = pen.Of(type) + (type == DamageType.Cold
                && m.Ailments.Magnitude(MonsterElementalAilments.Kind.Chill, now) > 0 ? pen.ColdVsChilled : 0) };
    }

    // Already rolled: no extra RNG or per-component rounding, including ordinary bow contact snapshots.
    public static int MonsterTakesRolled(SpawnedMonster m, DamagePacket rolled, bool spell, Penetration.PerType pen = default,
        double now = double.NaN, Random? random = null)
        => MonsterTakesMitigated(m, MitigateMonsterHit(m, rolled, now, pen), spell, now, random: random);

    /// <summary>
    /// The damage a resisted hit of the player deals to a monster: the physical part less its physical damage reduction,
    /// then its damage taken (Berserking's reduction, Spell Resistant's less spell damage, its shock and Glacial Wave's
    /// chill, the Intimidate or Unnerve on it at <paramref name="now"/>; without a time those do not count). Blocking is rolled before
    /// (<see cref="MonsterBlocks"/>).
    /// </summary>
    public static int MonsterTakesMitigated(SpawnedMonster m, DamagePacket mitigated, bool spell, double now = double.NaN,
        bool ignorePhysicalReduction = false, Random? random = null)
        => MonsterTakesMitigatedCore(m, mitigated, spell, now, 0, ignorePhysicalReduction, random);

    public static int MonsterTakesTrapOrMineHit(SpawnedMonster m, DamagePacket mitigated, bool spell, double now,
        Random? random = null, bool ignorePhysicalReduction = false)
        => MonsterTakesMitigatedCore(m, mitigated, spell, now, BearTrapDamageTaken(m, now), ignorePhysicalReduction, random);

    static int MonsterTakesMitigatedCore(SpawnedMonster m, DamagePacket mitigated, bool spell, double now,
        int additionalTakenPct, bool ignorePhysicalReduction, Random? random, int overwhelm = 0)
    {
        // The shrine aura's base_cannot_be_damaged (MonsterAuraEffect, temporary stats by name): the Divine shrine's
        // monsters cannot be damaged while it is unclaimed, as the row's own buff promises.
        if (m.Effects[Stat.BaseCannotBeDamaged] > 0) return 0;
        var e = m.Effects;
        double physical = mitigated[DamageType.Physical].Min;
        double physReduction = HitMitigation.PhysicalReduction(Monsters, m, physical,
            e.PhysicalReduction - CrushReduction(m, now), MonsterArmourEnabled);
        if (ignorePhysicalReduction && physReduction > 0) physReduction = 0;
        // Overwhelm takes its percent off the reduction the hit meets, and never turns it into damage taken.
        if (overwhelm > 0 && physReduction > 0) physReduction = Math.Max(0, physReduction - overwhelm);
        double physicalTaken = physical * Math.Max(0, 1 - physReduction / 100.0)
            * (1 + MaimedPhysicalTaken(m, now) / 100.0);
        double total = mitigated.MinTotal - physical + physicalTaken;
        // The player's debuffs on it add to its own increased damage taken (MonsterDamageTaken): his enemy auras' elemental
        // increase on fire, cold and lightning only, his Withered on chaos only, and Pride's more Physical Damage on the
        // physical part.
        double factor = MonsterDamageTaken(m, spell, now, elemental: false, hit: true, chaos: false, increasedPct: additionalTakenPct);
        double chaosDamage = mitigated[DamageType.Chaos].Min;
        double witheredPct = chaosDamage > 0 ? WitheredTaken(m, now) : 0;
        double withered = witheredPct == 0 ? 0 : chaosDamage * (MonsterDamageTaken(m, spell, now,
            elemental: false, hit: true, chaos: false, increasedPct: additionalTakenPct + witheredPct) - factor);
        double elementalDamage = mitigated[DamageType.Fire].Min + mitigated[DamageType.Cold].Min + mitigated[DamageType.Lightning].Min;
        double elementalFactor = m.TakenFromPlayer.Elemental == 0 ? factor
            : MonsterDamageTaken(m, spell, now, elemental: true, hit: true, chaos: false, increasedPct: additionalTakenPct);
        // Pride's more Physical Damage and Flesh and Stone's Blood Stance (TakenFromPlayer.Physical) apply to the
        // physical part only, never to the elemental share's fallback factor.
        double physicalFactor = (1 + Math.Max(0, m.PridePhysicalMore) / 100.0)
            * (1 + Math.Max(0, FleshTaken(m, now)) / 100.0)
            * (1 + e[Stat.PhysicalDamageTakenPlusPct] / 100.0);
        // Its fire, cold and lightning damage taken (Izaro's statues' buffs, Covered in Ash) on those parts.
        double typed = mitigated[DamageType.Fire].Min * ElementalHitTakenPercent(e, m, DamageType.Fire, now)
            + mitigated[DamageType.Cold].Min * ElementalHitTakenPercent(e, m, DamageType.Cold, now)
            + mitigated[DamageType.Lightning].Min * ElementalHitTakenPercent(e, m, DamageType.Lightning, now);
        double damage = (total - elementalDamage) * factor + elementalDamage * elementalFactor + withered
            + physicalTaken * factor * (physicalFactor - 1) + typed / 100.0 * elementalFactor;
        if (spell && e[Stat.BaseSpellSuppressionChancePct] is int chance and > 0
            && GameServer.Core.World.RandomRolls.Chance(chance, 100, random ?? Random.Shared))
        {
            int prevented = Math.Clamp(DamageModel.SpellSuppressionEffect + e[Stat.BaseSpellDamagePctSuppressed], 0, 100);
            damage *= (100 - prevented) / 100d;
        }
        return (int)Math.Round(damage);
    }

    /// <summary>Computes the explicit chaos-damage portion of <paramref name="finalDamage"/> (after crits, suppression,
    /// and global multipliers, before physical Impale reflection) so <see cref="AbsorbMonsterEnergyShieldLocked"/> lets
    /// chaos bypass monster Energy Shield without any mutable side-channel state.</summary>
    public static int MonsterHitChaosDamage(SpawnedMonster m, DamagePacket mitigated, bool spell, double now,
        int finalDamage, int additionalTakenPct = 0, bool ignorePhysicalReduction = false)
    {
        double rawChaos = mitigated[DamageType.Chaos].Min;
        if (finalDamage <= 0 || rawChaos <= 0 || m.Effects[Stat.BaseCannotBeDamaged] > 0) return 0;
        var e = m.Effects;
        double physical = mitigated[DamageType.Physical].Min;
        double physReduction = HitMitigation.PhysicalReduction(Monsters, m, physical,
            e.PhysicalReduction - CrushReduction(m, now), MonsterArmourEnabled);
        if (ignorePhysicalReduction && physReduction > 0) physReduction = 0;
        double physicalTaken = physical * Math.Max(0, 1 - physReduction / 100.0)
            * (1 + MaimedPhysicalTaken(m, now) / 100.0);
        double total = mitigated.MinTotal - physical + physicalTaken;
        double factor = MonsterDamageTaken(m, spell, now, elemental: false, hit: true, chaos: false, increasedPct: additionalTakenPct);
        double witheredPct = WitheredTaken(m, now);
        double chaosFactor = witheredPct == 0 ? factor
            : MonsterDamageTaken(m, spell, now, elemental: false, hit: true, chaos: false,
                increasedPct: additionalTakenPct + witheredPct);
        double withered = rawChaos * (chaosFactor - factor);
        double elementalDamage = mitigated[DamageType.Fire].Min + mitigated[DamageType.Cold].Min + mitigated[DamageType.Lightning].Min;
        double elementalFactor = m.TakenFromPlayer.Elemental == 0 ? factor
            : MonsterDamageTaken(m, spell, now, elemental: true, hit: true, chaos: false, increasedPct: additionalTakenPct);
        double physicalFactor = (1 + Math.Max(0, m.PridePhysicalMore) / 100.0)
            * (1 + Math.Max(0, FleshTaken(m, now)) / 100.0)
            * (1 + e[Stat.PhysicalDamageTakenPlusPct] / 100.0);
        double typed = mitigated[DamageType.Fire].Min * ElementalHitTakenPercent(e, m, DamageType.Fire, now)
            + mitigated[DamageType.Cold].Min * ElementalHitTakenPercent(e, m, DamageType.Cold, now)
            + mitigated[DamageType.Lightning].Min * ElementalHitTakenPercent(e, m, DamageType.Lightning, now);
        double totalTaken = (total - elementalDamage) * factor + elementalDamage * elementalFactor + withered
            + physicalTaken * factor * (physicalFactor - 1) + typed / 100.0 * elementalFactor;
        if (totalTaken <= 0) return 0;
        double chaosTaken = rawChaos * chaosFactor;
        return Math.Clamp((int)Math.Round(finalDamage * Math.Clamp(chaosTaken / totalTaken, 0.0, 1.0)), 0, finalDamage);
    }

    static double ElementalHitTakenPercent(MonsterModEffects effects, SpawnedMonster m, DamageType type, double now) => type switch
    {
        DamageType.Fire => (double)effects[Stat.FireDamageTakenPlusPct] + CoveredInAshFireTaken(m, now),
        DamageType.Cold => effects[Stat.ColdDamageTakenPlusPct],
        DamageType.Lightning => effects[Stat.LightningDamageTakenPlusPct],
        _ => 0,
    };

    public static DamagePacket MonsterTakenComponents(SpawnedMonster m, DamagePacket mitigated, bool spell,
        double now = double.NaN)
    {
        double physReduction = HitMitigation.PhysicalReduction(Monsters, m, mitigated[DamageType.Physical].Min,
            m.Effects.PhysicalReduction - CrushReduction(m, now), MonsterArmourEnabled);
        return mitigated.Select((type, range) => range.Scale(type == DamageType.Physical
            ? Math.Max(0, 1 - physReduction / 100.0)
                * (1 + MaimedPhysicalTaken(m, now) / 100.0) : 1))
            .Scale(m.Effects.DamageTaken(spell) * MonsterToughnessFactor(m.Effects));
    }

    /// <summary>The player's base_self_critical_strike_multiplier_-% (critical strikes against them deal less extra damage).</summary>
    public static int SelfCriticalReduction(Player player)
    {
        var stats = CharacterPlayerStats(player);
        if (stats.GetValueOrDefault(Stat.SelfTakeNoExtraDamageFromCriticalStrikesIfHaveBeenCritRecently) != 0
            && AreaTime(player) - player.LastCritTakenAt <= RecentEvents.WindowSeconds)
            return 100;
        return SelfCriticalReduction(name => name is var key ? stats.GetValueOrDefault(key) : 0);
    }

    public static int SelfCriticalReduction(Func<uint, int> get) =>
        get(Stat.SelfTakeNoExtraDamageFromCriticalStrikes) != 0 ? 100
            : get(Stat.BaseSelfCriticalStrikeMultiplierMinusPct);

    /// <summary>Whether the monster blocks a hit of the player (Stalwart's / species monster_base_block_% for attacks,
    /// and species base_spell_block_% for spells). Rolled before anything of the hit happens: a blocked hit has no
    /// damage, impale, flinch or hit message. The skill's "reduced Enemy Block Chance" (Block Chance Reduction) applies
    /// to both attack and spell block, and a blocked supported hit applies Overpowered.</summary>
    public static bool MonsterBlocks(SpawnedMonster m, Random random, bool spell, double now = 0,
        IReadOnlyDictionary<uint, int>? skill = null)
    {
        int baseChance = spell ? m.Effects.SpellBlockChance : m.Effects.BlockChance;
        if (baseChance <= 0) return false;
        int Skill(uint name) => skill is not null && name is var k ? skill.GetValueOrDefault(k) : 0;
        int chance = baseChance - Math.Max(0, Skill(Stat.SupportReduceEnemyBlockAndSpellBlockPct));
        if (m.OverpoweredUntil > now) chance -= m.OverpoweredPercent;
        if (chance <= 0 || random.Next(100) >= chance) return false;
        if (Skill(Stat.ApplyOverpoweredOnEnemyBlockReducedBlockAndSpellBlockPct) is var overpowered and > 0
            && Skill(Stat.SupportOverpoweredBaseDurationMs) is var duration and > 0)
        {
            m.OverpoweredPercent = overpowered;
            m.OverpoweredUntil = Math.Max(m.OverpoweredUntil, now + duration / 1000.0);
        }
        return true;
    }

    /// <summary>A blocked projectile's damage (<see cref="MonsterBlocks"/>): the impact applies nothing.</summary>
    public const int Blocked = -1;
    public const int Evaded = -2;

    /// <summary>A contact's hit unless blocked or evaded; rejected contacts have no life or on-hit mutation.
    /// The caller holds the area's frame.</summary>
    public static MonsterHit? HitUnlessBlockedLocked(Player player, SpawnedMonster m, int damage) =>
        damage is Blocked or Evaded ? null : ApplyMonsterHitLocked(player, m, damage);

    internal readonly record struct TargetCriticalBonuses(long FullLife, long Unique, long Taunted)
    {
        public static TargetCriticalBonuses From(IReadOnlyDictionary<uint, int>? player, IReadOnlyDictionary<uint, int>? own)
        {
            long Sum(uint key) => (long)(player?.GetValueOrDefault(key) ?? 0) + (own?.GetValueOrDefault(key) ?? 0);
            return new(Sum(Stat.CriticalStrikeMultiplierVsEnemiesOnFullLifePlus),
                Sum(Stat.CriticalStrikeMultiplierPlusVsUniqueEnemies), Sum(Stat.CriticalStrikeMultiplierPlusVsTauntedEnemies));
        }
    }

    /// <summary>The critical strike multiplier against this monster after reductions to the bonus above 100%.</summary>
    public static int CriticalMultiplierOn(SpawnedMonster m, int multiplier,
        IReadOnlyDictionary<uint, int>? player = null, IReadOnlyDictionary<uint, int>? own = null, bool noExtra = false) =>
        CriticalMultiplierOn(m, multiplier, TargetCriticalBonuses.From(player, own), double.PositiveInfinity,
            noExtra || player is not null && CriticalHitsDealNoExtraDamage(player, own));

    static int CriticalMultiplierOn(SpawnedMonster m, int multiplier, double at,
        IReadOnlyDictionary<uint, int> player, IReadOnlyDictionary<uint, int>? own = null) =>
        CriticalMultiplierOn(m, multiplier, TargetCriticalBonuses.From(player, own), at, CriticalHitsDealNoExtraDamage(player, own));

    static int CriticalMultiplierOn(SpawnedMonster m, int multiplier, TargetCriticalBonuses bonuses, double at, bool noExtra)
    {
        if (noExtra || m.Effects.NoExtraCriticalDamage) return 100;
        long ordinary = (long)multiplier + m.Curses[Stat.EnemyAdditionalCriticalStrikeMultiplierAgainstSelf];
        if (m.MaxLife > 0 && m.Life >= m.MaxLife) ordinary += bonuses.FullLife;
        if (m.Rarity == MonsterRarity.Unique) ordinary += bonuses.Unique;
        if (m.TauntedUntil > at) ordinary += bonuses.Taunted;
        if (ordinary > 100)
        {
            int reduction = Math.Clamp(m.Effects[Stat.BaseSelfCriticalStrikeMultiplierMinusPct], 0, 100);
            ordinary = 100 + (ordinary - 100) * (100 - reduction) / 100;
        }
        return CriticalHitMultiplier(ordinary, noExtra: false);
    }

    // For an impale's source multiplier.
    static int FullLifeCriticalMultiplier(SpawnedMonster m, IReadOnlyDictionary<uint, int> player,
        IReadOnlyDictionary<uint, int>? own) => m.MaxLife > 0 && m.Life >= m.MaxLife
        ? ActorScale.Saturate((long)player.GetValueOrDefault(Stat.CriticalStrikeMultiplierVsEnemiesOnFullLifePlus)
            + (own?.GetValueOrDefault(Stat.CriticalStrikeMultiplierVsEnemiesOnFullLifePlus) ?? 0))
        : 0;

    static int CriticalMultiplierWithWeapon(int multiplier, SpawnedMonster target, IReadOnlyDictionary<uint, int>? local)
    {
        int perPower = local?.GetValueOrDefault(Stat.HitsHaveCriticalStrikeMultiplerPlusPerMonsterPower) ?? 0;
        return perPower == 0 ? multiplier : ActorScale.Saturate((long)multiplier + (long)perPower * WarcryPower(target.Rarity));
    }

    /// <summary>One burn tick on a monster (Righteous Fire, fire damage over time), less <see cref="MonsterDotTaken"/>.</summary>
    public static (int Damage, double Carry) MonsterBurn(SpawnedMonster m, DamageOverTime burn, double seconds,
        double now = double.NaN) =>
        (burn with { DamagePerSecond = burn.DamagePerSecond * MonsterDotTaken(m, burn.Type, now) }).Tick(seconds, m.Burn);

    /// <summary>The share of damage over time of this type a monster takes (Righteous Fire's burn, ignites): less its
    /// resistance (its type's base and its mods', as for hits, without penetration) and with its damage taken
    /// (<see cref="MonsterDamageTaken"/>: its shock and Glacial Wave's chill at <paramref name="now"/>, without a time
    /// they do not count, and the player's debuffs on it). Spell Resistant counts for hits only: its stat has no
    /// description saying it covers damage over time.</summary>
    public static double MonsterDotTaken(SpawnedMonster m, DamageType type, double now = double.NaN)
    {
        if (m.Effects[Stat.BaseCannotBeDamaged] > 0) return 0;
        var resistance = MonsterHitResistance(m, type, default, now);
        double increasedTaken = type switch
        {
            DamageType.Physical => (double)m.Effects[Stat.PhysicalDamageTakenPlusPct] + MaimedPhysicalTaken(m, now),
            DamageType.Fire => (double)m.Effects[Stat.FireDamageTakenPlusPct] + CoveredInAshFireTaken(m, now),
            DamageType.Cold => m.Effects[Stat.ColdDamageTakenPlusPct],
            DamageType.Lightning => m.Effects[Stat.LightningDamageTakenPlusPct],
            _ => 0,
        };
        double factor = (100 - DamageModel.Capped(resistance.Value, resistance.Maximum)) / 100.0
            * MonsterDamageTaken(m, spell: false, now, DamageTypes.IsElemental(type), hit: false,
                chaos: type == DamageType.Chaos, increasedPct: increasedTaken);
        // Pride is a separate more multiplier; Flesh and Stone's Blood Stance affects hits only.
        if (type == DamageType.Physical)
            factor *= 1 + Math.Max(0, m.PridePhysicalMore) / 100.0;
        return factor;
    }

    // Monsters with life regeneration regain life in the area's frame (MonsterRegeneration).

    /// <summary>Whether a regenerated life is still the monster's when its 0x1b1 goes out: alive and unchanged.</summary>
    public static bool RegenStillCurrent(IReadOnlyDictionary<uint, SpawnedMonster> monsters, uint id, uint life) =>
        monsters.TryGetValue(id, out var m) && !m.Dead && m.Life == life;

    public const uint EnemyRegenerationOnHit = Stat.EnemyLifeRegenerationRatePlusPctFor4SecondsOnHit;

    /// <summary>One regeneration step over the living, damaged monsters (in the area's frame): the ones whose life
    /// changed, with their new life. The part of the step within 4 s of the player's last hit on a monster regenerates at
    /// (1 + <paramref name="onHitPct"/> / 100) of its rate, at least 0.</summary>
    public static List<(uint Id, uint Life)> RegenerateMonstersLocked(IReadOnlyList<(uint Id, SpawnedMonster Monster)> monsters,
        double seconds, double now = 0, int onHitPct = 0)
    {
        var changed = new List<(uint, uint)>();
        for (int i = 0; i < monsters.Count; i++)
        {
            var (id, m) = monsters[i];
            RegenerateOneMonsterLocked(id, m, seconds, now, onHitPct, changed);
        }
        return changed;
    }

    public static List<(uint Id, uint Life)> RegenerateMonstersLocked(IReadOnlyDictionary<uint, SpawnedMonster> monsters, double seconds,
        double now = 0, int onHitPct = 0)
    {
        var changed = new List<(uint, uint)>();
        foreach (var (id, m) in monsters)
            RegenerateOneMonsterLocked(id, m, seconds, now, onHitPct, changed);
        return changed;
    }

    static void RegenerateOneMonsterLocked(uint id, SpawnedMonster m, double seconds, double now, int onHitPct, List<(uint Id, uint Life)> changed)
    {
        double spreadRecovery = SpreadPoisonRecovery(m, now);
        if (m.Dead || m.Life == 0 || m.Life >= m.MaxLife) { m.Regen = 0; return; }
        double perSecond = m.Effects.LifeRegenPerSecond(m.MaxLife);
        if (onHitPct != 0 && seconds > 0)
        {
            double recently = Math.Max(0, Math.Min(now, m.PlayerHitAt + RecentlySeconds) - Math.Max(now - seconds, m.PlayerHitAt));
            perSecond *= 1 - recently / seconds * (1 - Math.Max(0, 1 + onHitPct / 100.0));
        }
        if (perSecond > 0 && seconds > 0) perSecond *= FrostBombRegenerationFactor(m, now - seconds, now);
        if (perSecond <= 0 && spreadRecovery <= 0) return;
        double total = m.Regen + Math.Max(0, perSecond) * seconds + spreadRecovery;
        uint whole = (uint)Math.Truncate(total);
        m.Regen = total - whole;
        if (whole == 0) return;
        m.Life = (uint)Math.Min(m.MaxLife, (long)m.Life + whole);
        changed.Add((id, m.Life));
    }
}
