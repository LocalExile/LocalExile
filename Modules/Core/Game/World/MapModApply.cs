using GameServer.Core.Protocol.Instance;

static partial class Game
{
    static MapMods? MapModsOf(ClientState conn) =>
        MapDevice.Current(CharacterName(conn), conn.Layout.Hash)?.Mods;

    internal static Dictionary<string, int> MapMonsterEffects(MapMods mods)
    {
        var extra = mods.MonsterEffectStats();
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

    static IEnumerable<AreaPopulation.Spawn> WithTwinBosses(IEnumerable<AreaPopulation.Spawn> spawns, MapMods? mods)
    {
        if ((mods?.Get("map_spawn_two_bosses") ?? 0) == 0) return spawns;
        var list = spawns.ToList();
        foreach (var boss in list.Where(s => s.Rarity == MonsterRarity.Unique).ToList())
            list.Add(boss with { X = boss.X + 6, Pack = boss.Pack + "-twin" });
        return list;
    }

    static IEnumerable<ItemDrops.Drop> ExtraDropsFromMap(ClientState conn, MonsterRarity rarity, Random rng)
    {
        if (MapModsOf(conn) is not { } mods) yield break;
        var drops = DropsFor(conn);
        if (rarity == MonsterRarity.Magic && mods.PacksDropCards > 0 && drops.Card(rng) is { } packCard)
            yield return packCard;
        if (mods.CardDropChance > 0 && rng.Next(100) < Math.Clamp(mods.CardDropChance, 0, 80) && drops.Card(rng) is { } card)
            yield return card;
        if (mods.ScarabDropChance > 0 && rng.Next(100) < Math.Clamp(mods.ScarabDropChance, 0, 80) && drops.Scarab(rng) is { } scarab)
            yield return scarab;
    }

    static SingleUseCooldown? WithMapCooldown(ClientState conn, SingleUseCooldown? plan)
    {
        if (plan is not { } cooldown) return plan;
        int more = MapModsOf(conn)?.Get("map_player_cooldown_speed_+%_final") ?? 0;
        if (more == 0) return cooldown;
        double rate = cooldown.RecoveryRate * Math.Max(0, 1 + more / 100.0);
        return cooldown with { RecoveryPercent = (int)Math.Round((rate - 1) * 100) };
    }
}
