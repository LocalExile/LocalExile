

/// <summary>One action's critical roll, shared by all of its hits. Luck is captured when the action is admitted;
/// chance and prohibition are evaluated per hit. The caller serializes access under the connection's monster lock.
/// Random values are allocated lazily so cancelled/missed/prohibited contacts consume no critical randomness.
/// That allocation order is local policy, not a claim about the official server's random-number stream.</summary>
public sealed class SkillCriticalRoll(int extraRolls)
{
    int? value;
    internal bool AmbushExerted { get; private set; }
    internal int AmbushBaseCrit { get; private set; }
    internal int AmbushCritMultiplier { get; private set; }

    internal void SetAmbush(int baseCrit, int multiplier)
    {
        AmbushExerted = true;
        AmbushBaseCrit = baseCrit;
        AmbushCritMultiplier = multiplier;
    }

    public bool Check(int chancePerMyriad, Random random)
    {
        if (chancePerMyriad <= 0) return false;
        if (value is null)
        {
            int first = random.Next(10_000);
            value = extraRolls == 0 ? first : extraRolls > 0
                ? Math.Min(first, random.Next(10_000)) : Math.Max(first, random.Next(10_000));
        }
        return value.Value < Math.Min(chancePerMyriad, 10_000);
    }
}
