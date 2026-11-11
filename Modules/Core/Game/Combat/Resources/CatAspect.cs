public static partial class Game
{
    public const string CatAspectFamily = "CatAspect";
    public const ushort CatAspectRootRow = 1036, CatsStealthRow = 1037, CatsAgilityRow = 1038;
    const string CatBuffEffect = "aspect_of_the_cat_buff_effect_+%";
    const string CatStealthDuration = "cats_stealth_duration_ms_+";
    const string CatAgilityDuration = "aspect_of_the_cat_base_secondary_duration";
    const string CatStealthCharges = "gain_maximum_frenzy_and_power_charges_when_you_gain_cats_stealth";
    const string CatAgilityCharges = "gain_maximum_frenzy_and_endurance_charges_when_you_gain_cats_agility";
    const string CatOnslaught = "gain_onslaught_while_you_have_cats_agility";
    const string CatStealthPhasing = "gain_phasing_while_you_have_cats_stealth";
    const string CatCrit = "critical_strike_chance_+%";
    const string CatAvoid = "avoid_damage_%";
    const string CatStealth = "stealth_+%";
    const string CatAttackSpeed = "attack_speed_+%";
    const string CatCastSpeed = "base_cast_speed_+%";

    public readonly record struct CatAspectPhaseStats(int CriticalStrikeChancePct, int AvoidDamagePct,
        int StealthPct, int AttackSpeedPct, int CastSpeedPct)
    {
        public int Value(string stat) => stat switch
        {
            CatCrit => CriticalStrikeChancePct,
            CatAvoid => AvoidDamagePct,
            CatStealth => StealthPct,
            CatAttackSpeed => AttackSpeedPct,
            CatCastSpeed => CastSpeedPct,
            _ => 0,
        };
    }

    static AuraCatalog.Aura? CatAspectRootLocked(ClientState conn) =>
        conn.Auras.Values.FirstOrDefault(a => AuraCatalog.Family(a.EffectId) == CatAspectFamily);

    public static bool CatAspectRootActiveLocked(ClientState conn)
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

    static void ClearCatRuntimeLocked(ClientState conn)
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
        state.CatRevision++;
        conn.AuraStatsDirty = true;
    }

    public static void EndCatAspectRuntimeLocked(ClientState conn, AuraCatalog.Aura aura)
    {
        if (conn.Reservations.CatOwner is not { } owner
            || owner.Skill != aura.SkillId || owner.RootBuff != aura.Buff) return;
        ClearCatRuntimeLocked(conn);
    }

    static (double Stealth, double Agility) CatAspectDurationsLocked(ClientState conn, AuraCatalog.Aura root,
        IReadOnlyDictionary<uint, int> rawSources)
    {
        IReadOnlyList<(uint Key, int Value)> rows = root.SkillStats
            ?? SkillsFor(conn).EffectStats(root.Effect, root.Level, root.Quality);
        int Skill(string name) => Stats.Key(name) is uint key
            ? rows.FirstOrDefault(row => row.Key == key).Value : 0;
        int Raw(string name) => Stats.Key(name) is uint key
            ? conn.PlayerStats.TryGetValue(key, out int replacement) ? replacement
                : rawSources.GetValueOrDefault(key)
            : 0;
        int durationPct = Skill("skill_effect_duration_+%") + Raw("skill_effect_duration_+%");
        double Scale(int ms) => Math.Max(0, ms) / 1000.0
            * Math.Max(0, 1 + durationPct / 100.0)
            * Math.Max(0, 1 + (Skill("support_increased_duration_skill_effect_duration_+%_final")
                + Raw("support_increased_duration_skill_effect_duration_+%_final")) / 100.0)
            * Math.Max(0, 1 + (Skill("support_reduced_duration_skill_effect_duration_+%_final")
                + Raw("support_reduced_duration_skill_effect_duration_+%_final")) / 100.0);
        int primary = Skill("base_skill_effect_duration");
        int secondary = Skill("base_secondary_skill_effect_duration");
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

    public static bool CatAspectReconcileLocked(ClientState conn, double now,
        IReadOnlyDictionary<uint, int>? rawSources = null)
    {
        var state = conn.Reservations;
        var root = CatAspectRootLocked(conn);
        if (root is null || conn.PlayerLife == 0)
        {
            bool wasActive = state.CatOwner is not null || state.Cat != ReservationState.CatPhase.None
                || state.CatPhaseBuff != 0 || state.CatPhasing
                || state.CatStealthGrantQualified || state.CatAgilityGrantQualified
                || state.CatOnslaughtQualified;
            ClearCatRuntimeLocked(conn);
            return wasActive;
        }
        rawSources ??= CharacterStatSources(conn).Stats;
        var qualifiedSources = conn.StatSourcesCache;
        var owner = new ReservationState.CatAspectOwner(root.SkillId, root.Buff);
        double previousStartedAt = state.CatPhaseStartedAt;
        bool changed = false;
        bool ownerStarted = false;
        if (state.CatOwner != owner || state.Cat == ReservationState.CatPhase.None)
        {
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
            changed = true;
        }
        var (stealth, agility) = CatAspectDurationsLocked(conn, root, rawSources);
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
            if (agilityGrantQualified && qualifiedAtEntry)
                GrantCatChargesLocked(conn, entryAt, now, rawSources, [ChargeType.Frenzy, ChargeType.Endurance],
                    "Replica Farrul's Fur: gained Cat's Agility", "Cat's Agility");
            changed = true;
        }
        bool phasing = phasingQualified && state.Cat == ReservationState.CatPhase.Stealth;
        if (state.CatPhasing != phasing)
        {
            state.CatPhasing = phasing;
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
            state.CatRevision++;
            conn.AuraStatsDirty = true;
        }
        return changed;
    }

    static int CatQualifiedEquipmentValue(ClientState conn, StatSourcesResult? sources, string stat)
    {
        if (Stats.Key(stat) is not uint key) return 0;
        if (conn.PlayerStats.TryGetValue(key, out int replacement)) return replacement;
        sources ??= conn.StatSourcesCache;
        if (sources is null) return 0;
        var active = sources.Worn.Where(w => !sources.Inactive.Contains(w.Id)).Select(w => w.Item);
        return ItemModStats.Sum(active, ItemsFor(conn).Mods).GetValueOrDefault(stat);
    }

    static void GrantCatChargesLocked(ClientState conn, double entryAt, double now,
        IReadOnlyDictionary<uint, int> rawSources, IReadOnlyList<ChargeType> types, string source, string entryName)
    {
        var session = ChargesOf(conn);
        int Get(string name) => Stats.Key(name) is uint key
            ? conn.PlayerStats.TryGetValue(key, out int replacement) ? replacement : rawSources.GetValueOrDefault(key)
            : 0;
        session.Observe(Get);
        var grants = types.Select(type =>
            new ChargeEvent.Grant(type, session.Limits(type).Maximum, source)).ToArray();
        var existing = session.State.Carry(now).ToDictionary(c => c.Type);
        var admitted = session.Admit(entryAt, grants, Get, () => WeaponProfileFor(conn), true, Random.Shared);
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
            Console.WriteLine($"[gateway] {conn.Wire.Remote} {line} ({entryName} entry {entryAt:0.###})");
    }

    public static bool CatOnslaughtActiveLocked(ClientState conn) =>
        conn.PlayerLife > 0 && CatAspectRootActiveLocked(conn)
        && conn.Reservations.Cat == ReservationState.CatPhase.Agility && conn.Reservations.CatOnslaughtQualified;

    public static IEnumerable<UtilityFlasks.Source> CatOnslaughtSources(ClientState conn, Func<string, int> character) =>
        CatOnslaughtActiveLocked(conn) && ItemsFor(conn).Onslaught is { } onslaught
            ? UtilityFlasks.OnslaughtSources(onslaught,
                Math.Max(0, 1 + character("onslaught_effect_+%") / 100.0)) : [];

    public static CatAspectPhaseStats CatAspectPhaseStatsLocked(ClientState conn, int auraEffectPct,
        Func<string, int> raw)
    {
        if (!CatAspectRootActiveLocked(conn) || CatAspectRootLocked(conn) is not { } root)
            return default;
        IReadOnlyList<(uint Key, int Value)> rows = root.SkillStats
            ?? SkillsFor(conn).EffectStats(root.Effect, root.Level, root.Quality);
        int Source(string name) => Stats.Key(name) is uint key
            ? rows.FirstOrDefault(row => row.Key == key).Value : 0;
        // Aspect of the Cat is a buff skill with the HasReservation type, not an ActiveSkillType Aura. Cat's own
        // increased buff effect always scales it; ordinary aura effect and support aura effect only do when the actual
        // skill catalog says the skill is an aura.
        int effectPct = raw(CatBuffEffect);
        if (SkillsFor(conn).IsAura(root.Effect))
            effectPct += auraEffectPct + root.SupportEffectPct;
        int Scaled(string source) => (int)Math.Round(Source(source) * Math.Max(0, 100 + effectPct) / 100.0,
            MidpointRounding.AwayFromZero);
        return conn.Reservations.Cat switch
        {
            ReservationState.CatPhase.Stealth => new(Scaled("skill_buff_grants_critical_strike_chance_+%"),
                Scaled(CatAvoid), Scaled(CatStealth), 0, 0),
            ReservationState.CatPhase.Agility => new(0, 0, 0,
                Scaled("attack_speed_+%_granted_from_skill"), Scaled("cast_speed_+%_granted_from_skill")),
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

    public static IReadOnlyList<(uint Key, int Value)> CatAspectRawStatsLocked(ClientState conn, int auraEffectPct,
        Func<string, int> raw)
    {
        var phase = CatAspectPhaseStatsLocked(conn, auraEffectPct, raw);
        bool active = CatAspectRootActiveLocked(conn);
        var result = new List<(uint, int)>(8);
        foreach (string stat in new[] { CatCrit, CatAvoid, CatStealth, CatAttackSpeed, CatCastSpeed })
            if (Stats.Key(stat) is uint key && phase.Value(stat) is var value && value != 0)
                result.Add((key, value));
        foreach (string flag in new[] { "has_cat_aspect", "has_cats_stealth", "has_cats_agility" })
            if (Stats.Key(flag) is uint key)
                result.Add((key, active && flag switch
                {
                    "has_cats_stealth" => conn.Reservations.Cat == ReservationState.CatPhase.Stealth,
                    "has_cats_agility" => conn.Reservations.Cat == ReservationState.CatPhase.Agility,
                    _ => true,
                } ? 1 : 0));
        if (conn.Reservations.CatPhasing)
            foreach (string stat in new[] { "phase_through_objects", "virtual_phase_through_objects" })
                if (Stats.Key(stat) is uint key) result.Add((key, 1));
        return result;
    }
}
