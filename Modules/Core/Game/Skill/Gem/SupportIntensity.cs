using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

public static partial class Game
{
    // This state owns the Intensity granted by the linked supports.
    // BuffDefinitions 1279 is spell_boost_charge (Intensity).
    const ushort IntensityBuffRow = 1279;
    static readonly Store<Player, IntensityState> Intensities = new();
    static readonly AsyncLocal<IntensityCast?> IntensityInUse = new();

    sealed class IntensityCast(Player owner, ushort skill, SupportedGem gem, int stacks)
    {
        int gained;
        public readonly int Stacks = stacks;
        public bool IsFor(Player player, ushort id) => ReferenceEquals(owner, player) && skill == id;
        public void Complete()
        {
            if (Interlocked.Exchange(ref gained, 1) != 0) return;
            GainIntensity(owner, gem);
        }
    }

    sealed class IntensityScope : IDisposable
    {
        readonly IntensityCast? previous = IntensityInUse.Value;
        public IntensityScope(IntensityCast cast) => IntensityInUse.Value = cast;
        public void Dispose() => IntensityInUse.Value = previous;
    }

    static IntensityCast? CaptureIntensityCast(Player player, ushort skill, SupportedGem? gem)
    {
        if (gem is null) return null;
        int stacks = 0;
        if (Intensities.TryGetValue(player, out var state))
        {
            state.Refresh(player, AreaTime(player));
            stacks = Math.Min(state.Stacks, gem.Stats[Stat.VirtualMaximumIntensity]);
        }
        return new(player, skill, gem, stacks);
    }

    static IntensityCast? CastingIntensity(Player player, ushort skill) =>
        IntensityInUse.Value is { } use && use.IsFor(player, skill) ? use : null;

    sealed class IntensityState(PlayerWalk walk, int areaEpoch, double now)
    {
        readonly PlayerWalk owner = walk;
        readonly int epoch = areaEpoch;
        (double Moving, int Teleports) observed = walk.ObserveTravel(now);
        readonly Dictionary<ushort, (SupportedGem Base, int Stacks, SupportedGem Projected)> projections = [];
        public int Stacks { get; private set; }
        public int Presented { get; set; }
        public int IntervalMs { get; set; }

        public bool Current(Player player) => ReferenceEquals(owner, player.Walk) && epoch == player.ProjectileEpoch;

        public void Refresh(Player player, double now)
        {
            if (Stacks == 0) return;
            if (!Current(player) || player.PlayerLife == 0)
            {
                Stacks = 0;
                projections.Clear();
                return;
            }

            var travel = owner.ObserveTravel(now);
            if (observed.Teleports != travel.Teleports)
            {
                Stacks = 0;
                observed = travel;
                projections.Clear();
                return;
            }

            double interval = IntervalMs / 1000d;
            int lost = (int)Math.Clamp(Math.Floor((travel.Moving - observed.Moving + 1e-9) / interval), 0, Stacks);
            Stacks -= lost;
            observed.Moving += lost * interval;
        }

        public void Gain(double now, int limit)
        {
            Stacks = Math.Min(Stacks + 1, limit);
            observed = owner.ObserveTravel(now);
        }

        public SupportedGem Project(ushort id, SupportedGem gem, int stacks)
        {
            if (projections.TryGetValue(id, out var previous)
                && ReferenceEquals(previous.Base, gem) && previous.Stacks == stacks)
                return previous.Projected;

            var own = new Dictionary<uint, int>(gem.Stats);
            Add(Stat.SupportSpellBoostAreaOfEffectPlusPctFinal, Stat.SupportSpellBoostAreaOfEffectPlusPctFinalPerCharge);
            Add(Stat.AreaDamagePlusPctFinalFromSpellBoostCharges, Stat.SupportSpellBoostAreaDamagePlusPctFinalPerCharge);
            Add(Stat.SupportGreaterProjectileIntensityProjectileDamagePlusPctFinalFromIntensity,
                Stat.GreaterProjectileIntensityProjectileDamagePlusPctFinalPerIntensity);
            Add(Stat.NumberOfAdditionalProjectiles, Stat.AdditionalProjectilesPerIntensity);
            var projected = gem with { Stats = own };
            projections[id] = (gem, stacks, projected);
            return projected;

            void Add(uint total, uint perStack)
            {
                int value = own.GetValueOrDefault(perStack);
                if (value != 0) own[total] = own.GetValueOrDefault(total) + value * stacks;
            }
        }
    }

    static void AddIntensityBaseStats(Player player, Dictionary<uint, int> own)
    {
        if (own.GetValueOrDefault(Stat.SupportSpellBoostAreaOfEffectPlusPctFinalPerCharge) == 0
            && own.GetValueOrDefault(Stat.AdditionalProjectilesPerIntensity) == 0) return;
        own[Stat.VirtualSkillGainsIntensity] = 1;
        own[Stat.VirtualMaximumIntensity] = (int)Math.Clamp(3L + RawGemStat(player, Stat.MaximumIntensifyStacks), 1, ushort.MaxValue);
        own[Stat.VirtualIntensityLossMsWhileMovingInterval] = Math.Max(1, (int)(500 /
            Math.Max(.01, 1 + RawGemStat(player, Stat.IntensityLossFrequencyWhileMovingPlusPct) / 100d)));
    }

    static void GainIntensity(Player player, SupportedGem gem)
    {
        if (!CurrentGameWork || player.PlayerLife == 0 || gem.Stats.GetValueOrDefault(Stat.VirtualSkillGainsIntensity) == 0) return;
        double now = AreaTime(player);
        if (!Intensities.TryGetValue(player, out var state) || !state.Current(player))
        {
            state = new(player.Walk, player.ProjectileEpoch, now);
            Intensities.AddOrUpdate(player, state);
        }

        state.Refresh(player, now);
        state.IntervalMs = gem.Stats[Stat.VirtualIntensityLossMsWhileMovingInterval];
        int before = state.Stacks;
        state.Gain(now, gem.Stats[Stat.VirtualMaximumIntensity]);
        if (state.Stacks != before) PresentStats(player);
    }

    static SupportedGem ApplyCurrentIntensity(Player player, ushort skillId, SupportedGem gem)
    {
        if (gem.Stats.GetValueOrDefault(Stat.VirtualSkillGainsIntensity) == 0
            || !Intensities.TryGetValue(player, out var state)) return gem;
        int stacks;
        if (CastingIntensity(player, skillId) is { } cast) stacks = cast.Stacks;
        else
        {
            state.Refresh(player, AreaTime(player));
            stacks = Math.Min(state.Stacks, gem.Stats[Stat.VirtualMaximumIntensity]);
        }
        return stacks > 0 ? state.Project(skillId, gem, stacks) : gem;
    }

    static void CheckIntensityPresentation(Player player, double now)
    {
        if (!Intensities.TryGetValue(player, out var state)) return;
        state.Refresh(player, now);
        if (state.Stacks != state.Presented) PresentStats(player);
    }

    static void PresentIntensity(Player player, double now)
    {
        if (!Intensities.TryGetValue(player, out var state)) return;
        state.Refresh(player, now);
        int stacks = state.Stacks;
        if (stacks == state.Presented) return;
        if (stacks > 0)
            player.Area.Buffs.Set(player.Id, new(IntensityBuffRow, player.Id, Stacks: (ushort)stacks));
        else
            player.Area.Buffs.Clear(player.Id, IntensityBuffRow, player.Id);
        state.Presented = stacks;
        player.SupportGems.PendingStats = true;
    }
}
