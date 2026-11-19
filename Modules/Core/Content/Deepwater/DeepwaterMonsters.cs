using GameServer.Core;
using GameServer.Core.Protocol.Instance;
using GameServer.Modding;

namespace GameServer.Modules.Deepwater;

/// <summary>The league's monsters of a run: planned per cell at the run's start from its biome's MonsterPacks (3149-3167),
/// and spawned only when a lantern's light first covers a pack's centre. The monsters then behave as any other.
/// The real server's mechanism is not known (stat <c>deepwater_hidden_monster_while_not_lantern_touched</c> suggests it
/// placed them hidden); spawning on the light is our guess, as are the counts and spacing below.</summary>
internal static class DeepwaterMonsters
{
    /// <summary>A module-defined monster stat for DeepwaterLifeScalingPerLevel's more life (the .ot's AdditionalLifeScaling
    /// index 26, DeepwaterBase.ot), handed over to MonsterModEffects in DeepwaterModule.Load.</summary>
    public const string LevelMoreLife = "deepwater_level_scaling_life_+%_final";

    /// <summary>A planned pack: its kind, its centre and rarity, and its members (from AreaPopulation.AtPacks).</summary>
    public sealed record Pack(int Cell, PackCatalog.Pack Kind, int X, int Y, MonsterRarity Rarity, IReadOnlyList<AreaPopulation.Spawn> Members);

    // G: the spacing between planned centres, their clearance, and the safe ring around the start (the .ot's killzone 3).
    const int Apart = 25, Clearance = 3, StartClear = 36, Tries = 60;
    /// <summary>The highest run level (G: as a map's).</summary>
    public const int MaxLevel = 84;

    static readonly Lazy<Dictionary<int, int>> PacksPerRoom = new(() => ClientTables.TableOrEmpty("DeepwaterRooms")
        .ToDictionary(r => r.Index, r => r.IntOrNull("__col8") ?? 0));
    static readonly Lazy<Dictionary<int, int>> MinLevels = new(() => ClientTables.TableOrEmpty("MonsterPacks")
        .ToDictionary(r => r.Index, r => r.IntOrNull("MinLevel") ?? 0));
    static readonly Lazy<Dictionary<int, int>> MoreLife = new(() => ClientTables.TableOrEmpty("DeepwaterLifeScalingPerLevel")
        .Where(r => r.IntOrNull("Level") is not null).ToDictionary(r => r.Int("Level"), r => r.IntOrNull("Life") ?? 0));

    /// <summary>DeepwaterLifeScalingPerLevel's Life (percent more life) at a level, clamped to the table.</summary>
    public static int LifeAt(int level)
    {
        var table = MoreLife.Value;
        if (table.Count == 0) return 0;
        return table.TryGetValue(Math.Clamp(level, table.Keys.Min(), table.Keys.Max()), out int life) ? life : 0;
    }

    static bool Spawnable(int variety) => Monsters.Find(variety.ToString()) is { Supported: true };

    /// <summary>The run's packs: per cell DeepwaterRooms <c>__col8</c> packs (8 in every room, 0 in Kishara's Rest; G that
    /// the column is the pack count), each a weighted pick from the cell's biome's scattered packs whose MinLevel is at
    /// most the run's level, centred on a walkable cell with clearance 3, 25 cells from the others and 36 from the start.
    /// The area-0 packs (crab buff, octopus, starfish, coral crab, quill crab) belong to special rooms and are not placed.</summary>
    public static List<Pack> Plan(AreaLayout layout, uint seed, DeepwaterRun run, Action<string> log)
    {
        var random = new Random(AreaPopulation.PopulationSeed(layout.Hash, seed) ^ 0x44575052);
        var start = run.Board.Cells[0].Point("default") ?? (layout.SpawnX, layout.SpawnY);
        var centres = new List<(int X, int Y)>();
        var taken = new List<(int X, int Y)>();
        var plan = new List<Pack>();
        var notes = new List<string>();
        foreach (var cell in run.Board.Cells)
        {
            int count = PacksPerRoom.Value.GetValueOrDefault(cell.Room);
            if (count <= 0 || cell.BiomeArea is not { } biome) continue;
            var pool = (Packs.For(biome)?.Scattered ?? [])
                .Where(p => MinLevels.Value.GetValueOrDefault(p.Pack.Row) <= run.Level && p.Pack.Entries.Any(e => Spawnable(e.Variety)))
                .ToList();
            if (pool.Count == 0) { notes.Add($"cell {cell.Index} {biome}: no packs"); continue; }
            var kinds = new List<string>();
            for (int i = 0; i < count; i++)
            {
                if (Centre(layout, cell, start, centres, random) is not { } at) break;
                centres.Add(at);
                var kind = Pick(pool, random);
                int roll = random.Next(100);
                var rarity = roll < AreaPopulation.RareChance ? MonsterRarity.Rare
                    : roll < AreaPopulation.RareChance + AreaPopulation.MagicChance ? MonsterRarity.Magic : MonsterRarity.Normal;
                var members = AreaPopulation.AtPacks([(kind, at.X, at.Y, rarity)], (x, y) => layout.WalkableFor(x, y, 1),
                    Spawnable, random.Next(), occupied: taken);
                if (members.Count == 0) continue;
                taken.AddRange(members.Select(m => (m.X, m.Y)));
                plan.Add(new Pack(cell.Index, kind, at.X, at.Y, rarity, members));
                kinds.Add(kind.Id.Replace("Deepwater", "") + (rarity == MonsterRarity.Normal ? "" : $"({rarity})"));
            }
            notes.Add($"cell {cell.Index} {biome}: {kinds.Count}/{count} [{string.Join(", ", kinds)}]");
        }
        log($"Allflame monsters: {plan.Count} packs, {plan.Sum(p => p.Members.Count)} monsters planned at level {run.Level} (life +{LifeAt(run.Level)}% more); "
            + string.Join("; ", notes));
        return plan;
    }

    static PackCatalog.Pack Pick(List<(PackCatalog.Pack Pack, int Weight)> pool, Random random)
    {
        int roll = random.Next(Math.Max(1, pool.Sum(p => p.Weight)));
        foreach (var (pack, weight) in pool) if ((roll -= weight) < 0) return pack;
        return pool[^1].Pack;
    }

    static (int X, int Y)? Centre(AreaLayout layout, DeepwaterBoard.Cell cell, (int X, int Y) start, List<(int X, int Y)> centres, Random random)
    {
        for (int t = 0; t < Tries; t++)
        {
            int x = random.Next(cell.X0, cell.X1), y = random.Next(cell.Y0, cell.Y1);
            if (!layout.WalkableFor(x, y, Clearance)) continue;
            if (Near((x, y), start, StartClear)) continue;
            if (centres.Any(c => Near((x, y), c, Apart))) continue;
            return (x, y);
        }
        return null;
    }

    static bool Near((int X, int Y) a, (int X, int Y) b, int distance)
    {
        long dx = a.X - b.X, dy = a.Y - b.Y;
        return dx * dx + dy * dy < (long)distance * distance;
    }

    /// <summary>Spawns the planned packs whose centre is inside <paramref name="lantern"/>'s full light, and takes them off
    /// the plan. Called on each lantern placement (and for the first lantern), never per tick. The monsters belong to
    /// the instance (kept for a re-entry), carry <c>is_near_deepwater_lantern</c> and <c>deepwater_lantern_touched</c>
    /// (G that the real server set them so) and rise as their base does.</summary>
    public static async Task Release(IModuleContext module, ClientState conn, DeepwaterRun run, DeepwaterRun.Lantern lantern)
    {
        List<Pack> due;
        int radius = DeepwaterConstants.BaseLanternRadius;
        lock (run.Gate)
        {
            due = run.Planned.Where(p => Near((p.X, p.Y), (lantern.X, lantern.Y), radius + 1)).ToList();
            if (due.Count == 0) return;
            run.Planned.RemoveAll(due.Contains);
            run.ReleasedPacks += due.Count;
        }
        byte level = (byte)Math.Clamp(run.Level, 1, MaxLevel);
        int life = LifeAt(level);
        var extraMods = life == 0 ? null : new MonsterModSet([], [], new MonsterModEffects(new Dictionary<string, int> { [LevelMoreLife] = life }));
        var extraStats = new List<(uint Key, int Value)>();
        foreach (string stat in (string[])["is_near_deepwater_lantern", "deepwater_lantern_touched"])
            if (Stats.Key(stat) is uint key) extraStats.Add((key, 1));
        var (remote, stream, ciphers) = conn.Wire;
        int spawned = 0;
        foreach (var pack in due)
            foreach (var m in pack.Members)
            {
                if (conn.Closed || conn.LeavingArea) break;
                if (Monsters.Find(m.Variety.ToString()) is not { Supported: true } variety) continue;
                uint id = await SpawnMonster(remote, stream, ciphers, conn, variety, level, m.Rarity, m.X, m.Y, log: false,
                    alone: false, extraStats: extraStats, extraMods: extraMods, instanceOwned: true);
                if (id != 0) spawned++;
            }
        lock (run.Gate) run.ReleasedMonsters += spawned;
        module.Log($"{CharacterName(conn)}: lantern {lantern.Order} at ({lantern.X}, {lantern.Y}) woke {due.Count} packs, {spawned} monsters ("
            + string.Join(", ", due.Select(p => $"{p.Kind.Id} cell {p.Cell} at ({p.X}, {p.Y})")) + ")");
    }
}
