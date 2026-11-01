using GameServer.Core.World;


/// <summary>Attacker inputs, captured at bow release or sampled at melee contact. Target evasion, rarity and
/// distance are supplied at actual contact. Final hand aliases are outputs, not raw sources.</summary>
readonly record struct AttackAccuracyProfile(int Rating, int MoreVsUnique = 0, int MoreInCloseRange = 0,
    bool CannotBeEvaded = false)
{
    public static AttackAccuracyProfile From(CharacterStats.Pools pools, IReadOnlyDictionary<uint, int> player,
        StatCatalog catalog, IReadOnlyDictionary<uint, int>? own = null,
        IReadOnlyDictionary<string, int>? local = null)
    {
        bool Flag(string name) => catalog.Key(name) is uint key
            && (player.GetValueOrDefault(key) != 0 || (own?.GetValueOrDefault(key) ?? 0) != 0);
        return new(pools.Accuracy, pools.AccuracyMoreVsUniquePct, pools.AccuracyMoreInCloseRangePct,
            Flag("global_always_hit") || (local?.GetValueOrDefault("local_always_hit") ?? 0) != 0);
    }

    public int Chance(int evasion, bool unique, bool close) => CannotBeEvaded ? 100
        : DamageModel.AccuracyHitChance(evasion,
            TargetAccuracy.AgainstTarget(Rating, MoreVsUnique, MoreInCloseRange, unique, close));
}
