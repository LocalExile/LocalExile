using GameServer.Core.Protocol.Instance;
using GameServer.Modding;
using static Game;

/// <summary>Atlas boss guards of the generated shrine rooms; ordinary instance population owns their lifetime.</summary>
public static class MapScarabBosses
{
    /// <summary>Fills this type's game data at startup (the world loader, before the Gateway listens).</summary>
    public static void LoadGameData()
    {
        Pool = LoadPool();
    }

    public const string Terrors = "Shrines.Terrors", PackId = "Scarab/ShrineAtlasBoss";
    public const string AtlasPacks = "AtlasBossPacks", AtlasPackId = "Scarab/AtlasBossPack";
    static IReadOnlyList<MonsterCatalog.Variety> Pool = default!;

    static IReadOnlyList<MonsterCatalog.Variety> LoadPool()
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
    }
    public static IReadOnlyList<MonsterCatalog.Variety> Bosses => Pool;
    public static bool IsBoss(MonsterCatalog.Variety variety) => Bosses.Any(v => v.Row == variety.Row);

    public static void Opening(MapOpening request)
    {
        if (request.Campaign || request.Opening is not { } opening || Bosses.Count == 0) return;
        request.HandledContent.Add(Terrors);
        if (AreaTransitionCatalog.HashOf(opening.Area.Id) is ushort hash && MapDevices.IsMapArea(hash))
            request.HandledContent.Add(AtlasPacks);
    }

    /// <summary>Pandemonium replaces a whole population pack with one standalone Atlas boss. The population plan owns
    /// its objects and restoration; existing unique packs and later encounter waves are left to their owners.</summary>
    public static List<AreaPopulation.Spawn> ReplacePacks(IReadOnlyList<AreaPopulation.Spawn> source, MapMods? mods,
        uint seed, Func<int, int, int, bool> walkable, List<string>? notes = null)
    {
        int chance = Math.Clamp(mods?.Get(Stat.MapAtlasBossPackReplacementChancePct) ?? 0, 0, 100);
        if (chance == 0 || Bosses.Count == 0) return source.ToList();
        var replacements = new Dictionary<int, AreaPopulation.Spawn>();
        foreach (var group in source.Where(s => s.PackId > 0).GroupBy(s => s.PackId))
        {
            var members = group.ToArray();
            if (members.Any(s => s.Rarity == MonsterRarity.Unique)) continue;
            // Each group has its own stable draw: skipping an already replaced group cannot shift another's result.
            var random = new Random(unchecked((int)(seed ^ 0x50414e44u ^ (uint)group.Key * 0x9e3779b9u)));
            if (random.Next(100) >= chance) continue;
            int start = random.Next(Bosses.Count);
            for (int offset = 0; offset < Bosses.Count; offset++)
            {
                var boss = Bosses[(start + offset) % Bosses.Count];
                int clearance = Math.Clamp(boss.ObjectSize - 1, 0, 4);
                var cell = members.FirstOrDefault(s => walkable(s.X, s.Y, clearance));
                if (cell is null) continue;
                replacements[group.Key] = new(boss.Row, cell.X, cell.Y, MonsterRarity.Unique, AtlasPackId) { PackId = group.Key };
                break;
            }
        }
        var emitted = new HashSet<int>();
        var result = new List<AreaPopulation.Spawn>(source.Count);
        foreach (var spawn in source)
            if (replacements.TryGetValue(spawn.PackId, out var boss))
            {
                if (emitted.Add(spawn.PackId)) result.Add(boss);
            }
            else result.Add(spawn);
        notes?.Add($"atlas boss pack replacements: {replacements.Count} ({chance}% per eligible pack)");
        return result;
    }

    public static IReadOnlyList<AreaPopulation.Spawn> Guards(Player player, IReadOnlyList<(int X, int Y)> shrines,
        IReadOnlyList<AreaPopulation.Spawn> existing, AreaLayout? layout = null)
    {
        // The population is planned ahead of the area's entry, before conn.Layout is the area's: it hands its layout.
        layout ??= player.Layout;
        if (MapModsOf(player)?.Get(Stat.MapShrinesHaveStandaloneMapBoss) is not > 0 || Bosses.Count == 0) return [];
        var random = new Random(unchecked((int)(player.Seed ^ 0x54455252)));
        var result = new List<AreaPopulation.Spawn>();
        foreach (var at in shrines.Distinct())
        {
            var boss = Bosses[random.Next(Bosses.Count)];
            var pack = new PackCatalog.Pack(-1, PackId, 1, 1, 0, 0, [], [(boss.Row, 1)], new Dictionary<int, int>());
            var guards = AreaPopulation.AtPacks([(pack, at.X + ShrinePlacement.ObjectSize + 4, at.Y, MonsterRarity.Unique)],
                (x, y) => layout.WalkableFor(x, y, Math.Clamp(boss.ObjectSize - 1, 0, 4)),
                row => row == boss.Row, random.Next(), occupied: existing.Concat(result).Select(s => (s.X, s.Y)).Append(at));
            result.AddRange(guards);
        }
        Console.WriteLine($"[map-content] shrine atlas bosses area={layout.Id}: placed {result.Count}/{shrines.Distinct().Count()}");
        return result;
    }
}
