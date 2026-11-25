using GameServer.Core.World;

public static partial class Game
{
    const ushort DoubleStrikeStagesBuffRow = 2325;
    static readonly RuleTable<Player, DoubleStrikeMomentumState> DoubleStrikeMomentum = new();

    sealed class DoubleStrikeMomentumState(Player owner, double now)
    {
        public readonly GameArea Area = owner.Area;
        public readonly PlayerWalk Walk = owner.Walk;
        public readonly int Epoch = owner.ProjectileEpoch;
        public readonly (double Moving, int Teleports) Travel = owner.Walk.ObserveTravel(now);
        public int Stages;
    }

    sealed record DoubleStrikePairs(double Start, double[] Durations, int Maximum);

    static void ShowDoubleStrikeStages(Player player, int stages)
    {
        if (stages > 0)
            player.Area.Buffs.Set(player.Id, new(DoubleStrikeStagesBuffRow, player.Id,
                Stacks: (ushort)stages, KeepInstance: true));
        else
            player.Area.Buffs.Clear(player.Id, DoubleStrikeStagesBuffRow, player.Id);
        player.PlayerSkillsCache = null;
        if (!PublishByFrame(player, PlayerStatChange.Panel)) PresentStats(player);
    }

    static DoubleStrikeMomentumState? RefreshDoubleStrikeMomentum(Player player, double now)
    {
        if (!DoubleStrikeMomentum.TryGetValue(player, out var state)) return null;
        if (state.Area != player.Area || state.Epoch != player.ProjectileEpoch || state.Walk != player.Walk
            || player.PlayerLife == 0 || player.Closed || player.LeavingArea
            || state.Travel != player.Walk.ObserveTravel(now) || player.Walk.Moving(now))
        {
            ClearDoubleStrikeMomentum(player);
            return null;
        }
        return state;
    }

    static void ClearDoubleStrikeMomentum(Player player)
    {
        if (!DoubleStrikeMomentum.TryGetValue(player, out var state)) return;
        DoubleStrikeMomentum.Remove(player);
        if (state.Stages > 0)
        {
            if (state.Area == player.Area) ShowDoubleStrikeStages(player, 0);
            else player.PlayerSkillsCache = null;
        }
    }

    static int DoubleStrikeStageCount(Player player, double now) => RefreshDoubleStrikeMomentum(player, now)?.Stages ?? 0;

    static void BeginDoubleStrikePair(Player player, int maximum, double now)
    {
        var state = RefreshDoubleStrikeMomentum(player, now) ?? DoubleStrikeMomentum.GetValue(player, owner => new(owner, now));
        int next = Math.Min(maximum, state.Stages + 1);
        if (state.Stages == next) return;
        state.Stages = next;
        ShowDoubleStrikeStages(player, next);
    }

    static DoubleStrikePairs? BeginDoubleStrikeUse(Player player, ushort skillId, SupportedGem gem,
        double swingAt, double previousDeadline)
    {
        int maximum = gem.Stats.GetValueOrDefault(Stat.DoubleStrikeMaxStages);
        if (maximum <= 0 || gem.Stats.GetValueOrDefault(Stat.DoubleStrikeAttackSpeedPlusPctFinalPerStage) == 0)
            return null;
        maximum = Math.Min(ushort.MaxValue, maximum);
        double now = AreaTime(player);
        int stages = DoubleStrikeStageCount(player, now);
        var durations = new double[1 + MeleeAttackRepeats(player, skillId)];
        for (int pair = 0; pair < durations.Length; pair++)
        {
            int paidStages = Math.Min(maximum, stages + pair + 1);
            durations[pair] = pair > 0 && paidStages == Math.Min(maximum, stages + pair) ? durations[pair - 1]
                : SupportedAttackTiming(player, skillId, null, clampToTick: true, proxy: false, doubleStrikeStages: paidStages).Seconds;
        }
        BeginDoubleStrikePair(player, maximum, now);
        ReserveVariableMeleeRepeats(player, Math.Max(swingAt, previousDeadline), durations);
        return new(swingAt, durations, maximum);
    }
}
