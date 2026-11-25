using GameServer.Core.Projectiles;

static partial class Game
{
    const ushort StaticStrikeBuffRow = 1172;
    const ushort StaticStrikeActionHash = 0x2254;

    sealed class StaticStrikeState
    {
        public long PulseGeneration;
        public long ExpiryGeneration;
        public bool Running;
        public GameArea? Area;
        public double Until;
        public int IntervalMs;
        public ushort SkillId;
        public ushort Sequence;
        public ushort BuffTag;
        public (ushort Id, int Effect, string Name, int Level, int Quality, uint Gem) Gem;
        public SupportedGem Source = null!;
    }

    static readonly RuleTable<Player, StaticStrikeState> StaticStrikeStates = new();

    public static bool IsStaticStrike(int effect) => Skills.EffectId(effect) is "StaticStrike" or "StaticStrikeAltX";

    static bool IsGatheringLightning(int effect) => Skills.EffectId(effect) == "StaticStrikeAltX";

    public static Task ScheduleStaticStrike(Player player,
        ushort skillId, (ushort Id, int Effect, string Name, int Level, int Quality, uint Gem) gem,
        (int X, int Y) from, (int X, int Y) aim, uint targetId, int recoveryGeneration,
        CooldownUseTicket? ticket, SkillCriticalRoll criticalRoll, ushort sequence, double? swingAt,
        bool cryExerted = false)
    {
        var session = ProjectilesFor(player);
        session.Advance();
        double acceptedAt = swingAt ?? ProjectileWorldTime(player);
        double duration = SupportedAttackSeconds(player, skillId, sequence);
        double contact = GemAttackContactDelay(player, gem.Effect, duration, out _, out _);
        int repeats = MeleeAttackRepeats(player, skillId);
        int action = player.ActionCount;
        var source = SupportedSkill(player, skillId);
        double ruthless = RuthlessBlowMore(player, skillId, name => source.Stats.GetValueOrDefault(name));
        bool Valid() => session.Active && player.ActionInterruptionGeneration == recoveryGeneration
            && (ticket?.Current(player) ?? PlayerStateAllowsAction(player))
            && SameTriggerSource(source, SupportedSkill(player, skillId))
            && GemSkillInstanceDisabled(player, gem.Effect, skillId) is null;
        for (int repeat = 0; repeat <= repeats; repeat++)
        {
            int index = repeat;
            session.Runtime.Schedule(ProjectileRuntime.Time(acceptedAt + contact + repeat * duration),
                () =>
                {
                    if (Valid() && (index == 0 || player.ActionCount == action))
                        ReleaseStaticStrike(player, session, skillId, gem, from, aim, targetId,
                            criticalRoll, sequence, index, cryExerted, ruthless);
                });
        }
        return Task.CompletedTask;
    }

    public static void ReleaseStaticStrike(Player player, ProjectileSession session, ushort skillId,
        (ushort Id, int Effect, string Name, int Level, int Quality, uint Gem) gem,
        (int X, int Y) from, (int X, int Y) aim, uint targetId, SkillCriticalRoll criticalRoll,
        ushort sequence, int repeat = 0, bool cryExerted = false, double? ruthlessMore = null)
    {
        if (IsGatheringLightning(gem.Effect))
        {
            ReleaseGatheringLightning(player, session, skillId, gem, from, aim, targetId, criticalRoll, sequence,
                repeat, cryExerted, ruthlessMore);
            return;
        }
        bool hit = false;
        var source = SupportedSkill(player, skillId);
        var melee = AreaSkillHitForUse(ruthlessMore, player, skillId, gem, from, aim,
            immediate: true, sequence: sequence, criticalRoll: criticalRoll, targetId: targetId,
            onHit: _ => hit = true, scheduledSession: session, cryExerted: cryExerted, repeat: repeat);
        if (!melee.IsCompleted) throw new InvalidOperationException("Scheduled Static Strike contact must not yield.");
        melee.GetAwaiter().GetResult();
        if (hit) StartStaticStrike(player, skillId, gem, sequence, source);
    }

    static void ClearStaticStrike(Player player, StaticStrikeState state)
    {
        state.Running = false;
        (state.Area ?? player.Area).Buffs.Clear(player.Id, StaticStrikeBuffRow, player.Id, tag: state.BuffTag);
        state.Area = null;
    }

    static void StartStaticStrike(Player player, ushort skillId,
        (ushort Id, int Effect, string Name, int Level, int Quality, uint Gem) gem, ushort sequence, SupportedGem source)
    {
        int baseDuration = source.Stats.GetValueOrDefault(Stat.BaseSkillEffectDuration);
        if (baseDuration <= 0) return;
        double seconds = Math.Max(.1, SkillDurationModifiers.Seconds(baseDuration,
            n => CharacterPlayerStats(player).GetValueOrDefault(n), n => source.Stats.GetValueOrDefault(n)));
        double now = AreaTime(player);
        int interval = StaticStrikeIntervalMs(source);
        var state = StaticStrikeStates.GetOrCreateValue(player);
        bool sameArea = ReferenceEquals(state.Area, player.Area);
        bool keepPulse = state.Running && sameArea && now < state.Until && state.IntervalMs == interval;
        if (state.Running && (!sameArea || state.BuffTag != sequence))
            (state.Area ?? player.Area).Buffs.Clear(player.Id, StaticStrikeBuffRow, player.Id, tag: state.BuffTag);
        long expiryGeneration = ++state.ExpiryGeneration;
        state.Area = player.Area;
        state.Until = now + seconds;
        state.IntervalMs = interval;
        state.SkillId = skillId;
        state.Sequence = sequence;
        state.BuffTag = sequence;
        state.Gem = gem;
        state.Source = source;
        state.Running = true;
        // Client StaticStrikeNew (3.29.3c FUN_141b17220 / FUN_141b17570 / FUN_141b175d0) activates and stops its
        // native periodic beam engine only when static_strike_buff carries SourceSkill == action sequence (+0x42)
        // and ActiveSkill.Id == skillId (+0x48). Using sequence as Tag forces a fresh wire instance on refresh so
        // the client restarts its countdown clock and binds to the newly cloned StaticStrikeNew instance.
        ushort actionHash = SkillsFor(player).ActionHash(gem.Effect) ?? StaticStrikeActionHash;
        player.Area.Buffs.Set(player.Id, new(StaticStrikeBuffRow, player.Id, sequence,
            U68: Buff.Seconds(seconds), ActiveSkill: (skillId, actionHash), Tag: sequence));
        if (!keepPulse)
        {
            long pulseGeneration = ++state.PulseGeneration;
            EveryAsPlayer(player, interval,
                () =>
                {
                    if (StaticStrikeCurrent(player, state, pulseGeneration)) return true;
                    if (state.Running && state.PulseGeneration == pulseGeneration) ClearStaticStrike(player, state);
                    return false;
                },
                _ => StaticStrikePulse(player, state, pulseGeneration));
        }
        AfterAsPlayer(player, seconds, p =>
        {
            if (!state.Running || state.ExpiryGeneration != expiryGeneration) return Task.CompletedTask;
            ClearStaticStrike(p, state);
            return Task.CompletedTask;
        });
    }

    static int StaticStrikeIntervalMs(SupportedGem source)
    {
        int baseMs = Math.Max(1, source.Stats.GetValueOrDefault(Stat.StaticStrikeBaseZapFrequencyMs));
        int speed = source.Stats.GetValueOrDefault(Stat.StaticStrikeZapSpeedPlusPct);
        return Math.Max(1, (int)Math.Round(baseMs * 100.0 / Math.Max(1, 100 + speed)));
    }

    static bool StaticStrikeCurrent(Player player, StaticStrikeState state, long pulseGeneration) =>
        state.Running && ReferenceEquals(state.Area, player.Area) && state.PulseGeneration == pulseGeneration
        && AreaTime(player) < state.Until
        && player.PlayerLife > 0 && !player.Closed && !player.LeavingArea
        && SameTriggerSource(state.Source, SupportedSkill(player, state.SkillId))
        && Skills.EffectId(state.Source.Effect) == "StaticStrike"
        && GemSkillInstanceDisabled(player, state.Source.Effect, state.SkillId) is null;

    public static Task PulseStaticStrike(Player player) =>
        StaticStrikeStates.TryGetValue(player, out var state) && state.Running
            ? StaticStrikePulse(player, state, state.PulseGeneration)
            : Task.CompletedTask;

    static Task StaticStrikePulse(Player player, StaticStrikeState state, long pulseGeneration)
    {
        if (!StaticStrikeCurrent(player, state, pulseGeneration))
        {
            if (state.Running && state.PulseGeneration == pulseGeneration) ClearStaticStrike(player, state);
            return Task.CompletedTask;
        }
        var session = ProjectilesFor(player);
        session.Advance();
        double now = ProjectileWorldTime(player);
        var source = SupportedSkill(player, state.SkillId);
        var from = player.Walk.Position(now);
        // Client StaticStrikeNew (3.29.3c FUN_141b16ad0) uses 25 cells (0x19 = 2.5 m) scaled by AoE.
        int radius = SkillRadius(player, 25, source.Stats, spell: false, source.Effect);
        int chainRadius = Math.Max(1, (int)Math.Round(radius * (100.0 + source.Stats.GetValueOrDefault(Stat.ChainingRangePlusPct)) / 100.0));
        int count = Math.Max(1, source.Stats.GetValueOrDefault(Stat.StaticStrikeNumberOfBeamTargets)
            + source.Stats.GetValueOrDefault(Stat.StaticStrikeAdditionalNumberOfBeamTargets));
        int chains = Math.Max(0, source.Stats.GetValueOrDefault(Stat.NumberOfChains));
        var targets = StaticStrikeTargets(player, from, radius, chainRadius, count, chains, now);
        if (targets.Count == 0) return Task.CompletedTask;
        double more = Math.Max(0, 1 + source.Stats.GetValueOrDefault(Stat.StaticStrikeBeamDamagePlusPctFinal) / 100.0);
        foreach (var target in targets)
        {
            int reach = Math.Max(radius, (int)Math.Ceiling(Dist(from, target.Position)) + 1);
            var task = AreaSkillHitForUse(1, player, state.SkillId, state.Gem,
                from, target.Position, immediate: true, sequence: state.Sequence, melee: false, criticalRoll: null,
                damageMore: more, radiusOverride: reach, singleStrikeTarget: target.Id,
                scheduledSession: session, cryExerted: false);
            if (!task.IsCompleted) throw new InvalidOperationException("Static Strike beam contact must not yield.");
            task.GetAwaiter().GetResult();
        }
        return Task.CompletedTask;
    }

    static IReadOnlyList<(uint Id, (int X, int Y) Position)> StaticStrikeTargets(Player player,
        (int X, int Y) from, int radius, int chainRadius, int count, int chains, double now)
    {
        var primary = player.Area.MonstersNear(from, radius)
            .Where(p => p.Value.Hostile && !p.Value.Dead && p.Value.Life > 0 && p.Value.OwnerId is null)
            .Select(p => (Id: p.Key, Position: p.Value.Walk.Position(now)))
            .Where(p => Dist(from, p.Position) <= radius)
            .OrderBy(p => Dist(from, p.Position)).ThenBy(p => p.Id)
            .ToList();
        var result = new List<(uint Id, (int X, int Y) Position)>();
        var used = new HashSet<uint>();
        foreach (var first in primary.Take(count))
        {
            if (!used.Add(first.Id)) continue;
            result.Add(first);
            var origin = first.Position;
            for (int chain = 0; chain < chains; chain++)
            {
                var next = player.Area.MonstersNear(origin, chainRadius)
                    .Where(p => !used.Contains(p.Key) && p.Value.Hostile && !p.Value.Dead && p.Value.Life > 0 && p.Value.OwnerId is null)
                    .Select(p => (Id: p.Key, Position: p.Value.Walk.Position(now)))
                    .Where(p => Dist(origin, p.Position) <= chainRadius)
                    .OrderBy(p => Dist(origin, p.Position)).ThenBy(p => p.Id)
                    .FirstOrDefault();
                if (next.Id == 0 || !used.Add(next.Id)) break;
                result.Add(next);
                origin = next.Position;
            }
        }
        return result;
    }
}
