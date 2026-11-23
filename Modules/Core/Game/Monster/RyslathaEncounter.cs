using GameServer.Core.Protocol.Instance;

public static class RyslathaEncounter
{
    public const string Area = "2_6_11";
    public const string Nest = "Metadata/Monsters/ParasiteNest/ParasiteNestRyslatha";
    public const string Boss = "Metadata/Monsters/InsectSpawner/InsectSpawnerParasiteBoss";

    // bossfight.arm puts three nests around a single unnumbered mapboss point.
    internal static (int X, int Y)? Place(IReadOnlyList<AreaPopulation.RoomPoint> points, Func<int, int, bool> walkable,
        List<AreaPopulation.Spawn> spawns)
    {
        if (Game.Monsters.Find(Nest) is not { Supported: true, Id: Nest } nest
            || Game.Monsters.Find(Boss) is not { Supported: true, Id: Boss }
            || points.FirstOrDefault(p => p.Name == "mapboss") is not { } boss
            || AreaPopulation.BossCell(boss, walkable) is not { } arena)
            return null;
        var positions = points.Where(p => p.Name == Nest).Select(p => (p.X, p.Y)).Distinct().ToList();
        if (positions.Count != 3 || positions.Any(p => !walkable(p.X, p.Y))) return null;
        foreach (var p in positions)
            spawns.Add(new(nest.Row, p.X, p.Y, MonsterRarity.Unique, "Ryslatha's nest"));
        return arena;
    }
}
