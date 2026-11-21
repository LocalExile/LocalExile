using System.Runtime.CompilerServices;

public static partial class Game
{
    sealed class ChannelTrigger(LinkedTriggerSource source, int action, int generation, double interval,
        double at, (int X, int Y) aim)
    {
        public readonly LinkedTriggerSource Source = source;
        public readonly int Action = action, Generation = generation;
        public readonly double Interval = interval;
        public double NextAt = at + interval;
        public (int X, int Y) Aim = aim;
    }
    static readonly Store<Player, ChannelTrigger> ChannelTriggers = new();

    public static void BeginChannelTriggers(Player player, ProjectileSession session, ushort skill,
        int action, int generation, (int X, int Y) aim, double at)
    {
        ChannelTriggers.Remove(player);
        var gem = SupportedSkill(player, skill);
        int milliseconds = GolemStat(gem.Stats, Stat.CastWhileChannellingTimeMs);
        if (milliseconds <= 0 || Supports.Of(gem.Effect)?.Types.Contains(47) != true
            || PlacedKind(gem) is not (false, false) || GolemStat(gem.Stats, Stat.IsTotem) != 0) return;
        var spells = CaptureLinkedSpells(player, gem, Stat.CastWhileChannellingTimeMs, Stat.CastSpellWhileLinkedSkillChannelling);
        if (spells.Length == 0 || !session.Active) return;
        ChannelTriggers.Add(player, new(new(skill, gem, spells, player.ProjectileEpoch, CombatLifetime),
            action, generation, milliseconds / 1000d, at, aim));
    }

    public static void AimChannelTriggers(Player player, (int X, int Y) aim)
    {
        if (ChannelTriggers.TryGetValue(player, out var state)) state.Aim = aim;
    }

    // Runs on the existing projectile pump, independently of channel attack/cast speed and monster contact.
    public static void TickChannelTriggers(Player player, ProjectileSession session, double at)
    {
        if (!ChannelTriggers.TryGetValue(player, out var state) || player.Paused) return;
        if (!LinkedSourceCurrent(player, state.Source) || !session.Active
            || !ChannelCurrent(player, state.Source.Skill, state.Action, state.Generation)
            || player.PaidChannel is not { } paid || !PaidChannelCurrent(player, paid))
        { ChannelTriggers.Remove(player); return; }
        if (at < state.NextAt) return;
        // At most one release per pump after a late tick; retain the admission-anchored phase without a burst.
        state.NextAt += (Math.Floor((at - state.NextAt) / state.Interval) + 1) * state.Interval;
        ReleaseLinkedSpell(player, session, state.Source, "CwC", state.Aim, at,
            Stat.CastSpellWhileLinkedSkillChannelling);
    }
}
