using GameServer.Core.Protocol.Instance;

public static partial class Game
{
    /// <summary>The map this connection's instance plays with (InstanceState.Map), or null outside a map.</summary>
    public static MapDevice.Run? MapRunOf(Player player) => player.Instance?.Map is { } run && run.Area.Hash16 == player.Layout.Hash ? run : null;

    /// <summary>The map mods this connection's instance plays with: its area stats as the client got them in 0x1d
    /// (MapAreaStats: the explicit mods scaled by the atlas's map mod effect, the atlas's own stats, the fragments), so
    /// what the server applies is what the map shows; the map item's own sum when the instance has no stats.</summary>
    public static MapMods? MapModsOf(Player player)
    {
        if (MapRunOf(player) is not { } run) return null;
        if (player.Instance?.KeptAreaStats is not { Count: > 0 } stats) return run.Mods;
        return MapModsByStats.GetValue(stats, s =>
        {
            var byId = new Dictionary<uint, int>();
            foreach (var (key, value) in s)
                if (key >= 1 && key <= Stats.Names.Count) byId[key] = byId.GetValueOrDefault(key) + value;
            return new MapMods(byId, run.Mods.Quality);
        });
    }

    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<IReadOnlyDictionary<uint, int>, MapMods> MapModsByStats = new();

    internal static bool ScarabsDisabled(MapMods? mods) => (mods?.Get(Stat.MapDisableScarabDrops) ?? 0) > 0;

    public static double MapScarabQuantity(MapMods? mods) =>
        ScarabsDisabled(mods) ? 0 : Math.Max(0, 1 + (mods?.Get(Stat.MapPlusPctIncreasedScarabDrops) ?? 0) / 100d);

    public static ItemDrops.Drop? MapScarab(ItemDrops drops, Random random, int level, MapMods? mods) =>
        MapScarab(drops, random, level, mods, null);

    public static ItemDrops.Drop? MapScarab(ItemDrops drops, Random random, int level, MapMods? mods, DropWeights? weights) =>
        ScarabsDisabled(mods) ? null : drops.Scarab(random, level, mods, weights);

    static readonly uint[] RandomPhysicalGain =
        [Stat.PhysicalDamagePctToAddAsFire, Stat.PhysicalDamagePctToAddAsCold, Stat.PhysicalDamagePctToAddAsLightning];
    sealed record MapElement(uint Stat);
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<MapMods, MapElement> PhysicalGainElementByMap = new();

    /// <summary>The vital and deadly Atlas keystones' more monster life and damage (Wellspring of Creation, Dance of
    /// Destruction): the area's map_ stat becomes the monster stat of the same name, which acts as a more modifier on
    /// the monster's life or hit (handed over to MonsterModEffects.HandOverMore in GameModule.Load).</summary>
    internal static readonly uint[] AtlasMoreLife = [Stat.MonsterLifePlusPctFinalFromVitalAtlas, Stat.MonsterLifePlusPctFinalFromDeadlyAtlas],
        AtlasMoreDamage = [Stat.MonsterDamagePlusPctFinalFromVitalAtlas, Stat.MonsterDamagePlusPctFinalFromDeadlyAtlas];

    // Each atlas monster stat with the area's map_ stat of the same name.
    static readonly (uint Map, uint Monster)[] AtlasPairs =
        [.. AtlasMoreLife.Concat(AtlasMoreDamage).Select(m => (Stat.Of("map_" + Stats.Name(m)), m))];

    public static Dictionary<uint, int> MapMonsterEffects(MapMods mods)
    {
        var extra = mods.MonsterEffectStats();
        foreach (var (map, atlas) in AtlasPairs)
            if (mods.Get(map) is int more and not 0) extra[atlas] = more;
        if (mods.Get(Stat.MapMonstersCannotBeStunned) != 0)
            extra[Stat.BaseCannotBeStunned] = 1;
        void Gain(uint mapId, uint monsterId)
        {
            if (mods.Get(mapId) is int value and not 0)
                extra[monsterId] = extra.GetValueOrDefault(monsterId) + value;
        }
        Gain(Stat.MapMonstersPctPhysicalDamageToAddAsFire, Stat.PhysicalDamagePctToAddAsFire);
        Gain(Stat.MapMonstersPctPhysicalDamageToAddAsCold, Stat.PhysicalDamagePctToAddAsCold);
        Gain(Stat.MapMonstersPctPhysicalDamageToAddAsLightning, Stat.PhysicalDamagePctToAddAsLightning);
        Gain(Stat.MapMonstersPctPhysicalDamageToAddAsChaos, Stat.PhysicalDamagePctToAddAsChaos);
        if (mods.Get(Stat.MapMonstersPhysicalDamagePctToAddAsRandomElement) is int elementalGain and > 0)
        {
            uint element = PhysicalGainElementByMap.GetValue(mods, static _ =>
                new(RandomPhysicalGain[Random.Shared.Next(RandomPhysicalGain.Length)])).Stat;
            extra[element] = ActorScale.Saturate((long)extra.GetValueOrDefault(element) + elementalGain);
        }
        Gain(Stat.MapMonstersAttackSpeedPlusPct, Stat.AttackSpeedPlusPct);
        Gain(Stat.MapMonstersCastSpeedPlusPct, Stat.BaseCastSpeedPlusPct);
        Gain(Stat.MapMonstersMovementSpeedPlusPct, Stat.BaseMovementVelocityPlusPct);
        if (mods.Get(Stat.MapMonstersMovementSpeedCannotBeReducedBelowBase) != 0)
            extra[Stat.MovementSpeedCannotBeReducedBelowBase] = 1;
        if (mods.Get(Stat.MapMonsterActionSpeedCannotBeReducedBelowBase) != 0)
            extra[Stat.ActionSpeedCannotBeReducedBelowBase] = 1;
        Gain(Stat.MapMonsterDebuffTimePassedPlusPct, Stat.DebuffTimePassedPlusPct);
        Gain(Stat.MapMonstersAdditionalPhysicalDamageReduction, Stat.BaseAdditionalPhysicalDamageReductionPct);
        Gain(Stat.MapMonstersBaseBlockPct, Stat.MonsterBaseBlockPct);
        Gain(Stat.MapMonstersAdditionalElementalResistance, Stat.BaseResistAllElementsPct);
        Gain(Stat.MapMonstersAdditionalChaosResistance, Stat.BaseChaosDamageResistancePct);
        Gain(Stat.MapMonstersSpellSuppressionChancePct, Stat.BaseSpellSuppressionChancePct);
        if (mods.Get(Stat.MapMonstersAreHexproof) != 0)
            extra[Stat.Hexproof] = 1;
        Gain(Stat.MapMonstersCurseEffectOnSelfPlusPctFinal, Stat.MapMonstersCurseEffectOnSelfPlusPctFinal);
        Gain(Stat.MapMonstersChanceToBlindOnHitPct, Stat.GlobalChanceToBlindOnHitPct);
        Gain(Stat.MapMonstersCriticalStrikeChancePlusPct, Stat.CriticalStrikeChancePlusPct);
        Gain(Stat.MapMonstersCriticalStrikeMultiplierPlus, Stat.BaseCriticalStrikeMultiplierPlus);
        Gain(Stat.MapMonstersBaseSelfCriticalStrikeMultiplierMinusPct, Stat.BaseSelfCriticalStrikeMultiplierMinusPct);
        Gain(Stat.MapMonstersCannotBeLeechedFrom, Stat.MapMonstersCannotBeLeechedFrom);
        Gain(Stat.MapMonstersPenetrateElementalResistancesPct, Stat.ReduceEnemyElementalResistancePct);
        Gain(Stat.MapMonstersBaseBleedDurationPlusPct, Stat.BaseBleedDurationPlusPct);
        // The extracted map rows use the real monster stats rather than a synthetic "global" flag:
        // MapPoisoning: map_monsters_poison_on_hit=1;
        // MapUberModPoison: the same plus all_damage_can_poison and +100% poison duration.
        if (mods.Get(Stat.MapMonstersPoisonOnHit) != 0)
            extra[Stat.BaseChanceToPoisonOnHitPct] =
                extra.GetValueOrDefault(Stat.BaseChanceToPoisonOnHitPct) + 100;
        Gain(Stat.MapMonstersBasePoisonDurationPlusPct, Stat.BasePoisonDurationPlusPct);
        Gain(Stat.MapMonstersAllDamageCanPoison, Stat.AllDamageCanPoison);
        return extra;
    }

    public static IReadOnlyDictionary<uint, int> MapBossEffects(MapMods mods) => new Dictionary<uint, int>
    {
        [Stat.MaximumLifePlusPct] = mods.Get(Stat.MapBossMaximumLifePlusPct),
        [Stat.DamagePlusPct] = mods.Get(Stat.MapBossDamagePlusPct),
        [Stat.AttackSpeedPlusPct] = mods.Get(Stat.MapBossAttackAndCastSpeedPlusPct),
        [Stat.BaseCastSpeedPlusPct] = mods.Get(Stat.MapBossAttackAndCastSpeedPlusPct),
        [Stat.BaseItemFoundQuantityPlusPct] = mods.Get(Stat.MapBossDroppedItemQuantityPlusPct),
        [Stat.BaseItemFoundRarityPlusPct] = mods.Get(Stat.MapBossItemRarityPlusPct),
    };

    static IEnumerable<AreaPopulation.Spawn> WithTwinBosses(IEnumerable<AreaPopulation.Spawn> spawns, MapMods? mods)
    {
        if ((mods?.Get(Stat.MapSpawnTwoBosses) ?? 0) == 0) return spawns;
        var list = spawns.ToList();
        foreach (var boss in list.Where(s => s.Rarity == MonsterRarity.Unique).ToList())
            list.Add(boss with { X = boss.X + 6, Pack = boss.Pack + "-twin" });
        return list;
    }

    public static ItemDrops.Drop? RollMapDropForKill(Player player, int level, MonsterRarity rarity, int tier, Random rng, double chanceFactor)
    {
        var mods = MapModsOf(player);
        if (mods is not null)
        {
            if (mods.Get(Stat.MapMonsterNoMapDrops) > 0) return null;
            int explicitMods = mods.Get(Stat.MapNumberOfExplicitMods);
            if (explicitMods == 0) explicitMods = mods.ExplicitMods.Count;
            long increased = 100L + mods.Get(Stat.MapMapItemDropChancePlusPct)
                + (long)mods.Get(Stat.MapMapItemDropChancePlusPctPerMapMod) * explicitMods;
            chanceFactor *= Math.Max(0L, increased) / 100.0;
            chanceFactor *= MapDropFactor(mods, Stat.MapMapItemDropChancePlusPctFinalFromUberMod,
                Stat.MapUnscaledMapItemDropChancePlusPctFinalFromUberMod);
        }
        int upgrade = mods?.Get(Stat.MapTierBonusPermillage) ?? 0;
        return DropsFor(player).Map(level, rarity, tier, rng, chanceFactor: chanceFactor, tierUpgradePermillage: upgrade);
    }

    /// <summary>A map's extra drops: a card from each monster of the card-dropping magic packs, and the map's card and
    /// scarab drop factors (its increased and more modifiers multiplied, 1 for none) on the base map chances
    /// (<c>drop_card_chance_pct</c>, <c>drop_scarab_chance_pct</c>, ItemDrops.Roll): a factor f adds the base chance
    /// × (f − 1) per kill. A mod's factors per card, scarab and currency (<see cref="DropWeights.Item"/>) weigh the picks;
    /// a currency kind factor of 0 leaves the extra currency out.</summary>
    static IEnumerable<ItemDrops.Drop> ExtraDropsFromMap(Player player, int monsterLevel, MonsterRarity rarity, Random rng,
        DropWeights? weights = null)
    {
        if (MapModsOf(player) is not { } mods) yield break;
        var drops = DropsFor(player);
        if (rarity == MonsterRarity.Magic && mods.PacksDropCards > 0 && drops.Card(rng, monsterLevel, weights) is { } packCard)
            yield return packCard;
        double card = MapDropFactor(mods, Stat.MapDivinationCardDropChancePlusPct, Stat.MapDivinationCardDropChancePlusPctFinalFromQuality,
            Stat.MapDivinationCardDropChancePlusPctFinalFromFaridunWish);
        if (card > 1 && MapDropExtra("drop_card_chance_pct", card - 1, rng) && drops.Card(rng, monsterLevel, weights) is { } extraCard)
            yield return extraCard;
        double scarab = MapDropFactor(mods, Stat.MapScarabDropChancePlusPctFinalFromUberMod,
            Stat.MapUnscaledScarabDropChancePlusPctFinalFromUberMod, Stat.MapScarabDropChancePlusPctFinalFromFaridunWish);
        if (scarab > 1 && MapDropExtra("drop_scarab_chance_pct", (scarab - 1) * MapScarabQuantity(mods), rng)
            && MapScarab(drops, rng, monsterLevel, mods, weights) is { } extraScarab)
            yield return extraScarab;
        double currency = MapDropFactor(mods, Stat.MapCurrencyDropChancePlusPctFinalFromUberMod);
        if (currency > 1 && (weights is null || weights.Currency > 0))
        {
            double expected = CurrencyDropExpected(rarity) * (currency - 1);
            int whole = (int)expected + (rng.NextDouble() < expected - (int)expected ? 1 : 0);
            for (int i = 0; i < whole; i++)
                if (drops.CurrencyDrop(monsterLevel, rng, weights) is { } extraCurrency) yield return extraCurrency;
        }
    }

    public static double CurrencyDropExpected(MonsterRarity rarity) => rarity switch
    {
        MonsterRarity.Magic => 0.45,
        MonsterRarity.Rare => 1.0,
        MonsterRarity.Unique => 1.75,
        _ => 0.15,
    };

    sealed record CachedCurrencyPool(object Snapshot, (ItemCatalog.BaseType Type, double Weight, int DropLevel)[] Pool);
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ItemCatalog, CachedCurrencyPool> CurrencyPoolsByCatalog = new();

    public static ItemDrops.Drop? RollWeightedCurrency(ItemCatalog catalog, int level, Random rng)
    {
        var snapshot = DropDataProvider.Default.Current;
        if (!CurrencyPoolsByCatalog.TryGetValue(catalog, out var cached) || !ReferenceEquals(cached.Snapshot, snapshot))
        {
            var list = new List<(ItemCatalog.BaseType Type, double Weight, int DropLevel)>();
            if (snapshot.CurrencyWeights.Count > 0)
            {
                foreach (var row in snapshot.CurrencyWeights.Values)
                    if (row is { Enabled: true, Weight: > 0 } && catalog.Find(row.CurrencyId) is { Supported: true } t && t.Id == row.CurrencyId)
                        list.Add((t, row.Weight, Math.Max(t.DropLevel, row.DropLevel)));
            }
            if (list.Count == 0)
            {
                for (int i = 0; i < ItemDrops.Currency.Length; i++)
                    if (catalog.Find(ItemDrops.Currency[i].Id) is { Supported: true } t)
                        list.Add((t, ItemDrops.Currency[i].Weight, t.DropLevel));
            }
            cached = new CachedCurrencyPool(snapshot, [.. list]);
            CurrencyPoolsByCatalog.AddOrUpdate(catalog, cached);
        }
        var pool = cached.Pool;
        double total = 0;
        for (int i = 0; i < pool.Length; i++)
            if (pool[i].DropLevel <= level && pool[i].Weight > 0) total += pool[i].Weight;
        if (total <= 0) return null;
        double roll = rng.NextDouble() * total;
        ItemCatalog.BaseType? picked = null;
        for (int i = 0; i < pool.Length; i++)
        {
            var entry = pool[i];
            if (entry.DropLevel > level || entry.Weight <= 0) continue;
            picked = entry.Type;
            if ((roll -= entry.Weight) < 0) break;
        }
        return picked is null ? null : new(ItemCatalog.Create(picked) with { StackSize = 1, ItemLevel = (byte)Math.Clamp(level, 1, 100) }, picked);
    }

    static double MapDropFactor(MapMods mods, params ReadOnlySpan<uint> ids)
    {
        double factor = 1.0;
        foreach (uint id in ids) factor *= Math.Max(0, 100 + mods.Get(id)) / 100.0;
        return factor;
    }

    static bool MapDropExtra(string setting, double quantity, Random rng) =>
        rng.NextDouble() < Math.Clamp(SystemSettings.Default.Get(setting), 0, 100) / 100.0 * quantity;

    static SingleUseCooldown? WithMapCooldown(Player player, SingleUseCooldown? plan)
    {
        if (plan is not { } cooldown) return plan;
        int more = MapModsOf(player)?.Get(Stat.MapPlayerCooldownSpeedPlusPctFinal) ?? 0;
        if (more == 0) return cooldown;
        double rate = cooldown.RecoveryRate * Math.Max(0, 1 + more / 100.0);
        return cooldown with { RecoveryPercent = (int)Math.Round((rate - 1) * 100) };
    }
}
