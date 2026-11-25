using GameServer.Core.Protocol.Instance;

public static partial class Game
{
    const uint WiseOakPenetration = Stat.LocalUniqueFlaskElementalPenetrationPctOfHighestUncappedResistanceType;
    const uint WiseOakDamageTaken = Stat.LocalUniqueFlaskElementalDamageTakenPlusPctOfLowestUncappedResistanceType;
    const uint TasteOfHateShift = Stat.LocalFireAndLightningHitAndDotDamagePctTakenAsColdDuringEffect;

    static readonly (uint Local, uint Target, bool Flag)[] ItemUtilityFlaskSources =
    [
        (Stat.LocalUniqueFlaskPhysicalDamagePctToAddAsChaosWhileHealing, Stat.PhysicalDamagePctToAddAsChaos, false),
        (Stat.LocalUniqueFlaskElementalDamagePctToAddAsChaosWhileHealing, Stat.ElementalDamagePctToAddAsChaos, false),
        (Stat.LocalUniqueFlaskLifeLeechFromChaosDamagePermyriadWhileHealing, Stat.BaseLifeLeechFromChaosDamagePermyriad, false),
        (Stat.LocalUniqueFlaskPhysicalDamagePctToAddAsColdWhileHealing, Stat.PhysicalDamagePctToAddAsCold, false),
        (Stat.LocalUniqueFlaskAvoidChillPctWhileHealing, Stat.BaseAvoidChillPct, false),
        (Stat.LocalUniqueFlaskAvoidFreezePctWhileHealing, Stat.BaseAvoidFreezePct, false),
        (Stat.LocalNoCriticalStrikeMultiplierDuringFlaskEffect, Stat.NoCriticalStrikeMultiplier, true),
        (Stat.LocalFlaskCullingStrikeDuringFlaskEffect, Stat.KillEnemyOnHitIfUnder10PctLife, true),
        (Stat.LocalUniqueFlaskVaalSkillDamagePlusPctDuringFlaskEffect, Stat.VaalSkillDamagePlusPct, false),
        (Stat.LocalUniqueFlaskVaalSkillCriticalStrikeChancePlusPctDuringFlaskEffect, Stat.VaalSkillCriticalStrikeChancePlusPct, false),
        (WiseOakPenetration, WiseOakPenetration, false),
        (WiseOakDamageTaken, WiseOakDamageTaken, false),
        (TasteOfHateShift, Stat.FireDamageTakenPctAsCold, false),
        (TasteOfHateShift, Stat.LightningDamageTakenPctAsCold, false),
        (Stat.LocalUniqueFlaskPhysicalDamageTakenPctAsColdWhileHealing, Stat.PhysicalDamageTakenPctAsCold, false),
    ];

    static readonly (uint Uncapped, uint Penetration, uint Taken)[] WiseOakElements =
    [
        (Stat.UncappedFireDamageResistancePct, Stat.BaseReduceEnemyFireResistancePct, Stat.FireDamageTakenPlusPct),
        (Stat.UncappedColdDamageResistancePct, Stat.BaseReduceEnemyColdResistancePct, Stat.ColdDamageTakenPlusPct),
        (Stat.UncappedLightningDamageResistancePct, Stat.BaseReduceEnemyLightningResistancePct, Stat.LightningDamageTakenPlusPct),
    ];

    static double MapFlaskEffectMultiplier(Player player) =>
        Math.Max(0, 100L + (MapModsOf(player)?.Get(Stat.MapPlayerFlaskEffectPlusPctFinal) ?? 0)) / 100.0;

    public static (IReadOnlyList<UtilityFlasks.Source> Stats, double Seconds) ItemUtilityFlaskFor(
        UtilityFlasks.Utility utility, Item flask, IReadOnlyDictionary<uint, int> local,
        Func<uint, int> character, int playerLevel = 0, double finalMultiplier = 1)
    {
        var ordinary = UtilityFlasks.For(utility, flask, local, character, playerLevel, finalMultiplier);
        if (!ItemUtilityFlaskSources.Any(row => local.GetValueOrDefault(row.Local) != 0)) return ordinary;
        double effect = FlaskEffectScaling.Multiplier(character, playerLevel, local.GetValueOrDefault(Stat.LocalFlaskEffectPlusPct),
            flask.Rarity == ItemRarity.Magic ? character(Stat.MagicUtilityFlaskEffectPlusPct) : 0, finalMultiplier);
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
            // Wise Oak's local flask stats are resolved in ApplyWiseOak and never written to the player's StatMap.
            if (name is not (Stat.AdditionalBlockPct or Stat.BaseSpellBlockPct or WiseOakPenetration or WiseOakDamageTaken)
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

    static void ApplyWiseOak(StatMap stats, IReadOnlyCollection<(uint Stat, int Value)> running)
    {
        int penetration = 0, taken = 0;
        foreach (var (stat, value) in running)
        {
            if (stat == WiseOakPenetration
                && (Math.Abs((long)value) > Math.Abs((long)penetration)
                    || Math.Abs((long)value) == Math.Abs((long)penetration) && value > penetration))
                penetration = value;
            else if (stat == WiseOakDamageTaken
                && (Math.Abs((long)value) > Math.Abs((long)taken)
                    || Math.Abs((long)value) == Math.Abs((long)taken) && value > taken))
                taken = value;
        }
        if (penetration == 0 && taken == 0) return;
        int fire = stats.GetValueOrDefault(Stat.UncappedFireDamageResistancePct);
        int cold = stats.GetValueOrDefault(Stat.UncappedColdDamageResistancePct);
        int lightning = stats.GetValueOrDefault(Stat.UncappedLightningDamageResistancePct);
        int highest = Math.Max(fire, Math.Max(cold, lightning)), lowest = Math.Min(fire, Math.Min(cold, lightning));
        foreach (var (resistance, pen, reduction) in WiseOakElements)
        {
            int value = stats.GetValueOrDefault(resistance);
            if (penetration != 0 && value == highest) stats[pen] = ActorScale.Add(pen, stats.GetValueOrDefault(pen), penetration);
            if (taken != 0 && value == lowest) stats[reduction] = ActorScale.Add(reduction, stats.GetValueOrDefault(reduction), taken);
        }
    }
}
