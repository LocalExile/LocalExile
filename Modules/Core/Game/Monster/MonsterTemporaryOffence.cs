using GameServer.Core.World;

public static partial class Game
{
    /// <summary>Temporary offensive sources are added together, then converted once. The attack already includes
    /// its permanent monster modifiers; temporary type increases also apply to newly gained damage.</summary>
    public static DamagePacket MonsterTemporaryHit(SpawnedMonster monster, DamagePacket hit, Random random)
    {
        if (monster.EncounterStats is null && monster.TemporaryStats.Count == 0) return hit;
        var extra = new Dictionary<uint, int>();
        void Add(IReadOnlyDictionary<uint, int> source)
        {
            foreach (var (key, value) in source)
                if (value != 0) extra[key] = ActorScale.Saturate((long)extra.GetValueOrDefault(key) + value);
        }
        if (monster.EncounterStats is { } encounter) Add(encounter);
        foreach (var source in monster.TemporaryStats.Values) Add(source);
        return TemporaryMonsterDamage(hit, extra, random);
    }

    public static DamagePacket TemporaryMonsterDamage(DamagePacket hit, IReadOnlyDictionary<uint, int> extra, Random random)
    {
        int Get(uint key) => extra.GetValueOrDefault(key);
        var conversion = new DamageConversion()
            .AddGain(DamageType.Physical, DamageType.Fire, Get(Stat.PhysicalDamagePctToAddAsFire))
            .AddGain(DamageType.Physical, DamageType.Cold, Get(Stat.PhysicalDamagePctToAddAsCold))
            .AddGain(DamageType.Physical, DamageType.Lightning, Get(Stat.PhysicalDamagePctToAddAsLightning))
            .AddGain(DamageType.Physical, DamageType.Chaos, Get(Stat.PhysicalDamagePctToAddAsChaos) + Get(Stat.NonChaosDamageToAddAsChaosDamagePct));
        int elementalChaos = Get(Stat.ElementalDamagePctToAddAsChaos) + Get(Stat.NonChaosDamageToAddAsChaosDamagePct);
        foreach (var type in new[] { DamageType.Fire, DamageType.Cold, DamageType.Lightning })
            conversion.AddGain(type, DamageType.Chaos, elementalChaos);
        int randomConversion = Get(Stat.BaseConvertPctPhysicalDamageToRandomElement);
        if (randomConversion > 0)
            conversion.Add(DamageType.Physical, random.Next(3) switch { 0 => DamageType.Fire, 1 => DamageType.Cold, _ => DamageType.Lightning }, randomConversion);
        var result = DamagePacket.Empty;
        foreach (var path in conversion.Trace(hit))
        {
            int increased = Get(Stat.DamagePlusPct);
            foreach (var type in DamageTypes.ConversionOrder)
                if ((path.History & DamageTypes.HistoryOf(type)) != 0)
                    increased += Get(type switch
                    {
                        DamageType.Physical => Stat.PhysicalDamagePlusPct, DamageType.Fire => Stat.FireDamagePlusPct,
                        DamageType.Cold => Stat.ColdDamagePlusPct, DamageType.Lightning => Stat.LightningDamagePlusPct,
                        _ => Stat.ChaosDamagePlusPct,
                    });
            result = result.Add(path.Type, path.Range.Scale(Math.Max(0, 1 + increased / 100d)));
        }
        return result.Round();
    }

    public static int MonsterPenetration(MonsterModEffects effects, DamageType type) => type switch
    {
        DamageType.Fire => effects[Stat.ReduceEnemyElementalResistancePct] + effects[Stat.BaseReduceEnemyFireResistancePct],
        DamageType.Cold => effects[Stat.ReduceEnemyElementalResistancePct] + effects[Stat.BaseReduceEnemyColdResistancePct],
        DamageType.Lightning => effects[Stat.ReduceEnemyElementalResistancePct] + effects[Stat.BaseReduceEnemyLightningResistancePct],
        DamageType.Chaos => effects[Stat.ReduceEnemyChaosResistancePct],
        _ => 0,
    };

    public static bool MonsterAvoidsProjectile(SpawnedMonster monster, Random random) =>
        RandomRolls.Chance(Math.Clamp(monster.Effects[Stat.BaseAvoidProjectilesPctChance]
            + monster.Effects[Stat.AvoidProjectilesPctChance], 0, 100), 100, random);
}
