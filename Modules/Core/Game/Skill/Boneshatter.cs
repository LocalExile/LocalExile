using GameServer.Core.Projectiles;
using GameServer.Core.World;

static partial class Game
{
    const ushort TraumaBuffRow = 1873;
    static readonly RuleTable<Player, TraumaState> TraumaStates = new();

    sealed class TraumaState(GameArea area, int epoch)
    {
        public readonly GameArea Area = area;
        public readonly int Epoch = epoch;
        public readonly PriorityQueue<double, double> Expiries = new();
    }

    internal sealed class TraumaUse
    {
        public bool Gained;
    }

    sealed class BoneshatterContact(double more, bool pulse = false)
    {
        public double More { get; } = more;
        public bool Pulse { get; } = pulse;
        public readonly HashSet<uint> DirectHits = [];
        public readonly List<((int X, int Y) At, double Seconds)> Stuns = [];
    }

    static bool IsBoneshatter(int effect) => Skills.EffectId(effect) is "Boneshatter" or "BoneshatterAltX" or "BoneshatterAltY";

    static int TraumaBaseDurationMs(IReadOnlyDictionary<uint, int> own) =>
        own.GetValueOrDefault(Stat.BoneshatterTraumaBaseDurationMs) is > 0 and var boneshatter
            ? boneshatter
            : own.GetValueOrDefault(Stat.SupportTraumaBaseDurationMs);

    static bool SkillGrantsTrauma(IReadOnlyDictionary<uint, int> own) =>
        own.GetValueOrDefault(Stat.TraumaStrikeSelfDamagePerTrauma) > 0 && TraumaBaseDurationMs(own) > 0;

    static TraumaUse? TraumaUseFor(Player player, SupportedGem source)
    {
        if (!SkillGrantsTrauma(source.Stats)) return null;
        ProjectilesFor(player).Advance();
        return new TraumaUse();
    }

    static int CurrentTraumaCount(Player player, double now) => CurrentTrauma(player, now)?.Expiries.Count ?? 0;

    static void AddTraumaStats(Player player, Dictionary<uint, int> stats)
    {
        if (!TraumaStates.TryGetValue(player, out _)) return;
        int count = CurrentTraumaCount(player, ProjectileWorldTime(player));
        if (count > 0) stats[Stat.CurrentTrauma] = count;
    }

    static void ClearTrauma(Player player)
    {
        if (!TraumaStates.TryGetValue(player, out var state)) return;
        TraumaStates.Remove(player);
        state.Expiries.Clear();
        state.Area.Buffs.Clear(player.Id, TraumaBuffRow, player.Id);
        if (!PublishByFrame(player, PlayerStatChange.Sources)) PresentStats(player);
    }

    static bool TryGainTraumaLocked(Player player, ProjectileSession? scheduledSession, SupportedGem source,
        TraumaUse? use, double now, string label = "Trauma")
    {
        if (use is not { Gained: false } || player.PlayerLife == 0 || player.Closed || player.LeavingArea) return false;
        int Own(uint key) => source.Stats.GetValueOrDefault(key);
        int damagePerStack = Own(Stat.TraumaStrikeSelfDamagePerTrauma);
        int duration = TraumaBaseDurationMs(source.Stats);
        if (damagePerStack <= 0 || duration <= 0) return false;
        var stats = CharacterPlayerStats(player);
        int Get(uint key) => ActorScale.Add(key, stats.GetValueOrDefault(key), Own(key));
        double secondsPerStack = SkillDurationModifiers.Seconds(duration, stats.GetValueOrDefault, Own, DurationKinds(player, source.Effect));
        if (secondsPerStack <= 0) return false;
        use.Gained = true;
        int gained = IsBoneshatter(source.Effect)
            && RandomRolls.Chance(Get(Stat.BoneshatterChanceToGainPlus1Trauma), 100, AreaRng(player)) ? 2 : 1;
        var trauma = CurrentTrauma(player, now) ?? new(player.Area, player.IncomingHitEpoch);
        TraumaStates.AddOrUpdate(player, trauma);
        double expiry = now + secondsPerStack;
        for (int i = 0; i < gained; i++) trauma.Expiries.Enqueue(expiry, expiry);
        int count = trauma.Expiries.Count;
        if (Own(Stat.LoseAllTraumaAtXTrauma) is int resetAt and > 0 && count >= resetAt)
            trauma.Expiries.Clear();
        ShowTrauma(player, trauma, now);
        var session = scheduledSession ?? ProjectilesFor(player);
        session.Runtime.Schedule(ProjectileRuntime.Time(expiry), () =>
        {
            if (session.Active && player.IncomingHitEpoch == trauma.Epoch
                && TraumaStates.TryGetValue(player, out var current) && ReferenceEquals(current, trauma))
            {
                ExpireTrauma(trauma, expiry);
                ShowTrauma(player, trauma, expiry);
            }
        });
        if (!RandomRolls.Chance(Get(Stat.IgnoreSelfDamageFromTraumaChancePct), 100, AreaRng(player)))
        {
            int selfDamage = ActorScale.Saturate((long)damagePerStack * count);
            var raw = DamagePacket.Of(DamageType.Physical, selfDamage, selfDamage);
            ApplySelfHitLocked(player, raw, now);
            if (player.PlayerLife == 0)
            {
                trauma.Expiries.Clear();
                ShowTrauma(player, trauma, now);
                int deathEpoch = player.IncomingHitEpoch;
                session.Enqueue(() => PlayerDiedAtEpoch(player, $"death from {label}", deathEpoch));
            }
            else
            {
                player.Frames.Area?.Life.Changed(player);
                if (player.Frames.Area is null) session.World(player.Id, PlayerLifeUpdate(player), $"life after {label}");
                ReleaseDamageTakenTriggers(player, session);
            }
        }
        return true;
    }

    static Task ScheduleBoneshatter(Player player, ushort skillId,
        (ushort Id, int Effect, string Name, int Level, int Quality, uint Gem) gem,
        (int X, int Y) from, (int X, int Y) aim, uint targetId, int recoveryGeneration,
        CooldownUseTicket? ticket, SkillCriticalRoll criticalRoll, ushort sequence, double? swingAt, bool cryExerted, int ancestralTargets, int? rallyingDamage,
        TraumaUse? traumaUse = null)
    {
        var session = ProjectilesFor(player);
        session.Advance();
        double acceptedAt = swingAt ?? ProjectileWorldTime(player);
        double duration = SupportedAttackSeconds(player, skillId, sequence);
        double contact = GemAttackContactDelay(player, gem.Effect, duration, out _, out _);
        int repeats = MeleeAttackRepeats(player, skillId);
        int action = player.ActionCount;
        var source = SupportedSkill(player, skillId);
        double ruthless = RuthlessBlowMore(player, skillId, source.Stats.GetValueOrDefault);
        traumaUse ??= SkillGrantsTrauma(source.Stats) ? new TraumaUse() : null;
        for (int repeat = 0; repeat <= repeats; repeat++)
        {
            int index = repeat;
            session.Runtime.Schedule(ProjectileRuntime.Time(acceptedAt + contact + repeat * duration), () =>
            {
                if (!session.Active || player.ActionInterruptionGeneration != recoveryGeneration
                    || !(ticket?.Current(player) ?? PlayerStateAllowsAction(player))
                    || (index != 0 && player.ActionCount != action)
                    || !SameTriggerSource(source, SupportedSkill(player, skillId))
                    || GemSkillInstanceDisabled(player, gem.Effect, skillId) is not null) return;
                ReleaseBoneshatter(player, session, skillId, gem, from, aim, targetId,
                    criticalRoll, sequence, index, cryExerted, ruthless, traumaUse, ancestralTargets, rallyingDamage);
            });
        }
        return Task.CompletedTask;
    }

    static bool ReleaseBoneshatter(Player player, ProjectileSession session, ushort skillId,
        (ushort Id, int Effect, string Name, int Level, int Quality, uint Gem) gem,
        (int X, int Y) from, (int X, int Y) aim, uint targetId, SkillCriticalRoll criticalRoll, ushort sequence,
        int repeat, bool cryExerted, double ruthless, TraumaUse? traumaUse, int ancestralTargets, int? rallyingDamage)
    {
        double now = session.Runtime.Now / (double)ProjectileRuntime.QuantaPerSecond;
        var source = SupportedSkill(player, skillId);
        int Own(uint key) => source.Stats.GetValueOrDefault(key);
        int stacks = CurrentTraumaCount(player, now);
        long increase = (long)stacks * Own(Stat.TraumaStrikeDamagePlusPctFinalPerTrauma)
            + Math.Min(200, (long)stacks * Own(Stat.TraumaStrikeDamagePlusPctFinalPerTraumaCapped));
        double more = Math.Max(0, 1 + increase / 100d);
        double repeatMore = MeleeRepeatDamageMore(player, skillId, repeat);
        var strike = new BoneshatterContact(more);
        var weapon = WeaponFor(player);
        int extraReach = MeleeSkillReach(player, weapon, gem.Effect, source.Stats) - weapon.Range;
        uint primary = LightningStrikeTarget(player, from, aim, targetId, now, extraReach);
        var centre = player.Area.Monsters.TryGetValue(primary, out var target) ? target.Walk.Position(now) : aim;
        var main = AreaSkillHitForUse(ruthless, player, skillId, gem, from, centre, immediate: true, sequence: sequence,
            damageMore: repeatMore, criticalRoll: criticalRoll, targetId: primary, repeat: repeat,
            scheduledSession: session, cryExerted: cryExerted, targetFilter: (id, _) => id == primary, boneshatter: strike,
            ancestralTargets: ancestralTargets, rallyingDamage: rallyingDamage);
        if (!main.IsCompleted) throw new InvalidOperationException("Scheduled Boneshatter contact must not yield.");
        main.GetAwaiter().GetResult();

        if (strike.Stuns.Count > 0)
        {
            var pulse = new BoneshatterContact(more, pulse: true);
            var area = SkillAreaStats(player, source.Stats, gem.Effect);
            int areaPerStep = Own(Stat.TraumaStrikeShockwaveAreaOfEffectPlusPctPer100msStunDurationUpTo400Pct);
            var alreadyHit = strike.DirectHits;
            foreach (var (at, seconds) in strike.Stuns)
            {
                int increased = Math.Min(400, (int)Math.Floor(10 * seconds) * areaPerStep);
                int radius = SkillArea.Radius(14, key => area.GetValueOrDefault(key)
                    + (key == SkillArea.BaseIncreased ? increased : 0), spell: false);
                if (radius <= 0) continue;
                session.BoneshatterPulse(at, radius, SkillArtVariation(player, gem.Gem));
                var splash = AreaSkillHitForUse(ruthless, player, skillId, gem, at, at, immediate: true, sequence: sequence,
                    damageMore: repeatMore, criticalRoll: criticalRoll, repeat: repeat, radiusOverride: radius, once: alreadyHit,
                    scheduledSession: session, cryExerted: cryExerted, boneshatter: pulse, ancestralTargets: ancestralTargets, rallyingDamage: rallyingDamage);
                if (!splash.IsCompleted) throw new InvalidOperationException("Scheduled Boneshatter pulse must not yield.");
                splash.GetAwaiter().GetResult();
            }
        }

        if (strike.DirectHits.Count == 0) return false;
        string label = Own(Stat.BoneshatterTraumaBaseDurationMs) > 0 ? "Boneshatter Trauma" : "Trauma";
        return TryGainTraumaLocked(player, session, source, traumaUse, now, label);
    }

    static TraumaState? CurrentTrauma(Player player, double now)
    {
        if (!TraumaStates.TryGetValue(player, out var state) || !ReferenceEquals(state.Area, player.Area)
            || state.Epoch != player.IncomingHitEpoch) return null;
        ExpireTrauma(state, now);
        return state;
    }

    static void ExpireTrauma(TraumaState state, double now)
    {
        while (state.Expiries.TryPeek(out _, out double expires) && expires <= now)
            _ = state.Expiries.Dequeue();
    }

    static void ShowTrauma(Player player, TraumaState state, double now)
    {
        if (state.Expiries.TryPeek(out _, out double expires))
            state.Area.Buffs.Set(player.Id, new(TraumaBuffRow, player.Id, U68: Buff.Seconds(expires - now),
                Stacks: (ushort)Math.Min(ushort.MaxValue, state.Expiries.Count)));
        else
            state.Area.Buffs.Clear(player.Id, TraumaBuffRow, player.Id);
        if (!PublishByFrame(player, PlayerStatChange.Sources)) PresentStats(player);
    }

    public sealed partial class ProjectileSession
    {
        internal void BoneshatterPulse((int X, int Y) at, int radius, int variation)
        {
            const string art = "Metadata/Effects/Spells/trauma_strike/shockwave.ao";
            Effect(at.X, at.Y, Art.AnimatedInPlaceOf(art, variation), ProjectileRuntime.Time(.6), radius);
        }
    }
}
