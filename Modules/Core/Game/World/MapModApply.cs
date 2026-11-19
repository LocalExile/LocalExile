using GameServer.Core.Protocol.Instance;

public static partial class Game
{
    /// <summary>The map this connection's instance plays with (InstanceState.Map), or null outside a map.</summary>
    public static MapDevice.Run? MapRunOf(Player conn) => conn.Instance?.Map is { } run && run.Area.Hash16 == conn.Layout.Hash ? run : null;

    /// <summary>The map mods this connection's instance plays with: its area stats as the client got them in 0x1d
    /// (MapAreaStats: the explicit mods scaled by the atlas's map mod effect, the atlas's own stats, the fragments), so
    /// what the server applies is what the map shows; the map item's own sum when the instance has no stats.</summary>
    public static MapMods? MapModsOf(Player conn)
    {
        if (MapRunOf(conn) is not { } run) return null;
        if (conn.Instance?.KeptAreaStats is not { Count: > 0 } stats) return run.Mods;
        return MapModsByStats.GetValue(stats, s =>
        {
            var byId = new Dictionary<uint, int>();
            foreach (var (key, value) in s)
                if (key >= 1 && key <= Stats.Names.Count) byId[key] = byId.GetValueOrDefault(key) + value;
            return new MapMods(byId, run.Mods.Quality);
        });
    }

    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<IReadOnlyDictionary<uint, int>, MapMods> MapModsByStats = new();

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

    /// <summary>A map's extra drops: a card from each monster of the card-dropping magic packs, and the map's card and
    /// scarab drop factors (its increased and more modifiers multiplied, 1 for none) on the base map chances
    /// (<c>drop_card_chance_pct</c>, <c>drop_scarab_chance_pct</c>, ItemDrops.Roll): a factor f adds the base chance
    /// × (f − 1) per kill.</summary>
    static IEnumerable<ItemDrops.Drop> ExtraDropsFromMap(Player conn, int monsterLevel, MonsterRarity rarity, Random rng)
    {
        if (MapModsOf(conn) is not { } mods) yield break;
        var drops = DropsFor(conn);
        if (rarity == MonsterRarity.Magic && mods.PacksDropCards > 0 && drops.Card(rng, monsterLevel) is { } packCard)
            yield return packCard;
        double card = MapDropFactor(mods, Stat.MapDivinationCardDropChancePlusPct, Stat.MapDivinationCardDropChancePlusPctFinalFromQuality,
            Stat.MapDivinationCardDropChancePlusPctFinalFromFaridunWish);
        if (card > 1 && MapDropExtra("drop_card_chance_pct", card - 1, rng) && drops.Card(rng, monsterLevel) is { } extraCard)
            yield return extraCard;
        double scarab = MapDropFactor(mods, Stat.MapScarabDropChancePlusPctFinalFromUberMod,
            Stat.MapUnscaledScarabDropChancePlusPctFinalFromUberMod, Stat.MapScarabDropChancePlusPctFinalFromFaridunWish);
        if (scarab > 1 && MapDropExtra("drop_scarab_chance_pct", scarab - 1, rng) && drops.Scarab(rng, monsterLevel) is { } extraScarab)
            yield return extraScarab;
    }

    static double MapDropFactor(MapMods mods, params uint[] ids)
    {
        double factor = 1.0;
        foreach (uint id in ids) factor *= Math.Max(0, 100 + mods.Get(id)) / 100.0;
        return factor;
    }

    static bool MapDropExtra(string setting, double quantity, Random rng) =>
        rng.NextDouble() < Math.Clamp(SystemSettings.Default.Get(setting), 0, 100) / 100.0 * quantity;

    static SingleUseCooldown? WithMapCooldown(Player conn, SingleUseCooldown? plan)
    {
        if (plan is not { } cooldown) return plan;
        int more = MapModsOf(conn)?.Get(Stat.MapPlayerCooldownSpeedPlusPctFinal) ?? 0;
        if (more == 0) return cooldown;
        double rate = cooldown.RecoveryRate * Math.Max(0, 1 + more / 100.0);
        return cooldown with { RecoveryPercent = (int)Math.Round((rate - 1) * 100) };
    }
}
