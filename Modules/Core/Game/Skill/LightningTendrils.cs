public static partial class Game
{
    sealed class TendrilsChannel(ushort skill, int generation, (int X, int Y) aim)
    {
        public readonly ushort Skill = skill;
        public readonly int Generation = generation;
        public (int X, int Y) Aim = aim;
        public (long X, long Y) Direction = (1, 0);
        public int Pulses;
    }

    static readonly RuleTable<Player, TendrilsChannel> TendrilsChannels = new();

    static bool IsLightningTendrils(string? effect) => effect is
        "LightningTendrils" or "LightningTendrilsAltX" or "LightningTendrilsAltY" or "LightningTendrilsRoyale";

    static TendrilsChannel? ActiveTendrilsChannel(Player player) =>
        TendrilsChannels.TryGetValue(player, out var channel) && channel.Skill == player.ChannelSkill
            && channel.Generation == player.ChannelGeneration ? channel : null;

    static bool AimLightningTendrils(Player player, (int X, int Y) aim)
    {
        if (ActiveTendrilsChannel(player) is not { } channel || channel.Aim == aim) return false;
        channel.Aim = aim;
        return true;
    }

    static Task PulseLightningTendrils(Player player, TendrilsChannel channel,
        (ushort Id, int Effect, string Name, int Level, int Quality, uint Gem) gem,
        int action, double elapsed, double pact)
    {
        (int X, int Y) origin;
        (long X, long Y) direction;
        int pulse;
        lock (LevelUpdatesFor(player).Gate)
        {
            double now = AreaTime(player);
            if (!ChannelCurrent(player, channel.Skill, action, channel.Generation, now)
                || PlayerActionBlocked(player, now, now)) return Task.CompletedTask;
            origin = player.Walk.Position(now);
            var aim = ((long)channel.Aim.X - origin.X, (long)channel.Aim.Y - origin.Y);
            if (aim != (0, 0)) channel.Direction = aim;
            direction = channel.Direction;
            pulse = ++channel.Pulses;
        }

        var own = SupportedSkill(player, channel.Skill).Stats;
        int interval = own.GetValueOrDefault(Stat.LightningTendrilsChannelledLargerPulseInterval);
        bool strong = interval > 0 && pulse % interval == 0;
        int radiusPerSecond = own.GetValueOrDefault(Stat.LightningTendrilsChannelledBaseRadiusPlusPerSecondWhileChannelling);
        int addedRadius = (int)(Math.Clamp(elapsed, 0, 2) * radiusPerSecond);
        int areaMore = 0;
        double strongMore = 1;
        if (strong)
        {
            addedRadius += own.GetValueOrDefault(Stat.LightningTendrilsChannelledLargerPulseRadiusPlus);
            areaMore = own.GetValueOrDefault(Stat.LightningTendrilsChannelledLargerPulseAreaOfEffectPlusPctFinal);
            strongMore += own.GetValueOrDefault(Stat.LightningTendrilsChannelledLargerPulseDamagePlusPctFinal) / 100d;
        }

        string? effect = SkillsFor(player).EffectId(gem.Effect);
        var area = SkillAreaStats(player, own, gem.Effect);
        int baseRadius = SkillArea.BaseRadius(effect, area.GetValueOrDefault);
        int radius = SkillArea.Radius(baseRadius + addedRadius, area.GetValueOrDefault, spell: true,
            areaMore: Math.Max(0, 1 + areaMore / 100d));
        double focus = FocusedChannelMore(elapsed, own.GetValueOrDefault(Stat.SupportFocusChannelDamagePlusPctFinalPerSecondChannellingUpTo60Pct));
        bool critical = strong && own.GetValueOrDefault(Stat.LightningTendrilsChannelledLargerPulseAlwaysCrit) != 0;

        return AreaSkillHitForUse(null, player, channel.Skill, gem, origin, origin, immediate: true,
            sequence: player.ChannelSkillSequence, actionToken: action, channelGeneration: channel.Generation,
            hitAndAilmentMore: pact * focus * Math.Max(0, strongMore), radiusOverride: radius, nearbyOnly: true,
            targetFilter: (_, at) => ((long)at.X - origin.X) * direction.X + ((long)at.Y - origin.Y) * direction.Y >= 0,
            forceCritical: critical);
    }
}
