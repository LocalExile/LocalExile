using System.Numerics;


/// <summary>Unconditional MORE/LESS chance inputs, after intrinsic/added BASE and INC, before rounding/capping.
/// Native final display aliases and target/owner conditions are not raw inputs to this stage.</summary>
static class CriticalChanceFinal
{
    public const string ControlledDestruction = "support_controlled_destruction_critical_strike_chance_+%_final";
    static readonly string[] Sources = [ControlledDestruction, "support_multiple_projectiles_critical_strike_chance_+%_final",
        "unique_critical_strike_chance_+%_final", "active_skill_critical_strike_chance_+%_final"];

    public static bool IsInput(string name) => Sources.Contains(name);

    public static int Project(long basis, long increased, Func<string, int> player, Func<string, int>? skill)
    {
        // Exact rational arithmetic keeps half-up permyriad rounding and signed source cancellation without an
        // intermediate 100% cap or overflow when several large developer values compose with LESS factors.
        BigInteger numerator = BigInteger.Max(0, basis) * BigInteger.Max(0, (BigInteger)100 + increased);
        BigInteger denominator = 100;
        foreach (string name in Sources)
        {
            long percent = (long)player(name) + (skill?.Invoke(name) ?? 0);
            if (percent == 0) continue;
            numerator *= BigInteger.Max(0, 100 + percent);
            denominator *= 100;
        }
        return (int)BigInteger.Min(10_000, (numerator + denominator / 2) / denominator);
    }
}
