namespace GameServer.Modules.PhreciaAscendancies.Classes.Harbinger;

/// <summary>Action speed is a multiplier on attack, cast and movement speed, separate from their own increases.
/// Cooldown recovery is deliberately not a consumer.</summary>
readonly record struct HarbingerActionSpeed(int Percent, bool MovementFloor)
{
    public const uint Reduction = Stat.ActionSpeedMinusPct;
    public const uint Virtual = Stat.VirtualActionSpeedPlusPct;
    public const uint ActionFloor = Stat.ActionSpeedCannotBeReducedBelowBase;
    public const uint MovementFloorStat = Stat.MovementSpeedCannotBeReducedBelowBase;
    public const uint HasTailwind = Stat.HasTailwind;
    public const uint TailwindEffect = Stat.TailwindEffectOnSelfPlusPct;

    public double Factor => Math.Max(0, 1 + Percent / 100.0);

    // Apply the movement floor to movement's own factor, before the independent action-speed factor.
    public int MovementPercent(int increased) => Math.Max(MovementFloor ? 0 : -100, increased);
    public double MovementFactor(int increased) => (1 + MovementPercent(increased) / 100.0) * Factor;

    public static bool IsInput(uint name) => name is Reduction or ActionFloor or MovementFloorStat or HasTailwind or TailwindEffect;

    // A published virtual stat is already final. Raw callers can still use the same pure source calculation.
    public static double Multiplier(IReadOnlyDictionary<uint, int>? stats)
    {
        if (stats is null) return 1;
        if (stats.TryGetValue(Virtual, out int percent))
            return Math.Max(0, 1 + percent / 100.0);
        return From(k => stats.GetValueOrDefault(k)).Factor;
    }

    public static HarbingerActionSpeed From(Func<uint, int> get)
    {
        // Buff 1094 already contributes -8 to the signed reduction. Only its effect-derived extra belongs here;
        // e.g. raw -18 = Tailwind 8 + Harbinger 10, with 50% Tailwind effect, gives 22 rather than 27 or 30.
        long tailwindExtra = get(HasTailwind) != 0
            ? (long)Math.Floor(8 * Math.Max(0, 1 + get(TailwindEffect) / 100.0)) - 8 : 0;
        double raw = -(long)get(Reduction) + tailwindExtra;
        double final = (100 + raw) * Math.Max(0, 1 + get(Stat.TemporalChainsActionSpeedPlusPctFinal) / 100.0) - 100;
        int percent = (int)Math.Clamp(Math.Floor(final + 1e-9), get(ActionFloor) != 0 ? 0 : -100, int.MaxValue);
        return new(percent, get(MovementFloorStat) != 0);
    }
}
