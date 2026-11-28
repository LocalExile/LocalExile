static partial class Game
{
    internal const string WitherEffect = "Wither";

    sealed class WitherChannel(ushort skill, int generation, (int X, int Y) aim)
    {
        public ushort Skill { get; } = skill;
        public int Generation { get; } = generation;
        public (int X, int Y) Aim = aim;
    }

    static readonly RuleTable<Player, WitherChannel> WitherChannels = new();

    static WitherChannel? ActiveWitherChannel(Player player) =>
        WitherChannels.TryGetValue(player, out var channel) && player.ChannelSkill == channel.Skill
        && player.ChannelGeneration == channel.Generation ? channel : null;

    static void WitherPulseLocked(Player player, SupportedGem skill, (int X, int Y) centre, double now,
        bool totem = false)
    {
        var caster = CharacterPlayerStats(player);
        if (totem) caster = TotemCasterStats(caster);
        int Own(uint stat) => skill.Stats.GetValueOrDefault(stat);
        int Get(uint stat) => Own(stat) + caster.GetValueOrDefault(stat);
        int slow = Math.Max(0, -Own(Stat.BaseMovementVelocityPlusPct));
        double hinderSeconds = SkillDurationModifiers.Seconds(Own(Stat.BaseSkillEffectDuration),
            caster.GetValueOrDefault, Own);
        double witherSeconds = WitherDurationSeconds(
            SkillDurationModifiers.Seconds(Own(Stat.ActiveSkillWitheredBaseDurationMs), caster.GetValueOrDefault, Own), Get);
        int radius = SkillRadius(player, SkillArea.BaseRadius(WitherEffect, Own), skill.Stats, spell: true, skill.Effect, caster);
        foreach (var (_, monster) in player.Area.MonstersNear(centre, radius))
        {
            if (!monster.Hostile || monster.Dead || monster.Life == 0 || monster.WorldHidden || monster.Invulnerable
                || ArenaHazards.Untargetable(player, monster)
                || Dist(centre, monster.Walk.Position(now)) > radius) continue;
            ApplyWitherStacksLocked(monster, 1, witherSeconds, Get, now, player.Id);
            if (hinderSeconds <= 0 || slow == 0 || monster.Effects[Stat.ImmuneToHinder] != 0
                || monster.Effects[Stat.YouCannotBeHindered] != 0) continue;
            int pct = (int)Math.Clamp(Math.Round(slow * (1 + monster.Effects[Stat.HinderEffectOnSelfPlusPct] / 100d)), 0, 100);
            if (pct == 0) continue;
            var hinders = monster.WitherHinders ??= [];
            double until = now + hinderSeconds;
            int same = hinders.FindIndex(h => h.Pct == pct);
            if (same < 0) hinders.Add((until, pct));
            else hinders[same] = (Math.Max(until, hinders[same].Until), pct);
            monster.PresentationChanged?.Invoke();
        }
    }

    static int WitherHinder(SpawnedMonster monster, double now)
    {
        int strongest = 0;
        if (monster.WitherHinders is { } hinders)
            foreach (var (until, pct) in hinders)
                if (until > now) strongest = Math.Max(strongest, pct);
        return strongest;
    }
}
