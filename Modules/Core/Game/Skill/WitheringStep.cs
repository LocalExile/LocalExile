using GameServer.Core.Protocol.Instance;

public static partial class Game
{
    internal const string WitheringStepEffect = "WitheringStep";
    internal const ushort WitheringStepBuffRow = 1360; // BuffDefinitions slither
    internal const int WitheringStepBaseRadius = 24;

    sealed class WitheringStepState
    {
        public bool Active;
        public ushort Skill;
        public int Effect, CooldownKey, CooldownVersion, Epoch;
        public object? Rules;
        public double PhasingUntil;
        public SupportedGem? Gem;
        public GemSourceLifetimes.SourceLifetime? Source;
        public readonly HashSet<uint> WitheredTargets = [];
    }

    static readonly RuleTable<Player, WitheringStepState> WitheringSteps = new();

    static void HoldSkillCooldownLocked(Player player, ushort skillId, int effect, double now,
        out int cooldownKey, out int cooldownVersion)
    {
        cooldownKey = CooldownKey(player, effect, skillId);
        cooldownVersion = -1;
        if (CooldownForSkill(player, skillId) is not { } plan) return;
        var state = CooldownStateLocked(player, cooldownKey, plan, now);
        state.ConfirmNativeUse(state.UseVersion, skillId, synchronizeProgress: true);
        state.Hold(plan.BaseMilliseconds);
        cooldownVersion = state.UseVersion;
    }

    static void ResumeSkillCooldownLocked(Player player, int cooldownKey, int cooldownVersion, double now)
    {
        if (cooldownVersion < 0 || !player.SingleUseCooldowns.TryGetValue(cooldownKey, out var state)
            || state.UseVersion != cooldownVersion || !state.Held) return;
        state.Resume(now, state.TotalBaseMilliseconds);
        player.SkillReadyAt[cooldownKey] = now + state.RemainingSeconds;
    }

    static bool WitheringStepLive(Player player, WitheringStepState state) =>
        state.Active && CurrentGameWork && state.Epoch == player.ProjectileEpoch && ReferenceEquals(state.Rules, CombatLifetime)
        && !player.Closed && !player.LeavingArea && player.PendingTransfer is null && player.PlayerLife > 0
        && GemSourceLifetimeCurrentLocked(player, state.Skill, state.Effect, state.Source)
        && ElusiveOf(player).Effect(ResourceEffectsTime(player)) > 0;

    internal static bool WitheringStepActiveLocked(Player player, double now)
    {
        if (!WitheringSteps.TryGetValue(player, out var state) || !state.Active) return false;
        if (!WitheringStepLive(player, state))
        {
            EndWitheringStepLocked(player, state, now, clearElusive: false);
            return false;
        }
        return true;
    }

    internal static bool CanUseWitheringStepReadOnly(Player player) =>
        !(WitheringSteps.TryGetValue(player, out var state) && WitheringStepLive(player, state))
        && ElusiveOf(player).Effect(ResourceEffectsTime(player)) <= 0;

    internal static bool CanUseWitheringStepLocked(Player player, double now)
    {
        if (WitheringStepActiveLocked(player, now)) return false;
        return ElusiveOf(player).Effect(ResourceEffectsTime(player)) <= 0;
    }

    static bool StartWitheringStepLocked(Player player, ushort skillId, double now)
    {
        if (!CanUseWitheringStepLocked(player, now)) return false;
        var gem = SupportedSkill(player, skillId);
        if (SkillsFor(player).EffectId(gem.Effect) != WitheringStepEffect) return false;
        var source = CaptureGemSourceLifetimeLocked(player, skillId, gem.Effect);
        if (source is null) return false;
        var playerStats = CharacterPlayerStats(player);
        if (!GrantWitheringStepElusiveLocked(player, gem.Stats, playerStats)) return false;

        var state = WitheringSteps.GetValue(player, static _ => new());
        state.Active = true;
        state.Skill = skillId;
        state.Effect = gem.Effect;
        state.Epoch = player.ProjectileEpoch;
        state.Rules = CombatLifetime;
        state.Gem = gem;
        state.Source = source;
        state.WitheredTargets.Clear();
        HoldSkillCooldownLocked(player, skillId, gem.Effect, now, out state.CooldownKey, out state.CooldownVersion);

        state.PhasingUntil = player.ElusiveUntil;
        player.PhasingUntil = Math.Max(player.PhasingUntil, state.PhasingUntil);
        var activeSkill = SkillsFor(player).ActionHash(gem.Effect) is ushort action ? (skillId, action) : ((ushort, ushort)?)null;
        player.Area.Buffs.Set(player.Id, new(WitheringStepBuffRow, player.Id, skillId, ActiveSkill: activeSkill));
        ApplyWitheringStepWitherLocked(player, state, now, playerStats);
        MarkPlayerStats(player, PlayerStatStep.Sources);
        RecomputePoolsAndSpeed(player);
        var session = ProjectilesFor(player);
        session.Enqueue(() => SendCharacterStats(player, PlayerStatChange.Sources));
        session.Enqueue(() => SendNativeCooldownRefresh(player));
        return true;
    }

    static void ApplyWitheringStepWitherLocked(Player player, WitheringStepState state, double now,
        IReadOnlyDictionary<uint, int>? playerStats = null)
    {
        if (state.Gem is not { } gem) return;
        playerStats ??= CharacterPlayerStats(player);
        int Own(uint stat) => gem.Stats.GetValueOrDefault(stat);
        int Get(uint stat) => ActorScale.Saturate((long)Own(stat) + playerStats.GetValueOrDefault(stat));
        int stacks = Math.Max(1, Own(Stat.SlitherWitherStacks));
        double witherSeconds = WitherDurationSeconds(
            SkillDurationModifiers.Seconds(Own(Stat.ActiveSkillWitheredBaseDurationMs), playerStats.GetValueOrDefault, Own), Get);
        int radius = SkillRadius(player, WitheringStepBaseRadius, gem.Stats, spell: true, gem.Effect, playerStats);
        var centre = player.Walk.Position(now);
        foreach (var (id, monster) in player.Area.MonstersNear(centre, radius))
        {
            if (state.WitheredTargets.Contains(id) || !monster.Hostile || monster.Dead || monster.Life == 0
                || monster.WorldHidden || monster.Invulnerable || ArenaHazards.Untargetable(player, monster)
                || Dist(centre, monster.Walk.Position(now)) > radius) continue;
            state.WitheredTargets.Add(id);
            ApplyWitherStacksLocked(monster, stacks, witherSeconds, Get, now, player.Id);
        }
    }

    static void EndWitheringStepLocked(Player player, WitheringStepState state, double now, bool clearElusive)
    {
        if (!state.Active) return;
        state.Active = false;
        state.WitheredTargets.Clear();
        if (state.PhasingUntil > 0 && Math.Abs(player.PhasingUntil - state.PhasingUntil) < 1e-9)
            player.PhasingUntil = now;
        state.PhasingUntil = 0;
        player.Area.Buffs.Clear(player.Id, WitheringStepBuffRow, player.Id);
        if (clearElusive || ElusiveOf(player).FromWitheringStep && ElusiveOf(player).Effect(ResourceEffectsTime(player)) <= 0)
            ClearElusiveLocked(player);
        else ElusivePresentation(player);
        ResumeSkillCooldownLocked(player, state.CooldownKey, state.CooldownVersion, now);
        state.CooldownVersion = -1;
        MarkPlayerStats(player, PlayerStatStep.Sources);
        if (!player.Closed && !player.LeavingArea && player.PlayerLife > 0)
        {
            RecomputePoolsAndSpeed(player);
            var session = ProjectilesFor(player);
            session.Enqueue(() => SendCharacterStats(player, PlayerStatChange.Sources));
            session.Enqueue(() => SendNativeCooldownRefresh(player));
        }
    }

    internal static void WitheringStepOnSkillUse(Player player, ushort skillId)
    {
        if (!WitheringSteps.TryGetValue(player, out var state) || !state.Active || state.Skill == skillId) return;
        using (ProjectileMutation(player, advance: false))
            if (state.Active && state.Skill != skillId)
                EndWitheringStepLocked(player, state, AreaTime(player), clearElusive: true);
    }

    internal static void ClearWitheringStepLocked(Player player, IReadOnlyCollection<ushort>? onlyIds = null)
    {
        if (!WitheringSteps.TryGetValue(player, out var state) || !state.Active) return;
        if (onlyIds is not null && !onlyIds.Contains(state.Skill)) return;
        EndWitheringStepLocked(player, state, AreaTime(player), clearElusive: false);
    }

    internal static void TickWitheringStepLocked(Player player, double now)
    {
        if (!WitheringSteps.TryGetValue(player, out var state) || !state.Active) return;
        if (!WitheringStepActiveLocked(player, now)) return;
        ElusivePresentation(player);
        ApplyWitheringStepWitherLocked(player, state, now);
    }

    static void AddWitheringStepStats(Player player, Dictionary<uint, int> stats)
    {
        if (!WitheringSteps.TryGetValue(player, out var state) || !state.Active || player.PlayerLife <= 0) return;
        if (state.Epoch != player.ProjectileEpoch || !ReferenceEquals(state.Rules, CombatLifetime)) return;
        stats[Stat.PhaseThroughObjects] = Math.Max(1, stats.GetValueOrDefault(Stat.PhaseThroughObjects));
        stats[Stat.SlitherCooldownDoesNotTick] = 1;
    }
}
