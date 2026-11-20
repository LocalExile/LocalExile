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
    /// <remarks>A planned run's cell (its own stats and the Voyage-wide ones, DeepwaterPlan.Effective) changes its packs:
    /// <c>map_number_of_magic/rare_packs_+%</c> scale the magic and rare chances, <c>map_all_monster_packs_magic_and_allow_rare_chance_%</c>
    /// makes its normal packs magic, and each <c>chart_contains_x_additional_*_packs</c> adds that many packs of its kind
    /// (all G: the counts and the kinds' packs are not in the client data).</remarks>
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
            var pool = Pool(biome, run.Level);
            if (pool.Count == 0) { notes.Add($"cell {cell.Index} {biome}: no packs"); continue; }
            var stats = run.Plan?.Effective(cell.Index);
            var extra = stats is null ? [] : Additional(stats, pool);
            int magic = stats is null ? 100 : Math.Max(0, 100 + stats.GetValueOrDefault("map_number_of_magic_packs_+%"));
            int rare = stats is null ? 100 : Math.Max(0, 100 + stats.GetValueOrDefault("map_number_of_rare_packs_+%"));
            bool allMagic = stats?.GetValueOrDefault("map_all_monster_packs_magic_and_allow_rare_chance_%") > 0;
            double packSize = stats is null ? 1.0 : Math.Max(0.1, (100 + stats.GetValueOrDefault("map_pack_size_+%")) / 100.0);
            var kinds = new List<string>();
            for (int i = 0; i < count + extra.Count; i++)
            {
                if (Centre(layout, cell, start, centres, random) is not { } at) break;
                centres.Add(at);
                var kind = i < count ? Pick(pool, random) : extra[i - count];
                var rarity = Rarity(random, magic, rare);
                if (allMagic && rarity == MonsterRarity.Normal) rarity = MonsterRarity.Magic;
                var members = AreaPopulation.AtPacks([(kind, at.X, at.Y, rarity)], (x, y) => layout.WalkableFor(x, y, 1),
                    Spawnable, random.Next(), size: packSize, occupied: taken);
                if (members.Count == 0) continue;
                taken.AddRange(members.Select(m => (m.X, m.Y)));
                plan.Add(new Pack(cell.Index, kind, at.X, at.Y, rarity, members));
                kinds.Add(kind.Id.Replace("Deepwater", "") + (rarity == MonsterRarity.Normal ? "" : $"({rarity})"));
            }
            bool wantOctopus = stats?.GetValueOrDefault("chart_contains_giant_octopus") > 0;
            bool wantCrab = stats?.GetValueOrDefault("chart_contains_crab_miniboss") > 0;
            void AddMiniboss(string varietyId)
            {
                if (Monsters.Find(varietyId) is not { Supported: true } boss
                    || Centre(layout, cell, start, centres, random) is not { } bossAt) return;
                centres.Add(bossAt);
                taken.Add(bossAt);
                var bossPack = new PackCatalog.Pack(-1, boss.Name, 1, 1, 0, 0, [], [(boss.Row, 1)], new Dictionary<int, int>());
                plan.Add(new Pack(cell.Index, bossPack, bossAt.X, bossAt.Y, MonsterRarity.Unique,
                    [new AreaPopulation.Spawn(boss.Row, bossAt.X, bossAt.Y, MonsterRarity.Unique, boss.Name)]));
                kinds.Add($"{boss.Name}(Unique)");
            }
            if (wantOctopus) AddMiniboss(DeepwaterCursedDucats.FilthscrabbleVariety);
            if (wantCrab) AddMiniboss(DeepwaterCursedDucats.CaptainsbaneVariety);
            // G: occasional Filthscrabble (Sandy) or Captainsbane (Coral) miniboss so their Cursed Ducats are organically obtainable.
            int minibossChance = DeepwaterLoot.Setting("deepwater_miniboss_chance");
            string? minibossId = biome switch
            {
                "DeepwaterSandy" when !wantOctopus => DeepwaterCursedDucats.FilthscrabbleVariety,
                "DeepwaterCoralForest" or "DeepwaterCoralReef" when !wantCrab => DeepwaterCursedDucats.CaptainsbaneVariety,
                _ => null,
            };
            if (minibossId is not null && minibossChance > 0 && random.Next(100) < minibossChance)
                AddMiniboss(minibossId);
            notes.Add($"cell {cell.Index} {biome}: {kinds.Count}/{count} [{string.Join(", ", kinds)}]");
        }
        log($"Allflame monsters: {plan.Count} packs, {plan.Sum(p => p.Members.Count)} monsters planned at level {run.Level} (life +{LifeAt(run.Level)}% more); "
            + string.Join("; ", notes));
        return plan;
    }

    /// <summary>A biome's scattered packs whose MinLevel is at most the run's level and that have a monster the server
    /// spawns, with their weights.</summary>
    /// <summary>Picks a supported monster variety Id from <paramref name="biome"/>'s scattered packs (fallback
    /// <c>DeepwaterSandy</c>), for Essence monolith placement inside a Deepwater cell.</summary>
    internal static string? PickVariety(string? biome, int level, Random random)
    {
        var pool = biome is not null ? Pool(biome, level) : [];
        if (pool.Count == 0 && biome != "DeepwaterSandy") pool = Pool("DeepwaterSandy", level);
        if (pool.Count == 0) return null;
        var pack = Pick(pool, random);
        var entries = pack.Entries.Where(e => Spawnable(e.Variety)).ToArray();
        if (entries.Length == 0) return null;
        int roll = random.Next(Math.Max(1, entries.Sum(e => e.Weight)));
        int variety = entries[^1].Variety;
        foreach (var e in entries) if ((roll -= e.Weight) < 0) { variety = e.Variety; break; }
        return Monsters.Find(variety.ToString())?.Id;
    }

    static List<(PackCatalog.Pack Pack, int Weight)> Pool(string biome, int level) => [.. (Packs.For(biome)?.Scattered ?? [])
        .Where(p => MinLevels.Value.GetValueOrDefault(p.Pack.Row) <= level && p.Pack.Entries.Any(e => Spawnable(e.Variety)))];

    internal static MonsterRarity Rarity(Random random) => Rarity(random, 100, 100);

    /// <summary>A pack's rarity with the magic and rare chances scaled by <paramref name="magic"/> and
    /// <paramref name="rare"/> percent (100: unchanged).</summary>
    static MonsterRarity Rarity(Random random, int magic, int rare)
    {
        int rareChance = AreaPopulation.RareChance * rare, magicChance = AreaPopulation.MagicChance * magic;
        int roll = random.Next(10000);
        return roll < rareChance ? MonsterRarity.Rare : roll < rareChance + magicChance ? MonsterRarity.Magic : MonsterRarity.Normal;
    }

    // The kinds of chart_contains_x_additional_<kind>_packs and the MonsterPacks they take, the cell's own pool first (G).
    static readonly (string Stat, string[] Packs)[] AdditionalKinds =
    [
        ("chart_contains_x_additional_crab_packs", ["DeepwaterSandyCrabPack", "DeepwaterCoralCrabPack"]),
        ("chart_contains_x_additional_starfish_packs", ["DeepwaterStarfishStatPack"]),
        ("chart_contains_x_additional_octopus_packs", ["DeepwaterOctopusStatPack"]),
        ("chart_contains_x_additional_drowned_packs", ["DeepwaterDrownedPack", "DeepwaterDrownedPackSlam"]),
        ("chart_contains_x_additional_sea_beast_packs", ["DeepwaterSandyBeastPackStarfish", "DeepwaterSandyBeastPackNoStarfish"]),
        ("chart_contains_an_additional_pirate_pack", ["atlas-ghostpirate1", "atlas-ghostpirate2", "Act6-GhostPirateGreen", "expedition-ghost-pirates"]),
    ];

    /// <summary>The extra packs a cell's stats ask for, in order.</summary>
    static List<PackCatalog.Pack> Additional(Dictionary<string, int> stats, List<(PackCatalog.Pack Pack, int Weight)> pool)
    {
        var extra = new List<PackCatalog.Pack>();
        foreach (var (stat, ids) in AdditionalKinds)
        {
            int count = Math.Clamp(stats.GetValueOrDefault(stat), 0, 20);
            if (count == 0) continue;
            var kind = pool.Select(p => p.Pack).FirstOrDefault(p => ids.Contains(p.Id))
                ?? ids.Select(id => Packs.Find(id)).FirstOrDefault(p => p is not null && p.Entries.Any(e => Spawnable(e.Variety)));
            if (kind is null) continue;
            for (int i = 0; i < count; i++) extra.Add(kind);
        }
        return extra;
    }

    /// <summary>A run's monster mods at <paramref name="level"/>: its more life, Cursed Ducat monster stats, and a Voyage
    /// cell's map effects (null when there are none).</summary>
    public static MonsterModSet? ModsFor(int level, IReadOnlyDictionary<string, int>? cell, IReadOnlyDictionary<string, int>? cursed = null)
    {
        var merged = DeepwaterCursedDucats.MergeMonsterStats(cell, cursed);
        var stats = new Dictionary<string, int>(merged ?? new Dictionary<string, int>(), StringComparer.Ordinal);
        if (LifeAt(Math.Clamp(level, 1, MaxLevel)) is int life and not 0) stats[LevelMoreLife] = stats.GetValueOrDefault(LevelMoreLife) + life;
        return stats.Count == 0 ? null : new MonsterModSet([], [], new MonsterModEffects(stats));
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
        int spawned = await Spawn(conn, run, due.SelectMany(p => p.Members));
        lock (run.Gate) run.ReleasedMonsters += spawned;
        module.Log($"{CharacterName(conn)}: lantern {lantern.Order} at ({lantern.X}, {lantern.Y}) woke {due.Count} packs, {spawned} monsters ("
            + string.Join(", ", due.Select(p => $"{p.Kind.Id} cell {p.Cell} at ({p.X}, {p.Y})")) + ")");
    }

    // Spawns monsters at the run's level with its more life and the lantern stats (G that the real server set them so).
    static async Task<int> Spawn(ClientState conn, DeepwaterRun run, IEnumerable<AreaPopulation.Spawn> members)
    {
        byte level = (byte)Math.Clamp(run.Level, 1, MaxLevel);
        var runMods = run.RunMods ?? ModsFor(level, null);
        var extraStats = new List<(uint Key, int Value)>();
        foreach (string stat in (string[])["is_near_deepwater_lantern", "deepwater_lantern_touched"])
            if (Stats.Key(stat) is uint key) extraStats.Add((key, 1));
        var (remote, stream, ciphers) = conn.Wire;
        int spawned = 0;
        foreach (var m in members)
        {
            if (conn.Closed || conn.LeavingArea) break;
            if (Monsters.Find(m.Variety.ToString()) is not { Supported: true } variety) continue;
            int cell = run.CellIndex(m.X, m.Y);
            // map_rare_monsters_trapped_in_essences: imprison rare monsters in Essence monoliths (respecting cell/upgrade mods).
            if (m.Rarity == MonsterRarity.Rare && run.CellRules is { } essenceRules
                && (uint)cell < (uint)essenceRules.Length && essenceRules[cell].RareTrappedInEssence)
            {
                var cellMods = run.Plan is not null ? new MapMods(run.Plan.Effective(cell)) : (MapModsOf(conn) ?? MapMods.None);
                string source = $"{m.Pack}/{m.Variety}/{m.X}/{m.Y}";
                if (await MapEssences.ImprisonVariety(conn, m.X, m.Y, variety.Id, source, level, cellMods,
                    new Random(unchecked((int)conn.Seed ^ m.X * 397 ^ m.Y))))
                    continue;
            }
            // A Voyage cell's own mods (DeepwaterRun.CellMods, precomputed with the more life).
            var extraMods = run.CellMods is { } cells && (uint)cell < (uint)cells.Length ? cells[cell] : runMods;
            MonsterModSet? rolledMods = null;
            if (m.Rarity == MonsterRarity.Magic && run.CellRules is { } magicRules
                && (uint)cell < (uint)magicRules.Length && magicRules[cell].MagicExtraMods is int extraMagic and > 0)
            {
                rolledMods = MonsterModsFor(conn, variety, level, m.Rarity, null, alone: false, extraMagicMods: extraMagic);
            }
            uint id = await SpawnMonster(remote, stream, ciphers, conn, variety, level, m.Rarity, m.X, m.Y, log: false,
                mods: rolledMods, alone: false, extraStats: extraStats, extraMods: extraMods, instanceOwned: true);
            if (id != 0) spawned++;
        }
        return spawned;
    }

    // G: the swarm's ring around the player.
    const int SwarmNear = 40, SwarmFar = 70;

    /// <summary>One swarm pack of the collapse (G: the wiki's "enemies swarm" with no numbers): a pack of the biome of the
    /// player's cell, 40-70 cells from the player on walkable ground, spawned as the lit packs are. The real server
    /// marked these <c>wave_monster</c> (their water-skin entrance and despawn timer), which is not built: they stay
    /// behind as ordinary monsters. Returns the monsters spawned.</summary>
    public static async Task<int> Swarm(IModuleContext module, ClientState conn, DeepwaterRun run)
    {
        var at = conn.Walk.Position(conn.Clock.Elapsed.TotalSeconds);
        if ((run.Board.CellAt(at) ?? run.Board.Cells[0]).BiomeArea is not { } biome) return 0;
        var pool = Pool(biome, run.Level);
        if (pool.Count == 0) return 0;
        var layout = conn.Layout;
        var random = Random.Shared;
        for (int t = 0; t < Tries; t++)
        {
            double angle = random.NextDouble() * Math.Tau, distance = random.Next(SwarmNear, SwarmFar + 1);
            int x = at.X + (int)Math.Round(Math.Cos(angle) * distance), y = at.Y + (int)Math.Round(Math.Sin(angle) * distance);
            if (!layout.WalkableFor(x, y, Clearance) || run.Board.OnDeck((x, y))) continue;
            var kind = Pick(pool, random);
            var members = AreaPopulation.AtPacks([(kind, x, y, Rarity(random))], (px, py) => layout.WalkableFor(px, py, 1), Spawnable, random.Next());
            if (members.Count == 0) continue;
            int spawned = await Spawn(conn, run, members);
            module.Log($"{CharacterName(conn)}: Allflame swarm {kind.Id} ({spawned} monsters) at ({x}, {y}), {distance:0} cells from ({at.X}, {at.Y})");
            return spawned;
        }
        return 0;
    }
}
