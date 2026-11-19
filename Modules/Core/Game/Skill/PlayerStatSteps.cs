using System.Collections.Frozen;
using GameServer.Core.World;

/// <summary>
/// The player's stats as ordered steps, each changing the stats the steps before it left (a <see cref="StatMap"/>). In an
/// area's frames each step is a rule of the player's stat graph (PlayerStatsCache.cs): it runs again only when a stat it
/// read changed before it or a change marks it (<see cref="PlayerStatStep"/>). Outside them, and for a fresh build, the
/// steps run in order on a plain map (<see cref="CharacterPlayerStatsSnapshot"/>).
/// <para>The character's own stats come first (the sources and what adds to them), then the class's attributes and pools,
/// which replace those ids, then the /set_stat replacements; the rest of the steps read the result.</para>
/// </summary>
public enum PlayerStatStep : byte
{
    Sources, Siphoning, CryMovement, Golems, Offerings, PanelDefaults, God, Auras, AuraPenetration, CatAspect,
    AvianCrab, Subscribers, ClassAttributes, OvercapCrit, AttackSpeed, UtilityFlasks, SelfVulnerability,
    Onslaught, VaalPrevention, VaalAura, SpiritLeadership, TotemAura, Rage, Blind, UsingFlask, SoulEater, MaximumPoolItems,
    ActorScale, Recovery, TimedRecovery, TailPanels, Rampage, TailOffence, TailCritical, LeechingDefences, AuraRestriction, AlwaysHit, RecentHits, Ward,
}

/// <summary>
/// What changed, as a caller of <c>SendCharacterStats</c> names it: the steps that read it run again in the next Stats
/// phase (<c>PlayerStatChangeReaders</c>), and the character panel is published once for the frame. What reaches the
/// stats only through CharacterStatSources is <see cref="Sources"/>; the pools are the caller's (a new Pools marks their
/// readers itself).
/// </summary>
public enum PlayerStatChange : byte
{
    /// <summary>Equipment, passives, jewels, level, tattoos, Pantheon, quest rewards: the steps that read the items,
    /// passives or pools (tracked inputs), and the sources.</summary>
    Build,
    /// <summary>A source CharacterStatSources folds in: charges, support gem buffs, links, guards, Hierophant, Slayer,
    /// conditional item stats.</summary>
    Sources,
    /// <summary>An aura, a stance or Petrified Blood on or off, or their reservation.</summary>
    Auras,
    /// <summary>A utility or recovery flask's effect.</summary>
    Flasks,
    /// <summary>The player's minions (golems, offerings, summon counts).</summary>
    Minions,
    /// <summary>Leech started or stopped.</summary>
    Leech,
    /// <summary>The player's elemental ailments.</summary>
    ElementalAilments,
    /// <summary>The damaging ailments on the player.</summary>
    IncomingAilments,
    /// <summary>/god.</summary>
    God,
    /// <summary>Only the panel: what changed is read when it is published (conditions the graph already tracks, a
    /// trap's degeneration, a revive).</summary>
    Panel,
}

public static partial class Game
{
    /// <summary>What a step hands on that is not a stat: the recovery baseline (the Recovery step's).</summary>
    public sealed class PlayerStepOutputs
    {
        public double BaselineLifePerSecond;
    }

    static readonly Action<ClientState, StatMap, PlayerStepOutputs>[] PlayerSteps = BuildPlayerSteps();

    static Action<ClientState, StatMap, PlayerStepOutputs>[] BuildPlayerSteps()
    {
        var steps = new Action<ClientState, StatMap, PlayerStepOutputs>[Enum.GetValues<PlayerStatStep>().Length];
        void Step(PlayerStatStep step, Action<ClientState, StatMap, PlayerStepOutputs> run) => steps[(int)step] = run;
        static int GetIn(StatMap stats, string name) => Stats.Key(name) is uint k ? stats.GetValueOrDefault(k) : 0;
        static void AddTo(StatMap stats, uint key, long value) => stats[key] = ActorScale.Saturate(stats.GetValueOrDefault(key) + value);

        // The character's own stats (BaseStats): every stat of the allocated passives and the active items, summed.
        Step(PlayerStatStep.Sources, (conn, stats, _) =>
        {
            List<ushort> nodes;
            lock (conn.Monsters) nodes = [.. conn.Passives];
            foreach (var (k, v) in CharacterStatSources(conn, nodes).Stats) stats[k] = v;
            // Their "recently" conditions change by themselves when an event ages out of its window.
            double now = conn.Clock.Elapsed.TotalSeconds;
            if (conn.RecentEvents.NextExpiry(now) is double expiry) StatsChangeIn(expiry - now);
            if (conn.ElementalAilments.NextChangeAt(now) is double ailment) StatsChangeIn(ailment - now);
            lock (conn.Monsters)
                if (conn.IncomingAilments.NextChangeAt(now) is double incoming) StatsChangeIn(incoming - now);
            MarkLaterStep(conn, PlayerStatStep.Onslaught);
        });
        Step(PlayerStatStep.Siphoning, (conn, stats, _) =>
        {
            IReadOnlyList<(uint Key, int Value)> siphoning;
            lock (conn.Monsters) siphoning = SiphoningRawStatsLocked(conn, stats);
            foreach (var (key, value) in siphoning) AddTo(stats, key, value);
        });
        Step(PlayerStatStep.CryMovement, (conn, stats, _) =>
        {
            if (Stats.Key("base_movement_velocity_+%") is uint cryMovement) AddTo(stats, cryMovement, IntimidatingCryMovement(conn));
        });
        Step(PlayerStatStep.Golems, (conn, stats, _) => AddGolemStats(conn, stats));
        Step(PlayerStatStep.Offerings, (conn, stats, _) => AddSelfOfferingStats(conn, stats));
        Step(PlayerStatStep.PanelDefaults, (_, stats, _) =>
        {
            foreach (var (stat, value) in PanelStatDefaults.Compute(name => GetIn(stats, name)))
                if (Stats.Key(stat) is uint key) stats[key] = value;
        });
        Step(PlayerStatStep.God, (conn, stats, _) =>
        {
            if (!conn.God) return;
            foreach (var (stat, value) in GodStats)
                if (Stats.Key(stat) is uint key) stats[key] = stats.GetValueOrDefault(key) + value;
            // the final value the client shows: 100% of maximum life per second, per minute
            if (Stats.Key("life_regeneration_rate_per_minute") is uint lr)
                stats[lr] = (int)Math.Min(int.MaxValue, (long)conn.Pools.Life * GodLifeRegenPctPerSecond / 100 * 60);
        });
        Step(PlayerStatStep.Auras, (conn, stats, _) =>
        {
            List<AuraCatalog.Aura> auras;
            lock (conn.Monsters) auras = AffectingAuras(conn);
            // "X% increased Effect of Herald Buffs on you" (passives, gear and tattoos) scales the heralds' buff stats.
            int heraldEffectPct = GetIn(stats, "herald_effect_on_self_+%");
            foreach (var (k, v) in AuraCatalog.RawStats(auras.Where(a => a.AffectsSelf), conn.Pools.AuraEffectPct, SkillsFor(conn), Stats,
                         heraldEffectPct).Concat(MinionAuraStats(conn, auras)).ToList())
                AddTo(stats, k, v);
        });
        Step(PlayerStatStep.AuraPenetration, (conn, stats, _) =>
        {
            List<AuraCatalog.Aura> auras;
            lock (conn.Monsters) auras = AffectingAuras(conn);
            foreach (var (k, v) in AuraCatalog.WhileAffectedPenetration(auras, stats, Stats).ToList()) AddTo(stats, k, v);
        });
        Step(PlayerStatStep.CatAspect, (conn, stats, _) =>
        {
            IReadOnlyList<(uint Key, int Value)> catPhase;
            lock (conn.Monsters)
                catPhase = CatAspectRawStatsLocked(conn, conn.Pools.AuraEffectPct,
                    name => Stats.Key(name) is uint key
                        ? stats.GetValueOrDefault(key)
                        : 0);
            foreach (var (k, v) in catPhase) AddTo(stats, k, v);
            MarkLaterStep(conn, PlayerStatStep.Onslaught);
        });
        Step(PlayerStatStep.AvianCrab, (conn, stats, _) =>
        {
            foreach (var (k, v) in AvianRawStatsLocked(conn, stats).Concat(CrabRawStatsLocked(conn, stats)).ToList()) AddTo(stats, k, v);
        });
        // The subscribers' stats (GameEvents.Stats), added like /god's: the pools (life, mana) are not rebuilt from them.
        Step(PlayerStatStep.Subscribers, (conn, stats, _) =>
        {
            if (!GameEvents.Stats.Any) return;
            foreach (var (stat, value) in GameEvents.RunStats(conn))
                if (Stats.Key(stat) is uint key) AddTo(stats, key, value);
        });
        // The class's attributes and the pools replace what the sources gave for those ids (the first of a repeated id).
        Step(PlayerStatStep.ClassAttributes, (conn, stats, _) =>
        {
            var seen = new HashSet<uint>();
            foreach (var (k, v) in ClassAttributes(conn.ClassRow, conn.Pools with
            {
                Accuracy = PlayerAccuracy(conn), Evasion = PlayerEvasion(conn),
                FireResistance = Resistance(conn, DamageType.Fire), ColdResistance = Resistance(conn, DamageType.Cold),
                LightningResistance = Resistance(conn, DamageType.Lightning), ChaosResistance = Resistance(conn, DamageType.Chaos),
            })) if (seen.Add(k)) stats[k] = v;
        });
        // The overcapped lightning resistance's increased crit (PoolsFor derived it once into Pools): the spell consumers read
        // the raw increased stats here (SpellCriticalStrike), while the attack and panel chances already fold the same value
        // into the pools. Additive with whatever won above, as a derived stat source.
        Step(PlayerStatStep.OvercapCrit, (conn, stats, _) =>
        {
            if (conn.Pools.LightningOvercapCritPct is int overCrit and not 0 && Stats.Key("critical_strike_chance_+%") is uint critInc)
                stats[critInc] = (int)Math.Clamp((long)stats.GetValueOrDefault(critInc) + overCrit, int.MinValue, int.MaxValue);
        });
        // The passives' conditional attack speed is already one final value in Pools. main_hand_attack_speed_+% is the
        // total the client swings with, so Unarmed reads only it.
        Step(PlayerStatStep.AttackSpeed, (conn, stats, _) =>
            stats[StatKeys.MainHandAttackSpeedPct] = conn.Pools.AttackSpeedPct
                + SoulEaterAttackSpeed(conn, stats) + LeechingAttackSpeed(conn, stats));
        // The utility flasks' plain stats (UtilityFlasks.RawStats: maximum resistances, crit, damage, cast speed), with
        // Mageblood's permanent flasks deduplicated against the running ones, plus the flask grounds.
        Step(PlayerStatStep.UtilityFlasks, (conn, stats, _) =>
        {
            int FlaskGet(string name) => GetIn(stats, name);
            AddItemUtilityFlaskRawStats(stats, UtilityStats(conn, FlaskGet)
                .Concat(conn.FlaskGrounds.PlayerStats(FlaskGet("consecrated_ground_effect_+%"))).ToList(), Stats);
        });
        Step(PlayerStatStep.SelfVulnerability, (conn, stats, _) => AddSelfVulnerabilityStats(conn, stats));
        Step(PlayerStatStep.Onslaught, (conn, stats, _) =>
        {
            if (Stats.Key("currently_has_onslaught") is uint onslaughtFlag) stats[onslaughtFlag] = HasOnslaught(conn) ? 1 : 0;
        });
        Step(PlayerStatStep.VaalPrevention, (conn, stats, _) => WithVaalPrevention(conn, stats));
        Step(PlayerStatStep.VaalAura, (conn, stats, _) => WithVaalAura(conn, stats));
        Step(PlayerStatStep.SpiritLeadership, (conn, stats, _) => AddSpiritLeadershipStats(conn, stats));
        Step(PlayerStatStep.TotemAura, (conn, stats, _) => AddTotemAuraStats(conn, stats));
        Step(PlayerStatStep.Rage, (conn, stats, _) => WithRage(conn, stats));
        Step(PlayerStatStep.Blind, (conn, stats, _) => WithBlind(conn, stats));
        Step(PlayerStatStep.UsingFlask, (conn, stats, _) => WithUsingFlask(conn, stats));
        Step(PlayerStatStep.SoulEater, (conn, stats, _) => WithSoulEater(conn, stats));
        Step(PlayerStatStep.MaximumPoolItems, (conn, stats, _) =>
            MaximumPoolItemSources.Apply(stats, Stats, conn.Pools.Life, conn.Pools.Mana));
        Step(PlayerStatStep.ActorScale, (conn, stats, _) => ActorScale.Project(stats, CosmeticScaleSources(conn)));
        // Devout of Garukhan: the while-moving evasion rating, and the while-moving regeneration in the recovery below; the
        // unmodified recovery baseline.
        Step(PlayerStatStep.Recovery, (conn, stats, outputs) =>
        {
            int Get(string name) => GetIn(stats, name);
            double statsAt = conn.Clock.Elapsed.TotalSeconds;
            bool moving = conn.Walk.Moving(statsAt);
            var recovery = PanelStats.WhileMoving(Get, moving);
            if (Stats.Key("evasion_rating") is uint evasionKey)
                stats[evasionKey] = PlayerEvasion(conn, statsAt);
            WithLeechConditions(conn, stats);
            // The unmodified recovery baseline, before the temporary contribution. The resource writer integrates
            // Fortification separately across exact expiry boundaries.
            outputs.BaselineLifePerSecond = PanelStats.LifePerMinute(recovery, conn.Pools.Life) / 60;
        });
        Step(PlayerStatStep.TimedRecovery, (conn, stats, _) =>
        {
            lock (conn.Monsters)
            {
                double now = conn.Clock.Elapsed.TotalSeconds;
                if (conn.Antiquarian.RecoveryUntil(now) is var stun && stun > now) StatsChangeIn(stun - now);
                if (conn.EnduringCryRecovery.Rate(now) > 0) StatsChangeIn(conn.EnduringCryRecovery.Until - now);
                // Turtle Shell's exact one-second integral is consumed separately by the resource writer.
                if (Stats.Key("base_life_regeneration_rate_per_minute") is uint turtle)
                    stats[turtle] = (int)Math.Clamp(stats.GetValueOrDefault(turtle)
                        + (conn.Antiquarian.RecoveryPerSecond(now) + conn.EnduringCryRecovery.Rate(now)) * 60, 0, int.MaxValue);
                var session = FortificationOf(conn);
                var value = session.Read(now);
                if (value.NextExpiry is double expires) StatsChangeIn(expires - now);
                AddFortificationCounts(stats, value);
                if (Stats.Key(FortificationSession.Regeneration) is uint regen)
                    stats[regen] = (int)Math.Clamp((long)stats.GetValueOrDefault(regen)
                        + value.LifeRegenerationPerMinutePercent(session.RegenPerStack), int.MinValue, int.MaxValue);
            }
        });
        Step(PlayerStatStep.TailPanels, (conn, stats, _) => ComputeStatSnapshotTail(PlayerTailInputs(conn, stats), StatSnapshotStage.Panels));
        Step(PlayerStatStep.Rampage, (conn, stats, _) => WithRampage(conn, stats));
        Step(PlayerStatStep.TailOffence, (conn, stats, _) => ComputeStatSnapshotTail(PlayerTailInputs(conn, stats), StatSnapshotStage.Offence));
        Step(PlayerStatStep.TailCritical, (conn, stats, _) =>
        {
            int Get(string name) => GetIn(stats, name);
            double now = conn.Clock.Elapsed.TotalSeconds;
            var ailments = conn.ElementalAilments.Snapshot(now);
            if (conn.ElementalAilments.NextChangeAt(now) is double ailment) StatsChangeIn(ailment - now);
            ComputeStatSnapshotTail(PlayerTailInputs(conn, stats) with
            {
                Ailments = [ailments.Ignite ? 1 : 0, ailments.Chill > 0 ? 1 : 0, ailments.Freeze ? 1 : 0, ailments.Shock > 0 ? 1 : 0],
                ActionFactor = PlayerActionFactorFrom(conn, Get),
                SuppressionChance = Suppression(conn).Chance,
            }, StatSnapshotStage.Critical);
        });
        Step(PlayerStatStep.LeechingDefences, (conn, stats, _) => WithLeechingDefences(stats));
        Step(PlayerStatStep.AuraRestriction, (conn, stats, _) => stats[OwnAuraRestriction.Disabled] = OwnAuraRestrictionValue(conn));
        Step(PlayerStatStep.AlwaysHit, (_, stats, _) =>
        {
            if (GetIn(stats, "global_always_hit") == 0) return;
            foreach (string name in AlwaysHitRows)
                if (Stats.Key(name) is uint hit) stats[hit] = 100;
        });
        Step(PlayerStatStep.RecentHits, (conn, stats, _) => { lock (conn.Monsters) AddRecentEnemyHitStatsLocked(conn, stats); });
        Step(PlayerStatStep.Ward, (conn, stats, _) =>
        {
            lock (conn.Monsters)
            {
                double now = conn.Clock.Elapsed.TotalSeconds;
                bool brokenNow = conn.Ward.Broken && now < conn.Ward.RestoresAt;
                if (brokenNow) StatsChangeIn(conn.Ward.RestoresAt - now);
                if (Stats.Key("ward") is uint ward) stats[ward] = (int)conn.Ward.Maximum;
                if (Stats.Key("ward_broken") is uint broken) stats[broken] = brokenNow ? 1 : 0;
            }
        });
        for (int i = 0; i < steps.Length; i++)
            if (steps[i] is null) throw new InvalidOperationException($"player stat step {(PlayerStatStep)i} has no code");
        return steps;
    }

    static readonly string[] AlwaysHitRows = ["main_hand_chance_to_hit_%", "off_hand_chance_to_hit_%",
        "main_hand_chance_to_hit_evasive_monsters_%", "off_hand_chance_to_hit_evasive_monsters_%"];

    // The tail's inputs as a step reads them: the stats before it and the state it reads.
    static StatSnapshotTailInputs PlayerTailInputs(ClientState conn, StatMap stats)
    {
        var (mainLocal, offLocal) = HandLocalStats(conn);
        var (_, lifeRegenerationToEnergyShield) = PanelStats.LifeRegeneration(
            name => Stats.Key(name) is uint key ? stats.GetValueOrDefault(key) : 0, conn.Pools.Life);
        return ReadStatSnapshotTailInputs(conn, stats, mainLocal, offLocal,
            lifeRegenerationToEnergyShield, conn.Walk.Moving(conn.Clock.Elapsed.TotalSeconds));
    }

    /// <summary>Runs one step (a player stat graph rule).</summary>
    internal static void RunPlayerStep(PlayerStatStep step, ClientState conn, StatMap stats, PlayerStepOutputs outputs) =>
        PlayerSteps[(int)step](conn, stats, outputs);

    public sealed record PanelSourceSnapshot(IReadOnlyDictionary<uint, int> Raw, int PlayerLevel,
        IReadOnlyDictionary<string, int> MainHand, IReadOnlyDictionary<string, int> OffHand, bool MainArmed, bool OffArmed);

    public static Dictionary<uint, int> CharacterPlayerStatsCore(ClientState conn, out double baselineLifePerSecond) =>
        CachedPlayerStats(conn, out baselineLifePerSecond);

    /// <summary>Every step in order on a plain map: a fresh build (outside an area's frames, or a scope that asks for one).
    /// With <paramref name="capturePanelSources"/>, the stats before the tail too, for the skill publication.</summary>
    public static Dictionary<uint, int> CharacterPlayerStatsSnapshot(ClientState conn, out double baselineLifePerSecond,
        out PanelSourceSnapshot? panelSources, bool capturePanelSources = false)
    {
        var stats = new PlainStatMap();
        var outputs = new PlayerStepOutputs();
        panelSources = null;
        for (int i = 0; i < PlayerSteps.Length; i++)
        {
            if (capturePanelSources && (PlayerStatStep)i == PlayerStatStep.TailPanels)
            {
                var (mainLocal, offLocal) = HandLocalStats(conn);
                panelSources = new(stats.Stats.ToFrozenDictionary(), conn.Pools.Level,
                    mainLocal.ToFrozenDictionary(), offLocal.ToFrozenDictionary(),
                    WeaponIn(conn, ActiveMainHand(conn)) is not null, OffhandWeaponFor(conn) is not null);
            }
            PlayerSteps[i](conn, stats, outputs);
        }
        baselineLifePerSecond = outputs.BaselineLifePerSecond;
        return stats.Stats;
    }
}
