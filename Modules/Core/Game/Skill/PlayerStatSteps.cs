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
    Sources, Siphoning, SpiritCharges, CryMovement, Golems, Offerings, PanelDefaults, God, Auras, AuraPenetration, CatAspect,
    AvianCrab, Subscribers, ClassAttributes, OvercapCrit, BlockDamage, AttackSpeed, UtilityFlasks, SelfVulnerability,
    Onslaught, VaalPrevention, VaalAura, SpiritLeadership, TotemAura, Rage, Blind, UsingFlask, SoulEater, MaximumPoolItems,
    ActorScale, Recovery, TimedRecovery, TailPanels, Rampage, TailOffence, TailCritical, LeechingDefences, AuraRestriction, AlwaysHit, RecentHits, Ward, Resonance,
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
    /// <summary>Trinity or Unholy Trinity resonance counters changed.</summary>
    Resonance,
}

public static partial class Game
{
    /// <summary>What a step hands on that is not a stat: the recovery baseline (the Recovery step's).</summary>
    public sealed class PlayerStepOutputs
    {
        public double BaselineLifePerSecond;
        /// <summary>The raw stats of the passives and items (CharacterStatSources) as the Sources step last summed them.</summary>
        public IReadOnlyDictionary<uint, int>? Sources;
    }

    static readonly Action<Player, StatMap, PlayerStepOutputs>[] PlayerSteps = BuildPlayerSteps();

    static Action<Player, StatMap, PlayerStepOutputs>[] BuildPlayerSteps()
    {
        var steps = new Action<Player, StatMap, PlayerStepOutputs>[Enum.GetValues<PlayerStatStep>().Length];
        void Step(PlayerStatStep step, Action<Player, StatMap, PlayerStepOutputs> run) => steps[(int)step] = run;
        static int GetIn(StatMap stats, uint name) => stats.GetValueOrDefault(name);
        static void AddTo(StatMap stats, uint key, long value) => stats[key] = ActorScale.Saturate(stats.GetValueOrDefault(key) + value);

        // The character's own stats (BaseStats): every stat of the allocated passives and the active items, summed.
        Step(PlayerStatStep.Sources, (player, stats, outputs) =>
        {
            List<ushort> nodes;
            nodes = [.. player.Character.Passives];
            var sources = CharacterStatSources(player, nodes).Stats;
            outputs.Sources = sources;
            foreach (var (k, v) in sources) stats[k] = v;
            if (MapModsOf(player) is { } map)
            {
                AddTo(stats, Stat.MapPlayersNoRegenerationIncludingEs, map.Get(Stat.MapPlayersNoRegenerationIncludingEs));
                AddTo(stats, Stat.MapPlayerLifeAndEsRecoverySpeedPlusPctFinal,
                    map.Get(Stat.MapPlayerLifeAndEsRecoverySpeedPlusPctFinal));
                AddTo(stats, Stat.MapPlayersSkillAreaOfEffectPlusPctFinal, map.Get(Stat.MapPlayersSkillAreaOfEffectPlusPctFinal));
                AddTo(stats, Stat.MapPlayerMaximumLeechRatePlusPct, map.Get(Stat.MapPlayerMaximumLeechRatePlusPct));
                AddTo(stats, Stat.BuffTimePassedPlusPctOnlyBuffCategory, map.Get(Stat.MapPlayerBuffTimePassedPlusPctOnlyBuffCategory));
            }
            // Their "recently" conditions change by themselves when an event ages out of its window.
            double now = AreaTime(player);
            if (player.RecentEvents.NextExpiry(now) is double expiry) StatsChangeIn(expiry - now);
            if (player.ElementalAilments.NextChangeAt(now) is double ailment) StatsChangeIn(ailment - now);
            if (player.IncomingAilments.NextChangeAt(now) is double incoming) StatsChangeIn(incoming - now);
            MarkLaterStep(player, PlayerStatStep.Onslaught);
        });
        Step(PlayerStatStep.Siphoning, (player, stats, _) =>
        {
            IReadOnlyList<(uint Key, int Value)> siphoning;
            siphoning = SiphoningRawStatsLocked(player, stats);
            foreach (var (key, value) in siphoning) AddTo(stats, key, value);
        });
        Step(PlayerStatStep.SpiritCharges, (player, stats, _) => ApplySpiritChargeStats(player, stats));
        Step(PlayerStatStep.CryMovement, (player, stats, _) =>
        {
            if (Stat.BaseMovementVelocityPlusPct is var cryMovement) AddTo(stats, cryMovement, IntimidatingCryMovement(player));
        });
        Step(PlayerStatStep.Golems, (player, stats, _) => AddGolemStats(player, stats));
        Step(PlayerStatStep.Offerings, (player, stats, _) => AddSelfOfferingStats(player, stats));
        Step(PlayerStatStep.PanelDefaults, (_, stats, _) =>
        {
            foreach (var (stat, value) in PanelStatDefaults.Compute(name => GetIn(stats, name)))
                if (stat is var key) stats[key] = value;
        });
        Step(PlayerStatStep.God, (player, stats, _) =>
        {
            if (!player.God) return;
            foreach (var (stat, value) in GodStats)
                if (stat is var key) stats[key] = stats.GetValueOrDefault(key) + value;
        });
        Step(PlayerStatStep.Auras, (player, stats, _) =>
        {
            List<AuraCatalog.Aura> auras;
            auras = AffectingAuras(player);
            if (OwnEternalBlessingActive(player)) stats[Stat.NonBlessingSkillsWithManaReservationAreDisabled] = 1;
            // "X% increased Effect of Herald Buffs on you" (passives, gear and tattoos) scales the heralds' buff stats.
            int heraldEffectPct = GetIn(stats, Stat.HeraldEffectOnSelfPlusPct);
            foreach (var (k, v) in AuraCatalog.RawStats(auras.Where(a => a.AffectsSelf), player.Pools.AuraEffectPct, SkillsFor(player), Stats,
                         heraldEffectPct).Concat(MinionAuraStats(player, auras)).ToList())
                AddTo(stats, k, v);
            // "Auras from your Skills grant #% increased Damage to you and Allies": once for each of the player's own Aura
            // skills affecting them, scaled by that aura's effect as its other stats are.
            // shortcut: allies and minions do not get it yet.
            if (GetIn(stats, Stat.AurasGrantDamagePlusPctToYouAndYourAllies) is int perAura and not 0)
                AddTo(stats, Stat.DamagePlusPct, auras
                    .Where(a => a.ExternalEffectPct is null && SkillsFor(player).IsAura(a.Effect))
                    .Sum(a => (long)Math.Round(perAura * Math.Max(0, 100 + player.Pools.AuraEffectPct + a.SupportEffectPct) / 100.0,
                        MidpointRounding.AwayFromZero)));
        });
        Step(PlayerStatStep.AuraPenetration, (player, stats, _) =>
        {
            List<AuraCatalog.Aura> auras;
            auras = AffectingAuras(player);
            foreach (var (k, v) in AuraCatalog.WhileAffectedPenetration(auras, stats, Stats).ToList()) AddTo(stats, k, v);
        });
        Step(PlayerStatStep.CatAspect, (player, stats, _) =>
        {
            IReadOnlyList<(uint Key, int Value)> catPhase;
            catPhase = CatAspectRawStatsLocked(player, player.Pools.AuraEffectPct,
                name => name is var key
                    ? stats.GetValueOrDefault(key)
                    : 0);
            foreach (var (k, v) in catPhase) AddTo(stats, k, v);
            MarkLaterStep(player, PlayerStatStep.Onslaught);
        });
        Step(PlayerStatStep.AvianCrab, (player, stats, _) =>
        {
            foreach (var (k, v) in AvianRawStatsLocked(player, stats).Concat(CrabRawStatsLocked(player, stats)).ToList()) AddTo(stats, k, v);
        });
        // The subscribers' stats (GameEvents.Stats), added like /god's: the pools (life, mana) are not rebuilt from them.
        Step(PlayerStatStep.Subscribers, (player, stats, _) =>
        {
            if (!GameEvents.Stats.Any) return;
            foreach (var (stat, value) in GameEvents.RunStats(player))
                if (stat is var key) AddTo(stats, key, value);
        });
        // The class's attributes and the pools replace what the sources gave for those ids (the first of a repeated id).
        Step(PlayerStatStep.ClassAttributes, (player, stats, _) =>
        {
            var seen = new HashSet<uint>();
            foreach (var (k, v) in ClassAttributes(player.Character.ClassRow, player.Pools with
            {
                Accuracy = PlayerAccuracy(player), Evasion = PlayerEvasion(player),
                FireResistance = Resistance(player, DamageType.Fire), ColdResistance = Resistance(player, DamageType.Cold),
                LightningResistance = Resistance(player, DamageType.Lightning), ChaosResistance = Resistance(player, DamageType.Chaos),
            })) if (seen.Add(k)) stats[k] = v;
        });
        // The overcapped lightning resistance's increased crit (PoolsFor derived it once into Pools): the spell consumers read
        // the raw increased stats here (SpellCriticalStrike), while the attack and panel chances already fold the same value
        // into the pools. Additive with whatever won above, as a derived stat source.
        Step(PlayerStatStep.OvercapCrit, (player, stats, _) =>
        {
            if (player.Pools.LightningOvercapCritPct is int overCrit and not 0 && Stat.CriticalStrikeChancePlusPct is var critInc)
                stats[critInc] = (int)Math.Clamp((long)stats.GetValueOrDefault(critInc) + overCrit, int.MinValue, int.MaxValue);
        });
        // "#% increased Damage per 1% Chance to Block Attack Damage": the capped attack block chance in Pools.
        Step(PlayerStatStep.BlockDamage, (player, stats, _) =>
        {
            if (GetIn(stats, Stat.DamagePlusPctPer1PctBlockChance) is int perBlock and not 0)
                AddTo(stats, Stat.DamagePlusPct,
                    (long)perBlock * Math.Clamp(player.Pools.BlockChance, 0, player.Pools.BlockChanceMax));
        });
        // The passives' conditional attack speed is already one final value in Pools. main_hand_attack_speed_+% is the
        // total the client swings with, so Unarmed reads only it.
        Step(PlayerStatStep.AttackSpeed, (player, stats, _) =>
            stats[Stat.MainHandAttackSpeedPlusPct] = player.Pools.AttackSpeedPct
                + SoulEaterAttackSpeed(player, stats) + LeechingAttackSpeed(player, stats));
        // The utility flasks' plain stats (UtilityFlasks.RawStats: maximum resistances, crit, damage, cast speed), with
        // Mageblood's permanent flasks deduplicated against the running ones, plus the flask grounds.
        Step(PlayerStatStep.UtilityFlasks, (player, stats, _) =>
        {
            int FlaskGet(uint name) => GetIn(stats, name);
            var running = UtilityStats(player, FlaskGet)
                .Concat(player.FlaskGrounds.PlayerStats(FlaskGet(Stat.ConsecratedGroundEffectPlusPct))).ToList();
            AddItemUtilityFlaskRawStats(stats, running, Stats);
            ApplyWiseOak(stats, running);
        });
        Step(PlayerStatStep.SelfVulnerability, (player, stats, _) =>
        {
            var selfVulnerability = AddSelfVulnerabilityStats(player, stats);
            AddMapHexStats(player, MapModsOf(player), stats, existingVulnerability: selfVulnerability);
        });
        Step(PlayerStatStep.Onslaught, (player, stats, _) =>
        {
            if (Stat.CurrentlyHasOnslaught is var onslaughtFlag) stats[onslaughtFlag] = HasOnslaught(player) ? 1 : 0;
        });
        Step(PlayerStatStep.VaalPrevention, (player, stats, _) => WithVaalPrevention(player, stats));
        Step(PlayerStatStep.VaalAura, (player, stats, _) =>
        {
            WithVaalAura(player, stats);
            WithTemporaryAuraStats(player, stats);
        });
        Step(PlayerStatStep.SpiritLeadership, (player, stats, _) => AddSpiritLeadershipStats(player, stats));
        Step(PlayerStatStep.TotemAura, (player, stats, _) => AddTotemAuraStats(player, stats));
        Step(PlayerStatStep.Rage, (player, stats, _) => WithRage(player, stats));
        Step(PlayerStatStep.Blind, (player, stats, _) => WithBlind(player, stats));
        Step(PlayerStatStep.UsingFlask, (player, stats, _) => WithUsingFlask(player, stats));
        Step(PlayerStatStep.SoulEater, (player, stats, _) => WithSoulEater(player, stats));
        Step(PlayerStatStep.MaximumPoolItems, (player, stats, _) =>
            MaximumPoolItemSources.Apply(stats, Stats, player.Pools.Life, player.Pools.Mana, player.Pools.EnergyShield));
        Step(PlayerStatStep.ActorScale, (player, stats, _) => ActorScale.Project(stats, CosmeticScaleSources(player)));
        // Devout of Garukhan: the while-moving evasion rating, and the while-moving regeneration in the recovery below; the
        // unmodified recovery baseline.
        Step(PlayerStatStep.Recovery, (player, stats, outputs) =>
        {
            int Get(uint name) => GetIn(stats, name);
            double statsAt = AreaTime(player);
            bool moving = player.Walk.Moving(statsAt);
            var recovery = PanelStats.WhileMoving(Get, moving);
            if (Stat.EvasionRating is var evasionKey)
                stats[evasionKey] = PlayerEvasion(player, statsAt);
            WithLeechConditions(player, stats);
            // The low-life flag the damage pipeline reads (Pain Attunement's "30% more Spell Damage when on Low Life"), kept
            // here with the other live state flags so it cannot go stale with the cached stat head.
            stats[Stat.OnLowLife] = CriticalOnLowLife(player, stats) ? 1 : 0;
            if (Get(Stat.KeystoneCorruptedDefences) != 0) AddTo(stats, IncomingResourceDefences.BypassStat, 50);
            // The unmodified recovery baseline, before the temporary contribution. The resource writer integrates
            // Fortification separately across exact expiry boundaries.
            outputs.BaselineLifePerSecond = PanelStats.LifePerMinute(recovery, player.Pools.Life) * LifeRegenerationKeystoneFactor(Get) / 60;
        });
        Step(PlayerStatStep.TimedRecovery, (player, stats, _) =>
        {
            double now = AreaTime(player);
            if (player.Antiquarian.RecoveryUntil(now) is var stun && stun > now) StatsChangeIn(stun - now);
            if (player.EnduringCryRecovery.Rate(now) > 0) StatsChangeIn(player.EnduringCryRecovery.Until - now);
            // Turtle Shell's exact one-second integral is consumed separately by the resource writer.
            if (Stat.BaseLifeRegenerationRatePerMinute is var turtle)
                stats[turtle] = (int)Math.Clamp(stats.GetValueOrDefault(turtle)
                    + (player.Antiquarian.RecoveryPerSecond(now) + player.EnduringCryRecovery.Rate(now)) * 60, 0, int.MaxValue);
            var session = FortificationOf(player);
            var value = session.Read(now);
            if (value.NextExpiry is double expires) StatsChangeIn(expires - now);
            AddFortificationCounts(stats, value);
            if (FortificationSession.Regeneration is var regen)
                stats[regen] = (int)Math.Clamp((long)stats.GetValueOrDefault(regen)
                    + value.LifeRegenerationPerMinutePercent(session.RegenPerStack), int.MinValue, int.MaxValue);
        });
        Step(PlayerStatStep.TailPanels, (player, stats, _) => ComputeStatSnapshotTail(PlayerTailInputs(player, stats), StatSnapshotStage.Panels));
        Step(PlayerStatStep.Rampage, (player, stats, _) => WithRampage(player, stats));
        Step(PlayerStatStep.TailOffence, (player, stats, _) => ComputeStatSnapshotTail(PlayerTailInputs(player, stats), StatSnapshotStage.Offence));
        Step(PlayerStatStep.TailCritical, (player, stats, _) =>
        {
            int Get(uint name) => GetIn(stats, name);
            double now = AreaTime(player);
            var ailments = player.ElementalAilments.Snapshot(now);
            if (player.ElementalAilments.NextChangeAt(now) is double ailment) StatsChangeIn(ailment - now);
            if (now - player.LastEnemyHitAt <= RecentlySeconds) StatsChangeIn(player.LastEnemyHitAt + RecentlySeconds - now + 1e-6);
            var spellDefences = SpellDefences(player);
            stats[Stat.ChanceToDodgeSpellsPct] = spellDefences.Dodge;
            ComputeStatSnapshotTail(PlayerTailInputs(player, stats) with
            {
                Ailments = [ailments.Ignite ? 1 : 0, ailments.Chill > 0 ? 1 : 0, ailments.Freeze ? 1 : 0, ailments.Shock > 0 ? 1 : 0],
                ActionFactor = PlayerActionFactorFrom(player, Get),
                SuppressionChance = spellDefences.Suppression,
            }, StatSnapshotStage.Critical);
        });
        Step(PlayerStatStep.LeechingDefences, (player, stats, _) => WithLeechingDefences(stats));
        Step(PlayerStatStep.AuraRestriction, (player, stats, _) => stats[OwnAuraRestriction.Disabled] = OwnAuraRestrictionValue(player));
        Step(PlayerStatStep.AlwaysHit, (_, stats, _) =>
        {
            if (GetIn(stats, Stat.GlobalAlwaysHit) == 0) return;
            foreach (uint name in AlwaysHitRows)
                if (name is var hit) stats[hit] = 100;
        });
        Step(PlayerStatStep.RecentHits, (player, stats, _) => { AddRecentEnemyHitStatsLocked(player, stats); });
        Step(PlayerStatStep.Ward, (player, stats, _) =>
        {
            double now = AreaTime(player);
            bool brokenNow = player.Ward.Broken && now < player.Ward.RestoresAt;
            if (brokenNow) StatsChangeIn(player.Ward.RestoresAt - now);
            if (Stat.Ward is var ward) stats[ward] = (int)player.Ward.Maximum;
            if (Stat.WardBroken is var broken) stats[broken] = brokenNow ? 1 : 0;
        });
        Step(PlayerStatStep.Resonance, (player, stats, _) => AddResonanceStats(player, stats));
        for (int i = 0; i < steps.Length; i++)
            if (steps[i] is null) throw new InvalidOperationException($"player stat step {(PlayerStatStep)i} has no code");
        return steps;
    }

    static readonly uint[] AlwaysHitRows = [Stat.MainHandChanceToHitPct, Stat.OffHandChanceToHitPct,
        Stat.MainHandChanceToHitEvasiveMonstersPct, Stat.OffHandChanceToHitEvasiveMonstersPct];

    // The tail's inputs as a step reads them: the stats before it and the state it reads.
    static StatSnapshotTailInputs PlayerTailInputs(Player player, StatMap stats)
    {
        var (mainLocal, offLocal) = HandLocalStats(player);
        var (_, lifeRegenerationToEnergyShield) = PanelStats.LifeRegeneration(
            name => name is var key ? stats.GetValueOrDefault(key) : 0, player.Pools.Life);
        return ReadStatSnapshotTailInputs(player, stats, mainLocal, offLocal,
            lifeRegenerationToEnergyShield,
            player.Walk.Moving(AreaTime(player)));
    }

    /// <summary>Runs one step (a player stat graph rule).</summary>
    internal static void RunPlayerStep(PlayerStatStep step, Player player, StatMap stats, PlayerStepOutputs outputs) =>
        PlayerSteps[(int)step](player, stats, outputs);

    public sealed record PanelSourceSnapshot(IReadOnlyDictionary<uint, int> Raw, int PlayerLevel,
        IReadOnlyDictionary<uint, int> MainHand, IReadOnlyDictionary<uint, int> OffHand, bool MainArmed, bool OffArmed);

    public static Dictionary<uint, int> CharacterPlayerStatsCore(Player player, out double baselineLifePerSecond) =>
        CachedPlayerStats(player, out baselineLifePerSecond);

    /// <summary>Every step in order on a plain map: a fresh build (outside an area's frames, or a scope that asks for one).
    /// With <paramref name="capturePanelSources"/>, the stats before the tail too, for the skill publication.</summary>
    public static Dictionary<uint, int> CharacterPlayerStatsSnapshot(Player player, out double baselineLifePerSecond,
        out PanelSourceSnapshot? panelSources, bool capturePanelSources = false)
    {
        var stats = new PlainStatMap();
        var outputs = new PlayerStepOutputs();
        panelSources = null;
        for (int i = 0; i < PlayerSteps.Length; i++)
        {
            if (capturePanelSources && (PlayerStatStep)i == PlayerStatStep.TailPanels)
                panelSources = PanelSources(player, stats.Stats.ToFrozenDictionary());
            PlayerSteps[i](player, stats, outputs);
        }
        baselineLifePerSecond = outputs.BaselineLifePerSecond;
        return stats.Stats;
    }

    // The panel's sources: the stats before the tail, and the hands'.
    static PanelSourceSnapshot PanelSources(Player player, IReadOnlyDictionary<uint, int> raw)
    {
        var (mainLocal, offLocal) = HandLocalStats(player);
        return new(raw, player.Pools.Level, mainLocal.ToFrozenDictionary(), offLocal.ToFrozenDictionary(),
            WeaponIn(player, ActiveMainHand(player)) is not null, OffhandWeaponFor(player) is not null);
    }
}
