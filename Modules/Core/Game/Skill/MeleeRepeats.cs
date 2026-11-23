public static partial class Game
{
    sealed record MeleeRepeatUse(double Start, double Seconds, int Repeats, double End, double Hold);
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

    /// <summary>The attack deadline after a replacement cancels only the repeats that have not started.</summary>
    public static double MeleeRepeatAttackDeadline(Player player, double at)
    {
        if (!MeleeRepeatUses.TryGetValue(player, out var use) || player.NextAttackAt != use.End)
            return player.NextAttackAt;
        int swings = Math.Clamp((int)Math.Floor(Math.Max(0, at - use.Start) / use.Seconds) + 1, 1, use.Repeats + 1);
        return use.Start + swings * use.Seconds;
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
        int swing = Math.Max(0, (int)Math.Floor((at - use.Start) / use.Seconds));
        return at >= use.Start + swing * use.Seconds + use.Hold;
    }

    public static MonsterIgnites.Settings RepeatAilment(MonsterIgnites.Settings ailment, double more) => ailment with
    {
        Min = ailment.Min * more, Max = ailment.Max * more,
        CriticalMin = ailment.CriticalMin * more, CriticalMax = ailment.CriticalMax * more,
    };

    public static MonsterPoisons.Settings RepeatAilment(MonsterPoisons.Settings ailment, double more) => ailment with
    {
        Min = ailment.Min * more, Max = ailment.Max * more,
        CriticalMin = ailment.CriticalMin * more, CriticalMax = ailment.CriticalMax * more,
    };
}
