using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;
using Kind = MonsterElementalAilments.Kind;

// The player's chill, freeze and shock on monsters (MonsterElementalAilments, Agents/docs/protocol/20-damage.md §3):
// inflicted by every outgoing hit, shock as increased damage taken, chill and freeze in the monster AI, and their buffs.
public static partial class Game
{
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
            + (m.Life * 2L <= m.MaxLife ? m.Curses["damage_taken_+%_on_low_life"] : 0))
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

    /// <summary>The types of ailment on a monster at <paramref name="now"/> that the player with PeerId
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
    public static void InflictedLocked(ClientState conn, SpawnedMonster m, AilmentType type, double until) =>
        m.MarkSource(conn.PeerId, InflictedKeys[(int)type], until);

    /// <summary><see cref="InflictedLocked(ClientState, SpawnedMonster, AilmentType, double)"/> for a chill, freeze or
    /// shock the player's hit or effect put on it (null: none).</summary>
    public static void InflictedLocked(ClientState conn, SpawnedMonster m, MonsterElementalAilments.Effect? effect)
    {
        if (effect is not null) InflictedLocked(conn, m, TypeOf(effect.Kind), effect.Until);
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
    /// CharacterPlayerStats, so a kill computes no stats of its own. Under the Monsters lock.</summary>
    public static void ShockOnKillLocked(ClientState conn, SpawnedMonster m, IReadOnlyDictionary<uint, int> player, double now)
    {
        int Get(string name) => Stats.Key(name) is uint key ? player.GetValueOrDefault(key) : 0;
        // Before Within the Tempest's, which comes after the death.
        if (Get("shock_X_nearby_enemies_for_2_s_on_killing_shocked_enemy") != 0 && m.Ailments.ShockAtDeath(now) is { } shock)
        {
            var at = m.Walk.Position(now);
            foreach (var other in conn.Monsters.Values)
                if (!ReferenceEquals(other, m) && !other.Dead && other.Hostile && Dist(other.Walk.Position(now), at) <= 20)
                {
                    other.Ailments.Equivalent(m.Ailments, shock, now, name => other.Effects[name]);
                    InflictedLocked(conn, other, AilmentType.Shock, shock.Until);
                }
        }
        if (Get("enemy_shock_on_kill") != 0)
            InflictedLocked(conn, m, m.Ailments.ShockOnKill(now, Get, name => m.Effects[name], AilmentArea(Get, _ => 0, spell: false)));
    }

    /// <summary>The chills and shocks without damage (MonsterElementalAilments.WithoutDamage) a monster hit that lands on
    /// the player puts on its monster: "Chill Enemy for X seconds when Hit, reducing their Action Speed by 30%" (The
    /// Pandemonius) on every one, blocked too, as Brine Bound's freeze, and "X% chance to Chill/Shock Attackers for 4
    /// seconds on Block" on a blocked one, each on its own roll of Random.Shared so the incoming hit's draws stay in order.
    /// Each spreads by the player's area modifier, as Within the Tempest's shock.
    /// </summary>
    public static void AilmentsWhenHitLocked(ClientState conn, SpawnedMonster m, Func<string, int> player, bool blocked, double now)
    {
        int Target(string name) => m.Effects[name];
        double area = AilmentArea(player, _ => 0, spell: false);
        if (player("chill_enemy_when_hit_duration_ms") is > 0 and int ms)
            InflictedLocked(conn, m, m.Ailments.WithoutDamage(Kind.Chill, ms / 1000.0, now, player, Target, area, stated: 30));
        if (!blocked) return;
        AlternateAilmentsOnBlockLocked(m, player, now);
        if (RandomRolls.Chance(player("chill_attackers_for_4_seconds_on_block_%_chance"), 100, Random.Shared))
            InflictedLocked(conn, m, m.Ailments.WithoutDamage(Kind.Chill, 4, now, player, Target, area));
        if (RandomRolls.Chance(player("shock_attackers_for_4_seconds_on_block_%_chance"), 100, Random.Shared))
            InflictedLocked(conn, m, m.Ailments.WithoutDamage(Kind.Shock, 4, now, player, Target, area));
    }

    /// <summary>The area of effect modifier a hit's shock spreads by (SkillArea.Modifier): the player's panel total
    /// skill_area_of_effect_+% (which holds the player's base form, so that is left out, as AreaPlayerStats does) and the
    /// skill's own stats.</summary>
    static double AilmentArea(Func<string, int> player, Func<string, int> skill, bool spell) =>
        SkillArea.Modifier(name => (name == SkillArea.BaseIncreased ? 0 : player(name)) + skill(name), spell);

    /// <summary>Proliferation, once per AI tick before the buffs are sent: every living hostile monster within a
    /// spreading chill's, freeze's or shock's own radius (cells, 10 to the metre) of the monster it is on, a corpse
    /// included, has the strongest of each kind that reaches it until that ends (MonsterElementalAilments.Proliferated).
    /// Under the Monsters lock.</summary>
    static void ProliferateLocked(ClientState conn, double now)
    {
        var sources = new List<(SpawnedMonster M, (int X, int Y) At, MonsterElementalAilments.Effect Ailment)>();
        foreach (var m in conn.Monsters.Values)
            foreach (var ailment in m.Ailments.Spreading(now)) sources.Add((m, m.Walk.Position(now), ailment));
        foreach (var m in conn.Monsters.Values)
        {
            bool reached = sources.Count > 0 && !m.Dead && m.Hostile;
            var at = reached ? m.Walk.Position(now) : default;
            for (var kind = Kind.Chill; kind <= Kind.Shock; kind++)
            {
                var reaching = reached ? sources.Where(s => s.Ailment.Kind == kind && !ReferenceEquals(s.M, m)
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
    static void ProliferateIgnitesLocked(ClientState conn, double now, List<(byte[] Packet, string Log)> packets)
    {
        var sources = new List<(uint Id, SpawnedMonster M, (int X, int Y) At, double Dps, double Until, int Radius)>();
        foreach (var (id, m) in conn.Monsters)
        {
            // A shattered or consumed corpse is gone: its retained ignites retire, and the copies it gave end below.
            if (m.Dead && (m.Ailments.Shattered || m.CorpseConsumed))
            {
                m.Ignites.RetireCorpse();
                continue;
            }
            foreach (var (dps, until, radius) in m.Ignites.Spreads(now)) sources.Add((id, m, m.Walk.Position(now), dps, until, radius));
        }
        foreach (var (id, m) in conn.Monsters)
        {
            if (sources.Count == 0 && m.Ignites.Idle) continue;
            (double Dps, double Until)? best = null;
            uint from = 0;
            SpawnedMonster? fromMonster = null;
            var e = m.Mods.Effects;
            if (sources.Count > 0 && !m.Dead && m.Life > 0 && m.Hostile && !m.Invulnerable
                && e["immune_to_ignite"] == 0 && e["cannot_be_ignited"] == 0)
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
            string log = $"monster 0x{id:x} ({m.Label}) ignited by proliferation from 0x{from:x}: {best!.Value.Dps:0.#}/s for {best.Value.Until - now:0.##} s";
            Console.WriteLine($"[gateway] {log}");
            // Not showing yet, so this is the add alone (no old instance to take off).
            foreach (var packet in ShowDotBuffLocked(conn, id, m.Ignites, IgnitedBuffRow, now)) packets.Add((packet, "0x01c1 " + log));
        }
    }

    /// <summary>Increased/reduced action speed adds with chill; less action speed (Temporal Chains) multiplies it.
    /// Freeze stops actions, while Unstoppable floors the final factor at the base value without discarding haste.</summary>
    public static double MonsterActionFactor(SpawnedMonster m, double now) => MonsterActionFactor(m, now, freeze: true);

    static double MonsterActionFactor(SpawnedMonster m, double now, bool freeze) =>
        Math.Max(m.Effects["action_speed_cannot_be_reduced_below_base"] > 0 || m.ReceivedActionFloor ? 1 : 0,
            freeze && m.Ailments.Magnitude(MonsterElementalAilments.Kind.Freeze, now) > 0 ? 0
                : (1 - (m.Effects["action_speed_-%"] + m.Ailments.Magnitude(MonsterElementalAilments.Kind.Chill, now)) / 100.0)
                    * (now < m.ReceivedActionUntil ? m.ReceivedActionFactor : 1));

    /// <summary>Maim's relative walk factor and the movement_velocity_+% the client should hold. Maim adds -30 into the
    /// monster's existing increased/reduced movement bucket; it never overwrites the speed roll or another modifier.</summary>
    public static (double Factor, int Pct) MaimedMovementFactor(SpawnedMonster m, double now)
    {
        int basePct = m.Effects.MovementPct;
        int maimPct = now < m.MaimedUntil ? -30 : 0;
        int hinderPct = Math.Max(SpiderHinder(m, now), m.HinderedUntil > now ? 30 : 0);
        int lowLife = MonsterOnLowLife(m) ? m.Effects["movement_velocity_+%_when_on_low_life"] : 0;
        int totalPct = basePct + maimPct - hinderPct + lowLife;
        if ((maimPct != 0 || hinderPct != 0 || lowLife != 0) && m.Effects["movement_speed_cannot_be_reduced_below_base"] > 0)
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
    /// <paramref name="skill"/> the skill's own stats; both count. Under the Monsters lock, after the hit's damage, so the
    /// hit does not gain from its own shock.
    /// A killing hit's ailments stay on the corpse (wiki Ailment: a killing hit that inflicts an elemental ailment counts
    /// as killing an affected enemy; Elemental proliferation goes on from corpses), then the kill's own shock (Within the
    /// Tempest), unless the monster shatters; a corpse neither flees nor reflects. A living monster may flee (MonsterFlee), and a Mirrored one puts the
    /// ailments on the player too: at once, or into <paramref name="reflected"/> for the caller to apply after a sweep
    /// whose hits land together (<see cref="ReflectAilmentsLocked"/>). <paramref name="poison"/>: the hit's poison rows
    /// (<see cref="PoisonOnHitLocked"/>); a hit without them does not poison.
    /// </summary>
    public static void AilmentsOnHitLocked(ClientState conn, uint targetId, SpawnedMonster m, DamagePacket mitigated, bool spell,
        bool critical, int criticalMultiplier, IReadOnlyDictionary<uint, int> player, IReadOnlyDictionary<uint, int>? skill,
        double now, Random random, List<MonsterElementalAilments.Effect>? reflected = null,
        MonsterPoisons.Settings poison = default, ushort sourceSkill = 0, BlindHitInput blind = default, (int X, int Y)? hitOrigin = null, bool playerHit = true) =>
        AilmentsOnHitLocked(conn, targetId, m, mitigated, spell, critical, criticalMultiplier, player, skill,
            now, random, default(AttackAilmentGains), reflected, poison, sourceSkill, blind, hitOrigin, playerHit);

    public static void AilmentsOnHitLocked(ClientState conn, uint targetId, SpawnedMonster m, DamagePacket mitigated, bool spell,
        bool critical, int criticalMultiplier, IReadOnlyDictionary<uint, int> player, IReadOnlyDictionary<uint, int>? skill,
        double now, Random random, AttackAilmentGains attackGains, List<MonsterElementalAilments.Effect>? reflected = null,
        MonsterPoisons.Settings poison = default, ushort sourceSkill = 0, BlindHitInput blind = default, (int X, int Y)? hitOrigin = null, bool playerHit = true)
    {
        HitSummonsOnHitLocked(conn, m, skill, sourceSkill, now, random);
        PhantasmsOnHitLocked(conn, m, sourceSkill, now, random, skill: skill);
        CursesOnHitLocked(conn, m, sourceSkill, spell, now);
        KnockbackOnHitLocked(conn, m, player, skill, now, random, hitOrigin);
        // "You gain Onslaught for 4 seconds on Hit" and "Gain Arcane Surge on Hit with Spells" (Honoured Berserker
        // and Flood tattoos): every player hit, the spells-only one gated by the hit's own spell flag.
        if (playerHit) OnslaughtOnHitLocked(conn, now);
        if (playerHit && spell) ArcaneSurgeOnSpellHitLocked(conn, player, now, random);
        // "on Critical Strike against Marked Unique Enemy" (Loyalty Tattoo of Rakiata).
        if (playerHit && critical) LoyaltyCritVsMarkedUniqueLocked(conn, m, player);
        if (playerHit && !spell && conn.Summons.Fighters.Values.Any(g => g.Definition.Kind == "HolyRelic"))
            HolyRelicsOnHitLocked(conn, targetId, m, now);
        double factor = MonsterDamageTaken(m, spell, now, hit: true) * (critical ? criticalMultiplier / 100.0 : 1);
        int Player(string name) => Stats.Key(name) is uint key ? player.GetValueOrDefault(key) : 0;
        int Skill(string name) => skill is not null && Stats.Key(name) is uint key ? skill.GetValueOrDefault(key) : 0;
        // The physical part counts after the monster's physical reduction and its Maim, as the hit's damage does.
        var taken = mitigated.Select((type, range) => range.Scale(factor
            * (type == DamageType.Physical
                ? Math.Max(0, 1 - (m.Effects.PhysicalReduction - CrushReduction(m, now)) / 100.0)
                    * (1 + MaimedPhysicalTaken(m, now) / 100.0) : 1)));
        int Attacker(string name) => Player(name) + PanelStats.Of(name, Skill);
        var added = m.Ailments.Inflict(taken, critical, m.MaxLife, now, Attacker, name => m.Effects[name], random,
            AilmentArea(Player, Skill, spell));
        // Scorch, Brittle and Sap (MonsterAlternateAilments): only from a source, after chill, freeze and shock's draws.
        var alternate = AlternateAilmentsOnHitLocked(m, taken, critical, now, Attacker, random);
        foreach (var effect in added)
        {
            InflictedLocked(conn, m, effect);
            if (playerHit && effect.Kind == Kind.Freeze)
            {
                conn.RecentEvents.Record(StatConditions.FrozeEnemy, now);
                if (m.Hostile) GainFromAttackAilmentLocked(conn, attackGains.FreezeEndurance,
                    ChargeType.Endurance, EnduranceOnAttackFreeze);
            }
            else if (effect.Kind == Kind.Shock) conn.RecentEvents.Record(StatConditions.ShockedEnemy, now);
        }
        if (m.Dead || m.Life == 0)
        {
            // Shatter (wiki Shatter, Freeze): a non-unique monster a hit kills while frozen, the killing hit's own freeze
            // included, leaves no corpse, so nothing spreads from it and the kill's shock has nothing to land on.
            if (m.Rarity != MonsterRarity.Unique && m.Ailments.Magnitude(Kind.Freeze, now) > 0)
            {
                m.Ailments.Shatter();
                m.RuleAilments.Clear();
                conn.RecentEvents.Record(StatConditions.ShatteredEnemy, now);
            }
            else ShockOnKillLocked(conn, m, player, now);
            return;
        }
        HitDebuffsOnHitLocked(m, name => Player(name) + Skill(name), spell, critical, now, random);
        InflictMonsterBlind(conn, targetId, m, now, blind, spell, critical, random);
        ExposureOnHitLocked(m, name => Player(name) + Skill(name), mitigated, now, Random.Shared);
        WitherOnHitLocked(m, name => Player(name) + Skill(name), now, Random.Shared);
        int poisonStacksBefore = m.Poisons.Stacks(now);
        if (TryPoisonOnHitLocked(conn, m, mitigated, spell, critical, player, skill, poison, now, random,
                out double poisonDps, out double poisonSeconds)
            && playerHit && Player("poison_reflected_to_self") != 0 && Player("cannot_be_poisoned") == 0
            && Player("immune_to_poison") == 0
            // Golden Rule's reflected-poison source stops adding player stacks at 100; this is not an incoming cap.
            && conn.IncomingAilments.PoisonStacks(now) < 100)
        {
            int accepted = m.Poisons.Stacks(now) - poisonStacksBefore;
            for (int i = 0; i < accepted && conn.IncomingAilments.PoisonStacks(now) < 100; i++)
                conn.IncomingAilments.ApplyPoison(now, poisonDps, poisonSeconds, reflected: true, victim: Player);
        }
        // Fleeing (MonsterFlee): its own draws (Random.Shared), so a projectile's reaction draws stay in order.
        if (!m.Fleeing && MonsterFlee.Rolls(MonsterFlee.Chance(spell, Player, Skill),
                m.Effects["monster_chance_to_not_flee_%"], Random.Shared))
        {
            int size = Math.Max(0, LivingCollision.Size(m));
            m.FleeTo = MonsterFlee.Point(m.Walk.Position(now), conn.Walk.Position(now),
                (x, y) => conn.Layout.WalkableFor(x, y, size) && !conn.Barrier.Contains((x, y)), Random.Shared);
            m.Fleeing = m.FleeTo is not null;
        }
        if (added.Count + alternate.Count == 0 || conn.PlayerLife == 0
            || !RandomRolls.Chance(m.Effects["reflect_non_damaging_ailments_%_chance"], 100, random)) return;
        // Scorch and Brittle stop no action, so they act on the player at once, even inside a sweep.
        ReflectAlternateAilmentsLocked(conn, alternate, now, Player, random);
        if (added.Count == 0) return;
        if (reflected is not null) reflected.AddRange(added);
        else ReflectAilmentsLocked(conn, added, now, player, random);
    }

    internal static void ReflectPendingAilmentsLocked(ClientState conn, List<MonsterElementalAilments.Effect> pending,
        double now, IReadOnlyDictionary<uint, int> player, Random random)
    {
        if (pending.Count == 0) return;
        var accepted = pending.ToArray();
        pending.Clear();
        ReflectAilmentsLocked(conn, accepted, now, player, random);
    }

    /// <summary>Mirrored: the ailments a hit added act on the player as well (wiki Reflect), as they landed, then through
    /// the player's own modifiers; the exact reflected magnitude is not verified. A new freeze cancels the player's action.
    /// Under the Monsters lock.</summary>
    public static void ReflectAilmentsLocked(ClientState conn, IEnumerable<MonsterElementalAilments.Effect> added, double now,
        IReadOnlyDictionary<uint, int> player, Random random)
    {
        if (conn.PlayerLife == 0) return;
        int Player(string name) => Stats.Key(name) is uint key ? player.GetValueOrDefault(key) : 0;
        ApplyPlayerAilmentsLocked(conn, now, ailments =>
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
    /// the estimate's walk included (07 §6). Under the Monsters lock.
    /// </summary>
    public static List<(byte[] Packet, string Log)> MonsterAilmentChangesLocked(ClientState conn, double now)
    {
        ProliferateLocked(conn, now);
        var packets = new List<(byte[], string)>();
        ProliferateIgnitesLocked(conn, now, packets);
        foreach (var (id, m) in conn.Monsters)
        {
            BearTrapChangesLocked(conn, id, m, now, packets);
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
            if (!m.RuleAilments.Idle) packets.AddRange(AlternateAilmentBuffs(id, $"monster 0x{id:x} ({m.Label})", m.RuleAilments, now));
            bool elementalIdle = a.Idle && now >= m.ReceivedActionUntil;
            bool lowLifeMove = MonsterOnLowLife(m) && m.Effects["movement_velocity_+%_when_on_low_life"] != 0;
            if (elementalIdle && !TimedWalkDebuff(now, m) && SpiderHinder(m, now) == 0 && vines == 1 && trap == 1
                && m.Effects["action_speed_-%"] == 0 && a.WalkFactor == 1 && a.SentActionSpeed == 0 && !m.MaimedMovementShown && !lowLifeMove) continue;
            if (!elementalIdle)
            {
                foreach (var (kind, removed, added, magnitude) in a.Present(now))
                {
                    if (removed is ushort r) packets.Add((InstanceEntities.RemoveBuff(id, r), $"0x01c2 monster 0x{id:x} {kind} ends"));
                    if (added is ushort b) packets.Add((AilmentBuff(id, b, kind, magnitude, a.Remaining(kind, now)), $"0x01c1 monster 0x{id:x} ({m.Label}) {kind} {magnitude}%"));
                }
            }
            double action = MonsterActionFactor(m, now, freeze: false);
            // Maim changes movement only: fold its -30 into the movement bucket rather than scaling action speed. So does
            // Bear Trap's more-multiplier: the client's walk reads movement_velocity_+%, not bear_trap_movement_speed_+%_final.
            bool speedFloored = m.Effects["movement_speed_cannot_be_reduced_below_base"] > 0;
            var movement = MaimedMovementFactor(m, now);
            if (trap != 1 && !(speedFloored && trap < 1))
                movement = (movement.Factor * trap, (int)Math.Round((100 + movement.Pct) * trap - 100));
            double walk = (speedFloored ? Math.Max(1, action) : action)
                * movement.Factor;
            if (!speedFloored) walk *= vines;
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
            if (speed != a.SentActionSpeed && Stats.Key("virtual_action_speed_+%") is uint key)
            {
                RetimeMonsterMeleeClockLocked(m, now, speed);
                a.SentActionSpeed = speed;
                packets.Add((InstanceEntities.StatsUpdate(id, [(key, speed)]), $"0x01b3 monster 0x{id:x} virtual_action_speed_+% = {speed}"));
            }
            bool maimed = m.MaimedUntil > now || m.HinderedUntil > now || SpiderHinder(m, now) != 0
                || lowLifeMove || trap != 1;
            bool maimShown = m.MaimedMovementShown;
            bool movementChanged = maimed
                ? !maimShown || movement.Pct != m.MaimedMovementShownPct
                : maimShown && movement.Pct != m.MaimedMovementShownPct;
            m.MaimedMovementShown = maimed;
            if (movementChanged)
            {
                m.MaimedMovementShownPct = movement.Pct;
                packets.Add((InstanceEntities.StatsUpdate(id, [(StatKeys.MovementVelocityPct, movement.Pct)]),
                    $"0x01b3 monster 0x{id:x} movement_velocity_+% = {movement.Pct} (movement modifiers)"));
            }
            else if (!m.MaimedMovementShown)
            {
                m.MaimedMovementShownPct = 0;
            }
        }
        return packets;
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
    public static byte[] AilmentBuff(uint id, ushort instance, Kind kind, int magnitude, double seconds = 0) =>
        InstanceEntities.AddBuff(id, instance, (ushort)(kind + 1), PlayerId, u82: 0,
            values: kind == Kind.Freeze ? null : [(uint)magnitude],
            u68: kind == Kind.Freeze && seconds > 0 ? BitConverter.SingleToUInt32Bits((float)seconds) : null);
    public static void AilmentsOnHitLocked(ClientState conn, SpawnedMonster m, DamagePacket mitigated, bool spell,
        bool critical, int criticalMultiplier, IReadOnlyDictionary<uint, int> player, IReadOnlyDictionary<uint, int>? skill,
        double now, Random random, List<MonsterElementalAilments.Effect>? reflected = null,
        MonsterPoisons.Settings poison = default, ushort sourceSkill = 0, (int X, int Y)? hitOrigin = null) =>
        AilmentsOnHitLocked(conn, conn.Monsters.FirstOrDefault(p => ReferenceEquals(p.Value, m)).Key, m,
            mitigated, spell, critical, criticalMultiplier, player, skill, now, random, reflected, poison,
            sourceSkill, BlindHitInput.Capture(player, skill), hitOrigin);

}
