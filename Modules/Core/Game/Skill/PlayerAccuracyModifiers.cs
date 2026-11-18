using System.Numerics;

public static class PlayerAccuracyModifiers
{
    public const string BootsMore = "boots_implicit_accuracy_rating_+%_final";

    // Keep flat, increased and more inputs unrounded until the final rating is published or used.
    public static int Calculate(long basis, long increased, int morePct, int blindLess = 0) =>
        (int)BigInteger.Min(int.MaxValue, BigInteger.Max(0, basis)
            * BigInteger.Max(0, (BigInteger)100 + increased) * Math.Max(0L, 100L + morePct)
            * Math.Max(0L, 100L - blindLess) / 1_000_000);

    public static int Unselected(CharacterStats.Pools pools, int morePct, int blindLess) => blindLess == 0
        ? pools.Accuracy : Calculate(pools.AccuracyBase, pools.AccuracyPct, morePct, blindLess);

    public static int Selected(CharacterStats.Pools pools, WeaponProfile weapon, Func<string, long> own,
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
    public static int WeaponAccuracyFor(ClientState conn, SkillCatalog.Unarmed? weapon,
        IReadOnlyDictionary<uint, int> player, IReadOnlyDictionary<uint, int>? own = null)
    {
        long Own(string name) => Stats.Key(name) is uint key ? own?.GetValueOrDefault(key) ?? 0 : 0;
        long localDifference = weapon?.LocalSources is { } local
            ? (long)local.GetValueOrDefault("local_accuracy_rating")
                - (MainHandLocalSources(conn)?.GetValueOrDefault("local_accuracy_rating") ?? 0) : 0;
        return PlayerAccuracyModifiers.Selected(conn.Pools, WeaponProfileFor(conn, activeSet: true), Own,
            GolemStat(player, PlayerAccuracyModifiers.BootsMore), localDifference, PlayerBlindValue(conn, player).Less);
    }
}
