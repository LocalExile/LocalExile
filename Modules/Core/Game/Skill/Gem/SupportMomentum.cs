using GameServer.Core.World;

public static partial class Game
{
    const ushort SwiftnessBuffRow = 2221; // support_momentum_buff is a non-recovery row. Its movement stat goes in the player stats.
    static readonly Store<Player, MomentumState> Momenta = new();

    readonly record struct MomentumGain(int Maximum, int MovementPerStack, double Duration, double Interval);
    sealed record MomentumChannel(ushort Skill, int Action, int Generation, MomentumGain Gain, double Next);

    sealed class MomentumState(Player player, double now)
    {
        readonly PlayerWalk walk = player.Walk;
        readonly int epoch = player.ProjectileEpoch;
        (double Moving, int Teleports) travel = player.Walk.ObserveTravel(now);
        readonly Dictionary<ushort, (SupportedGem Base, int Stacks, SupportedGem Projected)> projected = [];
        public int Stacks;
        public int Movement;
        public double Until;
        public int ShownStacks;
        public int ShownMovement;
        public double ShownUntil;
        public MomentumChannel? Channel;
        public bool SkillRefreshDeferred;

        public bool Current(Player owner) => ReferenceEquals(walk, owner.Walk) && epoch == owner.ProjectileEpoch
            && !owner.Closed && !owner.LeavingArea && owner.PlayerLife > 0;

        public void Gain(int count, MomentumGain gain, double at)
        {
            if (count <= 0) return;
            Stacks = Math.Min(gain.Maximum, Stacks + count);
            if (Stacks < gain.Maximum) return;
            Movement = Stacks * gain.MovementPerStack;
            Until = at + gain.Duration;
            Stacks = 0;
        }

        public void Refresh(Player owner, double now)
        {
            var observed = walk.ObserveTravel(now);
            bool moved = observed != travel || walk.Moving(now);
            travel = observed;
            if (moved) Stacks = 0;
            if (Channel is not { } channel) return;
            if (!ChannelCurrent(owner, channel.Skill, channel.Action, channel.Generation))
            {
                Channel = null;
                return;
            }
            if (owner.Paused) return;
            if (moved || PlayerActionBlocked(owner, now, now))
            {
                Channel = channel with { Next = now + channel.Gain.Interval };
                return;
            }
            if (now < channel.Next) return;

            double next = channel.Next;
            while (next <= now)
            {
                Gain(1, channel.Gain, next);
                next += channel.Gain.Interval;
            }
            Channel = channel with { Next = next };
        }

        public SupportedGem Project(ushort id, SupportedGem gem, int perStack, int stacks)
        {
            if (projected.TryGetValue(id, out var previous)
                && ReferenceEquals(previous.Base, gem) && previous.Stacks == stacks) return previous.Projected;
            var own = new Dictionary<uint, int>(gem.Stats);
            own[Stat.AttackSpeedPlusPct] = ActorScale.Saturate((long)own.GetValueOrDefault(Stat.AttackSpeedPlusPct) + (long)perStack * stacks);
            var next = gem with { Stats = own };
            projected[id] = (gem, stacks, next);
            return next;
        }
    }

    static void GainMomentumOnUse(Player player, ushort skill, SupportedGem gem, int action, int? generation, double now,
        bool movedDuringUse = false)
    {
        if (!CurrentGameWork || player.PlayerLife == 0 || player.LeavingArea) return;
        int Get(uint key) => gem.Stats.GetValueOrDefault(key);
        int maximum = Math.Clamp(Get(Stat.SupportMomentumMaxStacks), 1, 100);
        var playerStats = CharacterPlayerStats(player);
        double duration = SkillDurationModifiers.Seconds(Get(Stat.SupportMomentumBaseBuffDurationMs),
            key => playerStats.GetValueOrDefault(key), Get,
            DurationKinds(player, gem.Effect) | SkillDurationModifiers.Kinds.Buff);
        var gain = new MomentumGain(maximum, Math.Clamp(Get(Stat.SupportMomentumMovementSpeedPlusPctPerStackRemoved), 0, 10000),
            duration, Get(Stat.SupportMomentumStackWhileChannellingBaseMs) / 1000d);
        if (!Momenta.TryGetValue(player, out var state) || !state.Current(player))
        {
            state = new(player, now);
            Momenta.AddOrUpdate(player, state);
        }
        state.Refresh(player, now);
        if (movedDuringUse) state.Stacks = 0;
        if (generation is int epoch && gain.Interval > 0)
            state.Channel = new(skill, action, epoch, gain, now + gain.Interval);
        else if (generation is null && !movedDuringUse && !player.Walk.Moving(now))
            state.Gain(Math.Clamp(Get(Stat.CountAsMomentumPlusIfNotChannelled), 0, maximum), gain, now);
        PresentMomentum(player, state, now);
    }

    static SupportedGem ApplyCurrentMomentum(Player player, ushort id, SupportedGem gem)
    {
        int perStack = gem.Stats.GetValueOrDefault(Stat.SupportMomentumAttackSpeedPlusPctPerStack);
        if (perStack == 0 || !Momenta.TryGetValue(player, out var state) || !state.Current(player)) return gem;
        state.Refresh(player, AreaTime(player));
        int stacks = Math.Min(state.Stacks, gem.Stats.GetValueOrDefault(Stat.SupportMomentumMaxStacks));
        return stacks > 0 ? state.Project(id, gem, perStack, stacks) : gem;
    }

    static void AddMomentumStats(Player player, Dictionary<uint, int> stats)
    {
        if (!Momenta.TryGetValue(player, out var state) || !state.Current(player)) return;
        double now = AreaTime(player);
        state.Refresh(player, now);
        if (state.Stacks > 0)
        {
            stats[Stat.BaseCurrentMomentum] = state.Stacks;
            stats[Stat.CurrentMomentum] = state.Stacks;
        }
        if (state.Until <= now) return;
        stats[Stat.BaseMovementVelocityPlusPct] = ActorScale.Saturate((long)stats.GetValueOrDefault(Stat.BaseMovementVelocityPlusPct) + state.Movement);
        StatsChangeAt(state.Until, () => AreaTime(player));
    }

    // A value-only Actor skill-list refresh replaces the skill that the Lockstep client is channelling.
    // Keep resolving paid ticks at their current speed and publish the skill records when that use ends.
    static bool DeferMomentumChannelSkills(Player player)
    {
        if (!player.Connection.LockstepClient || !Momenta.TryGetValue(player, out var state)
            || !state.Current(player) || state.Channel is not { } channel
            || !ChannelCurrent(player, channel.Skill, channel.Action, channel.Generation)) return false;
        state.SkillRefreshDeferred = true;
        return true;
    }

    public static void CheckMomentumPresentation(Player player, double now)
    {
        if (!Momenta.TryGetValue(player, out var state)) return;
        if (!state.Current(player)) { ClearMomentum(player); return; }
        state.Refresh(player, now);
        if (state.Channel is null && state.SkillRefreshDeferred)
        {
            state.SkillRefreshDeferred = false;
            if (!PublishByFrame(player, PlayerStatChange.Sources)) PresentStats(player);
        }
        PresentMomentum(player, state, now);
    }

    static void PresentMomentum(Player player, MomentumState state, double now)
    {
        double until = state.Until > now ? state.Until : 0;
        int movement = until > 0 ? state.Movement : 0;
        bool movementChanged = state.ShownUntil != until || state.ShownMovement != movement;
        if (movementChanged)
        {
            if (until > 0)
                player.Area.Buffs.Set(player.Id, new(SwiftnessBuffRow, player.Id,
                    U68: Buff.Seconds(until - now)));
            else
                player.Area.Buffs.Clear(player.Id, SwiftnessBuffRow, player.Id);
            state.ShownUntil = until;
            state.ShownMovement = movement;
        }
        if (state.ShownStacks != state.Stacks || movementChanged)
        {
            state.ShownStacks = state.Stacks;
            if (movementChanged) RebuildPools(player);
            if (!PublishByFrame(player, PlayerStatChange.Sources)) PresentStats(player);
        }
    }

    public static void ClearMomentum(Player player)
    {
        if (!Momenta.TryGetValue(player, out var state)) return;
        Momenta.Remove(player);
        player.Area.Buffs.Clear(player.Id, SwiftnessBuffRow, player.Id);
        if (state.ShownUntil > 0 || state.Until > 0 || state.Movement != 0) RebuildPools(player);
        if (!PublishByFrame(player, PlayerStatChange.Sources)) PresentStats(player);
    }
}
