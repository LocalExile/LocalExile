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

    static Dictionary<string, int> MapMonsterEffects(MapMods mods)
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

    static IEnumerable<ItemDrops.Drop> ExtraDropsFromMap(ClientState conn, int monsterLevel, MonsterRarity rarity, Random rng)
    {
        if (MapModsOf(conn) is not { } mods) yield break;
        var drops = DropsFor(conn);
        if (rarity == MonsterRarity.Magic && mods.PacksDropCards > 0 && drops.Card(rng, monsterLevel) is { } packCard)
            yield return packCard;
        if (mods.CardDropChance > 0 && rng.Next(100) < Math.Clamp(mods.CardDropChance, 0, 80) && drops.Card(rng, monsterLevel) is { } card)
            yield return card;
        if (mods.ScarabDropChance > 0 && rng.Next(100) < Math.Clamp(mods.ScarabDropChance, 0, 80) && drops.Scarab(rng, monsterLevel) is { } scarab)
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
