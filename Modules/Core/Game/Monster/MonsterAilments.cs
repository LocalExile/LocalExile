using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;
using Kind = MonsterElementalAilments.Kind;

// The player's chill, freeze and shock on monsters (MonsterElementalAilments, Agents/docs/protocol/20-damage.md §3):
// inflicted by every outgoing hit, shock as increased damage taken, chill and freeze in the monster AI, and their buffs.
public static partial class Game
{
    static void ApplyRetaliationAilmentDuration(Dictionary<uint, int> stats)
    {
        uint duration = Stat.BaseAllAilmentDurationPlusPct;
        if (stats.GetValueOrDefault(Stat.RetaliationSkillsAllAilmentDurationPlusPct) is int increased and not 0)
            stats[duration] = ActorScale.Add(duration, stats.GetValueOrDefault(duration), increased);
    }

    static Func<uint, int> SupportedAilmentStats(Player player, ushort skillId,
        IReadOnlyDictionary<uint, int> playerStats, SupportedHitInputs? capturedInputs)
    {
        if (capturedInputs is not null) return capturedInputs.Get;
        var skill = SupportedSkill(player, skillId);
        var merged = MergeSkillStats(playerStats, skill.Stats);
        if (SkillsFor(player).IsRetaliation(skill.Effect)) ApplyRetaliationAilmentDuration(merged);
        return merged.GetValueOrDefault;
    }

    /// <summary>The factor on damage a monster takes: its mods' increased damage taken, its shock, Glacial Wave's chill and
    /// the player's debuffs on it (his taunt's on every type, his enemy auras' on <paramref name="elemental"/> damage, his
    /// Withered on <paramref name="chaos"/> damage), added together (PoB DamageTaken, ElementalDamageTaken and
    /// ChaosDamageTaken INC), then a spell's less damage taken.</summary>
    public static double MonsterDamageTaken(SpawnedMonster m, bool spell, double now, bool elemental = false, bool hit = false,
        bool chaos = false) => MonsterDamageTaken(m, spell, now, elemental, hit, chaos, 0);

    static double MonsterDamageTaken(SpawnedMonster m, bool spell, double now, bool elemental, bool hit,
        bool chaos, double increasedPct) =>
        m.Effects.DamageTaken(spell, increasedPct + m.Ailments.Magnitude(Kind.Shock, now) + m.Ailments.ChillDamageTakenPct(now)
            + m.TakenFromPlayer.All + (elemental ? m.TakenFromPlayer.Elemental : 0) + (hit ? HitDebuffTaken(m, spell, now) : 0)
            + m.SpiderWebs.Taken(now)
            + (chaos ? WitheredTaken(m, now) : 0)
            + m.Ignites.DamageTaken(now)
            + PerAilmentTypeTakenPct(m, now)
            + (m.HasCurses ? m.Curses.DamageTaken(now) : 0)
            + BrandMonsterTakenPctLocked(m)
            + (m.Life * 2L <= m.MaxLife ? m.Curses[Stat.DamageTakenPlusPctOnLowLife] : 0))
        * MonsterToughnessFactor(m.Effects);

    /// <summary>Yoke of Suffering: "Enemies take X% increased Damage for each type of Ailment you have inflicted on them".
    /// Each wearer keeps their own value on the monster (SpawnedMonster.SetSourceValue, EnemyDebuffsTick); the enemy
    /// takes, from every source of damage, the sum over the wearers of each one's value times the types of ailment that
    /// wearer inflicted on it which are still on it.</summary>
    public const string PerAilmentTypeTaken = "ailment_types_apply_damage_taken_+%";

    static double PerAilmentTypeTakenPct(SpawnedMonster m, double now)
    {
        // Every hit's damage taken passes here: a monster no wearer keeps a value on returns before the enumeration.
        if (!m.HasSourceValues) return 0;
        double total = 0;
        foreach (var (source, value) in m.SourceValues(PerAilmentTypeTaken)) total += value * AilmentTypesInflicted(m, source, now);
        return total;
    }

    /// <summary>The ailment types the server models, by the key a player's inflicting of one is marked with on a monster
    /// (InflictedLocked). Scorch, brittle and sap are not modelled.</summary>
    public enum AilmentType { Ignite, Chill, Freeze, Shock, Bleed, Poison }
    static readonly string[] InflictedKeys = ["inflicted_ignite", "inflicted_chill", "inflicted_freeze", "inflicted_shock",
        "inflicted_bleed", "inflicted_poison"];

    static bool AilmentOn(SpawnedMonster m, AilmentType type, double now) => type switch
    {
        AilmentType.Ignite => m.Ignites.IgnitedAt(now),
        AilmentType.Bleed => m.Bleeds.IgnitedAt(now),
        AilmentType.Poison => m.Poisons.Stacks(now) > 0,
        AilmentType.Chill => m.Ailments.Magnitude(Kind.Chill, now) > 0,
        AilmentType.Freeze => m.Ailments.Magnitude(Kind.Freeze, now) > 0,
        _ => m.Ailments.Magnitude(Kind.Shock, now) > 0,
    };

    static AilmentType TypeOf(Kind kind) => kind switch
    {
        Kind.Chill => AilmentType.Chill,
        Kind.Freeze => AilmentType.Freeze,
        _ => AilmentType.Shock,
    };

    /// <summary>The types of ailment on a monster at <paramref name="now"/> that the player with Id
    /// <paramref name="source"/> inflicted: their own ailment of that type has not run out (InflictedLocked) and the
    /// monster still has the type.</summary>
    public static int AilmentTypesInflicted(SpawnedMonster m, uint source, double now)
    {
        int count = 0;
        for (var type = AilmentType.Ignite; type <= AilmentType.Poison; type++)
            if (m.SourceMarked(source, InflictedKeys[(int)type], now) && AilmentOn(m, type, now)) count++;
        return count;
    }

    /// <summary>Records that this player (their hit, or their own totem's, trap's or mine's) inflicted an ailment of
    /// <paramref name="type"/> on a monster lasting until <paramref name="until"/>. A minion's ailments are not its
    /// owner's. Under the Monsters lock.</summary>
    public static void InflictedLocked(Player player, SpawnedMonster m, AilmentType type, double until) =>
        m.MarkSource(player.Id, InflictedKeys[(int)type], until);

    /// <summary><see cref="InflictedLocked(Player, SpawnedMonster, AilmentType, double)"/> for a chill, freeze or
    /// shock the player's hit or effect put on it (null: none).</summary>
    public static void InflictedLocked(Player player, SpawnedMonster m, MonsterElementalAilments.Effect? effect)
    {
        if (effect is not null) InflictedLocked(player, m, TypeOf(effect.Kind), effect.Until);
    }

    /// <summary>A proliferated ailment is its inflicters' too: the source monster's marks of the type pass to the
    /// monster it reached, for no longer than the spread copy lasts.</summary>
    static void ProliferatedMarksLocked(SpawnedMonster from, SpawnedMonster to, AilmentType type, double until)
    {
        string key = InflictedKeys[(int)type];
        foreach (var (source, mark) in from.SourceMarks(key).ToList()) to.MarkSource(source, key, Math.Min(mark, until));
    }

    /// <summary>The shocks a kill of a monster by the player (its life reached 0 from a hit or Righteous Fire) inflicts.
    /// Berek's Respite (shock_X_nearby_enemies_for_2_s_on_killing_shocked_enemy): when it was shocked, each other living
    /// hostile monster within 2 m of it (20 cells, which area of effect does not change) gets an equivalent shock (wiki
    /// Berek's Respite). Within the Tempest (enemy_shock_on_kill): it is shocked through the player's modifiers, and its
    /// corpse spreads that shock by the player's own area of effect. <paramref name="player"/> is the caller's
    /// CharacterPlayerStats, so a kill computes no stats of its own. In the area's frame.</summary>
    public static void ShockOnKillLocked(Player player, SpawnedMonster m, IReadOnlyDictionary<uint, int> playerStats, double now)
    {
        int Get(uint key) => playerStats.GetValueOrDefault(key);
        // Before Within the Tempest's, which comes after the death.
        if (Get(Stat.ShockXNearbyEnemiesFor2SOnKillingShockedEnemy) != 0 && m.Ailments.ShockAtDeath(now) is { } shock)
        {
            var at = m.Walk.Position(now);
            foreach (var other in player.Area.Monsters.Values)
                if (!ReferenceEquals(other, m) && !other.Dead && other.Hostile && Dist(other.Walk.Position(now), at) <= 20)
                {
                    other.Ailments.Equivalent(m.Ailments, shock, now, name => other.Effects[name]);
                    InflictedLocked(player, other, AilmentType.Shock, shock.Until);
                }
        }
        if (Get(Stat.EnemyShockOnKill) != 0)
            InflictedLocked(player, m, m.Ailments.ShockOnKill(now, Get, name => m.Effects[name], AilmentArea(Get, _ => 0, spell: false)));
    }

    /// <summary>The chills and shocks without damage (MonsterElementalAilments.WithoutDamage) a monster hit that lands on
    /// the player puts on its monster: "Chill Enemy for X seconds when Hit, reducing their Action Speed by 30%" (The
    /// Pandemonius) on every one, blocked too, as Brine Bound's freeze, and "X% chance to Chill/Shock Attackers for 4
    /// seconds on Block" on a blocked one, each on its own roll of AreaRng(conn) so the incoming hit's draws stay in order.
    /// Each spreads by the player's area modifier, as Within the Tempest's shock.
    /// </summary>
    public static void AilmentsWhenHitLocked(Player player, SpawnedMonster m, Func<uint, int> playerStats, bool blocked, double now, Random random)
    {
        int Target(uint name) => m.Effects[name];
        double area = AilmentArea(playerStats, _ => 0, spell: false);
        if (playerStats(Stat.ChillEnemyWhenHitDurationMs) is > 0 and int ms)
            InflictedLocked(player, m, m.Ailments.WithoutDamage(Kind.Chill, ms / 1000.0, now, playerStats, Target, area, stated: 30));
        if (!blocked) return;
        AlternateAilmentsOnBlockLocked(m, playerStats, now);
        if (RandomRolls.Chance(playerStats(Stat.ChillAttackersFor4SecondsOnBlockPctChance), 100, random))
            InflictedLocked(player, m, m.Ailments.WithoutDamage(Kind.Chill, 4, now, playerStats, Target, area));
        if (RandomRolls.Chance(playerStats(Stat.ShockAttackersFor4SecondsOnBlockPctChance), 100, random))
            InflictedLocked(player, m, m.Ailments.WithoutDamage(Kind.Shock, 4, now, playerStats, Target, area));
    }

    /// <summary>The area of effect modifier a hit's shock spreads by (SkillArea.Modifier): the player's panel total
    /// skill_area_of_effect_+% (which holds the player's base form, so that is left out, as AreaPlayerStats does) and the
    /// skill's own stats.</summary>
    static double AilmentArea(Func<uint, int> player, Func<uint, int> skill, bool spell) =>
        SkillArea.Modifier(name => (name == SkillArea.BaseIncreased ? 0 : player(name)) + skill(name), spell);

    /// <summary>Proliferation, once per AI tick before the buffs are sent: every living hostile monster within a
    /// spreading chill's, freeze's or shock's own radius (cells, 10 to the metre) of the monster it is on, a corpse
    /// included, has the strongest of each kind that reaches it until that ends (MonsterElementalAilments.Proliferated).
    /// In the area's frame.</summary>
    static void ProliferateLocked(Player player, double now)
    {
        if (!MonsterElementalAilments.AnySpreadAdded && !player.Area.HasProliferatedAilments) return;
        List<(SpawnedMonster M, (int X, int Y) At, MonsterElementalAilments.Effect Ailment)>? sources = null;
        foreach (var m in player.Area.Monsters.Values)
            if (!m.Ailments.Idle)
                foreach (var ailment in m.Ailments.Spreading(now))
                    (sources ??= []).Add((m, m.Walk.Position(now), ailment));
        if (sources is null && !player.Area.HasProliferatedAilments) return;
        player.Area.HasProliferatedAilments = sources is not null;
        foreach (var m in player.Area.Monsters.Values)
        {
            bool reached = sources is not null && !m.Dead && m.Hostile;
            var at = reached ? m.Walk.Position(now) : default;
            for (var kind = Kind.Chill; kind <= Kind.Shock; kind++)
            {
                var reaching = reached ? sources!.Where(s => s.Ailment.Kind == kind && !ReferenceEquals(s.M, m)
                    && Dist(s.At, at) <= s.Ailment.Spread).ToList() : [];
                var chosen = reaching.Count == 0 ? default : reaching.MaxBy(s => (s.Ailment.Magnitude, s.Ailment.Until));
                m.Ailments.Proliferated(kind, chosen.M is null ? null : chosen.Ailment, name => m.Effects[name]);
                if (chosen.M is not null) ProliferatedMarksLocked(chosen.M, m, TypeOf(kind), chosen.Ailment.Until);
            }
        }
    }

    /// <summary>Ignite proliferation, once per AI tick: every living hostile monster that can be ignited, within a
    /// spreading ignite's own radius (cells) of the monster it is on, a corpse included, burns with the strongest that
    /// reaches it (MonsterIgnites.Proliferated). A monster that starts burning from it gets the ignited buff. Under the
    /// Monsters lock.</summary>
    static void ProliferateIgnitesLocked(Player player, double now, List<(byte[] Packet, string Log)> packets)
    {
        if (!MonsterIgnites.AnySpreadAdded && !player.Area.HasProliferatedIgnites) return;
        List<(uint Id, SpawnedMonster M, (int X, int Y) At, double Dps, double Until, int Radius)>? sources = null;
        foreach (var (id, m) in player.Area.Monsters)
        {
            if (!m.Ignites.MaySpread) continue;
            // A shattered or consumed corpse is gone: its retained ignites retire, and the copies it gave end below.
            if (m.Dead && (m.Ailments.Shattered || m.CorpseConsumed))
            {
                m.Ignites.RetireCorpse();
                continue;
            }
            foreach (var (dps, until, radius) in m.Ignites.Spreads(now))
                (sources ??= []).Add((id, m, m.Walk.Position(now), dps, until, radius));
        }
        if (sources is null && !player.Area.HasProliferatedIgnites) return;
        player.Area.HasProliferatedIgnites = sources is not null;
        foreach (var (id, m) in player.Area.Monsters)
        {
            if (sources is null && m.Ignites.Idle) continue;
            (double Dps, double Until)? best = null;
            uint from = 0;
            SpawnedMonster? fromMonster = null;
            var e = m.Mods.Effects;
            if (sources is not null && !m.Dead && m.Life > 0 && m.Hostile && !m.Invulnerable
                && e[Stat.ImmuneToIgnite] == 0 && e[Stat.CannotBeIgnited] == 0)
            {
                var at = m.Walk.Position(now);
                foreach (var s in sources)
                    if (!ReferenceEquals(s.M, m) && Dist(s.At, at) <= s.Radius
                        && (best is not { } b || (s.Dps, s.Until).CompareTo((b.Dps, b.Until)) > 0))
                        (best, from, fromMonster) = ((s.Dps, s.Until), s.Id, s.M);
            }
            bool showing = m.Ignites.Buff is not null;
            bool started = m.Ignites.Proliferated(best, now);
            if (fromMonster is not null && best is { } spread) ProliferatedMarksLocked(fromMonster, m, AilmentType.Ignite, spread.Until);
            if (!started || showing) continue;
            player.Area.ActiveIgniteMonsters.Add(id);
            string log = $"monster 0x{id:x} ({m.Label}) ignited by proliferation from 0x{from:x}: {best!.Value.Dps:0.#}/s for {best.Value.Until - now:0.##} s";
            Console.WriteLine($"[gateway] {log}");
            // Not showing yet, so this is the add alone (no old instance to take off).
            ShowDotBuffLocked(player, id, m.Ignites, IgnitedBuffRow, now);
        }
    }

    /// <summary>Increased/reduced action speed adds with chill; less action speed (Temporal Chains) multiplies it.
    /// Freeze stops actions, while Unstoppable floors the final factor at the base value without discarding haste.</summary>
    public static double MonsterActionFactor(SpawnedMonster m, double now) => MonsterActionFactor(m, now, freeze: true);

    static double MonsterActionFactor(SpawnedMonster m, double now, bool freeze) =>
        Math.Max(m.Effects[Stat.ActionSpeedCannotBeReducedBelowBase] > 0 || m.ReceivedActionFloor ? 1 : 0,
            freeze && m.Ailments.Magnitude(MonsterElementalAilments.Kind.Freeze, now) > 0 ? 0
                : (1 - (m.Effects[Stat.ActionSpeedMinusPct] + m.Ailments.Magnitude(MonsterElementalAilments.Kind.Chill, now)) / 100.0)
                    * (now < m.ReceivedActionUntil ? m.ReceivedActionFactor : 1));

    /// <summary>Maim's relative walk factor and the movement_velocity_+% the client should hold. Maim adds -30 into the
    /// monster's existing increased/reduced movement bucket; it never overwrites the speed roll or another modifier.</summary>
    public static (double Factor, int Pct) MaimedMovementFactor(SpawnedMonster m, double now)
    {
        int basePct = m.Effects.MovementPct;
        int maimPct = now < m.MaimedUntil ? -30 : 0;
        int hinderPct = Math.Max(SpiderHinder(m, now), m.HinderedUntil > now ? 30 : 0);
        int lowLife = MonsterOnLowLife(m) ? m.Effects[Stat.MovementVelocityPlusPctWhenOnLowLife] : 0;
        int totalPct = basePct + maimPct - hinderPct + lowLife;
        if ((maimPct != 0 || hinderPct != 0 || lowLife != 0) && m.Effects[Stat.MovementSpeedCannotBeReducedBelowBase] > 0)
            totalPct = Math.Max(0, totalPct);
        double Movement(double pct) => m.Variety is { } variety
            ? MonsterSpeedRoll.Factor(m.SpeedRoll) * Math.Max(1, variety.MovementSpeed * (100 + pct) / 100.0)
            : Math.Max(.01, 1 + pct / 100.0);
        double before = Movement(basePct);
        double after = Movement(totalPct);
        return (after / before, totalPct);
    }

    /// <summary>
    /// A player hit that landed inflicts its ailments (MonsterElementalAilments.Inflict) from the damage the monster took
    /// per type: the mitigated packet × its damage taken × the critical multiplier on a critical strike. The threshold is
    /// its maximum life (wiki: equal to it for most monsters). <paramref name="player"/> is CharacterPlayerStats,
    /// <paramref name="skill"/> the skill's own stats; both count. In the area's frame, after the hit's damage, so the
    /// hit does not gain from its own shock.
    /// A killing hit's ailments stay on the corpse (wiki Ailment: a killing hit that inflicts an elemental ailment counts
    /// as killing an affected enemy; Elemental proliferation goes on from corpses), then the kill's own shock (Within the
    /// Tempest), unless the monster shatters; a corpse neither flees nor reflects. A living monster may flee (MonsterFlee), and a Mirrored one puts the
    /// ailments on the player too: at once, or into <paramref name="reflected"/> for the caller to apply after a sweep
    /// whose hits land together (<see cref="ReflectAilmentsLocked"/>). <paramref name="poison"/>: the hit's poison rows
    /// (<see cref="PoisonOnHitLocked"/>); a hit without them does not poison.
    /// </summary>
    public static void AilmentsOnHitLocked(Player player, uint targetId, SpawnedMonster m, DamagePacket mitigated, bool spell,
        bool critical, int criticalMultiplier, IReadOnlyDictionary<uint, int> playerStats, IReadOnlyDictionary<uint, int>? skill,
        double now, Random random, List<MonsterElementalAilments.Effect>? reflected = null,
        MonsterPoisons.Settings poison = default, ushort sourceSkill = 0, BlindHitInput blind = default, (int X, int Y)? hitOrigin = null,
        bool playerHit = true, int attackerSize = LivingCollision.PlayerSize, bool attackerHasReversedStat = true) =>
        AilmentsOnHitLocked(player, targetId, m, mitigated, spell, critical, criticalMultiplier, playerStats, skill,
            now, random, default(AttackAilmentGains), reflected, poison, sourceSkill, blind, hitOrigin, playerHit,
            attackerSize, attackerHasReversedStat);

    public static void AilmentsOnHitLocked(Player player, uint targetId, SpawnedMonster m, DamagePacket mitigated, bool spell,
        bool critical, int criticalMultiplier, IReadOnlyDictionary<uint, int> playerStats, IReadOnlyDictionary<uint, int>? skill,
        double now, Random random, AttackAilmentGains attackGains, List<MonsterElementalAilments.Effect>? reflected = null,
        MonsterPoisons.Settings poison = default, ushort sourceSkill = 0, BlindHitInput blind = default, (int X, int Y)? hitOrigin = null,
        bool playerHit = true, int attackerSize = LivingCollision.PlayerSize, bool attackerHasReversedStat = true,
        bool projectileDamage = false)
    {
        HitSummonsOnHitLocked(player, m, skill, sourceSkill, now, random);
        if (playerHit) QueueLivingLightningLocked(player, targetId, m, sourceSkill, skill, mitigated, now);
        if (playerHit && !spell && skill is not null && GolemStat(skill, Stat.HolyStrikeAnimatesOwnersMainHandWeapon) != 0)
            QueueHolyArmamentLocked(player, targetId, m, sourceSkill);
        PhantasmsOnHitLocked(player, m, sourceSkill, now, random, skill: skill);
        CursesOnHitLocked(player, m, sourceSkill, spell, now, skill, playerStats, playerHit, random);
        if (playerHit && critical) ArcaneSurgeOnCritLocked(player, playerStats, now, random);
        bool criticalProjectileKnockback = critical && projectileDamage
            && (playerStats.GetValueOrDefault(Stat.KnockbackOnCritWithProjectileDamage) != 0
                || skill?.GetValueOrDefault(Stat.KnockbackOnCritWithProjectileDamage) > 0);
        KnockbackOnHitLocked(player, m, playerStats, skill, now, random, hitOrigin,
            attackerSize: attackerSize, attackerHasReversedStat: attackerHasReversedStat, guaranteed: criticalProjectileKnockback);
        // "You gain Onslaught for 4 seconds on Hit" and "Gain Arcane Surge on Hit with Spells" (Honoured Berserker
        // and Flood tattoos): every player hit, the spells-only one gated by the hit's own spell flag.
        if (playerHit) OnslaughtOnHitLocked(player, now);
        if (playerHit && spell) ArcaneSurgeOnSpellHitLocked(player, playerStats, now, random);
        // "on Critical Strike against Marked Unique Enemy" (Loyalty Tattoo of Rakiata).
        if (playerHit && critical) LoyaltyCritVsMarkedUniqueLocked(player, m, playerStats);
        if (playerHit && !spell && player.Summons.Fighters.Values.Any(g => g.Definition.Kind == "HolyRelic"))
            HolyRelicsOnHitLocked(player, targetId, m, now);
        double factor = MonsterDamageTaken(m, spell, now, hit: true) * (critical ? criticalMultiplier / 100.0 : 1);
        int Player(uint key) => playerStats.GetValueOrDefault(key);
        int Skill(uint name) => skill is not null && name is var key ? skill.GetValueOrDefault(key) : 0;
        // The physical part counts after the monster's armour + physical reduction and its Maim, as the hit's damage does.
        double physRaw = mitigated[DamageType.Physical].Min;
        double physReduction = HitMitigation.PhysicalReduction(Monsters, m, physRaw,
            m.Effects.PhysicalReduction - CrushReduction(m, now), MonsterArmourEnabled);
        var taken = mitigated.Select((type, range) => range.Scale(factor
            * (type == DamageType.Physical
                ? Math.Max(0, 1 - physReduction / 100.0)
                    * (1 + MaimedPhysicalTaken(m, now) / 100.0) : 1)));
        bool tinctureWeapon = !spell && Player(Stat.TinctureApplied) != 0;
        int frozenShock = spell && m.Ailments.Magnitude(Kind.Freeze, now) > 0
            ? ActorScale.Saturate((long)Player(Stat.SpellChanceToShockFrozenEnemiesPct) +
                Skill(Stat.SpellChanceToShockFrozenEnemiesPct)) : 0;
        int Attacker(uint name)
        {
            int value = Player(name) + PanelStats.Of(name, Skill);
            if (name == Stat.ChanceToShockPct) value = ActorScale.Saturate((long)value + frozenShock);
            if (!tinctureWeapon) return value;
            return name switch
            {
                Stat.ChanceToFreezePct => value + Player(Stat.ChanceToFreezePctWithTincturedWeapons),
                Stat.ChanceToShockPct => value + Player(Stat.ChanceToShockPctWithTincturedWeapons),
                Stat.VirtualChillEffectPlusPct => value + Player(Stat.ChillEffectPlusPctWithTincturedWeapons),
                Stat.VirtualShockEffectPlusPct => value + Player(Stat.ShockEffectPlusPctWithTincturedWeapons),
                Stat.VirtualChillDurationPlusPct or Stat.VirtualFreezeDurationPlusPct or Stat.VirtualShockDurationPlusPct =>
                    value + Player(Stat.ElementalAilmentDurationPlusPctWithTincturedWeapons),
                Stat.AllDamageCanFreeze => value | Player(Stat.AllDamageWithTincturedWeaponsCanFreeze),
                Stat.AllDamageCanShock => value | Player(Stat.AllDamageWithTincturedWeaponsCanShock),
                _ => value,
            };
        }
        double thresholdScale = MonsterAilmentThresholdScaling;
        long ailmentThreshold = thresholdScale > 0 && m.Variety is not null
            ? Math.Max(1L, (long)Math.Round(Monsters.AilmentThreshold(m.Variety, m.Level, m.MaxLife) * thresholdScale))
            : m.MaxLife;
        var added = m.Ailments.Inflict(taken, critical, (uint)Math.Min(uint.MaxValue, ailmentThreshold), now, Attacker, name => m.Effects[name], random,
            AilmentArea(Player, Skill, spell));
        // Scorch, Brittle and Sap (MonsterAlternateAilments): only from a source, after chill, freeze and shock's draws.
        var alternate = AlternateAilmentsOnHitLocked(m, taken, critical, now, Attacker, random);
        foreach (var effect in added)
        {
            InflictedLocked(player, m, effect);
            if (playerHit && effect.Kind == Kind.Freeze)
            {
                player.RecentEvents.Record(StatConditions.FrozeEnemy, now);
                if (m.Hostile) GainFromAttackAilmentLocked(player, attackGains.FreezeEndurance,
                    ChargeType.Endurance, EnduranceOnAttackFreeze);
            }
            else if (effect.Kind == Kind.Shock) player.RecentEvents.Record(StatConditions.ShockedEnemy, now);
        }
        if (m.Dead || m.Life == 0)
        {
            if (playerHit && Player(Stat.ExplodeOnKillPctFireDamageToDeal) > 0
                && (spell || sourceSkill != 0 && SkillsFor(player).IsAttack(SupportedSkill(player, sourceSkill).Effect)))
                CaptureEquippedHitExplosionLocked(m, playerStats);
            // Shatter (wiki Shatter, Freeze): a non-unique monster a hit kills while frozen, the killing hit's own freeze
            // included, leaves no corpse, so nothing spreads from it and the kill's shock has nothing to land on.
            if (m.Rarity != MonsterRarity.Unique && m.Ailments.Magnitude(Kind.Freeze, now) > 0)
            {
                m.Ailments.Shatter();
                m.RuleAilments.Clear();
                player.RecentEvents.Record(StatConditions.ShatteredEnemy, now);
            }
            else ShockOnKillLocked(player, m, playerStats, now);
            return;
        }
        HitDebuffsOnHitLocked(m, name => Player(name) + Skill(name), spell, critical, now, random);
        InflictMonsterBlind(player, targetId, m, now, blind, spell, critical, random);
        ExposureOnHitLocked(m, name => Player(name) + Skill(name), mitigated, now, AreaRng(player));
        WitherOnHitLocked(m, name => Player(name) + Skill(name), now, AreaRng(player));
        int poisonStacksBefore = m.Poisons.Stacks(now);
        if (TryPoisonOnHitLocked(player, m, mitigated, spell, critical, playerStats, skill, poison, now, random,
                out double poisonDps, out double poisonSeconds))
        {
            player.Area.ActivePoisonMonsters.Add(targetId);
            if (playerHit && Player(Stat.PoisonReflectedToSelf) != 0 && Player(Stat.CannotBePoisoned) == 0
                && Player(Stat.ImmuneToPoison) == 0
                // Golden Rule's reflected-poison source stops adding player stacks at 100; this is not an incoming cap.
                && player.IncomingAilments.PoisonStacks(now) < 100)
            {
                int accepted = m.Poisons.Stacks(now) - poisonStacksBefore;
                for (int i = 0; i < accepted && player.IncomingAilments.PoisonStacks(now) < 100; i++)
                    player.IncomingAilments.ApplyPoison(now, poisonDps, poisonSeconds, reflected: true, victim: Player);
            }
        }
        // Fleeing (MonsterFlee): its own draws (AreaRng(player)), so a projectile's reaction draws stay in order.
        int fleeChance = MonsterFlee.Chance(spell, Player, Skill);
        if (TryFleeFromHitLocked(player, m, player.Walk.Position(now), fleeChance, now))
        {
            // Which hit made it run, and from what chance: the stat sources are not logged by the panel, and a fleeing
            // monster reads as broken AI (reported live 2026-11-23).
            Console.WriteLine($"[gateway] monster 0x{targetId:x} ({m.Label}) flees from a {(spell ? "spell" : "hit")} " +
                $"0x{sourceSkill:x4}: {fleeChance}% chance, {m.Effects[Stat.MonsterChanceToNotFleePct]}% not to, to {m.FleeTo}");
        }
        if (added.Count + alternate.Count == 0 || player.PlayerLife == 0
            || !RandomRolls.Chance(m.Effects[Stat.ReflectNonDamagingAilmentsPctChance], 100, random)) return;
        // Scorch and Brittle stop no action, so they act on the player at once, even inside a sweep.
        ReflectAlternateAilmentsLocked(player, alternate, now, Player, random);
        if (added.Count == 0) return;
        if (reflected is not null) reflected.AddRange(added);
        else ReflectAilmentsLocked(player, added, now, playerStats, random);
    }

    static bool TryFleeFromHitLocked(Player player, SpawnedMonster monster, (int X, int Y) from, int chance, double now)
    {
        if (chance <= 0 || monster.Fleeing
            || !MonsterFlee.Rolls(chance, monster.Effects[Stat.MonsterChanceToNotFleePct], AreaRng(player))) return false;
        int size = Math.Max(0, LivingCollision.Size(monster));
        monster.FleeTo = MonsterFlee.Point(monster.Walk.Position(now), from,
            (x, y) => player.Layout.WalkableFor(x, y, size) && !player.Barrier.Contains((x, y)), AreaRng(player));
        return monster.Fleeing = monster.FleeTo is not null;
    }

    internal static void ReflectPendingAilmentsLocked(Player player, List<MonsterElementalAilments.Effect> pending,
        double now, IReadOnlyDictionary<uint, int> playerStats, Random random)
    {
        if (pending.Count == 0) return;
        var accepted = pending.ToArray();
        pending.Clear();
        ReflectAilmentsLocked(player, accepted, now, playerStats, random);
    }

    /// <summary>Mirrored: the ailments a hit added act on the player as well (wiki Reflect), as they landed, then through
    /// the player's own modifiers; the exact reflected magnitude is not verified. A new freeze cancels the player's action.
    /// In the area's frame.</summary>
    public static void ReflectAilmentsLocked(Player player, IEnumerable<MonsterElementalAilments.Effect> added, double now,
        IReadOnlyDictionary<uint, int> playerStats, Random random)
    {
        if (player.PlayerLife == 0) return;
        int Player(uint key) => playerStats.GetValueOrDefault(key);
        ApplyPlayerAilmentsLocked(player, now, ailments =>
        {
            foreach (var e in added)
                ailments.Inflict(Enum.Parse<PlayerElementalAilments.Kind>(e.Kind.ToString()), e.Magnitude,
                    e.Until - e.From, now, Player, random);
        });
    }

    /// <summary>
    /// What changed on the monsters' ailments since the last AI tick, spread ones included, as packets: the chilled, frozen and shocked buffs
    /// (BuffDefinitions 1, 2, 3, which draw BuffVisuals 3, 4, 5) and the virtual_action_speed_+% the native walk
    /// (0x141e827a0) and action clips read, which network stats do not derive (04 §4.1a). A chill re-times the server's
    /// walk by the same factor, so both walk the monster alike. A freeze is not sent there (−100 would divide the native
    /// clip by zero); on a predictive client the AI stops the monster instead, and on a lockstep client nothing stops it,
    /// the estimate's walk included (07 §6). In the area's frame.
    /// </summary>
    public static List<(byte[] Packet, string Log)> MonsterAilmentChangesLocked(Player player, double now) =>
        AreaAilmentChangesLocked(player, now);

    /// <summary><see cref="MonsterAilmentChangesLocked"/> for the whole area.</summary>
    public static List<(byte[] Packet, string Log)> AreaAilmentChangesLocked(Player player, double now)
    {
        ProliferateLocked(player, now);
        var packets = new List<(byte[], string)>();
        ExpireConversionTrapsLocked(player, now, packets);
        ProliferateIgnitesLocked(player, now, packets);
        if (!MonsterElementalAilments.AnyApplied && !player.Area.HasProliferatedAilments && !player.Area.HasSpiderWebs)
        {
            var living = player.Area.SyncLivingMonsters();
            for (int i = 0; i < living.Count; i++)
            {
                var (id, m) = living[i];
                ProcessMonsterAilmentLocked(player, id, m, now, packets);
            }
            return packets;
        }
        foreach (var (id, m) in player.Area.Monsters)
            ProcessMonsterAilmentLocked(player, id, m, now, packets);
        return packets;
    }

    static void ProcessMonsterAilmentLocked(Player player, uint id, SpawnedMonster m, double now, List<(byte[] Packet, string Log)> packets)
    {
        if (m.TemporaryAllyUntil > 0 && (now >= m.TemporaryAllyUntil || m.Dead || m.Life == 0))
            EndConversionTrap(player, id, m, now, packets);
        BearTrapChangesLocked(player, id, m, now, packets);
        // A knockback is presented before the ailment-idle skip: an idle monster still moves.
        if (m.KnockbackPending is (int kx, int ky))
        {
            m.KnockbackPending = null;
            packets.Add((InstanceProtocol.Teleport(id, kx, ky),
                $"0x0f8 monster 0x{id:x} ({m.Label}) knocked back to ({kx}, {ky})"));
        }
        var a = m.Ailments;
        double vines = ToxicWalkFactor(m);
        double trap = BearTrapWalkFactor(m, now);
        double snare = EnsnaredWalkFactor(m, now);
        if (!m.RuleAilments.Idle) packets.AddRange(AlternateAilmentBuffs(id, $"monster 0x{id:x} ({m.Label})", m.RuleAilments, now));
        bool elementalIdle = a.Idle && now >= m.ReceivedActionUntil;
        bool lowLifeMove = MonsterOnLowLife(m) && m.Effects[Stat.MovementVelocityPlusPctWhenOnLowLife] != 0;
        if (elementalIdle && !TimedWalkDebuff(now, m) && SpiderHinder(m, now) == 0 && vines == 1 && trap == 1 && snare == 1
            && m.Effects[Stat.ActionSpeedMinusPct] == 0 && a.WalkFactor == 1 && a.SentActionSpeed == 0 && !m.MaimedMovementShown && !lowLifeMove) return;
        if (!elementalIdle)
        {
            foreach (var (kind, removed, added, magnitude, source) in a.PresentWithSource(now))
            {
                if (removed is not null && added is null) player.Area.Buffs.ClearRow(id, (ushort)(kind + 1));
                if (added is not null) SetAilmentBuff(player, id, kind, magnitude, source, a.Remaining(kind, now),
                    AilmentArtVisual(player, kind switch { Kind.Chill => "chill", Kind.Freeze => "freeze", _ => "shock" }));
            }
        }
        double action = MonsterActionFactor(m, now, freeze: false);
        var (walk, movementPct) = MonsterWalkModifiers(m, now, action, vines, trap, snare);
        if (walk != a.WalkFactor)
        {
            // PlayerWalk cannot represent zero. Keep the last recoverable speed so thawing or an expiry can restore
            // it without dividing by a zero factor.
            if (walk <= 0)
            {
                if (a.WalkFactor > 0) m.WalkSpeedAtZeroFactor = m.Walk.Speed / a.WalkFactor;
            }
            else
            {
                double baseline = a.WalkFactor > 0
                    ? m.Walk.Speed / a.WalkFactor
                    : m.WalkSpeedAtZeroFactor;
                if (baseline > 0) m.Walk.SetSpeed(baseline * walk, now);
            }
            a.WalkFactor = walk;
        }
        int speed = (int)Math.Round(100 * (action - 1));
        if (speed != a.SentActionSpeed && Stat.VirtualActionSpeedPlusPct is var key)
        {
            RetimeMonsterMeleeClockLocked(m, now, speed);
            a.SentActionSpeed = speed;
            SetStats(player, id, [(key, speed)]);
        }
        bool maimed = m.MaimedUntil > now || m.HinderedUntil > now || SpiderHinder(m, now) != 0
            || lowLifeMove || trap != 1 || snare != 1;
        bool maimShown = m.MaimedMovementShown;
        bool movementChanged = maimed
            ? !maimShown || movementPct != m.MaimedMovementShownPct
            : maimShown && movementPct != m.MaimedMovementShownPct;
        m.MaimedMovementShown = maimed;
        if (movementChanged)
        {
            m.MaimedMovementShownPct = movementPct;
            SetStats(player, id, [(Stat.MovementVelocityPlusPct, movementPct)]);
        }
        else if (!m.MaimedMovementShown)
        {
            m.MaimedMovementShownPct = 0;
        }
    }

    static (double Factor, int Pct) MonsterWalkModifiers(SpawnedMonster m, double now, double action, double vines, double trap, double snare)
    {
        // Maim changes movement only. Bear Trap's movement multiplier also enters the client's movement_velocity stat.
        bool speedFloored = m.Effects[Stat.MovementSpeedCannotBeReducedBelowBase] > 0;
        var movement = MaimedMovementFactor(m, now);
        double lessMove = trap * snare * (now < m.CoveredInAshUntil ? .8 : 1);
        if (lessMove != 1 && !(speedFloored && lessMove < 1))
            movement = (movement.Factor * lessMove, (int)Math.Round((100 + movement.Pct) * lessMove - 100));
        double walk = (speedFloored ? Math.Max(1, action) : action) * movement.Factor;
        return (speedFloored ? walk : walk * vines, movement.Pct);
    }

    /// <summary>
    /// The ailment buff on a monster (0x1c1): no duration (the server ends it with 0x1c2, so a pause cannot desync it).
    /// All three rows have the +0xff bool, so the reader takes a u16 (+0x82); chilled and shocked are IsRecovery and
    /// carry their one GrantedStats value (action_speed_-%_from_chill, base_damage_taken_+%_from_shock), the magnitude.
    /// The client shows that value increased by +0x82 percent (a 15% shock with +0x82 15 shows 17%, live 2026-11-03),
    /// so +0x82 is 0.
    /// </summary>
    // Freeze carries no value, so it is the one sent with its duration (u68): without one the client draws no icon under
    // the monster's life bar (live 2026-10-30). Chill and shock still end only by 0x1c2.
    // An elemental ailment's buff (its row is the kind's + 1): one per object, whoever caused it.
    public static void SetAilmentBuff(Player player, uint id, Kind kind, int magnitude, uint source, double seconds = 0, ushort? visual = null)
    {
        player.Area.Buffs.ClearRow(id, (ushort)(kind + 1));
        player.Area.Buffs.Set(id, new((ushort)(kind + 1), source, U82: 0, Values: kind == Kind.Freeze ? null : [(uint)magnitude],
            U68: kind == Kind.Freeze && seconds > 0 ? Buff.Seconds(seconds) : null, Visual: visual));
    }
    public static void AilmentsOnHitLocked(Player player, SpawnedMonster m, DamagePacket mitigated, bool spell,
        bool critical, int criticalMultiplier, IReadOnlyDictionary<uint, int> playerStats, IReadOnlyDictionary<uint, int>? skill,
        double now, Random random, List<MonsterElementalAilments.Effect>? reflected = null,
        MonsterPoisons.Settings poison = default, ushort sourceSkill = 0, (int X, int Y)? hitOrigin = null) =>
        AilmentsOnHitLocked(player, player.Area.Monsters.FirstOrDefault(p => ReferenceEquals(p.Value, m)).Key, m,
            mitigated, spell, critical, criticalMultiplier, playerStats, skill, now, random, reflected, poison,
            sourceSkill, BlindHitInput.Capture(playerStats, skill), hitOrigin);

}
