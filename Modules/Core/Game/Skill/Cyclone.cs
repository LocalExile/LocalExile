using GameServer.Core.Protocol.Instance;

public static partial class Game
{
    const ushort CycloneStagesBuffRow = 1305;
    static readonly RuleTable<Player, CycloneStageState> CycloneStages = new();

    sealed class CycloneStageState(Player player, ushort skill, SupportedGem gem, int action, int generation, double at)
    {
        public readonly GameArea Area = player.Area;
        public readonly int Epoch = player.ProjectileEpoch;
        public readonly ushort Skill = skill;
        public readonly object? Source = gem.SourceGem is { } item ? PlayerInventories.GemIdentity(item) : null;
        public readonly int Action = action;
        public readonly int Generation = generation;
        public readonly int Maximum = gem.Stats.GetValueOrDefault(Stat.CycloneMaxNumberOfStages);
        public readonly double GainSeconds = gem.Stats.GetValueOrDefault(Stat.CycloneGainStageEveryXMsWhileChannelling) / 1000.0;
        public readonly double DecaySeconds = gem.Stats.GetValueOrDefault(Stat.CycloneStageDecayTimeMs) / 1000.0;
        public readonly int MovementPerStage = gem.Stats.GetValueOrDefault(Stat.CycloneMovementSpeedPlusPctFinalPerStage);
        public readonly int BaseMovement = gem.Stats.GetValueOrDefault(Stat.CycloneMovementSpeedPlusPctFinal);
        public int Stages;
        public bool Held = true;
        public double LastTransition = at;

        public bool InArea(Player player) => player.Area == Area && player.ProjectileEpoch == Epoch;

        public bool MatchesSource(SupportedGem gem) =>
            gem.SourceGem is { } item && ReferenceEquals(Source, PlayerInventories.GemIdentity(item));
    }

    // The base reach of the weapon does not scale Cyclone's area. The local flat range mod is
    // included in WeaponFor's range; the character and the supported skill add the other bonuses.
    static int CycloneAreaFromRange(Player player, IReadOnlyDictionary<uint, int> own)
    {
        int perCell = own.GetValueOrDefault(Stat.CycloneAreaOfEffectPlusPctPerAdditionalMeleeRange);
        if (perCell == 0) return 0;
        var weapon = WeaponFor(player);
        int added = MeleeSkillReach(player, weapon, -1, own) - weapon.Range
            + (weapon.LocalSources?.GetValueOrDefault(Stat.LocalWeaponRangePlus) ?? 0);
        return ActorScale.Saturate((long)Math.Max(0, added) * perCell);
    }

    static int CycloneStageCount(Player player, ushort skill) =>
        CycloneStages.TryGetValue(player, out var state) && state.InArea(player)
            && state.Skill == skill && player.PlayerLife > 0
            ? state.Stages : 0;

    internal static int CycloneHitRadius(Player player, ushort skill, int effect, IReadOnlyDictionary<uint, int> own)
    {
        var area = SkillAreaStats(player, own, effect);
        int Get(uint key) => area.GetValueOrDefault(key);
        int radius = SkillArea.BaseRadius("CycloneAltX", Get)
            + Get(Stat.CycloneMeleeWeaponRangePlusPerStage) * CycloneStageCount(player, skill);
        return SkillArea.Radius(radius, Get, spell: false);
    }

    internal static void BeginCycloneStages(Player player, ushort skill, SupportedGem gem, int action, int generation, double at)
    {
        if (gem.Stats.GetValueOrDefault(Stat.CycloneMaxNumberOfStages) <= 0) return;
        lock (LevelUpdatesFor(player).Gate)
        {
            var state = new CycloneStageState(player, skill, gem, action, generation, at);
            if (state.GainSeconds <= 0 || state.DecaySeconds <= 0) return;
            if (CycloneStages.TryGetValue(player, out var previous) && previous.InArea(player))
            {
                state.Stages = previous.MatchesSource(gem) ? Math.Min(state.Maximum, previous.Stages) : 0;
                if (state.Stages != previous.Stages) PublishCycloneStages(player, state.Stages);
            }
            CycloneStages.AddOrUpdate(player, state);
            player.ChannelMovement = (state.Skill, CycloneMovementPercent(state));
            if (state.Stages < state.Maximum) ScheduleCycloneStep(player, state, held: true);
        }
    }

    static void EndCycloneStages(Player player, int generation, double at)
    {
        lock (LevelUpdatesFor(player).Gate)
        {
            if (!CycloneStages.TryGetValue(player, out var state) || !state.Held || state.Generation != generation) return;
            if (!state.InArea(player) || player.PlayerLife == 0 || player.LeavingArea || player.Closed
                || !state.MatchesSource(SupportedSkill(player, state.Skill)))
            {
                ClearCycloneStages(player);
                return;
            }
            state.Held = false;
            state.LastTransition = Math.Max(at, AreaTime(player));
            if (player.ChannelMovement.Skill == state.Skill)
            {
                player.ChannelMovement = default;
                player.ApplyMovementSpeedAt(state.LastTransition);
            }
            if (state.Stages > 0) ScheduleCycloneStep(player, state, held: false);
            else CycloneStages.Remove(player);
        }
    }

    static void ScheduleCycloneStep(Player player, CycloneStageState state, bool held)
    {
        double due = state.LastTransition + (held ? state.GainSeconds : state.DecaySeconds);
        AfterSeconds(player, due - AreaTime(player), () =>
        {
            using var mutation = ProjectileMutation(player);
            if (!CycloneStages.TryGetValue(player, out var current) || current != state || state.Held != held) return;
            if (!state.InArea(player) || player.PlayerLife == 0 || player.LeavingArea
                || !state.MatchesSource(SupportedSkill(player, state.Skill)))
            {
                ClearCycloneStages(player);
                return;
            }
            if (held && !ChannelCurrent(player, state.Skill, state.Action, state.Generation))
            {
                EndCycloneStages(player, state.Generation, AreaTime(player));
                return;
            }
            state.LastTransition = due;
            state.Stages += held ? 1 : -1;
            PublishCycloneStages(player, state.Stages);
            if (held) ApplyCycloneMovement(player, state, AreaTime(player));
            if (held ? state.Stages < state.Maximum : state.Stages > 0)
                ScheduleCycloneStep(player, state, held);
            else if (!held)
                CycloneStages.Remove(player);
        });
    }

    static void ApplyCycloneMovement(Player player, CycloneStageState state, double at)
    {
        int percent = CycloneMovementPercent(state);
        if (player.ChannelMovement == (state.Skill, percent)) return;
        player.ChannelMovement = (state.Skill, percent);
        player.ApplyMovementSpeedAt(at);
    }

    static int CycloneMovementPercent(CycloneStageState state) =>
        ActorScale.Saturate((long)state.BaseMovement + (long)state.MovementPerStage * state.Stages);

    static void PublishCycloneStages(Player player, int count)
    {
        player.Area.Stats.Set(player.Id, Stat.CycloneCurrentNumberOfStages, count);
        if (count > 0)
            player.Area.Buffs.Set(player.Id, new(CycloneStagesBuffRow, player.Id, Stacks: (ushort)count, KeepInstance: true));
        else
            player.Area.Buffs.Clear(player.Id, CycloneStagesBuffRow, player.Id);
    }

    static void ClearCycloneStages(Player player)
    {
        lock (LevelUpdatesFor(player).Gate)
        {
            if (!CycloneStages.TryGetValue(player, out var state)) return;
            CycloneStages.Remove(player);
            if (!state.InArea(player) || player.LeavingArea || player.Closed) return;
            if (state.Stages != 0) PublishCycloneStages(player, 0);
            if (state.Held && player.ChannelMovement.Skill == state.Skill)
            {
                player.ChannelMovement = default;
                player.ApplyMovementSpeedAt(AreaTime(player));
            }
        }
    }
}
