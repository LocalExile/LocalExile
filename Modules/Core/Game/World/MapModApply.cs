using GameServer.Core.Protocol.Instance;

public static partial class Game
{
    /// <summary>The map this connection's instance plays with (InstanceState.Map), or null outside a map.</summary>
    public static MapDevice.Run? MapRunOf(ClientState conn) => conn.Instance?.Map is { } run && run.Area.Hash16 == conn.Layout.Hash ? run : null;

    /// <summary>The map mods this connection's instance plays with: its area stats as the client got them in 0x1d
    /// (MapAreaStats: the explicit mods scaled by the atlas's map mod effect, the atlas's own stats, the fragments), so
    /// what the server applies is what the map shows; the map item's own sum when the instance has no stats.</summary>
    public static MapMods? MapModsOf(ClientState conn)
    {
        if (MapRunOf(conn) is not { } run) return null;
        if (conn.Instance?.KeptAreaStats is not { Count: > 0 } stats) return run.Mods;
        return MapModsByStats.GetValue(stats, s =>
        {
            var names = Stats.Names;
            var byName = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var (key, value) in s)
                if (key >= 1 && key <= names.Count) byName[names[(int)key - 1]] = byName.GetValueOrDefault(names[(int)key - 1]) + value;
            return new MapMods(byName, run.Mods.Quality);
        });
    }

    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<IReadOnlyDictionary<uint, int>, MapMods> MapModsByStats = new();

    internal static int AreaDropStat(ClientState conn, string id, MapMods? areaMods = null)
    {
        if (conn.Instance?.KeptAreaStats is { } kept)
            return kept.GetValueOrDefault(Program.Stats.Key(id)!.Value);
        return (areaMods ?? MapRunOf(conn)?.Mods)?.Get(id) ?? 0;
    }

    internal static void ApplyMonsterDropCorruption(ClientState conn, string? varietyId, List<ItemDrops.Drop> drops, Random random)
    {
        if (MapBeyond.CorruptsDrops(conn, varietyId)) CorruptDrops(drops);
        ApplyAreaDropCorruption(conn, drops, random);
    }

    internal static void ApplyAreaDropCorruption(ClientState conn, List<ItemDrops.Drop> drops, Random random, MapMods? areaMods = null)
    {
        int chance = AreaDropStat(conn, "map_items_drop_corrupted_%", areaMods);
        if (chance > 0)
            CorruptDrops(drops, chance, random, static type => type.CanBeCorrupted || type.Components.Contains("Map"));
    }

    /// <summary>The vital and deadly Atlas keystones' more monster life and damage (Wellspring of Creation, Dance of
    /// Destruction): the area's map_ stat becomes the monster stat of the same name, which acts as a more modifier on
    /// the monster's life or hit (handed over to MonsterModEffects.HandOverMore in GameModule.Load).</summary>
    internal static readonly string[] AtlasMoreLife = ["monster_life_+%_final_from_vital_atlas", "monster_life_+%_final_from_deadly_atlas"],
        AtlasMoreDamage = ["monster_damage_+%_final_from_vital_atlas", "monster_damage_+%_final_from_deadly_atlas"];

    public static Dictionary<string, int> MapMonsterEffects(MapMods mods)
    {
        var extra = mods.MonsterEffectStats();
        foreach (string atlas in AtlasMoreLife.Concat(AtlasMoreDamage))
            if (mods.Get("map_" + atlas) is int more and not 0) extra[atlas] = more;
        if (mods.Get("map_monsters_cannot_be_stunned") != 0)
            extra["base_cannot_be_stunned"] = 1;
        void Gain(string mapId, string monsterId)
        {
            if (mods.Get(mapId) is int value and not 0)
                extra[monsterId] = extra.GetValueOrDefault(monsterId) + value;
        }
        Gain("map_monsters_%_physical_damage_to_add_as_fire", "physical_damage_%_to_add_as_fire");
        Gain("map_monsters_%_physical_damage_to_add_as_cold", "physical_damage_%_to_add_as_cold");
        Gain("map_monsters_%_physical_damage_to_add_as_lightning", "physical_damage_%_to_add_as_lightning");
        Gain("map_monsters_attack_speed_+%", "attack_speed_+%");
        Gain("map_monsters_cast_speed_+%", "base_cast_speed_+%");
        Gain("map_monsters_movement_speed_+%", "base_movement_velocity_+%");
        if (mods.Get("map_monsters_movement_speed_cannot_be_reduced_below_base") != 0)
            extra["movement_speed_cannot_be_reduced_below_base"] = 1;
        Gain("map_monsters_additional_physical_damage_reduction", "base_additional_physical_damage_reduction_%");
        Gain("map_monsters_additional_elemental_resistance", "base_resist_all_elements_%");
        Gain("map_monsters_additional_chaos_resistance", "base_chaos_damage_resistance_%");
        if (mods.Get("map_monsters_are_hexproof") != 0)
            extra["hexproof"] = 1;
        Gain("map_monsters_curse_effect_on_self_+%_final", "map_monsters_curse_effect_on_self_+%_final");
        Gain("map_monsters_chance_to_blind_on_hit_%", "global_chance_to_blind_on_hit_%");
        Gain("map_monsters_critical_strike_chance_+%", "critical_strike_chance_+%");
        Gain("map_monsters_critical_strike_multiplier_+", "base_critical_strike_multiplier_+");
        Gain("map_monsters_penetrate_elemental_resistances_%", "reduce_enemy_elemental_resistance_%");
        Gain("map_monsters_base_bleed_duration_+%", "base_bleed_duration_+%");
        // The extracted map rows use the real monster stats rather than a synthetic "global" flag:
        // MapPoisoning: map_monsters_poison_on_hit=1;
        // MapUberModPoison: the same plus all_damage_can_poison and +100% poison duration.
        if (mods.Get("map_monsters_poison_on_hit") != 0)
            extra["base_chance_to_poison_on_hit_%"] =
                extra.GetValueOrDefault("base_chance_to_poison_on_hit_%") + 100;
        Gain("map_monsters_base_poison_duration_+%", "base_poison_duration_+%");
        Gain("map_monsters_all_damage_can_poison", "all_damage_can_poison");
        return extra;
    }

    public static IReadOnlyDictionary<string, int> MapBossEffects(MapMods mods) => new Dictionary<string, int>
    {
        ["maximum_life_+%"] = mods.Get("map_boss_maximum_life_+%"),
        ["damage_+%"] = mods.Get("map_boss_damage_+%"),
        ["attack_speed_+%"] = mods.Get("map_boss_attack_and_cast_speed_+%"),
        ["base_cast_speed_+%"] = mods.Get("map_boss_attack_and_cast_speed_+%"),
        ["base_item_found_quantity_+%"] = mods.Get("map_boss_dropped_item_quantity_+%"),
        ["base_item_found_rarity_+%"] = mods.Get("map_boss_item_rarity_+%"),
    };

    static IEnumerable<AreaPopulation.Spawn> WithTwinBosses(IEnumerable<AreaPopulation.Spawn> spawns, MapMods? mods)
    {
        if ((mods?.Get("map_spawn_two_bosses") ?? 0) == 0) return spawns;
        var list = spawns.ToList();
        foreach (var boss in list.Where(s => s.Rarity == MonsterRarity.Unique).ToList())
            list.Add(boss with { X = boss.X + 6, Pack = boss.Pack + "-twin" });
        return list;
    }

    /// <summary>A map's extra drops: a card from each monster of the card-dropping magic packs, and the map's card and
    /// scarab drop factors (its increased and more modifiers multiplied, 1 for none) on the base map chances
    /// (<c>drop_card_chance_pct</c>, <c>drop_scarab_chance_pct</c>, ItemDrops.Roll): a factor f adds the base chance
    /// × (f − 1) per kill.</summary>
    static IEnumerable<ItemDrops.Drop> ExtraDropsFromMap(ClientState conn, int monsterLevel, MonsterRarity rarity, Random rng)
    {
        if (MapModsOf(conn) is not { } mods) yield break;
        var drops = DropsFor(conn);
        if (rarity == MonsterRarity.Magic && mods.PacksDropCards > 0 && drops.Card(rng, monsterLevel) is { } packCard)
            yield return packCard;
        double card = MapDropFactor(mods, "map_divination_card_drop_chance_+%", "map_divination_card_drop_chance_+%_final_from_quality",
            "map_divination_card_drop_chance_+%_final_from_faridun_wish");
        if (card > 1 && MapDropExtra("drop_card_chance_pct", card - 1, rng) && drops.Card(rng, monsterLevel) is { } extraCard)
            yield return extraCard;
        double scarab = MapDropFactor(mods, "map_scarab_drop_chance_+%_final_from_uber_mod",
            "map_unscaled_scarab_drop_chance_+%_final_from_uber_mod", "map_scarab_drop_chance_+%_final_from_faridun_wish");
        if (scarab > 1 && MapDropExtra("drop_scarab_chance_pct", scarab - 1, rng) && drops.Scarab(rng, monsterLevel, mods) is { } extraScarab)
            yield return extraScarab;
        double currency = MapDropFactor(mods, "map_currency_drop_chance_+%_final_from_uber_mod");
        if (currency > 1)
        {
            double expected = CurrencyDropExpected(rarity) * (currency - 1);
            int whole = (int)expected + (rng.NextDouble() < expected - (int)expected ? 1 : 0);
            for (int i = 0; i < whole; i++)
                if (drops.CurrencyDrop(monsterLevel, rng) is { } extraCurrency) yield return extraCurrency;
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
                    if (row.Weight > 0 && catalog.Find(row.CurrencyId) is { Supported: true } t && t.Id == row.CurrencyId)
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

    static double MapDropFactor(MapMods mods, params ReadOnlySpan<string> ids)
    {
        double factor = 1.0;
        foreach (string id in ids) factor *= Math.Max(0, 100 + mods.Get(id)) / 100.0;
        return factor;
    }

    static bool MapDropExtra(string setting, double quantity, Random rng) =>
        rng.NextDouble() < Math.Clamp(SystemSettings.Default.Get(setting), 0, 100) / 100.0 * quantity;

    static SingleUseCooldown? WithMapCooldown(ClientState conn, SingleUseCooldown? plan)
    {
        if (plan is not { } cooldown) return plan;
        int more = MapModsOf(conn)?.Get("map_player_cooldown_speed_+%_final") ?? 0;
        if (more == 0) return cooldown;
        double rate = cooldown.RecoveryRate * Math.Max(0, 1 + more / 100.0);
        return cooldown with { RecoveryPercent = (int)Math.Round((rate - 1) * 100) };
    }
}
