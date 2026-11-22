using System.Numerics;
using GameServer.Core;

public static class PlayerAccuracyModifiers
{
    public const uint BootsMore = Stat.BootsImplicitAccuracyRatingPlusPctFinal;

    // Keep flat, increased and more inputs unrounded until the final rating is published or used.
    public static int Calculate(long basis, long increased, int morePct, int blindLess = 0) =>
        (int)BigInteger.Min(int.MaxValue, BigInteger.Max(0, basis)
            * BigInteger.Max(0, (BigInteger)100 + increased) * Math.Max(0L, 100L + morePct)
            * Math.Max(0L, 100L - blindLess) / 1_000_000);

    public static int Unselected(CharacterStats.Pools pools, int morePct, int blindLess) => blindLess == 0
        ? pools.Accuracy : Calculate(pools.AccuracyBase, pools.AccuracyPct, morePct, blindLess);

    public static int Selected(CharacterStats.Pools pools, WeaponProfile weapon, Func<uint, long> own,
        int morePct, long localDifference = 0, int blindLess = 0)
    {
        long flat = AccuracyRating.Flat(weapon, own, includeLocal: false), increased = AccuracyRating.Increased(weapon, own);
        return localDifference == 0 && flat == 0 && increased == 0 && blindLess == 0 ? pools.Accuracy
            : Calculate(pools.AccuracyBase + localDifference + flat, pools.AccuracyPct + increased, morePct, blindLess);
    }
}

public static partial class Game
{
    // Pools already include main-hand local accuracy. Only a substituted weapon replaces that contribution.
    // A shield damage override has no local weapon packet and uses the ordinary rating.
    public static int WeaponAccuracyFor(Player player, SkillCatalog.Unarmed? weapon,
        IReadOnlyDictionary<uint, int> playerStats, IReadOnlyDictionary<uint, int>? own = null)
    {
        long Own(uint key) => own?.GetValueOrDefault(key) ?? 0;
        long localDifference = weapon?.LocalSources is { } local
            ? (long)local.GetValueOrDefault(Stat.LocalAccuracyRating)
                - (MainHandLocalSources(player)?.GetValueOrDefault(Stat.LocalAccuracyRating) ?? 0) : 0;
        return PlayerAccuracyModifiers.Selected(player.Pools, WeaponProfileFor(player, activeSet: true), Own,
            GolemStat(playerStats, Stat.BootsImplicitAccuracyRatingPlusPctFinal), localDifference, PlayerBlindValue(player, playerStats).Less);
    }
}
