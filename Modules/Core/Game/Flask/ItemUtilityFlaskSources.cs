using GameServer.Core.Protocol.Instance;

public static partial class Game
{
    static readonly (uint Local, uint Target, bool Flag)[] ItemUtilityFlaskSources =
    [
        (Stat.LocalUniqueFlaskPhysicalDamagePctToAddAsChaosWhileHealing, Stat.PhysicalDamagePctToAddAsChaos, false),
        (Stat.LocalUniqueFlaskElementalDamagePctToAddAsChaosWhileHealing, Stat.ElementalDamagePctToAddAsChaos, false),
        (Stat.LocalUniqueFlaskLifeLeechFromChaosDamagePermyriadWhileHealing, Stat.BaseLifeLeechFromChaosDamagePermyriad, false),
        (Stat.LocalUniqueFlaskPhysicalDamagePctToAddAsColdWhileHealing, Stat.PhysicalDamagePctToAddAsCold, false),
        (Stat.LocalUniqueFlaskAvoidChillPctWhileHealing, Stat.BaseAvoidChillPct, false),
        (Stat.LocalUniqueFlaskAvoidFreezePctWhileHealing, Stat.BaseAvoidFreezePct, false),
        (Stat.LocalNoCriticalStrikeMultiplierDuringFlaskEffect, Stat.NoCriticalStrikeMultiplier, true),
    ];

    public static (IReadOnlyList<UtilityFlasks.Source> Stats, double Seconds) ItemUtilityFlaskFor(
        UtilityFlasks.Utility utility, Item flask, IReadOnlyDictionary<uint, int> local,
        Func<uint, int> character, int playerLevel = 0)
    {
        var ordinary = UtilityFlasks.For(utility, flask, local, character, playerLevel);
        if (!ItemUtilityFlaskSources.Any(row => local.GetValueOrDefault(row.Local) != 0)) return ordinary;
        double effect = FlaskEffectScaling.Multiplier(character, playerLevel, local.GetValueOrDefault(Stat.LocalFlaskEffectPlusPct),
            flask.Rarity == ItemRarity.Magic ? character(Stat.MagicUtilityFlaskEffectPlusPct) : 0);
        var sources = ordinary.Stats.ToList();
        foreach (var (input, target, flag) in ItemUtilityFlaskSources)
            if (local.GetValueOrDefault(input) is int value and not 0)
                sources.Add(new("mod " + target, target, flag ? 1 : UtilityFlasks.Scale(value, effect)));
        return (sources, ordinary.Seconds);
    }

    public static void AddItemUtilityFlaskRawStats(StatMap stats,
        IEnumerable<(uint Stat, int Value)> running, StatCatalog catalog)
    {
        var snapshot = running as IReadOnlyCollection<(uint Stat, int Value)> ?? running.ToArray();
        var ordinary = UtilityFlasks.RawStats(snapshot, catalog).Select(row => row.Key).ToHashSet();
        UtilityFlasks.AddRawStats(stats, snapshot, catalog);
        foreach (var (name, value) in snapshot)
            // Block chances are already published from the pools. Avoidance is already in the generic raw view.
            if (name is not (Stat.AdditionalBlockPct or Stat.BaseSpellBlockPct)
                && ItemUtilityFlaskSources.Any(row => row.Target == name) && name is var key
                && !ordinary.Contains(key))
                stats[key] = ActorScale.Add(key, stats.GetValueOrDefault(key), value);
    }

    public static CharacterStats.Bonus ApplyItemUtilityFlaskBonuses(CharacterStats.Bonus bonus,
        IEnumerable<(uint Stat, int Value)> running)
    {
        var snapshot = running as IReadOnlyCollection<(uint Stat, int Value)> ?? running.ToArray();
        var result = UtilityFlasks.Apply(bonus, snapshot);
        return result with
        {
            BlockChance = ActorScale.Saturate((long)result.BlockChance
                + snapshot.Where(row => row.Stat == Stat.AdditionalBlockPct).Sum(row => (long)row.Value)),
        };
    }
}
