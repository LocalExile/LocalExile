using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using DamageModel = GameServer.Core.World.DamageModel;
using DamagePacket = GameServer.Core.World.DamagePacket;
using DamageOverTime = GameServer.Core.World.DamageOverTime;
using DamageType = GameServer.Core.World.DamageType;
using DamageTypes = GameServer.Core.World.DamageTypes;

static partial class Game
{
    // Monster-domain Mods.dat rows, their names and effects; loaded with the first magic or rare monster.
    internal static readonly Lazy<MonsterModCatalog> MonsterModRolls = new(() => new MonsterModCatalog(Items.Mods));

    // Per instance, the mods its magic monsters share: "all monsters in an instance that are the same or very similar
    // will have the same magic monster modifier" (3.19.0 patch notes). Very similar: the same name (The Coast's
    // Drowned are several MonsterVarieties rows), as long as the affix can roll on it (MonsterModCatalog.SharedMagic).
    internal static readonly System.Runtime.CompilerServices.ConditionalWeakTable<InstanceState, Dictionary<string, MonsterModSet>> MagicModsOf = new();

    /// <summary>The mods a new monster gets: <paramref name="kept"/> (a monster of a kept instance, a /mob_mods
    /// choice) when it has any, else freshly rolled for its rarity. Area packs pass <paramref name="alone"/> false;
    /// their magic monsters share one roll per monster name in the instance.</summary>
    internal static MonsterModSet MonsterModsFor(ClientState conn, MonsterCatalog.Variety variety, byte level, MonsterRarity rarity,
        MonsterModSet? kept, bool alone)
    {
        if (kept is { Mods.Count: > 0 } || kept is not null && rarity == MonsterRarity.Normal) return kept;
        var rolls = MonsterModRolls.Value;
        if (rarity != MonsterRarity.Magic || alone) return rolls.Roll(variety, level, rarity, Random.Shared, alone);
        var shared = MagicModsOf.GetValue(conn.Instance, _ => []);
        lock (shared) return rolls.SharedMagic(shared, variety, level, Random.Shared);
    }

    /// <summary>The stats the client needs for the mods (it applies no modifier of its own to a monster, 14 §2): the
    /// rarity's life stats and the final maximum_life its life bars read, movement_velocity_+% for its walk (the server
    /// walks it at the same speed), actor_scale_+% for its size and life_regeneration_rate_per_minute_%.</summary>
    internal static IEnumerable<(uint Key, int Value)> MonsterModClientStats(MonsterModEffects e, uint maxLife, int baseLife)
    {
        if (e["monster_life_+%_final_from_rarity"] is int rarity and not 0) yield return (StatKeys.MonsterLifeFromRarityPct, rarity);
        if (e[MonsterModCatalog.RarityTableLifeStat] is int table and not 0 && Stats.Key(MonsterModCatalog.RarityTableLifeStat) is uint tk)
            yield return (tk, table);
        if (maxLife != baseLife) yield return (StatKeys.MaximumLife, (int)maxLife);
        if (e.MovementPct != 0) yield return (StatKeys.MovementVelocityPct, e.MovementPct);
        if (e.ActorScalePct != 0 && Stats.Key("actor_scale_+%") is uint scale) yield return (scale, e.ActorScalePct);
        if (e["life_regeneration_rate_per_minute_%"] is int regen and not 0 && Stats.Key("life_regeneration_rate_per_minute_%") is uint rk)
            yield return (rk, regen);
    }

    /// <summary>The monster's own part of its hit on the player, after the roll and before the player's defences: its
    /// critical strike (only with a chance from its mods: of Deadliness; × Monster.ot's 130% multiplier less the
    /// player's <paramref name="selfCriticalReduction"/>, base_self_critical_strike_multiplier_-%, read only on a crit)
    /// and the increased damage per 10% of the player's missing life (Executioner's). Call under the Monsters lock.</summary>
    internal static DamagePacket MonsterModHit(SpawnedMonster m, DamagePacket rolled, uint playerLife, uint playerMaxLife, Random random,
        out bool critical, Func<int>? selfCriticalReduction = null, bool cannotCrit = false)
    {
        var e = m.Effects;
        critical = !cannotCrit && e.CriticalChance > 0 && random.Next(10_000) < e.CriticalChance;
        double factor = critical ? MonsterModEffects.CriticalMultiplierOnPlayer(selfCriticalReduction?.Invoke() ?? 0) / 100.0 : 1;
        factor *= Math.Max(0, 1 + e[m.Rarity is MonsterRarity.Rare or MonsterRarity.Unique
            ? "enfeeble_damage_+%_vs_rare_or_unique_final" : "enfeeble_damage_+%_final"] / 100.0);
        if (e.ExecutionerPct(playerLife, playerMaxLife) is int executioner and > 0)
        {
            int increased = e["damage_+%"] + e["physical_damage_+%"];
            factor *= (100.0 + increased + executioner) / (100 + increased);
        }
        return factor == 1 ? rolled : rolled.Scale(factor);
    }

    /// <summary>
    /// The damage a hit of the player deals to a monster, rolled: each type less the resistance the monster's mods give
    /// (capped at Monster.ot's 75%), the physical part less its physical damage reduction, then its damage taken
    /// (Berserking's reduction, Spell Resistant's less spell damage). Blocking is rolled before (<see cref="MonsterBlocks"/>).
    /// Base and mod resistance compose before the cap; penetration subtracts once after that cap.
    /// </summary>
    internal static int MonsterTakes(SpawnedMonster m, DamagePacket hit, Random random, bool spell,
        Penetration.PerType pen = default) => MonsterTakesRolled(m, hit.Roll(random), spell, pen);

    /// <summary>A monster's resistance to one type of the player's hit: base and mod resistance compose before
    /// Monster.ot's 75% cap; penetration subtracts once after that cap, the cold one against a chilled monster at
    /// <paramref name="now"/> too (without a time it is not chilled).</summary>
    internal static DamageModel.ResistanceValue MonsterHitResistance(SpawnedMonster m, DamageType type,
        Penetration.PerType pen, double now = double.NaN)
    {
        var resistance = HitMitigation.Resistance(Monsters, m.Variety, m.Level, type);
        return resistance with { Value = resistance.Value + m.Effects.Resistance(type) + MonsterExposure(m, type, now),
            Maximum = MonsterModEffects.MaxResistance, Penetration = pen.Of(type) + (type == DamageType.Cold
                && m.Ailments.Magnitude(MonsterElementalAilments.Kind.Chill, now) > 0 ? pen.ColdVsChilled : 0) };
    }

    // Already rolled: no extra RNG or per-component rounding, including ordinary bow contact snapshots.
    internal static int MonsterTakesRolled(SpawnedMonster m, DamagePacket rolled, bool spell, Penetration.PerType pen = default)
        => MonsterTakesMitigated(m, DamageModel.Mitigate(rolled, type => MonsterHitResistance(m, type, pen)), spell);

    /// <summary>
    /// The damage a resisted hit of the player deals to a monster: the physical part less its physical damage reduction,
    /// then its damage taken (Berserking's reduction, Spell Resistant's less spell damage, its shock and Glacial Wave's
    /// chill, the Intimidate or Unnerve on it at <paramref name="now"/>; without a time those do not count). Blocking is rolled before
    /// (<see cref="MonsterBlocks"/>).
    /// </summary>
    internal static int MonsterTakesMitigated(SpawnedMonster m, DamagePacket mitigated, bool spell, double now = double.NaN)
    {
        var e = m.Effects;
        double physical = mitigated[DamageType.Physical].Min;
        double physicalTaken = physical * Math.Max(0, 1 - (e.PhysicalReduction - CrushReduction(m, now)) / 100.0)
            * (1 + MaimedPhysicalTaken(m, now) / 100.0);
        double total = mitigated.MinTotal - physical + physicalTaken;
        // The player's debuffs on it add to its own increased damage taken (MonsterDamageTaken): his enemy auras' elemental
        // increase on fire, cold and lightning only, his Withered on chaos only, and Pride's more Physical Damage on the
        // physical part.
        double factor = MonsterDamageTaken(m, spell, now, hit: true);
        double withered = m.Withered.Count == 0 ? 0
            : mitigated[DamageType.Chaos].Min * (MonsterDamageTaken(m, spell, now, hit: true, chaos: true) - factor);
        double elementalDamage = mitigated[DamageType.Fire].Min + mitigated[DamageType.Cold].Min + mitigated[DamageType.Lightning].Min;
        double elementalFactor = m.TakenFromPlayer.Elemental == 0 ? factor
            : MonsterDamageTaken(m, spell, now, elemental: true, hit: true);
        // Pride's more Physical Damage and Flesh and Stone's Blood Stance (TakenFromPlayer.Physical) apply to the
        // physical part only, never to the elemental share's fallback factor.
        double physicalFactor = (1 + Math.Max(0, m.PridePhysicalMore) / 100.0)
            * (1 + Math.Max(0, m.TakenFromPlayer.Physical) / 100.0)
            * (1 + e["physical_damage_taken_+%"] / 100.0);
        return (int)Math.Round((total - elementalDamage) * factor + elementalDamage * elementalFactor + withered
            + physicalTaken * factor * (physicalFactor - 1));
    }

    internal static DamagePacket MonsterTakenComponents(SpawnedMonster m, DamagePacket mitigated, bool spell,
        double now = double.NaN) =>
        mitigated.Select((type, range) => range.Scale(type == DamageType.Physical
            ? Math.Max(0, 1 - (m.Effects.PhysicalReduction - CrushReduction(m, now)) / 100.0)
                * (1 + MaimedPhysicalTaken(m, now) / 100.0) : 1))
            .Scale(m.Effects.DamageTaken(spell));

    /// <summary>The player's base_self_critical_strike_multiplier_-% (critical strikes against them deal less extra damage).</summary>
    internal static int SelfCriticalReduction(ClientState conn) =>
        Stats.Key("base_self_critical_strike_multiplier_-%") is uint key ? CharacterPlayerStats(conn).GetValueOrDefault(key) : 0;

    /// <summary>Whether the monster blocks a hit of the player (Stalwart's monster_base_block_%: attacks only). Rolled
    /// before anything of the hit happens: a blocked hit has no damage, impale, flinch or hit message. The skill's
    /// "reduced Enemy Block Chance" (Block Chance Reduction) applies to the roll, and a blocked supported hit applies
    /// Overpowered, which lowers the monster's block chance further for its duration.</summary>
    internal static bool MonsterBlocks(SpawnedMonster m, Random random, bool spell, double now = 0,
        IReadOnlyDictionary<uint, int>? skill = null)
    {
        if (spell) return false;
        int Skill(string name) => skill is not null && Stats.Key(name) is uint k ? skill.GetValueOrDefault(k) : 0;
        int chance = m.Effects.BlockChance - Math.Max(0, Skill("support_reduce_enemy_block_and_spell_block_%"));
        if (m.OverpoweredUntil > now) chance -= m.OverpoweredPercent;
        if (chance <= 0 || random.Next(100) >= chance) return false;
        if (Skill("apply_overpowered_on_enemy_block_reduced_block_and_spell_block_%") is var overpowered and > 0
            && Skill("support_overpowered_base_duration_ms") is var duration and > 0)
        {
            m.OverpoweredPercent = overpowered;
            m.OverpoweredUntil = Math.Max(m.OverpoweredUntil, now + duration / 1000.0);
        }
        return true;
    }

    /// <summary>A blocked projectile's damage (<see cref="MonsterBlocks"/>): the impact applies nothing.</summary>
    internal const int Blocked = -1;
    internal const int Evaded = -2;

    /// <summary>A contact's hit unless blocked or evaded; rejected contacts have no life or on-hit mutation.
    /// The caller holds the Monsters lock.</summary>
    internal static MonsterHit? HitUnlessBlockedLocked(ClientState conn, SpawnedMonster m, int damage) =>
        damage is Blocked or Evaded ? null : ApplyMonsterHitLocked(conn, m, damage);

    /// <summary>The critical strike multiplier a critical hit of the player has on this monster: 100% when it takes no
    /// extra damage from critical strikes (of Toughness).</summary>
    internal static int CriticalMultiplierOn(SpawnedMonster m, int multiplier) =>
        m.Effects.NoExtraCriticalDamage ? 100 : multiplier + m.Curses["enemy_additional_critical_strike_multiplier_against_self"];

    /// <summary>One burn tick on a monster (Righteous Fire, fire damage over time), less <see cref="MonsterDotTaken"/>.</summary>
    internal static (int Damage, double Carry) MonsterBurn(SpawnedMonster m, DamageOverTime burn, double seconds,
        double now = double.NaN) =>
        (burn with { DamagePerSecond = burn.DamagePerSecond * MonsterDotTaken(m, burn.Type, now) }).Tick(seconds, m.Burn);

    /// <summary>The share of damage over time of this type a monster takes (Righteous Fire's burn, ignites): less its
    /// resistance (its type's base and its mods', as for hits, without penetration) and with its damage taken
    /// (<see cref="MonsterDamageTaken"/>: its shock and Glacial Wave's chill at <paramref name="now"/>, without a time
    /// they do not count, and the player's debuffs on it). Spell Resistant counts for hits only: its stat has no
    /// description saying it covers damage over time.</summary>
    internal static double MonsterDotTaken(SpawnedMonster m, DamageType type, double now = double.NaN)
    {
        var resistance = MonsterHitResistance(m, type, default, now);
        double factor = (100 - DamageModel.Capped(resistance.Value, resistance.Maximum)) / 100.0
            * MonsterDamageTaken(m, spell: false, now, DamageTypes.IsElemental(type),
                chaos: type == DamageType.Chaos);
        // Pride's more damage, the Maim support's increased physical damage taken and Flesh and Stone's Blood Stance
        // all apply to the physical part only.
        if (type == DamageType.Physical)
            factor *= (1 + Math.Max(0, m.PridePhysicalMore) / 100.0) * (1 + MaimedPhysicalTaken(m, now) / 100.0)
                * (1 + m.Effects["physical_damage_taken_+%"] / 100.0);
        return factor;
    }

    // Monsters with life regeneration (of the Hydra) regain life: every 0.5 s the server adds it and sends the new life
    // (0x1b1) of each one that changed, as it does for hits.
    internal static async Task RunMonsterRegeneration(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, PlayerUpdatesState updates)
    {
        if (!PlayerUpdatesCurrent(conn, updates)) return;
        const int StepMs = 500;
        double last = updates.Schedule.Now(conn);
        try
        {
            while (PlayerUpdatesCurrent(conn, updates))
            {
                await updates.Schedule.Wait(PlayerUpdateRole.MonsterRegeneration, StepMs, updates.Stop.Token);
                double now = updates.Schedule.Now(conn), seconds = now - last;
                last = now;
                if (conn.Paused || seconds <= 0) continue;
                List<(uint Id, uint Life)> changed;
                using (ProjectileMutation(conn))
                {
                    if (!PlayerUpdatesCurrent(conn, updates)) return;
                    changed = RegenerateConnectionMonsters(conn, now, seconds);
                }
                // Checked again in the send gate: a kill in between sends 0 first, and an older life after it would
                // revive the monster on the client.
                foreach (var (id, life) in changed)
                    await Send(remote, stream, ciphers, InstanceProtocol.LifeUpdate(id, life), null,
                        still: () => { lock (conn.Monsters) return RegenStillCurrent(conn.Monsters, id, life); });
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // The connection closed while sending.
        }
    }

    /// <summary>Whether a regenerated life is still the monster's when its 0x1b1 goes out: alive and unchanged.</summary>
    internal static bool RegenStillCurrent(IReadOnlyDictionary<uint, SpawnedMonster> monsters, uint id, uint life) =>
        monsters.TryGetValue(id, out var m) && !m.Dead && m.Life == life;

    internal const string EnemyRegenerationOnHit = "enemy_life_regeneration_rate_+%_for_4_seconds_on_hit";

    /// <summary>The connection's regeneration step ending at <paramref name="now"/>, with the player's
    /// <see cref="EnemyRegenerationOnHit"/> (Devout of Ryslatha, Pools.EnemyRegenerationOnHitPct) on the enemies it hit in
    /// the last 4 s. The client does not step a monster's life from its regeneration stat (no life_recovery_per_minute is
    /// sent: re/recovery-timing.md), so the 0x1b1 updates alone show it.</summary>
    internal static List<(uint Id, uint Life)> RegenerateConnectionMonsters(ClientState conn, double now, double seconds)
    {
        int onHit = now - seconds - conn.LastEnemyHitAt <= RecentlySeconds ? conn.Pools.EnemyRegenerationOnHitPct : 0;
        using (ProjectileMutation(conn)) return RegenerateMonstersLocked(conn.Monsters, seconds, now, onHit);
    }

    /// <summary>One regeneration step over the living, damaged monsters (under the Monsters lock): the ones whose life
    /// changed, with their new life. The part of the step within 4 s of the player's last hit on a monster regenerates at
    /// (1 + <paramref name="onHitPct"/> / 100) of its rate, at least 0.</summary>
    internal static List<(uint Id, uint Life)> RegenerateMonstersLocked(IReadOnlyDictionary<uint, SpawnedMonster> monsters, double seconds,
        double now = 0, int onHitPct = 0)
    {
        var changed = new List<(uint, uint)>();
        foreach (var (id, m) in monsters)
        {
            if (m.Dead || m.Life == 0 || m.Life >= m.MaxLife) { m.Regen = 0; continue; }
            double perSecond = m.Effects.LifeRegenPerSecond(m.MaxLife);
            if (onHitPct != 0 && seconds > 0)
            {
                double recently = Math.Max(0, Math.Min(now, m.PlayerHitAt + RecentlySeconds) - Math.Max(now - seconds, m.PlayerHitAt));
                perSecond *= 1 - recently / seconds * (1 - Math.Max(0, 1 + onHitPct / 100.0));
            }
            if (perSecond <= 0) continue;
            double total = m.Regen + perSecond * seconds;
            uint whole = (uint)Math.Truncate(total);
            m.Regen = total - whole;
            if (whole == 0) continue;
            m.Life = (uint)Math.Min(m.MaxLife, (long)m.Life + whole);
            changed.Add((id, m.Life));
        }
        return changed;
    }
}
