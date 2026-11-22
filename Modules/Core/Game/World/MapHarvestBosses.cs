using GameServer.Core;

internal static class MapHarvestBosses
{
    static readonly string[] CountStats = [
        "map_contains_X_harvest_bear_bosses",
        "map_contains_X_harvest_cat_bosses",
        "map_contains_X_harvest_bird_bosses",
    ];

    static readonly Lazy<(string Stat, int Variety)[]> Bosses = new(() =>
    {
        var rows = ClientTables.Table("AdditionalMonstersFromMapStats").ToDictionary(r => r.Int("Stat"));
        return [.. CountStats.Select(stat =>
        {
            int index = checked((int)Program.Stats.Key(stat)!.Value - 1);
            return (stat, rows[index].Ints("Monsters").Single());
        })];
    });

    public static bool IsBoss(int variety) => Bosses.Value.Any(b => b.Variety == variety);

    public static PackCatalog.AreaPacks Apply(ClientState conn, PackCatalog.AreaPacks area, MapMods mods)
    {
        List<AreaPopulation.AdditionalUnique>? additions = null;
        foreach (var (stat, variety) in Bosses.Value)
        {
            int count = Game.AreaStat(conn, stat, mods);
            if (count <= 0) continue;
            int size = Game.Monsters.Find(variety.ToString())!.ObjectSize - 1;
            (additions ??= []).Add(new(stat, variety, count,
                (x, y) => conn.Layout.WalkableFor(x, y, size) && !conn.Barrier.Contains((x, y))));
        }
        return additions is null ? area : area with { AdditionalUniques = additions };
    }
}
