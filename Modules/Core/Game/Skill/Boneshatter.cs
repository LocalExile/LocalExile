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

    sealed class BoneshatterContact(double more, bool pulse = false)
    {
        public double More { get; } = more;
        public bool Pulse { get; } = pulse;
        public readonly HashSet<uint> DirectHits = [];
        public readonly List<((int X, int Y) At, double Seconds)> Stuns = [];
    }

    static bool IsBoneshatter(int effect) => Skills.EffectId(effect) is "Boneshatter" or "BoneshatterAltX" or "BoneshatterAltY";

    static Task ScheduleBoneshatter(Player player, ushort skillId,
        (ushort Id, int Effect, string Name, int Level, int Quality, uint Gem) gem,
        (int X, int Y) from, (int X, int Y) aim, uint targetId, int recoveryGeneration,
        CooldownUseTicket? ticket, SkillCriticalRoll criticalRoll, ushort sequence, double? swingAt, bool cryExerted, int ancestralTargets)
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
        bool gainedTrauma = false;
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
                gainedTrauma |= ReleaseBoneshatter(player, session, skillId, gem, from, aim, targetId,
                    criticalRoll, sequence, index, cryExerted, ruthless, !gainedTrauma, ancestralTargets);
            });
        }
        return Task.CompletedTask;
    }

    static bool ReleaseBoneshatter(Player player, ProjectileSession session, ushort skillId,
        (ushort Id, int Effect, string Name, int Level, int Quality, uint Gem) gem,
        (int X, int Y) from, (int X, int Y) aim, uint targetId, SkillCriticalRoll criticalRoll, ushort sequence,
        int repeat, bool cryExerted, double ruthless, bool canGainTrauma, int ancestralTargets)
    {
        double now = session.Runtime.Now / (double)ProjectileRuntime.QuantaPerSecond;
        var source = SupportedSkill(player, skillId);
        int Own(uint key) => source.Stats.GetValueOrDefault(key);
        var trauma = CurrentTrauma(player, now);
        int stacks = trauma?.Expiries.Count ?? 0;
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
            ancestralTargets: ancestralTargets);
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
                    scheduledSession: session, cryExerted: cryExerted, boneshatter: pulse, ancestralTargets: ancestralTargets);
                if (!splash.IsCompleted) throw new InvalidOperationException("Scheduled Boneshatter pulse must not yield.");
                splash.GetAwaiter().GetResult();
            }
        }

        int damagePerStack = Own(Stat.TraumaStrikeSelfDamagePerTrauma);
        int duration = Own(Stat.BoneshatterTraumaBaseDurationMs);
        if (!canGainTrauma || strike.DirectHits.Count == 0 || damagePerStack <= 0 || duration <= 0 || player.PlayerLife == 0)
            return false;
        var stats = CharacterPlayerStats(player);
        int Get(uint key) => ActorScale.Add(key, stats.GetValueOrDefault(key), Own(key));
        double secondsPerStack = SkillDurationModifiers.Seconds(duration, stats.GetValueOrDefault, Own, DurationKinds(player, gem.Effect));
        if (secondsPerStack <= 0) return false;
        int gained = RandomRolls.Chance(Get(Stat.BoneshatterChanceToGainPlus1Trauma), 100, AreaRng(player)) ? 2 : 1;
        trauma ??= new(player.Area, player.IncomingHitEpoch);
        TraumaStates.AddOrUpdate(player, trauma);
        double expiry = now + secondsPerStack;
        for (int i = 0; i < gained; i++) trauma.Expiries.Enqueue(expiry, expiry);
        int count = trauma.Expiries.Count;
        if (Own(Stat.LoseAllTraumaAtXTrauma) is int resetAt and > 0 && count >= resetAt)
            trauma.Expiries.Clear();
        ShowTrauma(player, trauma, now);
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
                session.Enqueue(() => PlayerDiedAtEpoch(player, "death from Boneshatter Trauma", deathEpoch));
            }
            else
            {
                player.Frames.Area?.Life.Changed(player);
                if (player.Frames.Area is null) session.World(player.Id, PlayerLifeUpdate(player), "life after Boneshatter Trauma");
                ReleaseDamageTakenTriggers(player, session);
            }
        }
        return true;
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
