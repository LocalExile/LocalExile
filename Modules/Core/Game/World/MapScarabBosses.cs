using GameServer.Core.Protocol.Instance;
using GameServer.Modding;
using static Game;

/// <summary>Atlas boss guards of the generated shrine rooms; ordinary instance population owns their lifetime.</summary>
public static class MapScarabBosses
{
    public const string Terrors = "Shrines.Terrors", PackId = "Scarab/ShrineAtlasBoss";
    static readonly Lazy<IReadOnlyList<MonsterCatalog.Variety>> Pool = new(() =>
    {
        var result = new List<MonsterCatalog.Variety>();
        foreach (string id in MapBossFights.Current.AtlasBosses.Distinct())
        {
            if (Monsters.Find(id) is not { } source) continue;
            // The standalone varieties omit their arena scripts. Do not transplant a campaign phase controller.
            var standalone = Monsters.All.Where(v => v.Supported && v.Name == source.Name
                && v.Id.Contains("Standalone", StringComparison.Ordinal) && !v.Id.Contains("AtlasUber", StringComparison.Ordinal))
                .OrderByDescending(v => v.Id == id + "Standalone").ThenBy(v => v.Id.Length).ThenBy(v => v.Id, StringComparer.Ordinal).FirstOrDefault();
            if (standalone is not null && result.All(v => v.Row != standalone.Row)) result.Add(standalone);
        }
        return result;
    });
    public static IReadOnlyList<MonsterCatalog.Variety> Bosses => Pool.Value;
    public static bool IsBoss(MonsterCatalog.Variety variety) => Bosses.Any(v => v.Row == variety.Row);

    public static void Opening(MapOpening request)
    {
        if (!request.Campaign && request.Opening is not null && Bosses.Count > 0) request.HandledContent.Add(Terrors);
    }

    public static IReadOnlyList<AreaPopulation.Spawn> Guards(ClientState conn, IReadOnlyList<(int X, int Y)> shrines,
        IReadOnlyList<AreaPopulation.Spawn> existing)
    {
        if (MapModsOf(conn)?.Get("map_shrines_have_standalone_map_boss") is not > 0 || Bosses.Count == 0) return [];
        var random = new Random(unchecked((int)(conn.Seed ^ 0x54455252)));
        var result = new List<AreaPopulation.Spawn>();
        foreach (var at in shrines.Distinct())
        {
            var boss = Bosses[random.Next(Bosses.Count)];
            var pack = new PackCatalog.Pack(-1, PackId, 0, 0, 100, 1, [boss.Row], [], new Dictionary<int, int>());
            var guards = AreaPopulation.AtPacks([(pack, at.X + ShrinePlacement.ObjectSize + 4, at.Y, MonsterRarity.Unique)],
                (x, y) => conn.Layout.WalkableFor(x, y, Math.Clamp(boss.ObjectSize - 1, 0, 4)),
                row => row == boss.Row, random.Next(), occupied: existing.Concat(result).Select(s => (s.X, s.Y)).Append(at));
            result.AddRange(guards);
        }
        Console.WriteLine($"[map-content] shrine atlas bosses area={conn.Layout.Id}: placed {result.Count}/{shrines.Distinct().Count()}");
        return result;
    }
}
