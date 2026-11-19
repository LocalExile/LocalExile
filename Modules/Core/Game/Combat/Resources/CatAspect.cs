public static partial class Game
{
    public const string CatAspectFamily = "CatAspect";
    public const ushort CatAspectRootRow = 1036, CatsStealthRow = 1037, CatsAgilityRow = 1038;
    const uint CatBuffEffect = Stat.AspectOfTheCatBuffEffectPlusPct;
    const uint CatStealthDuration = Stat.CatsStealthDurationMsPlus;
    const uint CatAgilityDuration = Stat.AspectOfTheCatBaseSecondaryDuration;
    const uint CatStealthCharges = Stat.GainMaximumFrenzyAndPowerChargesWhenYouGainCatsStealth;
    const uint CatAgilityCharges = Stat.GainMaximumFrenzyAndEnduranceChargesWhenYouGainCatsAgility;
    const uint CatOnslaught = Stat.GainOnslaughtWhileYouHaveCatsAgility;
    const uint CatStealthPhasing = Stat.GainPhasingWhileYouHaveCatsStealth;
    const uint CatCrit = Stat.CriticalStrikeChancePlusPct;
    const uint CatAvoid = Stat.AvoidDamagePct;
    const uint CatStealth = Stat.StealthPlusPct;
    const uint CatAttackSpeed = Stat.AttackSpeedPlusPct;
    const uint CatCastSpeed = Stat.BaseCastSpeedPlusPct;

    public readonly record struct CatAspectPhaseStats(int CriticalStrikeChancePct, int AvoidDamagePct,
        int StealthPct, int AttackSpeedPct, int CastSpeedPct)
    {
        public int Value(uint stat) => stat switch
        {
            CatCrit => CriticalStrikeChancePct,
            CatAvoid => AvoidDamagePct,
            CatStealth => StealthPct,
            CatAttackSpeed => AttackSpeedPct,
            CatCastSpeed => CastSpeedPct,
            _ => 0,
        };
    }

    static AuraCatalog.Aura? CatAspectRootLocked(Player conn) =>
        conn.Auras.Values.FirstOrDefault(a => AuraCatalog.Family(a.EffectId) == CatAspectFamily);

    public static bool CatAspectRootActiveLocked(Player conn)
    {
        var state = conn.Reservations;
        return CatAspectRootLocked(conn) is { } root && state.CatOwner is { } owner
            && owner.Skill == root.SkillId && owner.RootBuff == root.Buff
            && state.Cat is not ReservationState.CatPhase.None;
    }

    public static ushort CatAspectPhaseRow(ReservationState.CatPhase phase) => phase switch
    {
        ReservationState.CatPhase.Stealth => CatsStealthRow,
        ReservationState.CatPhase.Agility => CatsAgilityRow,
        _ => 0,
    };

    static void ClearCatRuntimeLocked(Player conn)
    {
        var state = conn.Reservations;
        if (state.CatOwner is null && state.Cat == ReservationState.CatPhase.None && state.CatPhaseBuff == 0
            && !state.CatStealthGrantQualified && !state.CatAgilityGrantQualified
            && !state.CatOnslaughtQualified && !state.CatPhasing) return;
        state.CatOwner = null;
        state.Cat = ReservationState.CatPhase.None;
        state.CatPhaseStartedAt = state.CatPhaseDeadline = 0;
        state.CatLastStealthStartedAt = state.CatObservedStealthEntryAt = double.NegativeInfinity;
        state.CatLastAgilityStartedAt = state.CatObservedAgilityEntryAt = double.NegativeInfinity;
        state.CatStealthGrantQualifiedSince = state.CatAgilityGrantQualifiedSince = double.PositiveInfinity;
        state.CatStealthGrantQualified = state.CatAgilityGrantQualified = state.CatOnslaughtQualified = false;
        state.CatPhasing = false;
        state.CatPhaseBuff = 0;
        state.CatOnslaughtBuff = 0;
        state.CatRevision++; MarkPlayerStats(conn, PlayerStatStep.CatAspect);
        conn.AuraStatsDirty = true;
    }

    public static ReservationState.PausedCatCycle? CaptureCatCycle(Player conn, double now)
    {
        var state = conn.Reservations;
        if (state.CatTransferLoading || conn.ResourceEffectsState is ResourceEffectsState { Loading: true }) now = 0;
        if (state.Cat == ReservationState.CatPhase.None) return state.PausedCat;
        var phase = SettleCatPhase(state.Cat, state.CatPhaseStartedAt, state.CatPhaseDeadline,
            now, state.CatStealthSeconds, state.CatAgilitySeconds);
        return new(phase.Phase, Math.Max(0, now - phase.Started), state.CatStealthSeconds, state.CatAgilitySeconds);
    }

    public static void RestoreCatCycle(Player conn, ReservationState.PausedCatCycle? cycle)
    {
        ClearCatRuntimeLocked(conn);
        conn.Reservations.PausedCat = cycle;
    }

    public static void EndCatAspectRuntimeLocked(Player conn, AuraCatalog.Aura aura)
    {
        if (conn.Reservations.CatOwner is not { } owner
            || owner.Skill != aura.SkillId || owner.RootBuff != aura.Buff) return;
        if (conn.Reservations.Cat == ReservationState.CatPhase.Stealth)
            QueueCatStealthLossLocked(conn, AreaTime(conn), AreaTime(conn));
        var paused = CaptureCatCycle(conn, AreaTime(conn));
        ClearCatRuntimeLocked(conn);
        conn.Reservations.PausedCat = paused;
    }

    static (double Stealth, double Agility) CatAspectDurationsLocked(Player conn, AuraCatalog.Aura root,
        IReadOnlyDictionary<uint, int> rawSources)
    {
        IReadOnlyList<(uint Key, int Value)> rows = root.SkillStats
            ?? SkillsFor(conn).EffectStats(root.Effect, root.Level, root.Quality);
        int Skill(uint key) => rows.FirstOrDefault(row => row.Key == key).Value;
        int Raw(uint key) => rawSources.GetValueOrDefault(key);
        int durationPct = Skill(Stat.SkillEffectDurationPlusPct) + Raw(Stat.SkillEffectDurationPlusPct);
        double Scale(int ms) => Math.Max(0, ms) / 1000.0
            * Math.Max(0, 1 + durationPct / 100.0)
            * Math.Max(0, 1 + (Skill(Stat.Of("support_increased_duration_skill_effect_duration_+%_final"))
                + Raw(Stat.Of("support_increased_duration_skill_effect_duration_+%_final"))) / 100.0)
            * Math.Max(0, 1 + (Skill(Stat.SupportReducedDurationSkillEffectDurationPlusPctFinal)
                + Raw(Stat.SupportReducedDurationSkillEffectDurationPlusPctFinal)) / 100.0);
        int primary = Skill(Stat.BaseSkillEffectDuration);
        int secondary = Skill(Stat.BaseSecondarySkillEffectDuration);
        // Each Fur modifier extends its own base phase before duration scaling. Replica Fur's modifier is additive
        // seconds on the exported Agility base, not a replacement duration.
        primary = Math.Max(0, (primary > 0 ? primary : 4000) + Raw(CatStealthDuration));
        secondary = Math.Max(0, (secondary > 0 ? secondary : 6000) + Raw(CatAgilityDuration));
        return (Scale(primary), Scale(secondary));
    }

    static double CatPhaseDuration(ReservationState.CatPhase phase, double stealth, double agility) => phase switch
    {
        ReservationState.CatPhase.Stealth => stealth,
        ReservationState.CatPhase.Agility => agility,
        _ => 0,
    };

    static (ReservationState.CatPhase Phase, double Started, double Deadline) SettleCatPhase(
        ReservationState.CatPhase phase, double started, double deadline, double now, double stealth, double agility)
    {
        if (deadline > now) return (phase, started, deadline);
        double transition = deadline;
        var next = phase == ReservationState.CatPhase.Stealth
            ? ReservationState.CatPhase.Agility : ReservationState.CatPhase.Stealth;
        double first = CatPhaseDuration(next, stealth, agility);
        if (transition + first > now) return (next, transition, transition + first);
        double pair = stealth + agility;
        if (pair <= 0) return (next, transition, transition + first);
        // Full alternating pairs are arithmetic, so a long pause cannot require an unbounded loop.
        long completedPairs = (long)Math.Floor((now - transition + 1e-9) / pair);
        transition += completedPairs * pair;
        double elapsed = now - transition;
        if (elapsed < first) return (next, transition, transition + first);
        var other = next == ReservationState.CatPhase.Stealth
            ? ReservationState.CatPhase.Agility : ReservationState.CatPhase.Stealth;
        double second = CatPhaseDuration(other, stealth, agility);
        return (other, transition + first, transition + first + second);
    }

    public static bool CatAspectReconcileLocked(Player conn, double now,
        IReadOnlyDictionary<uint, int>? rawSources = null)
    {
        var state = conn.Reservations;
        if (state.CatOwner is null && state.PausedCat is null) PrimeCatTransfer(conn);
        if (state.CatTransferLoading || conn.ResourceEffectsState is ResourceEffectsState { Loading: true }) now = 0;
        var root = CatAspectRootLocked(conn);
        if (root is null || conn.PlayerLife == 0)
        {
            if (conn.PlayerLife == 0) ReconcileCatCrySourcesLocked(conn, now);
            bool wasActive = state.CatOwner is not null || state.Cat != ReservationState.CatPhase.None
                || state.CatPhaseBuff != 0 || state.CatPhasing
                || state.CatStealthGrantQualified || state.CatAgilityGrantQualified
                || state.CatOnslaughtQualified;
            var paused = conn.PlayerLife > 0 ? CaptureCatCycle(conn, now) : null;
            ClearCatRuntimeLocked(conn);
            state.PausedCat = paused;
            return wasActive;
        }
        rawSources ??= CharacterStatSources(conn).Stats;
        ReconcileCatCrySourcesLocked(conn, now);
        var qualifiedSources = conn.StatSourcesCache;
        var owner = new ReservationState.CatAspectOwner(root.SkillId, root.Buff);
        double previousStartedAt = state.CatPhaseStartedAt;
        bool changed = false;
        bool ownerStarted = false;
        if (state.CatOwner != owner || state.Cat == ReservationState.CatPhase.None)
        {
            state.PausedCat ??= state.Cat == ReservationState.CatPhase.None ? null : CaptureCatCycle(conn, now);
            ownerStarted = true;
            state.CatOwner = owner;
            state.Cat = ReservationState.CatPhase.Stealth;
            state.CatPhaseStartedAt = now;
            state.CatPhaseDeadline = now + 4;
            state.CatLastStealthStartedAt = now;
            state.CatObservedStealthEntryAt = double.NegativeInfinity;
            state.CatLastAgilityStartedAt = state.CatObservedAgilityEntryAt = double.NegativeInfinity;
            state.CatStealthGrantQualifiedSince = state.CatAgilityGrantQualifiedSince = double.PositiveInfinity;
            state.CatStealthGrantQualified = state.CatAgilityGrantQualified = false;
            state.CatOnslaughtQualified = state.CatPhasing = false;
            state.CatPhaseBuff = 0;
            state.CatOnslaughtBuff = 0;
            if (state.PausedCat is { } paused)
            {
                state.Cat = paused.Phase;
                state.CatPhaseStartedAt = now - paused.Elapsed;
                state.CatLastStealthStartedAt = state.CatObservedStealthEntryAt =
                    paused.Phase == ReservationState.CatPhase.Stealth ? state.CatPhaseStartedAt : double.NegativeInfinity;
                state.CatLastAgilityStartedAt = state.CatObservedAgilityEntryAt =
                    paused.Phase == ReservationState.CatPhase.Agility ? state.CatPhaseStartedAt : double.NegativeInfinity;
                state.PausedCat = null;
            }
            changed = true;
        }
        var (stealth, agility) = CatAspectDurationsLocked(conn, root, rawSources);
        state.CatStealthSeconds = stealth;
        state.CatAgilitySeconds = agility;
        double currentDuration = CatPhaseDuration(state.Cat, stealth, agility);
        double expectedDeadline = state.CatPhaseStartedAt + currentDuration;
        if (Math.Abs(expectedDeadline - state.CatPhaseDeadline) > 1e-9)
        {
            state.CatPhaseDeadline = expectedDeadline;
            changed = true;
        }
        var settled = SettleCatPhase(state.Cat, state.CatPhaseStartedAt, state.CatPhaseDeadline,
            now, stealth, agility);
        if (settled.Phase != state.Cat || Math.Abs(settled.Deadline - state.CatPhaseDeadline) > 1e-9)
        {
            state.Cat = settled.Phase;
            state.CatPhaseStartedAt = settled.Started;
            state.CatPhaseDeadline = settled.Deadline;
            changed = true;
        }
        if (!ownerStarted && settled.Started > previousStartedAt + 1e-9)
        {
            double lastStealthStarted = settled.Phase == ReservationState.CatPhase.Stealth
                ? settled.Started : settled.Started - stealth;
            if (Math.Abs(lastStealthStarted - state.CatLastStealthStartedAt) > 1e-9)
            {
                state.CatLastStealthStartedAt = lastStealthStarted;
                changed = true;
            }
            double lastAgilityStarted = settled.Phase == ReservationState.CatPhase.Agility
                ? settled.Started : settled.Started - agility;
            if (double.IsFinite(lastAgilityStarted)
                && Math.Abs(lastAgilityStarted - state.CatLastAgilityStartedAt) > 1e-9)
            {
                state.CatLastAgilityStartedAt = lastAgilityStarted;
                changed = true;
            }
        }

        bool stealthGrantQualified = CatQualifiedEquipmentValue(conn, qualifiedSources, CatStealthCharges) > 0;
        bool agilityGrantQualified = CatQualifiedEquipmentValue(conn, qualifiedSources, CatAgilityCharges) > 0;
        bool onslaughtQualified = CatQualifiedEquipmentValue(conn, qualifiedSources, CatOnslaught) > 0;
        bool phasingQualified = CatQualifiedEquipmentValue(conn, qualifiedSources, CatStealthPhasing) > 0;
        bool wasStealthGrantQualified = state.CatStealthGrantQualified;
        bool wasAgilityGrantQualified = state.CatAgilityGrantQualified;
        double previousStealthQualifiedSince = state.CatStealthGrantQualifiedSince;
        double previousAgilityQualifiedSince = state.CatAgilityGrantQualifiedSince;
        state.CatStealthGrantQualified = stealthGrantQualified;
        state.CatStealthGrantQualifiedSince = stealthGrantQualified
            ? wasStealthGrantQualified ? previousStealthQualifiedSince : now
            : double.PositiveInfinity;
        state.CatAgilityGrantQualified = agilityGrantQualified;
        state.CatAgilityGrantQualifiedSince = agilityGrantQualified
            ? wasAgilityGrantQualified ? previousAgilityQualifiedSince : now
            : double.PositiveInfinity;
        if (state.CatOnslaughtQualified != onslaughtQualified)
        {
            state.CatOnslaughtQualified = onslaughtQualified;
            changed = true;
        }
        bool newStealthEntry = state.CatLastStealthStartedAt > state.CatObservedStealthEntryAt + 1e-9;
        if (newStealthEntry)
        {
            double entryAt = state.CatLastStealthStartedAt;
            bool qualifiedAtEntry = wasStealthGrantQualified && previousStealthQualifiedSince <= entryAt + 1e-9
                || state.CatStealthGrantQualified && state.CatStealthGrantQualifiedSince <= entryAt + 1e-9;
            state.CatObservedStealthEntryAt = entryAt;
            if (stealthGrantQualified && qualifiedAtEntry)
                GrantCatChargesLocked(conn, entryAt, now, rawSources, [ChargeType.Frenzy, ChargeType.Power],
                    "Farrul's Fur: gained Cat's Stealth", "Cat's Stealth");
            changed = true;
        }
        bool newAgilityEntry = state.CatLastAgilityStartedAt > state.CatObservedAgilityEntryAt + 1e-9;
        if (newAgilityEntry)
        {
            double entryAt = state.CatLastAgilityStartedAt;
            bool qualifiedAtEntry = wasAgilityGrantQualified && previousAgilityQualifiedSince <= entryAt + 1e-9
                || state.CatAgilityGrantQualified && state.CatAgilityGrantQualifiedSince <= entryAt + 1e-9;
            state.CatObservedAgilityEntryAt = entryAt;
            if (!ownerStarted) QueueCatStealthLossLocked(conn, entryAt, now);
            if (agilityGrantQualified && qualifiedAtEntry)
                GrantCatChargesLocked(conn, entryAt, now, rawSources, [ChargeType.Frenzy, ChargeType.Endurance],
                    "Replica Farrul's Fur: gained Cat's Agility", "Cat's Agility");
            changed = true;
        }
        bool phasing = phasingQualified && state.Cat == ReservationState.CatPhase.Stealth;
        if (state.CatPhasing != phasing)
        {
            state.CatPhasing = phasing;
            PresentStats(conn);
            changed = true;
        }
        bool onslaught = state.Cat == ReservationState.CatPhase.Agility && state.CatOnslaughtQualified;
        if (onslaught && state.CatOnslaughtBuff == 0)
        {
            state.CatOnslaughtBuff = conn.NextBuff++;
            changed = true;
        }
        else if (!onslaught && state.CatOnslaughtBuff != 0)
        {
            state.CatOnslaughtBuff = 0;
            changed = true;
        }
        if (changed)
        {
            state.CatPhaseBuff = conn.NextBuff++;
            state.CatRevision++; MarkPlayerStats(conn, PlayerStatStep.CatAspect);
            conn.AuraStatsDirty = true;
        }
        return changed;
    }

    static int CatQualifiedEquipmentValue(Player conn, StatSourcesResult? sources, uint stat)
    {
        if (!(stat is var key)) return 0;
                sources ??= conn.StatSourcesCache;
        if (sources is null) return 0;
        var active = sources.Worn.Where(w => !sources.Inactive.Contains(w.Id)).Select(w => w.Item);
        return ItemModStats.Sum(active, ItemsFor(conn).Mods).GetValueOrDefault(stat);
    }

    static void GrantCatChargesLocked(Player conn, double entryAt, double now,
        IReadOnlyDictionary<uint, int> rawSources, IReadOnlyList<ChargeType> types, string source, string entryName)
    {
        var session = ChargesOf(conn);
        int Get(uint key) => rawSources.GetValueOrDefault(key);
        session.Observe(Get);
        var grants = types.Select(type =>
            new ChargeEvent.Grant(type, session.Limits(type).Maximum, source)).ToArray();
        var existing = session.State.Carry(now).ToDictionary(c => c.Type);
        var admitted = session.Admit(entryAt, grants, Get, () => WeaponProfileFor(conn), true, AreaRng(conn));
        var aged = session.Refresh(now);
        var merged = session.State.Carry(now).ToDictionary(c => c.Type);
        foreach (var (type, carried) in existing)
        {
            if (merged.TryGetValue(type, out var live))
                merged[type] = live with
                {
                    Held = Math.Max(live.Held, carried.Held),
                    Remaining = Math.Max(live.Remaining, carried.Remaining),
                };
            else merged[type] = carried;
        }
        session.State.Restore(merged.Values, now);
        var restored = session.Refresh(now);
        foreach (string line in admitted.Log.Concat(aged.Log).Concat(restored.Log))
            Console.WriteLine($"[gateway] {conn.Remote} {line} ({entryName} entry {entryAt:0.###})");
    }

    public static bool CatOnslaughtActiveLocked(Player conn) =>
        conn.PlayerLife > 0 && CatAspectRootActiveLocked(conn)
        && conn.Reservations.Cat == ReservationState.CatPhase.Agility && conn.Reservations.CatOnslaughtQualified;

    public static IEnumerable<UtilityFlasks.Source> CatOnslaughtSources(Player conn, Func<uint, int> character) =>
        CatOnslaughtActiveLocked(conn) && ItemsFor(conn).Onslaught is { } onslaught
            ? UtilityFlasks.OnslaughtSources(onslaught,
                Math.Max(0, 1 + character(Stat.OnslaughtEffectPlusPct) / 100.0)) : [];

    public static CatAspectPhaseStats CatAspectPhaseStatsLocked(Player conn, int auraEffectPct,
        Func<uint, int> raw)
    {
        if (!CatAspectRootActiveLocked(conn) || CatAspectRootLocked(conn) is not { } root)
            return default;
        IReadOnlyList<(uint Key, int Value)> rows = root.SkillStats
            ?? SkillsFor(conn).EffectStats(root.Effect, root.Level, root.Quality);
        int Source(uint key) => rows.FirstOrDefault(row => row.Key == key).Value;
        // Aspect of the Cat is a buff skill with the HasReservation type, not an ActiveSkillType Aura. Cat's own
        // increased buff effect always scales it; ordinary aura effect and support aura effect only do when the actual
        // skill catalog says the skill is an aura.
        int effectPct = raw(CatBuffEffect);
        if (SkillsFor(conn).IsAura(root.Effect))
            effectPct += auraEffectPct + root.SupportEffectPct;
        int Scaled(uint source) => (int)Math.Round(Source(source) * Math.Max(0, 100 + effectPct) / 100.0,
            MidpointRounding.AwayFromZero);
        return conn.Reservations.Cat switch
        {
            ReservationState.CatPhase.Stealth => new(Scaled(Stat.SkillBuffGrantsCriticalStrikeChancePlusPct),
                Scaled(CatAvoid), Scaled(CatStealth), 0, 0),
            ReservationState.CatPhase.Agility => new(0, 0, 0,
                Scaled(Stat.AttackSpeedPlusPctGrantedFromSkill), Scaled(Stat.CastSpeedPlusPctGrantedFromSkill)),
            _ => default,
        };
    }

    public static IReadOnlyList<uint> CatAspectPhaseValues(ReservationState.CatPhase phase,
        CatAspectPhaseStats phaseStats) => phase switch
    {
        ReservationState.CatPhase.Stealth =>
            [unchecked((uint)phaseStats.CriticalStrikeChancePct), unchecked((uint)phaseStats.AvoidDamagePct),
                unchecked((uint)phaseStats.StealthPct)],
        ReservationState.CatPhase.Agility =>
            [unchecked((uint)phaseStats.AttackSpeedPct), unchecked((uint)phaseStats.CastSpeedPct)],
        _ => [],
    };

    public static IReadOnlyList<(uint Key, int Value)> CatAspectRawStatsLocked(Player conn, int auraEffectPct,
        Func<uint, int> raw)
    {
        var phase = CatAspectPhaseStatsLocked(conn, auraEffectPct, raw);
        bool active = CatAspectRootActiveLocked(conn);
        var result = new List<(uint, int)>(8);
        foreach (uint stat in new[] { CatCrit, CatAvoid, CatStealth, CatAttackSpeed, CatCastSpeed })
            if (stat is var key && phase.Value(stat) is var value && value != 0)
                result.Add((key, value));
        foreach (uint flag in new[] { Stat.HasCatAspect, Stat.HasCatsStealth, Stat.HasCatsAgility })
            if (flag is var key)
                result.Add((key, active && flag switch
                {
                    Stat.HasCatsStealth => conn.Reservations.Cat == ReservationState.CatPhase.Stealth,
                    Stat.HasCatsAgility => conn.Reservations.Cat == ReservationState.CatPhase.Agility,
                    _ => true,
                } ? 1 : 0));
        if (conn.Reservations.CatPhasing)
            foreach (uint stat in new[] { Stat.PhaseThroughObjects, Stat.VirtualPhaseThroughObjects })
                if (stat is var key) result.Add((key, 1));
        return result;
    }
}
