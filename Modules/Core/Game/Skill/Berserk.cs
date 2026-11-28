using GameServer.Core.Protocol.Instance;

public static partial class Game
{
    internal const string BerserkEffect = "Berserk";
    internal const ushort BerserkBuffRow = 1310; // BuffDefinitions berserk

    sealed class BerserkState
    {
        public bool Active;
        public ushort Skill;
        public int Effect, CooldownKey, CooldownVersion, Epoch;
        public int RageEffectPct, BaseLossPerSec, AccelPctPerSec, LossModPct;
        public double StartedAt, LastTickAt, DrainCarry;
        public object? Rules;
        public GemSourceLifetimes.SourceLifetime? Source;
    }

    static readonly RuleTable<Player, BerserkState> Berserks = new();

    static bool BerserkLive(Player player, BerserkState state) =>
        state.Active && CurrentGameWork && state.Epoch == player.ProjectileEpoch && ReferenceEquals(state.Rules, CombatLifetime)
        && !player.Closed && !player.LeavingArea && player.PendingTransfer is null && player.PlayerLife > 0
        && GemSourceLifetimeCurrentLocked(player, state.Skill, state.Effect, state.Source);

    internal static bool BerserkActiveLocked(Player player)
    {
        if (!Berserks.TryGetValue(player, out var state) || !state.Active) return false;
        if (!BerserkLive(player, state))
        {
            EndBerserkLocked(player, state, AreaTime(player));
            return false;
        }
        return true;
    }

    internal static bool CanStartBerserkReadOnly(Player player, ushort? skillId, int effect)
    {
        if (Berserks.TryGetValue(player, out var state) && BerserkLive(player, state)) return false;
        int minRage = 5;
        if (skillId is ushort sid)
        {
            var gem = SupportedSkill(player, sid);
            if (gem.Effect != effect) return false;
            minRage = Math.Max(1, gem.Stats.GetValueOrDefault(Stat.BerserkMinimumRage));
        }
        var playerStats = CharacterPlayerStats(player);
        var rage = player.Rage.Read(ProjectileWorldTime(player), RageStats.Inputs(player, playerStats.GetValueOrDefault));
        return rage.Current >= minRage;
    }

    internal static bool CanStartBerserkLocked(Player player, ushort skillId, int effect)
    {
        if (BerserkActiveLocked(player)) return false;
        return CanStartBerserkReadOnly(player, skillId, effect);
    }

    static bool StartBerserkLocked(Player player, ushort skillId, double now)
    {
        var gem = SupportedSkill(player, skillId);
        if (SkillsFor(player).EffectId(gem.Effect) != BerserkEffect || !CanStartBerserkLocked(player, skillId, gem.Effect))
            return false;
        var source = CaptureGemSourceLifetimeLocked(player, skillId, gem.Effect);
        if (source is null) return false;

        var playerStats = CharacterPlayerStats(player);
        int Own(uint stat) => gem.Stats.GetValueOrDefault(stat);
        int PlayerStat(uint stat) => playerStats.GetValueOrDefault(stat);
        double buffEffect = Math.Max(0, 1 + ((long)Own(Stat.BerserkBuffEffectPlusPct)
            + PlayerStat(Stat.BerserkBuffEffectPlusPct) + PlayerStat(Stat.BuffEffectOnSelfPlusPct)) / 100d);

        var state = Berserks.GetValue(player, static _ => new());
        state.Active = true;
        state.Skill = skillId;
        state.Effect = gem.Effect;
        state.Epoch = player.ProjectileEpoch;
        state.Rules = CombatLifetime;
        state.Source = source;
        state.RageEffectPct = ActorScale.Saturate((long)Math.Floor(Own(Stat.BerserkRageEffectPlusPct) * buffEffect));
        state.BaseLossPerSec = Math.Max(1, Own(Stat.BerserkBaseRageLossPerSecond));
        state.AccelPctPerSec = Math.Max(0, Own(Stat.BerserkRageLossPlusPctPerSecond));
        state.LossModPct = ActorScale.Saturate((long)Own(Stat.BerserkRageLossPlusPct) + PlayerStat(Stat.BerserkRageLossPlusPct));
        state.StartedAt = now;
        state.LastTickAt = now;
        state.DrainCarry = 0;

        HoldSkillCooldownLocked(player, skillId, gem.Effect, now, out state.CooldownKey, out state.CooldownVersion);
        var activeSkill = SkillsFor(player).ActionHash(gem.Effect) is ushort action ? (skillId, action) : ((ushort, ushort)?)null;
        player.Area.Buffs.Set(player.Id, new(BerserkBuffRow, player.Id, skillId, ActiveSkill: activeSkill));
        MarkPlayerStats(player, PlayerStatStep.Sources);
        var session = ProjectilesFor(player);
        session.Enqueue(() => SendCharacterStats(player, PlayerStatChange.Sources));
        session.Enqueue(() => SendNativeCooldownRefresh(player));
        return true;
    }

    static void EndBerserkLocked(Player player, BerserkState state, double now)
    {
        if (!state.Active) return;
        state.Active = false;
        state.RageEffectPct = 0;
        state.DrainCarry = 0;
        player.Area.Buffs.Clear(player.Id, BerserkBuffRow, player.Id);
        ResumeSkillCooldownLocked(player, state.CooldownKey, state.CooldownVersion, now);
        state.CooldownVersion = -1;
        MarkPlayerStats(player, PlayerStatStep.Sources);
        if (!player.Closed && !player.LeavingArea && player.PlayerLife > 0)
        {
            var session = ProjectilesFor(player);
            session.Enqueue(() => SendCharacterStats(player, PlayerStatChange.Sources));
            session.Enqueue(() => SendNativeCooldownRefresh(player));
        }
    }

    internal static void ClearBerserkLocked(Player player, IReadOnlyCollection<ushort>? onlyIds = null)
    {
        if (!Berserks.TryGetValue(player, out var state) || !state.Active) return;
        if (onlyIds is not null && !onlyIds.Contains(state.Skill)) return;
        EndBerserkLocked(player, state, AreaTime(player));
    }

    internal static void TickBerserkLocked(Player player, double now)
    {
        if (!Berserks.TryGetValue(player, out var state) || !state.Active) return;
        if (!BerserkActiveLocked(player)) return;
        double dt = Math.Max(0, now - state.LastTickAt);
        var stats = CharacterPlayerStats(player);
        var inputs = RageStats.Inputs(player, stats.GetValueOrDefault);
        var rage = player.Rage.Read(now, inputs);
        if (rage.Current <= 0)
        {
            EndBerserkLocked(player, state, now);
            return;
        }
        if (dt > 0)
        {
            double t0 = Math.Max(0, state.LastTickAt - state.StartedAt);
            double t1 = t0 + dt;
            double mod = Math.Max(0, 1 + state.LossModPct / 100d);
            // Exact integral of baseLoss * mod * (1 + accelPct/100 * t) over [t0, t1].
            double drained = state.BaseLossPerSec * mod * (dt + (state.AccelPctPerSec / 100d) * 0.5 * (t1 * t1 - t0 * t0));
            state.LastTickAt = now;
            state.DrainCarry += drained;
            int whole = (int)Math.Floor(state.DrainCarry + 1e-9);
            if (whole > 0)
            {
                state.DrainCarry -= whole;
                int spend = Math.Min(whole, rage.Current);
                if (spend > 0) player.Rage.Spend(spend, now, inputs);
                rage = player.Rage.Read(now, inputs);
                if (rage.Current <= 0)
                    EndBerserkLocked(player, state, now);
            }
        }
    }

    static void AddBerserkStats(Player player, Dictionary<uint, int> stats)
    {
        if (!Berserks.TryGetValue(player, out var state) || !state.Active || player.PlayerLife <= 0) return;
        if (state.Epoch != player.ProjectileEpoch || !ReferenceEquals(state.Rules, CombatLifetime)) return;
        stats[Stat.AffectedByBerserk] = 1;
        stats[Stat.BerserkCooldownDoesNotTick] = 1;
        if (state.RageEffectPct != 0)
            stats[Stat.RageEffectPlusPct] = ActorScale.Saturate(
                (long)stats.GetValueOrDefault(Stat.RageEffectPlusPct) + state.RageEffectPct);
    }
}
