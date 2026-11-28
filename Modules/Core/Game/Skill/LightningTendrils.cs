public static partial class Game
{
    internal sealed class AimedPulseChannel(ushort skill, int generation, (int X, int Y) aim)
    {
        public readonly ushort Skill = skill;
        public readonly int Generation = generation;
        public (int X, int Y) Aim = aim;
        public (long X, long Y) Direction = (1, 0);
        public int Pulses;
        public IncinerateChannel? Incinerate;
        public StormBurstChannel? StormBurst;

        public (long X, long Y) DirectionFrom((int X, int Y) origin)
        {
            var aim = ((long)Aim.X - origin.X, (long)Aim.Y - origin.Y);
            if (aim != (0, 0)) Direction = aim;
            return Direction;
        }

        public static bool InFront((int X, int Y) origin, (long X, long Y) direction, (int X, int Y) at) =>
            ((long)at.X - origin.X) * direction.X + ((long)at.Y - origin.Y) * direction.Y >= 0;
    }

    static readonly RuleTable<Player, AimedPulseChannel> AimedPulseChannels = new();

    static bool IsLightningTendrils(string? effect) => effect is
        "LightningTendrils" or "LightningTendrilsAltX" or "LightningTendrilsAltY" or "LightningTendrilsRoyale";

    static bool IsAimedPulseEffect(string? effect) => IsLightningTendrils(effect) || IsBlightChannel(effect)
        || IsScorchingRay(effect) || IsIncinerate(effect) || IsStormBurst(effect);

    static AimedPulseChannel? ActiveAimedPulseChannel(Player player) =>
        AimedPulseChannels.TryGetValue(player, out var channel) && channel.Skill == player.ChannelSkill
            && channel.Generation == player.ChannelGeneration ? channel : null;

    static bool AimPulseChannel(Player player, (int X, int Y) aim)
    {
        if (ActiveAimedPulseChannel(player) is not { } channel || channel.Aim == aim) return false;
        channel.Aim = aim;
        return true;
    }

    static Task PulseLightningTendrils(Player player, AimedPulseChannel channel,
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
            direction = channel.DirectionFrom(origin);
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
            targetFilter: (_, at) => AimedPulseChannel.InFront(origin, direction, at),
            forceCritical: critical);
    }
}
