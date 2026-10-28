namespace GameServer.Core.World;

/// <summary>Signed extra rolls: positive keeps the highest damage roll, negative the lowest. A chance roll has the
/// opposite preference because a lower random number succeeds. Opposing luck sources cancel before calling this.</summary>
public static class RandomRolls
{
    public static int Next(Random random, int minInclusive, int maxExclusive, int extraRolls = 0)
    {
        int result = random.Next(minInclusive, maxExclusive);
        for (long i = 0, count = Math.Abs((long)Math.Clamp(extraRolls, -1, 1)); i < count; i++)
        {
            int next = random.Next(minInclusive, maxExclusive);
            result = extraRolls > 0 ? Math.Max(result, next) : Math.Min(result, next);
        }
        return result;
    }

    /// <summary>Roll a chance in percent (scale 100) or permyriad (scale 10000). Lucky chance means that any of its
    /// rolls can succeed; unlucky means that all of them must succeed.</summary>
    public static bool Chance(int chance, int scale, Random random, int extraRolls = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(scale);
        if (chance <= 0) return false;
        if (chance >= scale) return true;
        bool result = random.Next(0, scale) < chance;
        for (long i = 0, count = Math.Abs((long)Math.Clamp(extraRolls, -1, 1)); i < count; i++)
        {
            bool next = random.Next(0, scale) < chance;
            result = extraRolls > 0 ? result || next : result && next;
        }
        return result;
    }
}
