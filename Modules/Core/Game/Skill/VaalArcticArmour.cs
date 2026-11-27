
public static partial class Game
{
    const string VaalArcticArmourEffect = "VaalArcticArmour";
    const ushort VaalArcticArmourIceRow = 2175;
    const ushort VaalArcticArmourRecoveryRow = 2178;

    sealed class VaalArcticArmourState(GameArea area, int epoch, double until, double castFinishes, ushort skill,
        int hits, int hitDamageChange, int manaRegen, int shieldRegen)
    {
        public readonly GameArea Area = area;
        public readonly int Epoch = epoch;
        public readonly double Until = until, CastFinishes = castFinishes;
        public readonly ushort Skill = skill;
        public int Hits = hits;
        public readonly int HitDamageChange = hitDamageChange, ManaRegen = manaRegen, ShieldRegen = shieldRegen;
        public bool RecoveryPresented;
    }

    static readonly RuleTable<Player, VaalArcticArmourState> VaalArcticArmours = new();

    static bool VaalArcticArmourCurrent(Player player, VaalArcticArmourState state, double at) =>
        state.Epoch == player.ProjectileEpoch && !player.Closed && !player.LeavingArea
        && player.PendingTransfer is null && player.PlayerLife > 0 && at < state.Until && state.Hits > 0;

    static bool VaalArcticArmourActive(Player player, double at) =>
        VaalArcticArmours.TryGetValue(player, out var state) && VaalArcticArmourCurrent(player, state, at);

    static void StartVaalArcticArmour(Player player, ushort skill, int castMs, double now)
    {
        var gem = SupportedSkill(player, skill);
        var stats = CharacterPlayerStats(player);
        int Own(uint key) => gem.Stats.GetValueOrDefault(key);
        int PlayerStat(uint key) => stats.GetValueOrDefault(key);
        double seconds = SkillDurationModifiers.Seconds(Own(Stat.BaseSkillEffectDuration), PlayerStat, Own,
            DurationKinds(player, gem.Effect)) / RecipientEffectTime.Rate(1, PlayerStat);
        if (seconds <= 0 || Own(Stat.VaalArcticArmourNumberOfHitsAbsorbed) <= 0) return;
        EndVaalArcticArmour(player);
        var state = VaalArcticArmours.GetValue(player, _ => new(player.Area, player.ProjectileEpoch, now + seconds,
            now + castMs / 1000d, skill, Own(Stat.VaalArcticArmourNumberOfHitsAbsorbed),
            Own(Stat.VaalArcticArmourDamageTakenPlusPctFinalFromHits), Own(Stat.BaseManaRegenerationRatePerMinute),
            Own(Stat.BaseEnergyShieldRegenerationRatePerMinute)));
        player.Walk.Cancel(now);
        player.Area.Buffs.Set(player.Id, new(VaalArcticArmourIceRow, player.Id, skill,
            Values: [unchecked((uint)state.HitDamageChange)], U68: Buff.Seconds(seconds)));
        MarkPlayerStats(player, PlayerStatStep.Sources);
    }

    static void EndVaalArcticArmour(Player player)
    {
        if (!VaalArcticArmours.TryGetValue(player, out var state)) return;
        lock (state)
        {
            if (!VaalArcticArmours.TryGetValue(player, out var current) || !ReferenceEquals(current, state)) return;
            VaalArcticArmours.Remove(player);
            state.Area.Buffs.Clear(player.Id, VaalArcticArmourIceRow, player.Id);
            state.Area.Buffs.Clear(player.Id, VaalArcticArmourRecoveryRow, player.Id);
        }
        MarkPlayerStats(player, PlayerStatStep.Sources);
    }

    internal static void EndTimedSkillBuffs(Player player)
    {
        EndVaalArcticArmour(player);
        ClearConvergence(player);
        ClearRetributionConflux(player);
    }

    internal sealed class TimedSkillBuffCleanup : IDisposable
    {
        public void Dispose()
        {
            foreach (var (player, _) in VaalArcticArmours) EndVaalArcticArmour(player);
            foreach (var (player, _) in ConvergenceWindows) ClearConvergence(player);
            foreach (var (player, _) in RetributionConflux) ClearRetributionConflux(player);
        }
    }

    public static Task VaalArcticArmourTick(Player player)
    {
        if (!VaalArcticArmours.TryGetValue(player, out var state)) return Task.CompletedTask;
        double now = AreaTime(player);
        bool end = false, presented = false;
        lock (state)
        {
            if (!VaalArcticArmours.TryGetValue(player, out var current) || !ReferenceEquals(current, state))
                return Task.CompletedTask;
            if (!VaalArcticArmourCurrent(player, state, now)) end = true;
            else if (!state.RecoveryPresented && now >= state.CastFinishes)
            {
                state.RecoveryPresented = true;
                presented = true;
                state.Area.Buffs.Set(player.Id, new(VaalArcticArmourRecoveryRow, player.Id, state.Skill,
                    Values: [(uint)state.ManaRegen, (uint)state.ShieldRegen], U68: Buff.Seconds(state.Until - now)));
            }
        }
        if (end) EndVaalArcticArmour(player);
        else if (presented) MarkPlayerStats(player, PlayerStatStep.Sources);
        return Task.CompletedTask;
    }

    static Dictionary<uint, int> WithVaalArcticArmour(Player player, Dictionary<uint, int> stats)
    {
        double now = AreaTime(player);
        if (!VaalArcticArmours.TryGetValue(player, out var state) || !VaalArcticArmourCurrent(player, state, now))
            return stats;
        StatsChangeIn((now < state.CastFinishes ? state.CastFinishes : state.Until) - now);
        stats[Stat.IsFrozen] = 1;
        stats[Stat.YouCountAsFrozen] = 1;
        if (now < state.CastFinishes) return stats;
        Add(Stat.BaseManaRegenerationRatePerMinute, state.ManaRegen);
        Add(Stat.BaseEnergyShieldRegenerationRatePerMinute, state.ShieldRegen);
        return stats;

        void Add(uint key, int amount) => stats[key] = ActorScale.Saturate((long)stats.GetValueOrDefault(key) + amount);
    }

    static (int Physical, int Other, int Chaos) VaalArcticArmourHit(Player player,
        int physical, int other, int chaos, double at)
    {
        if (((long)physical + other + chaos) <= 0 || !VaalArcticArmours.TryGetValue(player, out var state))
            return (physical, other, chaos);
        long percent;
        bool exhausted;
        lock (state)
        {
            if (!VaalArcticArmours.TryGetValue(player, out var current) || !ReferenceEquals(current, state)
                || !VaalArcticArmourCurrent(player, state, at))
                return (physical, other, chaos);
            percent = Math.Max(0, 100L + state.HitDamageChange);
            exhausted = --state.Hits == 0;
        }
        if (exhausted) EndVaalArcticArmour(player);
        int Kept(int value) => (int)Math.Clamp(value * percent / 100, 0, int.MaxValue);
        return (Kept(physical), Kept(other), Kept(chaos));
    }
}