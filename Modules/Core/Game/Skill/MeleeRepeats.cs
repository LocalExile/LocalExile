public static partial class Game
{
    sealed record MeleeRepeatUse(double Start, double Seconds, int Repeats, double End, double Hold, double[]? Pairs = null)
    {
        public (double At, double Seconds, double Hold) SwingAt(double now)
        {
            if (Pairs is null)
            {
                int swing = Math.Clamp((int)Math.Floor(Math.Max(0, now - Start) / Seconds), 0, Repeats);
                return (Start + swing * Seconds, Seconds, Hold);
            }
            int pair = 0;
            double at = Start;
            while (pair < Pairs.Length - 1 && now >= at + Pairs[pair])
                at += Pairs[pair++];
            return (at, Pairs[pair], Hold * (Pairs[pair] / Seconds));
        }
    }
    static readonly Store<Player, MeleeRepeatUse> MeleeRepeatUses = new();

    // The first swing has already reserved NextAttackAt. Repeats cost nothing and hold the same use's cadence.
    public static void ReserveMeleeRepeats(Player player, ushort skill, double seconds)
    {
        int repeats = MeleeAttackRepeats(player, skill);
        if (repeats == 0) return;
        double start = player.NextAttackAt - seconds;
        player.NextAttackAt += repeats * seconds;
        MeleeRepeatUses.AddOrUpdate(player, new(start, seconds, repeats, player.NextAttackAt,
            Math.Clamp(player.LockstepBusyUntil - start, 0, seconds)));
    }

    static void ReserveVariableMeleeRepeats(Player player, double start, double[] pairs)
    {
        player.NextAttackAt = start + pairs.Sum();
        if (pairs.Length > 1)
            MeleeRepeatUses.AddOrUpdate(player, new(start, pairs[0], pairs.Length - 1, player.NextAttackAt,
                Math.Clamp(player.LockstepBusyUntil - start, 0, pairs[0]), pairs));
        else
            MeleeRepeatUses.Remove(player);
    }

    /// <summary>The attack deadline after a replacement cancels only the repeats that have not started.</summary>
    public static double MeleeRepeatAttackDeadline(Player player, double at)
    {
        if (!MeleeRepeatUses.TryGetValue(player, out var use) || player.NextAttackAt != use.End)
            return player.NextAttackAt;
        var swing = use.SwingAt(at);
        return swing.At + swing.Seconds;
    }

    // Native 0x1419e6460 lets Multistrike use each swing's cancellation gate. A replacement ends the unstarted
    // repeats, but the swing already in progress keeps its attack-time cost (0x141942b50).
    public static void CancelMeleeRepeats(Player player, double at)
    {
        double deadline = MeleeRepeatAttackDeadline(player, at);
        MeleeRepeatUses.Remove(player);
        player.NextAttackAt = deadline;
    }

    public static bool MeleeRepeatAllowsMove(Player player, double at)
    {
        if (!MeleeRepeatUses.TryGetValue(player, out var use) || at >= use.End) return true;
        var swing = use.SwingAt(at);
        return at >= swing.At + swing.Hold;
    }

    public static MonsterIgnites.Settings RepeatAilment(MonsterIgnites.Settings ailment, double more) =>
        ailment.ScaleDamage(more);

    public static MonsterPoisons.Settings RepeatAilment(MonsterPoisons.Settings ailment, double more) => ailment with
    {
        Min = ailment.Min * more, Max = ailment.Max * more,
        CriticalMin = ailment.CriticalMin * more, CriticalMax = ailment.CriticalMax * more,
    };
}
