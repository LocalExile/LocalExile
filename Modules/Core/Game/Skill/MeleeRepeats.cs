public static partial class Game
{
    sealed record MeleeRepeatUse(double Start, double Seconds, int Repeats, double End, double Hold);
    static readonly RuleTable<ClientState, MeleeRepeatUse> MeleeRepeatUses = new();

    // The first swing has already reserved NextAttackAt. Repeats cost nothing and hold the same use's cadence.
    public static void ReserveMeleeRepeats(ClientState conn, ushort skill, double seconds)
    {
        int repeats = MeleeAttackRepeats(conn, skill);
        if (repeats == 0) return;
        lock (conn.Monsters)
        {
            double start = conn.NextAttackAt - seconds;
            conn.NextAttackAt += repeats * seconds;
            MeleeRepeatUses.AddOrUpdate(conn, new(start, seconds, repeats, conn.NextAttackAt,
                Math.Clamp(conn.LockstepBusyUntil - start, 0, seconds)));
        }
    }

    // Native 0x1419e6460 lets Multistrike use each swing's cancellation gate. A replacement ends the unstarted
    // repeats, but the swing already in progress keeps its attack-time cost (0x141942b50).
    public static void CancelMeleeRepeats(ClientState conn, double at)
    {
        lock (conn.Monsters)
        {
            if (!MeleeRepeatUses.TryGetValue(conn, out var use)) return;
            MeleeRepeatUses.Remove(conn);
            if (conn.NextAttackAt != use.End) return;
            int swings = Math.Clamp((int)Math.Floor(Math.Max(0, at - use.Start) / use.Seconds) + 1, 1, use.Repeats + 1);
            conn.NextAttackAt = use.Start + swings * use.Seconds;
        }
    }

    public static bool MeleeRepeatAllowsMove(ClientState conn, double at)
    {
        if (!MeleeRepeatUses.TryGetValue(conn, out var use) || at >= use.End) return true;
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
