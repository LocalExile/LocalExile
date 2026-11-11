static partial class Game
{
    internal const string CatAspectFamily = "CatAspect";
    internal const ushort CatAspectRootRow = 1036, CatsStealthRow = 1037, CatsAgilityRow = 1038;
    const string CatBuffEffect = "aspect_of_the_cat_buff_effect_+%";
    const string CatStealthDuration = "cats_stealth_duration_ms_+";
    const string CatCrit = "critical_strike_chance_+%";
    const string CatAvoid = "avoid_damage_%";
    const string CatStealth = "stealth_+%";
    const string CatAttackSpeed = "attack_speed_+%";
    const string CatCastSpeed = "base_cast_speed_+%";

    internal readonly record struct CatAspectPhaseStats(int CriticalStrikeChancePct, int AvoidDamagePct,
        int StealthPct, int AttackSpeedPct, int CastSpeedPct)
    {
        internal int Value(string stat) => stat switch
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

    internal static bool CatAspectRootActiveLocked(ClientState conn)
    {
        var state = conn.Reservations;
        return CatAspectRootLocked(conn) is { } root && state.CatOwner is { } owner
            && owner.Skill == root.SkillId && owner.RootBuff == root.Buff
            && state.Cat is not ReservationState.CatPhase.None;
    }

    internal static ushort CatAspectPhaseRow(ReservationState.CatPhase phase) => phase switch
    {
        ReservationState.CatPhase.Stealth => CatsStealthRow,
        ReservationState.CatPhase.Agility => CatsAgilityRow,
        _ => 0,
    };

    static void ClearCatRuntimeLocked(ClientState conn)
    {
        var state = conn.Reservations;
        if (state.CatOwner is null && state.Cat == ReservationState.CatPhase.None && state.CatPhaseBuff == 0) return;
        state.CatOwner = null;
        state.Cat = ReservationState.CatPhase.None;
        state.CatPhaseStartedAt = state.CatPhaseDeadline = 0;
        state.CatPhaseBuff = 0;
        state.CatRevision++;
        conn.AuraStatsDirty = true;
    }

    internal static void EndCatAspectRuntimeLocked(ClientState conn, AuraCatalog.Aura aura)
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
        // Fur's modifier extends the base Stealth phase before duration scaling.
        primary = Math.Max(0, (primary > 0 ? primary : 4000) + Raw(CatStealthDuration));
        secondary = secondary > 0 ? secondary : 6000;
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

    internal static bool CatAspectReconcileLocked(ClientState conn, double now,
        IReadOnlyDictionary<uint, int>? rawSources = null)
    {
        var state = conn.Reservations;
        var root = CatAspectRootLocked(conn);
        if (root is null)
        {
            bool wasActive = state.CatOwner is not null || state.Cat != ReservationState.CatPhase.None;
            ClearCatRuntimeLocked(conn);
            return wasActive;
        }
        var owner = new ReservationState.CatAspectOwner(root.SkillId, root.Buff);
        bool changed = false;
        if (state.CatOwner != owner || state.Cat == ReservationState.CatPhase.None)
        {
            state.CatOwner = owner;
            state.Cat = ReservationState.CatPhase.Stealth;
            state.CatPhaseStartedAt = now;
            state.CatPhaseDeadline = now + 4;
            state.CatPhaseBuff = 0;
            changed = true;
        }
        rawSources ??= CharacterStatSources(conn).Stats;
        var (stealth, agility) = CatAspectDurationsLocked(conn, root, rawSources);
        double currentDuration = CatPhaseDuration(state.Cat, stealth, agility);
        double expectedDeadline = state.CatPhaseStartedAt + currentDuration;
        if (Math.Abs(expectedDeadline - state.CatPhaseDeadline) > 1e-9)
        {
            state.CatPhaseDeadline = expectedDeadline;
            changed = true;
        }
        var settled = SettleCatPhase(state.Cat, state.CatPhaseStartedAt, state.CatPhaseDeadline, now, stealth, agility);
        if (settled.Phase != state.Cat || Math.Abs(settled.Deadline - state.CatPhaseDeadline) > 1e-9)
        {
            state.Cat = settled.Phase;
            state.CatPhaseStartedAt = settled.Started;
            state.CatPhaseDeadline = settled.Deadline;
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

    internal static CatAspectPhaseStats CatAspectPhaseStatsLocked(ClientState conn, int auraEffectPct,
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

    internal static IReadOnlyList<uint> CatAspectPhaseValues(ReservationState.CatPhase phase,
        CatAspectPhaseStats phaseStats) => phase switch
    {
        ReservationState.CatPhase.Stealth =>
            [unchecked((uint)phaseStats.CriticalStrikeChancePct), unchecked((uint)phaseStats.AvoidDamagePct),
                unchecked((uint)phaseStats.StealthPct)],
        ReservationState.CatPhase.Agility =>
            [unchecked((uint)phaseStats.AttackSpeedPct), unchecked((uint)phaseStats.CastSpeedPct)],
        _ => [],
    };

    internal static IReadOnlyList<(uint Key, int Value)> CatAspectRawStatsLocked(ClientState conn, int auraEffectPct,
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
        return result;
    }
}
