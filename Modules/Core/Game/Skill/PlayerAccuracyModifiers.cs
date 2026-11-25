using System.Numerics;
using GameServer.Core;

public static class PlayerAccuracyModifiers
{
    public const uint BootsMore = Stat.BootsImplicitAccuracyRatingPlusPctFinal;

    // Keep flat, increased and more inputs unrounded until the final rating is published or used.
    public static int Calculate(long basis, long increased, int morePct, int blindLess = 0, int areaMorePct = 0) =>
        Calculate(basis, increased, morePct, blindLess, areaMorePct, weaponMorePct: 0);

    public static int Calculate(long basis, long increased, int morePct, int blindLess,
        int areaMorePct, int weaponMorePct, int chargeMorePct = 0)
    {
        var scaled = BigInteger.Max(0, basis) * BigInteger.Max(0, (BigInteger)100 + increased)
            * Math.Max(0L, 100L + morePct) * Math.Max(0L, 100L - blindLess);
        long denominator = 1_000_000;
        if (areaMorePct != 0)
        {
            scaled *= Math.Max(0L, 100L + areaMorePct);
            denominator *= 100;
        }
        if (weaponMorePct != 0)
        {
            scaled *= Math.Max(0L, 100L + weaponMorePct);
            denominator *= 100;
        }
        if (chargeMorePct != 0)
        {
            scaled *= Math.Max(0L, 100L + chargeMorePct);
            denominator *= 100;
        }
        return (int)BigInteger.Min(int.MaxValue, scaled / denominator);
    }

    public static int Unselected(CharacterStats.Pools pools, int morePct, int blindLess) => blindLess == 0
        ? pools.Accuracy : Calculate(pools.AccuracyBase, pools.AccuracyPct, morePct, blindLess,
            pools.AccuracyAreaMorePct, pools.AccuracyWeaponMorePct, pools.AccuracyChargeMorePct);

    public static int Selected(CharacterStats.Pools pools, WeaponProfile weapon, Func<uint, long> own,
        int morePct, long localDifference = 0, int blindLess = 0)
    {
        long flat = AccuracyRating.Flat(weapon, own, includeLocal: false), increased = AccuracyRating.Increased(weapon, own);
        return localDifference == 0 && flat == 0 && increased == 0 && blindLess == 0 ? pools.Accuracy
            : Calculate(pools.AccuracyBase + localDifference + flat, pools.AccuracyPct + increased, morePct, blindLess,
                pools.AccuracyAreaMorePct, pools.AccuracyWeaponMorePct, pools.AccuracyChargeMorePct);
    }
}

public static partial class Game
{
    // Pools already include main-hand local accuracy. Only a substituted weapon replaces that contribution.
    // A shield damage override has no local weapon packet and uses the ordinary rating.
    public static int WeaponAccuracyFor(Player player, SkillCatalog.Unarmed? weapon,
        IReadOnlyDictionary<uint, int> playerStats, IReadOnlyDictionary<uint, int>? own = null) =>
        WeaponAccuracyFor(player, weapon, playerStats, own, effect: -1);

    static int WeaponAccuracyFor(Player player, SkillCatalog.Unarmed? weapon,
        IReadOnlyDictionary<uint, int> playerStats, IReadOnlyDictionary<uint, int>? own, int effect)
    {
        var weapons = WeaponProfileFor(player, activeSet: true);
        int jewel = playerStats.GetValueOrDefault(Stat.DualStrikeAccuracyRatingPlusPctWhileWieldingSword);
        if (jewel != 0 && (!weapons.Sword || effect < 0
            || SkillsFor(player).EffectId(effect) is not ("DualStrike" or "DualStrikeAltX" or "DualStrikeAltY" or "DualStrikeRoyale")))
            jewel = 0;
        long Own(uint key) => (own?.GetValueOrDefault(key) ?? 0L) + (key == Stat.AccuracyRatingPlusPct ? jewel : 0);
        long localDifference = weapon?.LocalSources is { } local
            ? (long)local.GetValueOrDefault(Stat.LocalAccuracyRating)
                - (MainHandLocalSources(player)?.GetValueOrDefault(Stat.LocalAccuracyRating) ?? 0) : 0;
        return PlayerAccuracyModifiers.Selected(player.Pools, weapons, Own,
            GolemStat(playerStats, Stat.BootsImplicitAccuracyRatingPlusPctFinal), localDifference, PlayerBlindValue(player, playerStats).Less);
    }
}
