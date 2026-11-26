using System.Numerics;


/// <summary>Unconditional MORE/LESS chance inputs, after intrinsic/added BASE and INC, before rounding/capping.
/// Native final display aliases and target/owner conditions are not raw inputs to this stage.</summary>
public static class CriticalChanceFinal
{
    public const uint ControlledDestruction = Stat.SupportControlledDestructionCriticalStrikeChancePlusPctFinal;
    public const uint Unhinged = Stat.CriticalStrikeChancePlusPctFinalWhileUnhinged;
    static readonly uint[] Sources = [ControlledDestruction, Stat.SupportMultipleProjectilesCriticalStrikeChancePlusPctFinal,
        Stat.UniqueCriticalStrikeChancePlusPctFinal, Stat.ActiveSkillCriticalStrikeChancePlusPctFinal,
        Unhinged, Stat.GladiatorCriticalStrikeChancePlusPctFinalWhileWieldingDagger];

    const uint SpellMore = Stat.AssassinAscendancySpellCriticalStrikeChancePlusPctFinal;

    public static bool IsInput(uint name) => name == SpellMore || Sources.Contains(name);

    public static int Project(long basis, long increased, Func<uint, int> player, Func<uint, int>? skill) =>
        Project(basis, increased, player, skill, spell: false);

    internal static int Project(long basis, long increased, Func<uint, int> player, Func<uint, int>? skill, bool spell)
    {
        // Exact rational arithmetic keeps half-up permyriad rounding and signed source cancellation without an
        // intermediate 100% cap or overflow when several large developer values compose with LESS factors.
        BigInteger numerator = BigInteger.Max(0, basis) * BigInteger.Max(0, (BigInteger)100 + increased);
        BigInteger denominator = 100;
        foreach (uint name in Sources)
        {
            long percent = (long)player(name) + (skill?.Invoke(name) ?? 0);
            if (percent == 0) continue;
            numerator *= BigInteger.Max(0, 100 + percent);
            denominator *= 100;
        }
        if (spell)
        {
            long percent = (long)player(SpellMore) + (skill?.Invoke(SpellMore) ?? 0);
            if (percent != 0)
            {
                numerator *= BigInteger.Max(0, 100 + percent);
                denominator *= 100;
            }
        }
        return (int)BigInteger.Min(10_000, (numerator + denominator / 2) / denominator);
    }
}
