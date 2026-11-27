using GameServer.Core;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

// Hostile monster allied auras and player-affecting monster aura mods:
//   - Allied buff auras (radius 50 cells from BuffTemplates.AuraRadius):
//       of Haste (MonsterModSpeedAura), of Damaging (MonsterModPhysicalDamageAura),
//       of Precision (MonsterModAccuracyAndCritAura), of Discipline (MonsterModEnergyShieldAura + MonsterImplicitAuraEnergyShield1),
//       of Recovery (MonsterModHealingAura, radius 45), Consecrated Ground (MonsterModConsecratedGround, radius 30)
//   - Player-affecting aura mods:
//       of Hindering (MonsterModHinderAura, radius 30 -> -30% movement speed Hinder debuff)
//       Temporal (MonsterModTemporalAura, radius 32 -> Proximity Shield + -25% action speed bubble)
//       Siphoning (MonsterModManaSiphonAura, ring 12..35 cells -> lightning DoT + 10%/s mana drain)
// All state lives in RuleTables for hot-reload and multi-instance safety, with zero hot-path overhead when no aura
// carrier is present in the area.
public static partial class Game
{
    const string AlliedAuraStatsKey = "monster_allied_auras";
    const string EncounterProximityShieldKey = "encounter_proximity_shield";
    const ushort TemporalBubbleBuffRow = 2008; // archnemesis_time_bubble
    const double TemporalBubbleRadius = 32.0;
    const double AlliedAuraRadius = 50.0;
    const double HinderAuraRadius = 30.0;
    const double RecoveryAuraRadius = 45.0;
    const double ConsecratedAuraRadius = 30.0;
    const double ManaSiphonInnerRadius = 12.0;
    const double ManaSiphonOuterRadius = 35.0;

    [Flags]
    enum MonsterAuraFlags
    {
        None = 0,
        Speed = 1 << 0,            // of Haste: +40% atk/cast, +30% ms
        Damage = 1 << 1,           // of Damaging: +40% phys dmg
        AccuracyCrit = 1 << 2,     // of Precision: +100% acc, +300% crit
        EnergyShield40 = 1 << 3,   // of Discipline: +40% life as extra max ES
        EnergyShield35 = 1 << 4,   // MonsterImplicitAuraEnergyShield1: +35% life as extra max ES
        Hinder = 1 << 5,           // of Hindering: -30% ms to nearby players (r=30)
        Temporal = 1 << 6,         // Temporal: proximity shield (r=32) + -25% action speed to nearby players
        Recovery = 1 << 7,         // of Recovery: 4%/s life regen to self & allies (r=45)
        Consecrated = 1 << 8,      // Consecrated Ground: 5%/s life regen to self & allies (r=30)
        ManaSiphon = 1 << 9,       // Siphoning: ring 12..35 drains mana & lightning DoT
    }

    sealed class MonsterAuraState
    {
        public bool Checked;
        public MonsterAuraFlags CarrierFlags;
        public MonsterAuraFlags AppliedAlliedFlags;
        public bool HasTemporalShield;
        public bool HasEncounterShield;
    }

    sealed class AreaTemporalCounter
    {
        public int Count;
    }

    sealed class PlayerAuraExposure
    {
        public bool Hindered;
        public bool Temporal;
        public readonly object HinderHolder = new();
        public readonly object TemporalHolder = new();
        public double SiphonDps;
        public double SiphonManaCarry;
    }

    static readonly RuleTable<SpawnedMonster, MonsterAuraState> MonsterAuraStates = new();
    static readonly RuleTable<GameArea, AreaTemporalCounter> AreaTemporalShieldCounts = new();
    static readonly RuleTable<Player, PlayerAuraExposure> PlayerMonsterAuraExposures = new();

    static readonly IReadOnlyDictionary<uint, int> HinderAuraPlayerStats =
        new Dictionary<uint, int> { [Stat.BaseMovementVelocityPlusPct] = -30 };
    static readonly IReadOnlyDictionary<uint, int> TemporalAuraPlayerStats =
        new Dictionary<uint, int> { [Stat.ActionSpeedMinusPct] = 25, [Stat.DebuffTimePassedPlusPct] = -40, [Stat.BaseCooldownSpeedPlusPct] = -60 };

    static ushort _auraSpeedHash, _auraDamageHash, _auraAccCritHash, _auraEsHash, _auraHinderHash,
        _auraTemporalHash, _auraHealHash, _auraConsecratedHash, _auraSiphonHash, _auraEsImplicitHash;

    /// <summary>Pre-resolves monster aura mod hashes once at startup (<see cref="LoadMonsterModsData"/>).</summary>
    public static void LoadMonsterAurasData()
    {
        ushort HashOf(string id) => MonsterModRolls.Mods.ById(id)?.Hash ?? 0;
        _auraSpeedHash = HashOf("MonsterModSpeedAura");
        _auraDamageHash = HashOf("MonsterModPhysicalDamageAura");
        _auraAccCritHash = HashOf("MonsterModAccuracyAndCritAura");
        _auraEsHash = HashOf("MonsterModEnergyShieldAura");
        _auraHinderHash = HashOf("MonsterModHinderAura");
        _auraTemporalHash = HashOf("MonsterModTemporalAura");
        _auraHealHash = HashOf("MonsterModHealingAura");
        _auraConsecratedHash = HashOf("MonsterModConsecratedGround");
        _auraSiphonHash = HashOf("MonsterModManaSiphonAura");
        _auraEsImplicitHash = HashOf("MonsterImplicitAuraEnergyShield1");
    }

    static MonsterAuraFlags DetectCarrierFlags(SpawnedMonster m)
    {
        var flags = MonsterAuraFlags.None;
        if (!m.Hostile) return flags;
        if (m.Effects[Stat.CannotBeDamagedByThingsOutsideRadius] > 0
            && !m.TemporaryStats.ContainsKey(EncounterProximityShieldKey))
            flags |= MonsterAuraFlags.Temporal;

        var list = m.Mods.Mods;
        if (list.Count > 0)
        {
            for (int i = 0; i < list.Count; i++)
            {
                ushort h = list[i].Hash;
                if (h == 0) continue;
                if (h == _auraSpeedHash) flags |= MonsterAuraFlags.Speed;
                else if (h == _auraDamageHash) flags |= MonsterAuraFlags.Damage;
                else if (h == _auraAccCritHash) flags |= MonsterAuraFlags.AccuracyCrit;
                else if (h == _auraEsHash) flags |= MonsterAuraFlags.EnergyShield40;
                else if (h == _auraHinderHash) flags |= MonsterAuraFlags.Hinder;
                else if (h == _auraTemporalHash) flags |= MonsterAuraFlags.Temporal;
                else if (h == _auraHealHash) flags |= MonsterAuraFlags.Recovery;
                else if (h == _auraConsecratedHash) flags |= MonsterAuraFlags.Consecrated;
                else if (h == _auraSiphonHash) flags |= MonsterAuraFlags.ManaSiphon;
            }
        }

        // Species implicit ES aura (MonsterImplicitAuraEnergyShield1).
        if (_auraEsImplicitHash != 0 && m.Variety is { } v && v.ActiveSpeciesModsOrEmpty.Count > 0)
        {
            var rows = v.ActiveSpeciesModsOrEmpty;
            for (int i = 0; i < rows.Count; i++)
                if (MonsterModRolls.Mods.Row(rows[i])?.Hash == _auraEsImplicitHash) { flags |= MonsterAuraFlags.EnergyShield35; break; }
        }
        return flags;
    }

    /// <summary>Registers a monster's proximity shield (Temporal Bubble, radius 32) on spawn so the hot-path check in
    /// <see cref="MonsterProximityShieldBlocksLocked"/> stays zero-cost when no Temporal rare exists in the area.</summary>
    public static void RegisterMonsterAuraCarrier(GameArea area, SpawnedMonster m)
    {
        if (!m.Hostile) return;
        var flags = DetectCarrierFlags(m);
        if (flags == MonsterAuraFlags.None) return;
        var state = MonsterAuraStates.GetValue(m, _ => new MonsterAuraState());
        state.Checked = true;
        state.CarrierFlags = flags;
        if ((flags & MonsterAuraFlags.Temporal) != 0 && !state.HasTemporalShield)
        {
            state.HasTemporalShield = true;
            AreaTemporalShieldCounts.GetValue(area, _ => new AreaTemporalCounter()).Count++;
        }
    }

    /// <summary>Zero-allocation hot-path check called from hit paths: blocks direct hits from attackers standing outside
    /// a Temporal Bubble monster's 32-cell proximity shield.</summary>
    public static bool MonsterProximityShieldBlocksLocked(Player player, SpawnedMonster m, (int X, int Y)? attackerAt)
    {
        if (!AreaTemporalShieldCounts.TryGetValue(player.Area, out var c) || c.Count == 0) return false;
        int radius = m.Effects[Stat.CannotBeDamagedByThingsOutsideRadius];
        if (radius <= 0) return false;
        double now = AreaTime(player);
        var from = attackerAt ?? player.Walk.Position(now);
        var to = m.Walk.Position(now);
        double dx = from.X - to.X, dy = from.Y - to.Y;
        return dx * dx + dy * dy > (double)radius * radius;
    }

    /// <summary>Steps allied monster auras and player-affecting monster aura debuffs/siphon once per recovery tick.</summary>
    public static void StepMonsterAurasLocked(GameArea area, AreaFrames frames, IReadOnlyList<(uint Id, SpawnedMonster Monster)> living, double seconds, double now)
    {
        // 1. Gather living aura carriers (fast pass; avoids allocations when no carriers exist).
        List<(uint Id, SpawnedMonster Monster, (int X, int Y) At, MonsterAuraFlags Flags)>? carriers = null;
        for (int i = 0; i < living.Count; i++)
        {
            var (id, m) = living[i];
            if (!m.Hostile || m.Dead || m.Life == 0 || m.EntranceGrace || m.OnNonLethalZero is not null) continue;
            MonsterAuraFlags flags;
            if (MonsterAuraStates.TryGetValue(m, out var st) && st.Checked)
            {
                flags = st.CarrierFlags;
            }
            else if (m.Rarity is MonsterRarity.Magic or MonsterRarity.Rare || m.Effects[Stat.CannotBeDamagedByThingsOutsideRadius] > 0
                || m.Effects[Stat.MaximumLifePctToAddAsMaximumEnergyShield] > 0)
            {
                flags = DetectCarrierFlags(m);
                var created = MonsterAuraStates.GetValue(m, _ => new MonsterAuraState());
                created.Checked = true;
                created.CarrierFlags = flags;
                if ((flags & MonsterAuraFlags.Temporal) != 0 && !created.HasTemporalShield)
                {
                    created.HasTemporalShield = true;
                    AreaTemporalShieldCounts.GetValue(area, _ => new AreaTemporalCounter()).Count++;
                }
            }
            else
            {
                flags = MonsterAuraFlags.None;
            }

            if (flags != MonsterAuraFlags.None)
                (carriers ??= []).Add((id, m, m.Walk.Position(now), flags));
        }

        // 2. Apply/clear allied aura flags for every living hostile monster.
        const MonsterAuraFlags alliedMask = MonsterAuraFlags.Speed | MonsterAuraFlags.Damage | MonsterAuraFlags.AccuracyCrit
            | MonsterAuraFlags.EnergyShield40 | MonsterAuraFlags.EnergyShield35 | MonsterAuraFlags.Recovery | MonsterAuraFlags.Consecrated;
        for (int i = 0; i < living.Count; i++)
        {
            var (id, m) = living[i];
            if (!m.Hostile || m.Dead || m.Life == 0 || m.EntranceGrace || m.OnNonLethalZero is not null) continue;
            var received = MonsterAuraFlags.None;
            if (carriers is not null)
            {
                var at = m.Walk.Position(now);
                for (int c = 0; c < carriers.Count; c++)
                {
                    var carrier = carriers[c];
                    var cf = carrier.Flags & alliedMask;
                    if (cf == MonsterAuraFlags.None) continue;
                    bool self = ReferenceEquals(carrier.Monster, m);
                    // Self already owns Speed/Damage/AccuracyCrit/ES stats from its own mod; Recovery and Consecrated
                    // are daemon mods with empty Mods.dat stats so they apply to self too.
                    if (self)
                    {
                        received |= cf & (MonsterAuraFlags.Recovery | MonsterAuraFlags.Consecrated);
                        continue;
                    }
                    double dx = at.X - carrier.At.X, dy = at.Y - carrier.At.Y;
                    double d2 = dx * dx + dy * dy;
                    if (d2 <= AlliedAuraRadius * AlliedAuraRadius)
                        received |= cf & (MonsterAuraFlags.Speed | MonsterAuraFlags.Damage | MonsterAuraFlags.AccuracyCrit
                            | MonsterAuraFlags.EnergyShield40 | MonsterAuraFlags.EnergyShield35);
                    if (d2 <= RecoveryAuraRadius * RecoveryAuraRadius)
                        received |= cf & MonsterAuraFlags.Recovery;
                    if (d2 <= ConsecratedAuraRadius * ConsecratedAuraRadius)
                        received |= cf & MonsterAuraFlags.Consecrated;
                }
            }

            bool hasState = MonsterAuraStates.TryGetValue(m, out var state);
            MonsterAuraFlags prev = hasState ? state!.AppliedAlliedFlags : MonsterAuraFlags.None;
            if (received == prev) continue;
            state ??= MonsterAuraStates.GetValue(m, _ => new MonsterAuraState());
            state.AppliedAlliedFlags = received;
            ApplyAlliedAuraStatsLocked(area, id, m, received);
        }

        // 3. Apply/release player-affecting auras (Hinder, Temporal Bubble, Mana Siphoner).
        var players = frames.Players();
        for (int p = 0; p < players.Length; p++)
        {
            var player = players[p];
            if (player.Closed || player.LeavingArea || player.PlayerLife == 0 || InGracePeriod(player))
            {
                QueueClearPlayerMonsterAuras(player);
                continue;
            }
            uint hinderCaster = 0, temporalCaster = 0;
            SpawnedMonster? siphonSource = null;
            if (carriers is not null)
            {
                var pat = player.Walk.Position(now);
                for (int c = 0; c < carriers.Count; c++)
                {
                    var carrier = carriers[c];
                    if ((carrier.Flags & (MonsterAuraFlags.Hinder | MonsterAuraFlags.Temporal | MonsterAuraFlags.ManaSiphon)) == 0) continue;
                    double dx = pat.X - carrier.At.X, dy = pat.Y - carrier.At.Y;
                    double d2 = dx * dx + dy * dy;
                    if (hinderCaster == 0 && (carrier.Flags & MonsterAuraFlags.Hinder) != 0 && d2 <= HinderAuraRadius * HinderAuraRadius)
                        hinderCaster = carrier.Id;
                    if (temporalCaster == 0 && (carrier.Flags & MonsterAuraFlags.Temporal) != 0 && d2 <= TemporalBubbleRadius * TemporalBubbleRadius)
                        temporalCaster = carrier.Id;
                    if (siphonSource is null && (carrier.Flags & MonsterAuraFlags.ManaSiphon) != 0
                        && d2 >= ManaSiphonInnerRadius * ManaSiphonInnerRadius && d2 <= ManaSiphonOuterRadius * ManaSiphonOuterRadius)
                        siphonSource = carrier.Monster;
                }
            }
            if (hinderCaster == 0 && temporalCaster == 0 && siphonSource is null && !PlayerMonsterAuraExposures.TryGetValue(player, out _))
                continue;
            var exp = PlayerMonsterAuraExposures.GetValue(player, _ => new PlayerAuraExposure());
            if (FlaskMovementImmune(player, HinderedBuffRow, AreaTime(player))) hinderCaster = 0;
            if (hinderCaster != 0 && !exp.Hindered)
            {
                exp.Hindered = true;
                uint caster = hinderCaster;
                _ = AreaFrames.Start(() => HoldPlayerDebuff(player, exp.HinderHolder, HinderedBuffRow, caster, HinderAuraPlayerStats));
            }
            else if (hinderCaster == 0 && exp.Hindered)
            {
                exp.Hindered = false;
                _ = AreaFrames.Start(() => ReleasePlayerDebuff(player, exp.HinderHolder));
            }
            if (temporalCaster != 0 && !exp.Temporal)
            {
                exp.Temporal = true;
                uint caster = temporalCaster;
                _ = AreaFrames.Start(() => HoldPlayerDebuff(player, exp.TemporalHolder, TemporalBubbleBuffRow, caster, TemporalAuraPlayerStats));
            }
            else if (temporalCaster == 0 && exp.Temporal)
            {
                exp.Temporal = false;
                _ = AreaFrames.Start(() => ReleasePlayerDebuff(player, exp.TemporalHolder));
            }
            if (siphonSource is not null && seconds > 0 && !player.Immune)
            {
                StepManaSiphonLocked(player, siphonSource, exp, seconds, now);
            }
            else if (exp.SiphonDps > 0 || exp.SiphonManaCarry > 0)
            {
                exp.SiphonDps = 0;
                exp.SiphonManaCarry = 0;
                player.Degens[exp] = 0;
            }
        }
    }

    static void ApplyAlliedAuraStatsLocked(GameArea area, uint id, SpawnedMonster m, MonsterAuraFlags flags)
    {
        if (flags == MonsterAuraFlags.None)
        {
            m.RemoveTemporaryStats(AlliedAuraStatsKey);
        }
        else
        {
            var stats = new Dictionary<uint, int>();
            if ((flags & MonsterAuraFlags.Speed) != 0)
            {
                stats[Stat.AttackSpeedPlusPct] = 40;
                stats[Stat.BaseCastSpeedPlusPct] = 40;
                stats[Stat.BaseMovementVelocityPlusPct] = 30;
            }
            if ((flags & MonsterAuraFlags.Damage) != 0)
                stats[Stat.PhysicalDamagePlusPct] = 40;
            if ((flags & MonsterAuraFlags.AccuracyCrit) != 0)
            {
                stats[Stat.AccuracyRatingPlusPct] = 100;
                stats[Stat.CriticalStrikeChancePlusPct] = 300;
            }
            int esPct = (flags & MonsterAuraFlags.EnergyShield40) != 0 ? 40
                : (flags & MonsterAuraFlags.EnergyShield35) != 0 ? 35 : 0;
            if (esPct > 0)
                stats[Stat.MaximumLifePctToAddAsMaximumEnergyShield] = esPct;
            int regenPerMin = 0;
            if ((flags & MonsterAuraFlags.Recovery) != 0) regenPerMin += 240;
            if ((flags & MonsterAuraFlags.Consecrated) != 0) regenPerMin += 300;
            if (regenPerMin > 0)
                stats[Stat.LifeRegenerationRatePerMinutePct] = regenPerMin;
            m.SetTemporaryStats(AlliedAuraStatsKey, stats);
        }
        // Always rebuild stats so allied Haste/Damage/Precision/Recovery auras take effect immediately even if ES is off.
        m.RebuildStats();
        // Recompute Monster ES if an ES aura entered or left.
        if (MonsterEsEnabled && m.Variety is { } variety)
        {
            int baseLife = Monsters.BaseLife(variety, m.Level) ?? (int)m.MaxLife;
            uint newMaxEs = ComputeMonsterMaxEnergyShield(variety, m.Level, baseLife, m.MaxLife, m.Effects);
            SyncMonsterMaxEnergyShieldLocked(area, id, m, newMaxEs);
        }
    }

    static void StepManaSiphonLocked(Player player, SpawnedMonster source, PlayerAuraExposure exp, double seconds, double now)
    {
        // Drain 10% of max mana per second inside the Mana Siphoner ring.
        double manaDrain = player.Pools.Mana * 0.10 * seconds + exp.SiphonManaCarry;
        uint manaLoss = (uint)Math.Truncate(manaDrain);
        exp.SiphonManaCarry = manaDrain - manaLoss;
        if (manaLoss > 0 && player.PlayerMana > 0)
        {
            player.PlayerMana = manaLoss >= player.PlayerMana ? 0 : player.PlayerMana - manaLoss;
            player.Area.Life.Changed(player);
        }
        // Lightning degeneration routed through Player.Degens so recovery, ES, Petrified Blood and PlayerDied all apply.
        double baseDps = (Monsters.BaseDamage(source.Level) ?? 10) * 1.5;
        double mitigated = baseDps * DamageModel.Mitigate(1.0, Resistance(player, DamageType.Lightning), MaxResistance(player, DamageType.Lightning))
            * PlayerAilmentDamageTakenFactor(player, now, 0);
        if (mitigated != exp.SiphonDps)
        {
            exp.SiphonDps = mitigated;
            player.Degens[exp] = mitigated;
        }
    }

    static void QueueClearPlayerMonsterAuras(Player player)
    {
        if (!PlayerMonsterAuraExposures.TryGetValue(player, out var exp)) return;
        if (exp.Hindered) { exp.Hindered = false; _ = AreaFrames.Start(() => ReleasePlayerDebuff(player, exp.HinderHolder)); }
        if (exp.Temporal) { exp.Temporal = false; _ = AreaFrames.Start(() => ReleasePlayerDebuff(player, exp.TemporalHolder)); }
        if (exp.SiphonDps > 0) { exp.SiphonDps = 0; player.Degens[exp] = 0; }
        exp.SiphonManaCarry = 0;
    }

    /// <summary>Awaited cleanup on <see cref="GameEvents.PlayerLeaving"/> so monster aura debuffs and Mana Siphoner
    /// degens never survive zone transitions or disconnects.</summary>
    public static async Task ClearPlayerMonsterAurasAsync(Player player)
    {
        if (!PlayerMonsterAuraExposures.TryGetValue(player, out var exp)) return;
        bool releaseHinder = exp.Hindered;
        bool releaseTemporal = exp.Temporal;
        exp.Hindered = false;
        exp.Temporal = false;
        if (exp.SiphonDps > 0) { exp.SiphonDps = 0; player.Degens[exp] = 0; }
        exp.SiphonManaCarry = 0;
        if (releaseHinder) await ReleasePlayerDebuff(player, exp.HinderHolder);
        if (releaseTemporal) await ReleasePlayerDebuff(player, exp.TemporalHolder);
    }

    /// <summary>Enables or clears an encounter-owned proximity shield (e.g. Dominus Ascendant Blood Rain safe bubble)
    /// without applying the Temporal Bubble action-speed slow to players inside the bubble.</summary>
    public static void SetEncounterProximityShield(GameArea area, SpawnedMonster m, int radius)
    {
        var state = MonsterAuraStates.GetValue(m, _ => new MonsterAuraState());
        if (!state.Checked)
        {
            state.CarrierFlags = DetectCarrierFlags(m);
            state.Checked = true;
            if ((state.CarrierFlags & MonsterAuraFlags.Temporal) != 0 && !state.HasTemporalShield)
            {
                state.HasTemporalShield = true;
                AreaTemporalShieldCounts.GetValue(area, _ => new AreaTemporalCounter()).Count++;
            }
        }
        if (radius > 0)
        {
            m.SetTemporaryStats(EncounterProximityShieldKey, new Dictionary<uint, int> { [Stat.CannotBeDamagedByThingsOutsideRadius] = radius });
            m.RebuildStats();
            if (!state.HasEncounterShield)
            {
                state.HasEncounterShield = true;
                AreaTemporalShieldCounts.GetValue(area, _ => new AreaTemporalCounter()).Count++;
            }
        }
        else
        {
            m.RemoveTemporaryStats(EncounterProximityShieldKey);
            m.RebuildStats();
            if (state.HasEncounterShield)
            {
                state.HasEncounterShield = false;
                if (AreaTemporalShieldCounts.TryGetValue(area, out var c) && c.Count > 0) c.Count--;
            }
        }
    }

    /// <summary>Cleans up temporal and encounter proximity shield counters for <paramref name="area"/> when a monster dies.</summary>
    public static void ClearMonsterAuraCarrier(GameArea area, SpawnedMonster m)
    {
        if (!MonsterAuraStates.TryGetValue(m, out var state)) return;
        if (state.HasTemporalShield)
        {
            state.HasTemporalShield = false;
            if (AreaTemporalShieldCounts.TryGetValue(area, out var c) && c.Count > 0) c.Count--;
        }
        if (state.HasEncounterShield)
        {
            state.HasEncounterShield = false;
            if (AreaTemporalShieldCounts.TryGetValue(area, out var c) && c.Count > 0) c.Count--;
        }
    }
}
