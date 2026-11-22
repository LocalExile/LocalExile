using GameServer.Core;

internal static class MapValdoUniques
{
    const string ExilehunterStat = "map_contains_evil_einhar";
    static readonly string[] HarvestCountStats = [
        "map_contains_X_harvest_bear_bosses",
        "map_contains_X_harvest_cat_bosses",
        "map_contains_X_harvest_bird_bosses",
    ];

    static readonly Lazy<(string Stat, int Variety)[]> HarvestBosses = new(() =>
        [.. HarvestCountStats.Select(stat => (stat, VarietyFor(stat)))]);
    static readonly Lazy<int> Exilehunter = new(() => VarietyFor(ExilehunterStat));

    static int VarietyFor(string stat)
    {
        int index = checked((int)Program.Stats.Key(stat)!.Value - 1);
        return ClientTables.Table("AdditionalMonstersFromMapStats").Single(r => r.Int("Stat") == index).Ints("Monsters").Single();
    }

    public static bool IsHarvestBoss(int variety) => HarvestBosses.Value.Any(b => b.Variety == variety);

    public static PackCatalog.AreaPacks Apply(ClientState conn, PackCatalog.AreaPacks area, MapMods mods)
    {
        List<AreaPopulation.AdditionalUnique>? additions = null;
        void AddUnique(string stat, int variety, int count)
        {
            int size = Game.Monsters.Find(variety.ToString())!.ObjectSize - 1;
            (additions ??= []).Add(new(stat, variety, count,
                (x, y) => conn.Layout.WalkableFor(x, y, size) && !conn.Barrier.Contains((x, y))));
        }
        foreach (var (stat, variety) in HarvestBosses.Value)
        {
            int count = Game.AreaStat(conn, stat, mods);
            if (count > 0) AddUnique(stat, variety, count);
        }
        // The Exilehunter modifier indicates presence even when explicit modifier effect raises its value.
        if (Game.AreaStat(conn, ExilehunterStat, mods) > 0) AddUnique(ExilehunterStat, Exilehunter.Value, 1);
        return additions is null ? area : area with { AdditionalUniques = additions };
    }
}
