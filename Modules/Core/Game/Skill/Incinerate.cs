using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

public static partial class Game
{
    const ushort IncinerateStagesBuff = 228;
    const int IncinerateBaseLength = 25;
    const double IncinerateBaseHalfAngle = 15;
    const double IncinerateFirstStageFraction = .5825;
    static readonly uint GrantedIncinerateReleaseHit = Stat.Of("grant_expanding_fire_cone_release_hit_damage_+%_final");

    sealed class IncinerateChannel(SupportedGem source, int action, int epoch, int interruption, int maximum,
        int initialStages, double started)
    {
        public readonly SupportedGem Source = source;
        public readonly int Action = action, Epoch = epoch, Interruption = interruption, Maximum = maximum;
        public readonly int InitialStages = initialStages;
        public readonly double Started = started;
        public int Stages;
    }

    readonly record struct IncinerateWave(double Speed, double IgniteMore, bool AlwaysIgnites);

    static bool IsIncinerate(string? effect) => effect is "Incinerate" or "IncinerateAltX" or "IncinerateAltY" or "IncinerateRoyale";

    static IncinerateChannel BeginIncinerate(Player player, SupportedGem source, int action)
    {
        var stats = CharacterPlayerStats(player);
        int Get(uint key) => source.Stats.GetValueOrDefault(key) + stats.GetValueOrDefault(key);
        int maximum = Get(Stat.ExpandingFireConeMaximumNumberOfStages) + Get(Stat.ExpandingFireConeAdditionalMaximumNumberOfStages);
        int extra = Get(Stat.IncinerateStartsWithXAdditionalStages);
        return new(source, action, player.ProjectileEpoch, player.ActionInterruptionGeneration,
            Math.Clamp(maximum, 1, 255), Math.Clamp(extra, 0, 254), AreaTime(player));
    }

    static void PresentIncinerateStages(Player player, ushort skill, int stages)
    {
        if (player.Closed || player.LeavingArea) return;
        if (stages == 0) player.Area.Buffs.Clear(player.Id, IncinerateStagesBuff, player.Id);
        else player.Area.Buffs.Set(player.Id, new(IncinerateStagesBuff, player.Id, skill, Stacks: (ushort)stages, KeepInstance: true));
    }

    static void RetireIncinerateLocked(Player player, int generation)
    {
        if (!AimedPulseChannels.TryGetValue(player, out var channel) || channel.Generation != generation
            || channel.Incinerate is not { } cast) return;
        AimedPulseChannels.Remove(player);
        if (cast.Stages != 0) PresentIncinerateStages(player, channel.Skill, 0);
    }

    static int IncinerateLength(Player player, SupportedGem source, int stage, double factor = 1)
    {
        var stats = SkillAreaStats(player, source.Stats, source.Effect);
        int Get(uint key) => stats.GetValueOrDefault(key);
        int added = Math.Min(stage * Get(Stat.ExpandingFireConeRadiusPlusPerStage), Get(Stat.ExpandingFireConeRadiusLimit));
        return SkillArea.Radius((int)Math.Round((IncinerateBaseLength + added) * factor), Get, spell: true);
    }

    static bool IncinerateContact(Player player, (int X, int Y) origin, (double X, double Y) direction,
        uint id, (int X, int Y) at, double halfAngle, int length) =>
        GroundSlamWave.Covers(direction, origin, at, halfAngle, length)
        && (player.Layout.Sight is not { } sight || !ClientPathfinder.LineBelow(origin, at, sight, 1))
        && player.Area.Monsters.TryGetValue(id, out var monster) && !monster.WorldHidden;

    static double IncinerateMore(IncinerateChannel cast, int stage, double now, double pact)
    {
        var stats = cast.Source.Stats;
        double focus = FocusedChannelMore(now - cast.Started,
            stats.GetValueOrDefault(Stat.SupportFocusChannelDamagePlusPctFinalPerSecondChannellingUpTo60Pct));
        return pact * focus * Math.Max(0, 1 + stage * stats.GetValueOrDefault(Stat.FlamethrowerDamagePlusPctPerStageFinal) / 100d);
    }

    static Task PulseIncinerate(Player player, AimedPulseChannel channel, IncinerateChannel cast,
        (ushort Id, int Effect, string Name, int Level, int Quality, uint Gem) gem, double pact)
    {
        (int X, int Y) origin;
        (double X, double Y) direction;
        int stage;
        double now;
        lock (LevelUpdatesFor(player).Gate)
        {
            now = AreaTime(player);
            if (!ChannelCurrent(player, channel.Skill, cast.Action, channel.Generation, now)
                || cast.Interruption != player.ActionInterruptionGeneration || PlayerActionBlocked(player, now, now))
                return Task.CompletedTask;
            stage = Math.Min(cast.Maximum, cast.Stages == 0 ? 1 + cast.InitialStages : cast.Stages + 1);
            if (cast.Stages != stage)
            {
                cast.Stages = stage;
                PresentIncinerateStages(player, channel.Skill, stage);
            }
            origin = player.Walk.Position(now);
            direction = NormalizedIncinerateDirection(channel, origin);
        }

        int length = IncinerateLength(player, cast.Source, stage);
        var own = cast.Source.Stats;
        double halfAngle = IncinerateBaseHalfAngle
            * (1 + Math.Clamp(stage * own.GetValueOrDefault(Stat.ExpandingFireConeAnglePlusPctPerStage), 0, 300) / 100d);
        return AreaSkillHitForUse(null, player, channel.Skill, gem, origin, origin, immediate: true,
            sequence: player.ChannelSkillSequence, actionToken: cast.Action, channelGeneration: channel.Generation,
            radiusOverride: length, hitAndAilmentMore: IncinerateMore(cast, stage, now, pact), nearbyOnly: true,
            targetFilter: (id, at) => IncinerateContact(player, origin, direction, id, at, halfAngle, length));
    }

    static (double X, double Y) NormalizedIncinerateDirection(AimedPulseChannel channel, (int X, int Y) origin)
    {
        var direction = channel.DirectionFrom(origin);
        double length = Math.Sqrt((double)direction.X * direction.X + (double)direction.Y * direction.Y);
        return (direction.X / length, direction.Y / length);
    }

    static void ReleaseIncinerateLocked(Player player, double now, LyciaChannel? lycia)
    {
        if (ActiveAimedPulseChannel(player) is not { Incinerate: { } cast } channel || cast.Stages == 0) return;
        int stage = cast.Stages;
        cast.Stages = 0;
        PresentIncinerateStages(player, channel.Skill, 0);
        if (cast.Action != player.ActionCount || cast.Interruption != player.ActionInterruptionGeneration
            || PlayerActionBlocked(player, now, now)) return;
        if (cast.Epoch != player.ProjectileEpoch) return;
        var stats = cast.Source.Stats;
        bool ignites = stats.GetValueOrDefault(Stat.ExpandingFireConeFinalWaveAlwaysIgnite) != 0;
        int hitMore = stats.GetValueOrDefault(Stat.ExpandingFireConeReleaseHitDamagePlusPctFinal,
            stats.GetValueOrDefault(GrantedIncinerateReleaseHit));
        int igniteMore = stats.GetValueOrDefault(Stat.ExpandingFireConeReleaseIgniteDamagePlusPctFinal,
            stats.GetValueOrDefault(Stat.GrantExpandingFireConeReleaseIgniteDamagePlusPctFinal));
        if (!ignites && hitMore == 0 && igniteMore == 0) return;

        var origin = player.Walk.Position(now);
        var direction = NormalizedIncinerateDirection(channel, origin);
        ushort sequence = player.ChannelSkillSequence;
        double pact = lycia?.Generation == channel.Generation ? lycia.Pact.DamageMore : 1;
        _ = SendIncinerateWave(player, channel.Skill, cast, stage, origin, direction, sequence, now, pact, hitMore, igniteMore, ignites);
    }

    static async Task SendIncinerateWave(Player player, ushort skill, IncinerateChannel cast, int stage,
        (int X, int Y) origin, (double X, double Y) direction, ushort sequence, double now, double pact,
        int hitMore, int igniteMore, bool ignites)
    {
        bool Current() => CurrentGameWork && !player.Closed && !player.LeavingArea && player.PlayerLife > 0
            && player.ProjectileEpoch == cast.Epoch;
        try
        {
            await AreaDelay(player, .001);
            if (!Current()) return;
            var gem = GemSkillEffects(player).FirstOrDefault(g => g.Id == skill);
            if (gem.Name is null || gem.Effect != cast.Source.Effect
                || GemSkillInstanceDisabled(player, gem.Effect, skill) is not null
                || !SameTriggerSource(cast.Source, SupportedSkill(player, skill))) return;
            bool expanse = SkillsFor(player).EffectId(gem.Effect) == "IncinerateAltX";
            int length = IncinerateLength(player, cast.Source, stage, expanse ? 2 : 1.4);
            var wave = new IncinerateWave(expanse ? 60 : 220, Math.Max(0, 1 + igniteMore / 100d), ignites);

            await AreaSkillHitForUse(null, player, skill, gem, origin, origin, immediate: true, sequence: sequence,
                triggeredCurrent: Current, radiusOverride: length, nearbyOnly: true,
                hitAndAilmentMore: IncinerateMore(cast, stage, now, pact), spellMore: Math.Max(0, 1 + hitMore / 100d),
                targetFilter: (id, at) => IncinerateContact(player, origin, direction, id, at, 90, length),
                incinerateWave: wave);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException) { }
    }
}
