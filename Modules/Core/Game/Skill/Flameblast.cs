public static partial class Game
{
    static readonly RuleTable<Player, FlameblastChannel> FlameblastChannels = new();

    sealed class FlameblastChannel(SupportedGem source, ushort skill, int action, int generation, int epoch,
        int interruption, ushort sequence, (int X, int Y) aim, double startedAt, int startingStages, bool autoDetonates)
    {
        public readonly SupportedGem Source = source;
        public readonly ushort Skill = skill, Sequence = sequence;
        public readonly int Action = action, Generation = generation, Epoch = epoch, Interruption = interruption;
        public readonly (int X, int Y) Aim = aim;
        public readonly double StartedAt = startedAt;
        public readonly int Maximum = Math.Max(1, source.Stats.GetValueOrDefault(Stat.FlameblastMaximumStages));
        public readonly bool AutoDetonates = autoDetonates;
        public int Stages, StartingStages = startingStages;
    }

    static bool IsFlameblast(string? effect) => effect is "Flameblast" or "FlameblastAltX" or "FlameblastAltY";

    static void BeginFlameblast(Player player, SupportedGem source, ushort skill, int action, int generation,
        ushort sequence, (int X, int Y) aim)
    {
        int extra = CharacterPlayerStats(player).GetValueOrDefault(Stat.FlameblastStartsWithXAdditionalStages)
            + source.Stats.GetValueOrDefault(Stat.FlameblastStartsWithXAdditionalStages);
        FlameblastChannels.AddOrUpdate(player, new(source, skill, action, generation, player.ProjectileEpoch,
            player.ActionInterruptionGeneration, sequence, aim, AreaTime(player), Math.Max(0, extra),
            SkillsFor(player).EffectId(source.Effect) == "FlameblastAltX"));
    }

    static bool FlameblastCurrent(Player player, FlameblastChannel channel) =>
        channel.Skill == player.ChannelSkill && channel.Generation == player.ChannelGeneration
            && channel.Action == player.ActionCount && channel.Interruption == player.ActionInterruptionGeneration;

    static async Task TickFlameblast(Player player, FlameblastChannel channel, double now, bool prepaid,
        int overheatPerThree, LyciaChannel? lycia)
    {
        uint mana = 0, life = 0;
        bool stopped;
        Task? explosion = null;
        lock (LevelUpdatesFor(player).Gate)
        {
            if (!FlameblastCurrent(player, channel)) return;
            stopped = !prepaid && !PayMana(player, channel.Skill, out mana, out life, 100, useSpell: false);
            if (stopped)
            {
                ReleaseFlameblastLocked(player, now, lycia);
                AbortStartedChannel(player, channel.Generation);
                if (player.Connection.LockstepClient) player.LockstepSkill.Clear();
            }
            else
            {
                if (!prepaid && overheatPerThree > 0)
                    OverheatConsumeLocked(player, channel.Skill, key =>
                        key == Stat.SupportOverheatAilmentDamagePlusPctFinalPer3FortificationConsumed ? overheatPerThree : 0, now);
                channel.Stages = Math.Min(channel.Maximum, channel.Stages + 1 + channel.StartingStages);
                channel.StartingStages = 0;
                if (channel.AutoDetonates && channel.Stages == channel.Maximum)
                {
                    explosion = ExplodeFlameblast(player, channel, channel.Stages, now, lycia);
                    channel.Stages = 0;
                }
            }
        }
        if (stopped)
        {
            await SendPlayerMana(player, "channel stopped");
            if (player.Connection.LockstepClient)
                await SendWorldRecord(player, player.Id, ChannelFinishedRecord(player, channel.Sequence, null),
                    $"0x01ae Flameblast 0x{channel.Sequence:x4} ended");
        }
        else if (mana > 0 || life > 0)
            await SendSkillCost(player, life, $"channel cost {mana} mana, {life} life");

        if (explosion is not null) await explosion;
    }

    static void RetireFlameblastLocked(Player player, int generation)
    {
        if (FlameblastChannels.TryGetValue(player, out var channel) && channel.Generation == generation)
            FlameblastChannels.Remove(player);
    }

    static void ReleaseFlameblastLocked(Player player, double now, LyciaChannel? lycia)
    {
        if (!FlameblastChannels.TryGetValue(player, out var channel) || channel.Generation != player.ChannelGeneration)
            return;
        FlameblastChannels.Remove(player);
        if (channel.Stages > 0 && FlameblastCurrent(player, channel) && !PlayerActionBlocked(player, now, now))
            _ = ExplodeFlameblast(player, channel, channel.Stages, now, lycia);
    }

    static async Task ExplodeFlameblast(Player player, FlameblastChannel channel, int stages, double now, LyciaChannel? lycia)
    {
        double pact = lycia?.Generation == channel.Generation ? lycia.Pact.DamageMore : 1;
        bool Current() => CurrentGameWork && PlayerStateAllowsAction(player) && player.ProjectileEpoch == channel.Epoch
            && player.ActionInterruptionGeneration == channel.Interruption;
        try
        {
            // Leave the channel mutation boundary before applying contacts or publishing hits.
            await AreaDelay(player, .001);
            if (!Current()) return;
            var gem = GemSkillEffects(player).FirstOrDefault(g => g.Id == channel.Skill);
            if (gem.Name is null || gem.Effect != channel.Source.Effect
                || GemSkillInstanceDisabled(player, gem.Effect, channel.Skill) is not null
                || !SameTriggerSource(channel.Source, SupportedSkill(player, channel.Skill))) return;

            var stats = channel.Source.Stats;
            int baseRadius = stats.GetValueOrDefault(Stat.FlameblastBaseRadiusOverride, 2);
            baseRadius = Math.Max(0, baseRadius + (stages - 1) * stats.GetValueOrDefault(Stat.VaalFlameblastRadiusPlusPerStage));
            var area = SkillAreaStats(player, stats, gem.Effect);
            int radius = SkillArea.Radius(baseRadius, area.GetValueOrDefault, spell: true);
            double hitMore = Math.Max(0, 1 + stages * stats.GetValueOrDefault(Stat.ChargedBlastSpellDamagePlusPctFinalPerStack) / 100d);
            double ailmentMore = Math.Max(0, 1 + stages * stats.GetValueOrDefault(Stat.FlameblastAilmentDamagePlusPctFinalPerStack) / 100d);
            double focus = FocusedChannelMore(now - channel.StartedAt,
                stats.GetValueOrDefault(Stat.SupportFocusChannelDamagePlusPctFinalPerSecondChannellingUpTo60Pct));
            var from = player.Walk.Position(now);
            await AreaSkillHitForUse(null, player, channel.Skill, gem, from, channel.Aim, immediate: true,
                sequence: channel.Sequence, actionToken: null, triggeredCurrent: Current, radiusOverride: radius,
                hitAndAilmentMore: pact * focus, spellMore: hitMore, ailmentMore: ailmentMore);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException) { }
    }
}
