/// <summary>Action speed is a multiplier on attack, cast and movement speed, separate from their own increases.
/// Cooldown recovery is deliberately not a consumer.</summary>
readonly record struct ActionSpeed(int Percent, bool MovementFloor)
{
    public const string Reduction = "action_speed_-%";
    public const string Virtual = "virtual_action_speed_+%";
    public const string ActionFloor = "action_speed_cannot_be_reduced_below_base";
    public const string MovementFloorStat = "movement_speed_cannot_be_reduced_below_base";
    public const string HasTailwind = "has_tailwind";
    public const string TailwindEffect = "tailwind_effect_on_self_+%";

    public double Factor => Math.Max(0, 1 + Percent / 100.0);

    // Apply the movement floor to movement's own factor, before the independent action-speed factor.
    public int MovementPercent(int increased) => Math.Max(MovementFloor ? 0 : -100, increased);
    public double MovementFactor(int increased) => (1 + MovementPercent(increased) / 100.0) * Factor;

    public static bool IsInput(string name) => name is Reduction or ActionFloor or MovementFloorStat or HasTailwind or TailwindEffect;

    // A published virtual stat is already final. Raw callers can still use the same pure source calculation.
    public static double Multiplier(IReadOnlyDictionary<uint, int>? stats, Func<string, uint?> key)
    {
        if (stats is null) return 1;
        if (key(Virtual) is uint final && stats.TryGetValue(final, out int percent))
            return Math.Max(0, 1 + percent / 100.0);
        return From(name => key(name) is uint k ? stats.GetValueOrDefault(k) : 0).Factor;
    }

    public static ActionSpeed From(Func<string, int> get)
    {
        // Buff 1094 already contributes -8 to the signed reduction. Only its effect-derived extra belongs here;
        // e.g. raw -18 = Tailwind 8 + Harbinger 10, with 50% Tailwind effect, gives 22 rather than 27 or 30.
        long tailwindExtra = get(HasTailwind) != 0
            ? (long)Math.Floor(8 * Math.Max(0, 1 + get(TailwindEffect) / 100.0)) - 8 : 0;
        int percent = (int)Math.Clamp(-(long)get(Reduction) + tailwindExtra,
            get(ActionFloor) != 0 ? 0 : -100, int.MaxValue);
        return new(percent, get(MovementFloorStat) != 0);
    }
}
