using GameServer.Core;
using GameServer.Core.Protocol.Instance;
using GameServer.Modding;
using static Game;

public static class MapAllflame
{
    public const string Content = "Betrayal.Allflame";
    const string Label = "Allflame/";
    sealed record Entry(uint Stat, IReadOnlyList<PackCatalog.Pack> Packs);
    static IReadOnlyList<Entry> entries = [];

    public static void LoadGameData()
    {
        var packs = ClientTables.TableOrEmpty("MonsterPacks").ToArray();
        entries = [.. ClientTables.TableOrEmpty("AdditionalMonsterPacksFromStats")
            .Where(r => r.IntOrNull("__col1") == 5 && r.IntOrNull("StatsKey") is int stat
                && Program.Stats.Name((uint)stat + 1).StartsWith("map_num_allflame_", StringComparison.Ordinal))
            .Select(r => new Entry((uint)r.Int("StatsKey") + 1,
                r.Ints("MonsterPacksKeys").Where(k => k >= 0 && k < packs.Length)
                    .Select(k => Game.Packs.Find(packs[k].Text("Id") ?? ""))
                    .OfType<PackCatalog.Pack>().Where(p => p.Entries.Count > 0).ToArray()))];
    }

    public static void Opening(MapOpening request)
    {
        if (!request.Campaign && request.Opening is { } opening && MapDevices.IsMapArea(opening.Area.Hash16)
            && entries.Count > 0) request.HandledContent.Add(Content);
    }

    public static int Count(int original, int increased) => (int)Math.Clamp((long)Math.Max(0, original)
        * Math.Max(0L, 100L + increased) / 100, 0, ushort.MaxValue);

    public static List<AreaPopulation.Spawn> ReplacePacks(IReadOnlyList<AreaPopulation.Spawn> source, MapMods? mods,
        uint seed, Func<int, int, bool> floor, List<string>? notes = null)
    {
        if (mods is null || entries.All(e => mods.Get(e.Stat) <= 0)) return source.ToList();
        var groups = source.Where(s => s.PackId > 0).GroupBy(s => s.PackId)
            .Where(g => g.All(s => s.Rarity != MonsterRarity.Unique && !s.Pack.StartsWith(Label, StringComparison.Ordinal)))
            .OrderBy(g => g.Key).ToArray();
        var rng = new Random(unchecked((int)seed ^ 0x414c4c46));
        rng.Shuffle(groups);
        var replacements = new Dictionary<int, List<AreaPopulation.Spawn>>();
        int next = 0;
        foreach (var entry in entries)
        {
            int count = Math.Max(0, Count(mods.Get(entry.Stat), mods.Get(Stat.MapAllflameWeightEffectivenessPct))
                - source.Where(s => s.Pack == Label + entry.Stat).Select(s => s.PackId).Distinct().Count());
            var pool = entry.Packs.Where(p => p.Entries.Select(e => e.Variety).Concat(p.BossVarieties)
                .All(v => Game.Monsters.Find(v.ToString()) is { Supported: true })).ToArray();
            if (pool.Length == 0) continue;
            int replaced = 0;
            while (replaced < count && next < groups.Length)
            {
                var group = groups[next++];
                var members = group.ToArray();
                var at = members[rng.Next(members.Length)];
                var pack = pool[rng.Next(pool.Length)];
                var made = AreaPopulation.AtPacks([(pack, at.X, at.Y, at.Rarity)], floor,
                    v => Game.Monsters.Find(v.ToString()) is { Supported: true }, rng.Next(), mods.PackSizeFactor);
                if (made.Count == 0) continue;
                replacements[group.Key] = [.. made.Select(s => s with { PackId = group.Key, Pack = Label + entry.Stat })];
                replaced++;
            }
            if (count > 0) notes?.Add($"allflame {Program.Stats.Name(entry.Stat)}: replaced {replaced}/{count} packs");
        }
        var result = new List<AreaPopulation.Spawn>();
        var emitted = new HashSet<int>();
        foreach (var spawn in source)
            if (!replacements.TryGetValue(spawn.PackId, out var replacement)) result.Add(spawn);
            else if (emitted.Add(spawn.PackId)) result.AddRange(replacement);
        return result;
    }
}
