using System.Runtime.CompilerServices;

static partial class Game
{
    sealed class ChannelTrigger(LinkedTriggerSource source, int action, int generation, double interval,
        double at, (int X, int Y) aim)
    {
        internal readonly LinkedTriggerSource Source = source;
        internal readonly int Action = action, Generation = generation;
        internal readonly double Interval = interval;
        internal double NextAt = at + interval;
        internal (int X, int Y) Aim = aim;
    }
    static readonly ConditionalWeakTable<ClientState, ChannelTrigger> ChannelTriggers = new();

    internal static void BeginChannelTriggers(ClientState conn, ProjectileSession session, ushort skill,
        int action, int generation, (int X, int Y) aim, double at)
    {
        ChannelTriggers.Remove(conn);
        var gem = SupportedSkill(conn, skill);
        int milliseconds = GolemStat(gem.Stats, "cast_while_channelling_time_ms");
        if (milliseconds <= 0 || Supports.Of(gem.Effect)?.Types.Contains(47) != true
            || PlacedKind(gem) is not (false, false) || GolemStat(gem.Stats, "is_totem") != 0) return;
        var spells = CaptureLinkedSpells(conn, gem, "cast_while_channelling_time_ms", "cast_spell_while_linked_skill_channelling");
        if (spells.Length == 0 || !session.Active) return;
        ChannelTriggers.Add(conn, new(new(skill, gem, spells, conn.ProjectileEpoch, CombatLifetime),
            action, generation, milliseconds / 1000d, at, aim));
    }

    internal static void AimChannelTriggers(ClientState conn, (int X, int Y) aim)
    {
        if (ChannelTriggers.TryGetValue(conn, out var state)) state.Aim = aim;
    }

    // Runs on the existing projectile pump, independently of channel attack/cast speed and monster contact.
    internal static void TickChannelTriggers(ClientState conn, ProjectileSession session, double at)
    {
        lock (conn.Monsters)
        {
            if (!ChannelTriggers.TryGetValue(conn, out var state) || conn.Paused) return;
            if (!LinkedSourceCurrent(conn, state.Source) || !session.Active
                || !ChannelCurrent(conn, state.Source.Skill, state.Action, state.Generation)
                || conn.PaidChannel is not { } paid || !PaidChannelCurrent(conn, paid))
            { ChannelTriggers.Remove(conn); return; }
            if (at < state.NextAt) return;
            // At most one release per pump after a late tick; retain the admission-anchored phase without a burst.
            state.NextAt += (Math.Floor((at - state.NextAt) / state.Interval) + 1) * state.Interval;
            ReleaseLinkedSpell(conn, session, state.Source, "CwC", state.Aim, at,
                "cast_spell_while_linked_skill_channelling");
        }
    }
}
