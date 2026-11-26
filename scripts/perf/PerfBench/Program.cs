using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Modding;


// The Gateway's repeatable in-process measurements (Agents/docs/dev/performance.md), run from the repository root:
//   dotnet run -c Release --project scripts/perf/PerfBench -- <catalogue|area|busy|planner|single|idle|sweep|statflips>
// The first-player catalogue loads, an area instance's creation (server level generation, layout, monster population),
// a busy instance's monster AI with its swings and projectiles, the AI planner alone, one player's memory through the
// town, a combat area and a boss area, and one idle player's per-connection loops, and one player's stat builds
// while poison and movement flip. One scenario per process
// (catalogues load once per process). Results go to stdout and as JSON lines to artifacts/perf/bench.jsonl
// (Game_PERF_LABEL names the run). The level generation needs the client in Client/ (either layout) and Node (Tools/ooz), as the
// Gateway's own. Not a test: it waits on real time and measures this machine.
static class PerfBenchMain
{
    // Not top-level statements: their class would be named Program, beside the Gateway's own.
    static async Task<int> Main(string[] args)
    {
        // The game data built before anything reads it, as the Gateway does before it listens (the tests' TestEnvironment);
        // catalogue and single measure those loads themselves.
        if (args.FirstOrDefault() is not ("catalogue" or "single")) PerfBench.LoadWorld();
        // The game module, as the Gateway loads it: it gives the framework its game logic (the loops ask for it).
        var lifetime = new ModuleLifetime("Game");
        Game.CombatLifetime = lifetime;
        Program.SetGameLogic(new GameServer.Modules.Game.GameLogic());
        // Its systems in each area's world (stats, AI, ailments, movement) and its stat hand-over, as its Load gives them.
        if (args.FirstOrDefault() is not ("catalogue" or "single"))
        {
            var context = new PerfModuleContext(lifetime);
            AreaSetup.Load(context);
            Game.LoadStats(context);
        }
        try
        {
            switch (args.FirstOrDefault() ?? "")
            {
                case "catalogue": PerfBench.Catalogue_loads(); break;
                case "area": await PerfBench.Area_instance_creation(); break;
                case "busy": await PerfBench.Busy_instance_ticks(); break;
                case "planner": await PerfBench.Monster_planner(); break;
                case "single": await PerfBench.Single_player_memory(); break;
                case "idle": await PerfBench.Idle_player_cpu(); break;
                case "sweep": await PerfBench.Area_sweep(); break;
                case "stats": PerfBench.Player_stat_builds(); break;
                case "statflips": PerfBench.Statflips_stat_builds(); break;
                case "pause": await PerfBench.Pause_and_resume(); break;
                case "rf": await PerfBench.Righteous_fire_toggles(); break;
                default: Console.Error.WriteLine("usage: PerfBench catalogue|area|busy|planner|single|idle|sweep|stats|statflips|pause|rf"); return 2;
            }
        }
        // The run's save database (EnsureSaves), deleted here: a ProcessExit handler ran too late to let go of it.
        finally { lifetime.Stop(ex => Console.Error.WriteLine(ex)); PerfBench.DeleteSaves(); }
        return 0;
    }
}

static class PerfBench
{
    static readonly string Label = Environment.GetEnvironmentVariable("Game_PERF_LABEL") ?? "run";

    // The results go to the real stdout: the scenarios' own logging goes to a file (ConsoleLog).
    static readonly TextWriter Out = Console.Out;

    static void Record(string scenario, string metric, double value, string unit)
    {
        Out.WriteLine($"{scenario,-24} {metric,-48} {value,12:0.###} {unit}");
        Directory.CreateDirectory(Path.Combine("artifacts", "perf"));
        File.AppendAllText(Path.Combine("artifacts", "perf", "bench.jsonl"), JsonSerializer.Serialize(new
        {
            label = Label, scenario, metric, value = Math.Round(value, 3), unit,
            at = DateTimeOffset.Now.ToString("O"),
        }) + "\n");
    }

    sealed class Wire : IDisposable
    {
        readonly TcpListener listener = new(IPAddress.Loopback, 0);
        public readonly TcpClient Writer = new(), Reader;
        public readonly ConnectionCiphers Ciphers = new(new byte[64], isClient: false);
        readonly CancellationTokenSource stop = new();
        readonly Task draining;
        public long Received;

        public Wire()
        {
            listener.Start();
            var accepting = listener.AcceptTcpClientAsync();
            Writer.Connect(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
            Reader = accepting.GetAwaiter().GetResult();
            // The client side reads everything, as a client does, so the server's sends never block on a full buffer.
            draining = Task.Run(async () =>
            {
                byte[] buffer = new byte[64 * 1024];
                try
                {
                    while (!stop.IsCancellationRequested)
                    {
                        int n = await Reader.GetStream().ReadAsync(buffer, stop.Token);
                        if (n == 0) break;
                        Interlocked.Add(ref Received, n);
                    }
                }
                catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException) { }
            });
        }

        public NetworkStream Stream => Writer.GetStream();

        public void Dispose()
        {
            stop.Cancel();
            Writer.Dispose(); Reader.Dispose(); listener.Stop();
            try { draining.Wait(2000); } catch (AggregateException) { }
        }
    }

    // The Gateway's log as a server writes it (live.ps1 redirects stdout to a file; ServerLog stamps every line), so the
    // logging a scenario does costs what it costs in a server. Once per test process (perf runs only).
    static readonly Lazy<string> ConsoleLog = new(() =>
    {
        string path = Path.GetFullPath(Path.Combine("artifacts", "perf", $"console-{Environment.ProcessId}.log"));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        Console.SetOut(new StreamWriter(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true });
        GameServer.Core.Diagnostics.ServerLog.Install("gateway");
        return path;
    });

    static long LogLines()
    {
        using var reader = new StreamReader(new FileStream(ConsoleLog.Value, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
        long lines = 0;
        while (reader.ReadLine() is not null) lines++;
        return lines;
    }

    static Player Player(AreaLayout layout, uint seed, Wire wire)
    {
        EnsureSaves();
        // Game_PERF_CHARACTER (with Game_PERF_SAVES, a copy of a characters.db): that saved character, its class, level,
        // passives and gear, loaded as a login does (Game.LoadProgress), in its league.
        var saved = SavedCharacter();
        var player = new Player(layout, 1, seed)
        {
            Character = { Name = saved?.Name ?? "PerfBench" + Guid.NewGuid().ToString("N"), ClassRow = saved?.ClassRow ?? 1, League = saved?.League },
            Connection = { PlayerSent = true, Session = new("perf", wire.Stream, wire.Ciphers) },
            Instance = new()
        };
        Program.ConnectionMetrics.Add(new ConnectionCiphers(new byte[64], false), player.Connection.Performance);
        if (saved is { } character)
        {
            Game.LoadProgress(player, character.Level);
            player.PlayerLife = player.Pools.Life;
            player.PlayerMana = player.Pools.Mana;
            return player;
        }
        // Nothing the monsters do ends the measurement: a large life pool, and no death screen.
        player.Pools = Game.PoolsFor(player, 40) with { Life = 100_000_000, Mana = 1000 };
        player.PlayerLife = player.Pools.Life;
        player.PlayerMana = 1000;
        return player;
    }

    static (string Name, int ClassRow, int Level, string League)? SavedCharacter()
    {
        if (Environment.GetEnvironmentVariable("Game_PERF_CHARACTER") is not { Length: > 0 } name) return null;
        return Saves.Db.Read(() =>
        {
            using var cmd = Saves.Db.Command("select class, level, league from account_characters where name = @n", ("@n", name));
            using var r = cmd.ExecuteReader();
            if (!r.Read()) throw new InvalidOperationException($"no character {name} in the save copy");
            return (name, Program.Skills.RowOfClass(r.GetInt32(0)), r.GetInt32(1), r.GetString(2));
        });
    }

    static Task PopulateArea(Wire wire, Player player) => Game.PopulateArea(player);

    // A copy of world.db (the Gateway holds its own open), then the client tables and the catalogues.
    public static void LoadWorld()
    {
        string world = Path.Combine(Path.GetTempPath(), $"localexile-perf-world-{Guid.NewGuid():N}.db");
        File.Copy(GameData.DefaultPath, world);
        GameData.Open(world);
        GameServer.Core.ClientTables.LoadAll();
        WorldLoader.Load();
        GameServer.Modules.Game.GameModule.LoadData();
    }

    // What the area-generated message (0x7d) starts: the world frames (the area's driver, its systems and the monster AI)
    // and the player's updates. The returned task ends a little after the player closes.
    static async Task StartArea(Player player)
    {
        Game.ActivateResourceEffects(player, Game.ProjectileWorldTime(player));
        Program.StartWorldFrames(player);
        Program.ActivatePlayerUpdates(player);
        while (!player.Closed) await Task.Delay(100);
        await Task.Delay(500);
    }

    // A save database of its own for the run (the characters the scenarios create); Main deletes it.
    static string? _saves;
    static void EnsureSaves()
    {
        if (_saves != null) return;
        _saves = Path.Combine(Path.GetTempPath(), $"localexile-perf-{Guid.NewGuid():N}.db");
        // A copy of a real save database (Game_PERF_SAVES): the run writes to its own copy of the copy.
        if (Environment.GetEnvironmentVariable("Game_PERF_SAVES") is { Length: > 0 } from) File.Copy(from, _saves);
        Saves.Open(_saves);
    }

    public static void DeleteSaves()
    {
        if (_saves == null) return;
        Saves.Db.Dispose();
        foreach (string f in new[] { _saves, _saves + "-wal", _saves + "-shm" })
            try { File.Delete(f); }
            catch (IOException e) { Console.Error.WriteLine($"perfbench: {f} not deleted: {e.Message}"); }
    }

    static long Allocated() => GC.GetTotalAllocatedBytes(precise: false);

    static double PrivateMegabytes()
    {
        using var self = Process.GetCurrentProcess();
        return self.PrivateMemorySize64 / 1048576.0;
    }

    // What the first player in a fresh Gateway loads: the catalogues behind an item, a skill, a stat key, a monster
    // and an area (each is loaded once per process, so the order matters and is fixed).
    public static void Catalogue_loads()
    {
        var steps = new (string Name, Action Load)[]
        {
            ("Program statics", () => _ = Program.Items),
            ("Stats", () => Program.Stats.Key("base_maximum_life")),
            ("Items (BaseItemTypes)", () => Program.Items.Find("Iron Ring")),
            ("Items.Mods (Mods.json)", () => _ = Program.Items.Mods.All.Count),
            ("Skills", () => Program.Skills.RowOfClass(1)),
            // The ActiveSkillType sets (instant, attack, aura Ã¢â‚¬Â¦) a player's and the monsters' skills ask for.
            ("Skill type sets", () =>
            {
                var skills = new SkillCatalog();
                _ = skills.IsInstant(0) | skills.IsMovement(0) | skills.IsRangedAttack(0) | skills.IsSpell(0) | skills.IsWarcry(0)
                    | skills.IsAura(0, [0]) | skills.IsBrand(0);
            }),
            ("Monsters", () => Game.Monsters.Find("Metadata/Monsters/Zombies/ZombieBiteRaise")),
            ("Packs", () => Game.Packs.For("1_1_2")),
            ("Area transitions", () => AreaTransitionCatalog.HashOf("1_1_2")),
            ("Waypoints", () => _ = WaypointCatalog.Areas),
            ("Passive tree", () => Program.Passives.Find(0)),
        };
        double total = 0;
        foreach (var (name, load) in steps)
        {
            long live = GC.GetTotalMemory(forceFullCollection: true);
            long before = Allocated();
            var clock = Stopwatch.StartNew();
            load();
            total += clock.Elapsed.TotalMilliseconds;
            Record("catalogue", $"{name} ms", clock.Elapsed.TotalMilliseconds, "ms");
            Record("catalogue", $"{name} allocated MB", (Allocated() - before) / 1048576.0, "MB");
            Record("catalogue", $"{name} retained MB", (GC.GetTotalMemory(forceFullCollection: true) - live) / 1048576.0, "MB");
        }
        Record("catalogue", "total ms", total, "ms");
        if (typeof(Program).Assembly.GetType("GameTables") is { } tables
            && tables.GetProperty("Counts")?.GetValue(null) is ValueTuple<long, long> counts)
        {
            Record("catalogue", "shared table parses", counts.Item1, "");
            Record("catalogue", "shared table reuses", counts.Item2, "");
        }
        // A per-gem question the skill panel and every hit ask (three type sets per call).
        {
            const int Calls = 1_000_000;
            bool any = false;
            long before = Allocated();
            var clock = Stopwatch.StartNew();
            for (int i = 0; i < Calls; i++) any |= Program.Skills.IsRangedAttack(i & 0x3ff);
            Record("catalogue", "IsRangedAttack ns per call", clock.Elapsed.TotalMilliseconds * 1e6 / Calls, "ns");
            Record("catalogue", "IsRangedAttack bytes per call", (Allocated() - before) / (double)Calls, "B");
            GC.KeepAlive(any);
        }
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        Record("catalogue", "live heap after full GC MB", GC.GetTotalMemory(false) / 1048576.0, "MB");
        Record("catalogue", "process private MB", PrivateMegabytes(), "MB");
    }

    // What a single-player Gateway keeps: the startup loads (GameData.Open, as Program does), then a player's first
    // loads, the town and one area with its monsters; after the loads' buffers are released and a full collection, the
    // heap, its large-object part (where ArrayPool keeps parse buffers) and the process's private bytes.
    public static async Task Single_player_memory()
    {
        _ = ConsoleLog.Value;
        string db = Path.Combine(Path.GetTempPath(), $"localexile-perf-{Guid.NewGuid():N}.db");
        File.Copy(Path.Combine("data", "validation-characters.db"), db);
        File.Copy(Path.Combine("data", "validation-characters-auth.db"), GameServer.Storage.SaveDatabase.AuthPathFor(db));
        try
        {
            void Snapshot(string phase)
            {
                if (typeof(Program).Assembly.GetType("GameTables")?.GetMethod("Release") is { } release) release.Invoke(null, null);
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
                var info = GC.GetGCMemoryInfo();
                Record("single", $"{phase}: heap MB", info.HeapSizeBytes / 1048576.0, "MB");
                Record("single", $"{phase}: large-object heap MB", info.GenerationInfo[3].SizeAfterBytes / 1048576.0, "MB");
                Record("single", $"{phase}: GC committed MB", info.TotalCommittedBytes / 1048576.0, "MB");
                Record("single", $"{phase}: process private MB", PrivateMegabytes(), "MB");
            }
            var open = typeof(Program).Assembly.GetType("GameTables")?.GetMethod("Isolated", [typeof(Action)]);
            Action startup = () => GameData.Open(db);
            if (open is not null) open.Invoke(null, [startup]); else startup();
            Snapshot("startup");
            _ = Program.Stats.Key("base_maximum_life"); _ = Program.Skills.RowOfClass(1); _ = Program.Skills.IsRangedAttack(0);
            _ = Program.Passives.Find(0); _ = WaypointCatalog.Areas;
            // One player's way: the town, a combat area with its monsters, then a boss area (Hillock on The Twilight
            // Strand), each entered as a connection does (layout, then the area's packs); every layout stays held, as a
            // connection holds its own.
            var held = new List<AreaLayout>();
            foreach (var (name, hash, seed) in new[] { ("town", AreaLayouts.LioneyesWatch, 1u), ("combat area 1_1_2", AreaLayouts.TheCoast, 1u),
                ("boss area 1_1_1", AreaTransitionCatalog.HashOf("1_1_1")!.Value, 1u) })
            {
                var layout = AreaLayouts.For(hash, seed);
                held.Add(layout);
                using var wire = new Wire();
                var player = Player(layout, seed, wire);
                await PopulateArea(wire, player);
                Record("single", $"{name}: monsters", player.Area.Monsters.Count, "");
                Snapshot(name);
                player.Closed = true;
            }
            if (typeof(ServerLevels).GetField("_fs", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null) is { } fs)
                foreach (string field in new[] { "_cacheBytes", "_chunkBytes" })
                    if (fs.GetType().GetField(field, BindingFlags.NonPublic | BindingFlags.Instance) is { } f)
                        Record("single", $"game files {field} MB", (long)f.GetValue(fs)! / 1048576.0, "MB");
            await Hold("single");
            GC.KeepAlive(held);
        }
        finally
        {
            GameData.Close();
            foreach (string f in new[] { db, db + "-wal", db + "-shm" }) try { File.Delete(f); } catch (IOException) { }
        }
    }

    // One player standing in town: the per-connection loops the game module starts once the area is generated (as its
    // Messages does on 0x7d: monster regeneration, the server frames, the monster AI, living collisions, the player tick,
    // continuous recovery, charges, fortification, rage, flask charges; the feature modules' own loops are not started),
    // over a loopback connection whose client side reads everything. The measure is the process CPU per second after a
    // warm-up, and what the loops allocate.
    public static async Task Idle_player_cpu()
    {
        _ = ConsoleLog.Value;
        var town = AreaLayouts.For(AreaLayouts.LioneyesWatch, 1);
        using var wire = new Wire();
        var player = Player(town, 1, wire);
        var area = StartArea(player);
        await Task.Delay(TimeSpan.FromSeconds(5));
        using var self = Process.GetCurrentProcess();
        self.Refresh();
        var cpuBefore = self.TotalProcessorTime;
        long allocated = Allocated();
        long received = Interlocked.Read(ref wire.Received);
        var clock = Stopwatch.StartNew();
        await Task.Delay(TimeSpan.FromSeconds(20));
        self.Refresh();
        double seconds = clock.Elapsed.TotalSeconds;
        Record("idle player", "process CPU ms per second", (self.TotalProcessorTime - cpuBefore).TotalMilliseconds / seconds, "ms/s");
        Record("idle player", "allocated KB per second", (Allocated() - allocated) / 1024.0 / seconds, "KB/s");
        Record("idle player", "bytes sent per second", (Interlocked.Read(ref wire.Received) - received) / seconds, "B/s");
        player.Closed = true;
        await area;
    }

    // An area instance as a player's entry creates it: the layout (generated by the server for a supported area, at a
    // seed nobody asked for before), then the area's monster packs placed and created over a connection.
    public static async Task Area_instance_creation()
    {
        _ = ConsoleLog.Value;
        string[] areas = ["1_1_2", "1_1_3", "1_1_4_1", "1_1_5", "1_1_7_1", "1_2_1"];
        // Game_PERF_MORE_AREAS=N: then the layouts of N more supported areas (no monsters), as a long session visits
        // them, for what the caches grow to.
        int more = int.TryParse(Environment.GetEnvironmentVariable("Game_PERF_MORE_AREAS"), out int n) ? n : 0;
        if (more > 0)
            areas = [.. areas, .. ServerLevels.Supported.Except(areas).Order(StringComparer.Ordinal)
                .Where(a => AreaTransitionCatalog.HashOf(a) is not null).Take(more)];
        bool first = true;
        AreaLayout? held = null;   // as a connection holds its layout while the others are generated
        foreach (string id in areas)
        {
            if (AreaTransitionCatalog.HashOf(id) is not ushort hash || !ServerLevels.IsSupported(id)) continue;
            if (Array.IndexOf(areas, id) >= 6)
            {
                var timer = Stopwatch.StartNew();
                AreaLayouts.For(hash, (uint)(1000 + Array.IndexOf(areas, id)));
                Record("area", "more: layout ms", timer.Elapsed.TotalMilliseconds, "ms");
                continue;
            }
            // A seed per area and run that no earlier generation cached.
            uint seed = (uint)(1000 + Array.IndexOf(areas, id));
            long live = GC.GetTotalMemory(forceFullCollection: true);
            long before = Allocated();
            var clock = Stopwatch.StartNew();
            var layout = AreaLayouts.For(hash, seed);
            double layoutMs = clock.Elapsed.TotalMilliseconds;
            held ??= layout;
            Record("area", $"{id} layout{(first ? " (first: opens the game files)" : "")} ms", layoutMs, "ms");
            Record("area", $"{id} layout allocated MB", (Allocated() - before) / 1048576.0, "MB");
            Record("area", $"{id} layout retained MB", (GC.GetTotalMemory(forceFullCollection: true) - live) / 1048576.0, "MB");
            first = false;
            using var wire = new Wire();
            var player = Player(layout, seed, wire);
            before = Allocated();
            clock.Restart();
            await PopulateArea(wire, player);
            Record("area", $"{id} populate ms", clock.Elapsed.TotalMilliseconds, "ms");
            Record("area", $"{id} populate allocated MB", (Allocated() - before) / 1048576.0, "MB");
            Record("area", $"{id} monsters", player.Area.Monsters.Count, "");
            player.Closed = true;
        }
        // What the Gateway keeps afterwards: the generated levels (ServerLevels keeps the last few), the game files'
        // caches and the catalogues.
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        Record("area", "live heap after full GC MB", GC.GetTotalMemory(false) / 1048576.0, "MB");
        Record("area", "process private MB", PrivateMegabytes(), "MB");
        if (typeof(ServerLevels).GetField("_fs", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null) is { } fs)
        {
            foreach (string field in new[] { "_cacheBytes", "_chunkBytes" })
                if (fs.GetType().GetField(field, BindingFlags.NonPublic | BindingFlags.Instance) is { } f)
                    Record("area", $"game files {field} MB", (long)f.GetValue(fs)! / 1048576.0, "MB");
            if (fs.GetType().GetField("_bundleReads", BindingFlags.NonPublic | BindingFlags.Instance) is { } reads)
                Record("area", "game files bundle reads", (long)reads.GetValue(fs)!, "");
        }
        // The first layout's level, held all along: still there, not generated again (1 = the same level).
        if (held?.Level is { } level)
        {
            var timer = Stopwatch.StartNew();
            bool same = ReferenceEquals(ServerLevels.For(held.Id, 1000u), level);
            Record("area", "held level kept", same ? 1 : 0, "");
            Record("area", "held level lookup ms", timer.Elapsed.TotalMilliseconds, "ms");
            // What a projectile step's grid and height lookups pay per call (ProjectileGrid, TerrainHeights).
            const int Calls = 1_000_000;
            timer.Restart();
            for (int i = 0; i < Calls; i++) ProjectileGrid.Blocks(held.Id, 1000u, i & 0xff, 100);
            Record("area", "ProjectileGrid.Blocks ns per call (generated level)", timer.Elapsed.TotalMilliseconds * 1e6 / Calls, "ns");
            timer.Restart();
            for (int i = 0; i < Calls; i++) TerrainHeights.At(held.Id, 1000u, i & 0xff, 100);
            Record("area", "TerrainHeights.At ns per call (generated level)", timer.Elapsed.TotalMilliseconds * 1e6 / Calls, "ns");
        }
        // The generated levels ServerLevels keeps for the next entry (their grids; the doodad lists are small beside them).
        if (typeof(ServerLevels).GetField("Generated", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null)
            is System.Collections.IDictionary kept)
        {
            long bytes = kept.Values.Cast<object>().Select(v => v.GetType().GetField("Level")?.GetValue(v)).OfType<ServerLevels.Level>()
                .Sum(l => (long)l.Walk.Length + l.Heights.Length * 2L + l.Projectile.Length);
            Record("area", "kept levels", kept.Count, "");
            Record("area", "kept levels' grids MB", bytes / 1048576.0, "MB");
        }
        if (typeof(Program).Assembly.GetType("GameTables") is { } tables
            && tables.GetProperty("Counts")?.GetValue(null) is ValueTuple<long, long> counts)
        {
            Record("area", "shared table parses", counts.Item1, "");
            Record("area", "shared table reuses", counts.Item2, "");
        }
        await Hold("area");
    }

    // Game_PERF_HOLD=1: the test waits (up to 2 minutes) with its state alive until artifacts/perf/<name>.release
    // exists, after writing its process id to artifacts/perf/<name>.pid, for `dotnet-gcdump collect -p <pid>`.
    static async Task Hold(string name)
    {
        if (Environment.GetEnvironmentVariable("Game_PERF_HOLD") != "1") return;
        string folder = Path.Combine("artifacts", "perf"), release = Path.Combine(folder, name + ".release");
        File.Delete(release);
        File.WriteAllText(Path.Combine(folder, name + ".pid"), Environment.ProcessId.ToString());
        for (var clock = Stopwatch.StartNew(); !File.Exists(release) && clock.Elapsed < TimeSpan.FromMinutes(2);) await Task.Delay(200);
    }

    // A busy instance, the same in every run: the kinds of the area's scattered packs (and a Sand Spitter, for
    // projectiles), Crowd of them around a player who keeps walking, so they chase, surround, swing and shoot, and Idle
    // more farther away, as the rest of an area's population. (PopulateArea is not used: its placement is seeded with
    // the process's randomized HashCode.)
    static async Task<(Wire Wire, Player Player, (int X, int Y)[] Path)> BusyInstance()
    {
        _ = ConsoleLog.Value;
        var wire = new Wire();
        const string id = "1_1_2";
        const int Crowd = 60, Idle = 140;
        ushort hash = AreaTransitionCatalog.HashOf(id)!.Value;
        var layout = AreaLayouts.For(hash, 1);
        var player = Player(layout, 1, wire);
        var kinds = Game.Packs.For(id)!.Scattered.SelectMany(p => p.Pack.Entries).Select(e => Game.Monsters.Find(e.Variety.ToString()))
            .OfType<MonsterCatalog.Variety>().Where(v => v.Supported).DistinctBy(v => v.Row)
            // Monsters that wait buried rise on a wall-clock watch (MonsterEmerge): left out, so every run is the same.
            .Where(v => Game.Emerges.Kind(v, MonsterRarity.Normal) == EmergeKind.None).ToList();
        if (Game.Monsters.Find("Metadata/Monsters/SandSpitters/SandSpitterUntainted") is { Supported: true } spitter
            && Game.Emerges.Kind(spitter, MonsterRarity.Normal) == EmergeKind.None) kinds.Add(spitter);
        if (kinds.Count == 0) throw new InvalidOperationException("no monster kinds for the busy instance");
        var random = new Random(7);
        var (px, py) = (layout.SpawnX, layout.SpawnY);
        int placed = 0;
        for (int attempt = 0; placed < Crowd + Idle && attempt < (Crowd + Idle) * 50; attempt++)
        {
            int spread = placed < Crowd ? 40 : 400;
            int x = px + random.Next(-spread, spread + 1), y = py + random.Next(-spread, spread + 1);
            if (placed >= Crowd && Math.Max(Math.Abs(x - px), Math.Abs(y - py)) < 150) continue;
            if (!layout.WalkableFor(x, y, 2)) continue;
            await Game.SpawnMonster(player, kinds[placed % kinds.Count], 5,
                MonsterRarity.Normal, x, y, log: false);
            placed++;
        }
        Record("busy", "monsters", player.Area.Monsters.Count, "");
        Record("busy", "kinds", kinds.Count, "");

        // The player walks between points around the spawn, as a player kiting the crowd.
        var path = new[] { (px + 12, py), (px, py + 12), (px - 12, py), (px, py - 12) }
            .Where(p => layout.WalkableFor(p.Item1, p.Item2, 0)).DefaultIfEmpty((px, py)).ToArray();
        return (wire, player, path);
    }

    // The real AI loop runs over the busy instance (RunMonsterAi, its swings, contacts and projectiles, every packet
    // encrypted and sent); the measure is the process CPU it costs per second of game time.
    public static async Task Busy_instance_ticks()
    {
        const double Seconds = 10;
        var (wire, player, path) = await BusyInstance();
        using var _ = wire;
        using var stop = new CancellationTokenSource();
        var walking = Task.Run(async () =>
        {
            for (int i = 0; !stop.IsCancellationRequested; i++)
            {
                var (x, y) = path[i % path.Length];
                player.Walk.Order(x, y, player.Clock.Elapsed.TotalSeconds);
                await Task.Delay(700);
            }
        });
        using var self = Process.GetCurrentProcess();
        var ai = StartArea(player);
        // A warm-up first (Game_PERF_WARMUP seconds, default 5): the JIT's tier-1 recompilation of the hot methods is a
        // one-time cost of a fresh process, not of a running server's instance.
        await Task.Delay(TimeSpan.FromSeconds(double.TryParse(Environment.GetEnvironmentVariable("Game_PERF_WARMUP"), out var w) ? w : 5));
        self.Refresh();
        var cpuBefore = self.TotalProcessorTime;
        long allocatedBefore = Allocated();
        int gen0 = GC.CollectionCount(0), gen2 = GC.CollectionCount(2);
        var pauseBefore = GC.GetTotalPauseDuration();
        // Game_PERF_ALLOC=1: the runtime's allocation samples (one per ~100 KB) by type over the measured window.
        using var allocations = Environment.GetEnvironmentVariable("Game_PERF_ALLOC") == "1" ? new AllocationSampler() : null;
        long workBefore = ThreadPool.CompletedWorkItemCount;
        long sentBefore = Interlocked.Read(ref wire.Received);
        long linesBefore = LogLines();
        var clock = Stopwatch.StartNew();
        await Task.Delay(TimeSpan.FromSeconds(Seconds));
        double elapsed = clock.Elapsed.TotalSeconds;
        self.Refresh();
        double cpuMs = (self.TotalProcessorTime - cpuBefore).TotalMilliseconds;
        player.Closed = true;
        stop.Cancel();
        await ai;
        await walking;
        Record("busy", "process CPU ms per game second", cpuMs / elapsed, "ms/s");
        Record("busy", "allocated MB per game second", (Allocated() - allocatedBefore) / 1048576.0 / elapsed, "MB/s");
        Record("busy", "gen0 GCs per game second", (GC.CollectionCount(0) - gen0) / elapsed, "/s");
        Record("busy", "gen2 GCs", GC.CollectionCount(2) - gen2, "");
        if (allocations is not null)
            foreach (var (type, kb) in allocations.Top(25)) Record("busy alloc", type, kb / elapsed, "KB/s");
        Record("busy", "GC pause ms per game second", (GC.GetTotalPauseDuration() - pauseBefore).TotalMilliseconds / elapsed, "ms/s");
        Record("busy", "thread-pool work items per game second", (ThreadPool.CompletedWorkItemCount - workBefore) / elapsed, "/s");
        Record("busy", "bytes sent per game second", (Interlocked.Read(ref wire.Received) - sentBefore) / elapsed, "B/s");
        Record("busy", "aggro monsters", player.Area.Monsters.Values.Count(m => m.Aggro), "");
        Record("busy", "log lines per game second", (LogLines() - linesBefore) / elapsed, "/s");
    }

    // The busy instance paused (the Escape menu, Game.SetPaused) for 10 s and resumed: the area's frames a second before
    // and after. A frame driver that replays the paused time runs far more than 30 a second after the resume.
    // Righteous Fire turned on and off in the busy instance, a second apart, as a player toggles it: the longest wait for
    // a frame around each toggle (the first is the area's first use of the skill).
    public static async Task Righteous_fire_toggles()
    {
        var (wire, player, _) = await BusyInstance();
        using var __ = wire;
        // Level 90 for the gem's level requirement; god mode for its attributes and for life the crowd cannot take. A saved
        // character (Game_PERF_CHARACTER) keeps its level and gear, with its own Righteous Fire.
        player.God = true;
        if (Environment.GetEnvironmentVariable("Game_PERF_CHARACTER") is not { Length: > 0 })
        {
            player.Character.Experience = CharacterStats.ExperienceFor(90);
            WearRighteousFire(player);
        }
        var jitStart = System.Runtime.JitInfo.GetCompilationTime();
        long methodsStart = System.Runtime.JitInfo.GetCompiledMethodCount();
        var area = StartArea(player);
        await Task.Delay(TimeSpan.FromSeconds(4));
        Record("rf", "JIT time in the area's first 4 s", (System.Runtime.JitInfo.GetCompilationTime() - jitStart).TotalMilliseconds, "ms");
        Record("rf", "methods compiled in the area's first 4 s", System.Runtime.JitInfo.GetCompiledMethodCount() - methodsStart, "");
        await Task.Delay(TimeSpan.FromSeconds(1));
        var rf = Game.GemSkillEffects(player).First(g => g.Name.Contains("Righteous Fire"));
        double LongestGap(Func<Task> toggle)
        {
            double longest = 0, lastAt = 0;
            long last = player.Frames.Frame;
            var clock = Stopwatch.StartNew();
            // In the area's frame, as the player's message is handled.
            var running = player.Frames.Run(toggle);
            while (clock.Elapsed.TotalSeconds < 1)
            {
                Thread.Sleep(2);
                long frame = player.Frames.Frame;
                double at = clock.Elapsed.TotalMilliseconds;
                if (frame != last) { longest = Math.Max(longest, at - lastAt); (last, lastAt) = (frame, at); }
            }
            running.GetAwaiter().GetResult();
            return Math.Max(longest, clock.Elapsed.TotalMilliseconds - lastAt);
        }
        for (int i = 1; i <= 4; i++)
        {
            var jitBefore = System.Runtime.JitInfo.GetCompilationTime();
            long methodsBefore = System.Runtime.JitInfo.GetCompiledMethodCount();
            double activation = 0;
            Record("rf", $"longest frame gap, turned on ({i})", LongestGap(async () =>
            {
                long started = Stopwatch.GetTimestamp();
                await Game.UseInstantSkill(player, rf.Id, player.Id, player.Walk.Position(Game.AreaTime(player)));
                activation = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            }), "ms");
            Record("rf", $"its activation call ({i})", activation, "ms");
            Record("rf", $"JIT time in that second ({i})", (System.Runtime.JitInfo.GetCompilationTime() - jitBefore).TotalMilliseconds, "ms");
            Record("rf", $"methods compiled in that second ({i})", System.Runtime.JitInfo.GetCompiledMethodCount() - methodsBefore, "");
            if (player.RighteousFire is null) throw new InvalidOperationException("Righteous Fire did not turn on");
            Record("rf", $"longest frame gap, turned off ({i})", LongestGap(() => Game.EndRighteousFire(player, "perf")), "ms");
        }
        // The cast alone, warm, many times: the use message's work in the area's frame (UseInstantSkill), each turned
        // on and off, a frame apart.
        var casts = new List<double>();
        for (int i = 0; i < 40; i++)
        {
            double took = 0;
            await player.Frames.Run(async () =>
            {
                long started = Stopwatch.GetTimestamp();
                await Game.UseInstantSkill(player, rf.Id, player.Id, player.Walk.Position(Game.AreaTime(player)));
                took = Stopwatch.GetElapsedTime(started).TotalMilliseconds * 1000;
            });
            await Task.Delay(Program.FrameMs * 2);
            await player.Frames.Run(() => Game.EndRighteousFire(player, "perf"));
            await Task.Delay(Program.FrameMs * 2);
            casts.Add(took);
        }
        casts.Sort();
        Record("rf", "cast, median of 40", casts[casts.Count / 2], "us");
        Record("rf", "cast, 90th percentile of 40", casts[casts.Count * 9 / 10], "us");
        Record("rf", "cast, best of 40", casts[0], "us");
        // Back to back in one frame (Game_PERF_CASTS, default 0): the cast's own work, for a profiler.
        if (int.TryParse(Environment.GetEnvironmentVariable("Game_PERF_CASTS"), out int loops) && loops > 0)
        {
            double total = 0;
            await player.Frames.Run(async () =>
            {
                for (int i = 0; i < loops; i++)
                {
                    long started = Stopwatch.GetTimestamp();
                    await Game.UseInstantSkill(player, rf.Id, player.Id, player.Walk.Position(Game.AreaTime(player)));
                    total += Stopwatch.GetElapsedTime(started).TotalMilliseconds * 1000;
                    await Game.EndRighteousFire(player, "perf");
                }
            });
            Record("rf", $"cast, mean of {loops} back to back", total / loops, "us");
        }
        player.Closed = true;
        await area;
    }

    // A robe with a level 1 Righteous Fire gem worn; the character does not meet the gem's requirements, so it is exempt,
    // as one of the modules the player's league lists (only those are asked).
    static void WearRighteousFire(Player player)
    {
        var items = Game.ItemsFor(player);
        var (gemType, variant) = items.FindGem("Righteous Fire") ?? throw new InvalidOperationException("no Righteous Fire gem");
        var created = ItemCatalog.Create(gemType) with { GemExperience = items.Gem(gemType)!.ExperienceFor(1), GemVariant = variant.Index };
        var inventories = Game.ItemsOf(player);
        uint robe = inventories.NewItemId();
        inventories.Put(PlayerInventories.BodyArmour, new InventoryItem(robe, 0, 0,
            ItemCatalog.Create(items.Find("Simple Robe")!) with { Sockets = [new ItemSocket(SocketColour.Red, created)] }));
        GameEvents.RequirementExemptions.Add(Program.ModulesFor(player).First(), c => ReferenceEquals(c, player) ? [robe] : []);
    }

    public static async Task Pause_and_resume()
    {
        var (wire, player, _) = await BusyInstance();
        using var __ = wire;
        // 10,000 more life: the crowd kills the character in about 15 s, and a death's frame is not the resume's.
        player.God = true;
        // A robe with a Righteous Fire gem worn (Game_PERF_RF=0: none). The skill itself is not turned on here.
        if (Environment.GetEnvironmentVariable("Game_PERF_RF") != "0") WearRighteousFire(player);
        var area = StartArea(player);
        await Task.Delay(TimeSpan.FromSeconds(5));
        long FramesIn(double seconds)
        {
            long from = player.Frames.Frame;
            Thread.Sleep(TimeSpan.FromSeconds(seconds));
            return player.Frames.Frame - from;
        }
        Record("pause", "frames in the second before", FramesIn(1), "");
        await Game.SetPaused(player, true);
        Record("pause", "frames in 10 s paused", FramesIn(10), "");
        await Game.SetPaused(player, false);
        // The longest wait for the next frame in the 3 s after the resume (the client sees nothing meanwhile).
        double longest = 0;
        var clock = Stopwatch.StartNew();
        long last = player.Frames.Frame;
        double lastAt = 0;
        while (clock.Elapsed.TotalSeconds < 3)
        {
            Thread.Sleep(2);
            long frame = player.Frames.Frame;
            double at = clock.Elapsed.TotalMilliseconds;
            if (frame != last) { longest = Math.Max(longest, at - lastAt); (last, lastAt) = (frame, at); }
        }
        longest = Math.Max(longest, clock.Elapsed.TotalMilliseconds - lastAt);
        Record("pause", "longest gap between frames after the resume", longest, "ms");
        Record("pause", "frames a second after the resume", FramesIn(1), "");
        player.Closed = true;
        await area;
    }

    // The planner alone, single-threaded, over the same crowd: one decision pass per AI step (no sends).
    public static async Task Monster_planner()
    {
        var (wire, player, path) = await BusyInstance();
        using var _ = wire;
        var clock = new Stopwatch();
        var cadence = new MonsterAiCadence();
        const int Steps = 2000;
        double now = player.Clock.Elapsed.TotalSeconds;
        for (int i = 0; i < 50; i++) Game.PlanMonsterAi(player, cadence, now += 0.05);   // warm-up
        var surroundBefore = player.Connection.Performance.Read(LatencyPhase.Surround);
        var blockersBefore = player.Connection.Performance.Read(LatencyPhase.BlockerBuild);
        long planAllocated = GC.GetAllocatedBytesForCurrentThread();
        using var self = Process.GetCurrentProcess();
        var cpuBefore = self.TotalProcessorTime;
        clock.Restart();
        int moves = 0;
        for (int i = 0; i < Steps; i++)
        {
            if (i % 14 == 0) { var (x, y) = path[i / 14 % path.Length]; player.Walk.Order(x, y, now); }
            var plan = Game.PlanMonsterAi(player, cadence, now += 0.05);
            moves += plan.Moves.Count;
            // Swings complete at once here: the planner keeps reserving them.
            foreach (var (_, m, action) in plan.Swings) cadence.CompleteSwing(m, action);
        }
        Record("planner", "PlanMonsterAi Ã‚Âµs per step", clock.Elapsed.TotalMilliseconds * 1000 / Steps, "Ã‚Âµs");
        self.Refresh();
        // Less disturbed by other processes on a shared machine than the wall clock.
        Record("planner", "PlanMonsterAi process CPU Ã‚Âµs per step", (self.TotalProcessorTime - cpuBefore).TotalMilliseconds * 1000 / Steps, "Ã‚Âµs");
        Record("planner", "PlanMonsterAi allocated KB per step", (GC.GetAllocatedBytesForCurrentThread() - planAllocated) / 1024.0 / Steps, "KB");
        Record("planner", "PlanMonsterAi moves per step", moves / (double)Steps, "");
        // The planner's phases (ConnectionPerformance): the surround's slot search and the blocker snapshot.
        Record("planner", "MonsterSurround.Assign Ã‚Âµs per step",
            (player.Connection.Performance.Read(LatencyPhase.Surround).TotalMilliseconds - surroundBefore.TotalMilliseconds) * 1000 / Steps, "Ã‚Âµs");
        Record("planner", "blocker frame Ã‚Âµs per step",
            (player.Connection.Performance.Read(LatencyPhase.BlockerBuild).TotalMilliseconds - blockersBefore.TotalMilliseconds) * 1000 / Steps, "Ã‚Âµs");
        player.Closed = true;
    }

    // One Ice Nova (level 20, in a body armour socket) on N monsters inside its radius: the real AreaSkillHit with every
    // packet encrypted and sent, at 100, 300 and 500 monsters, and the per-call cost of the inputs its loop reads.
    // The monsters keep a huge life pool, so the sweeps measure hits (no deaths, loot or experience).
    // One player's full stat build (CharacterPlayerStats) in a loop for Game_PERF_SECONDS (default 20), after a warm-up:
    // its cost per call and allocation, steady enough to sample with dotnet-stack while it runs.
    public static void Player_stat_builds()
    {
        _ = ConsoleLog.Value;
        using var wire = new Wire();
        var layout = AreaLayouts.For(AreaTransitionCatalog.HashOf("1_1_2")!.Value, 1);
        var player = Player(layout, 1, wire);
        for (int i = 0; i < 300; i++) Game.CharacterPlayerStats(player);
        double seconds = double.TryParse(Environment.GetEnvironmentVariable("Game_PERF_SECONDS"), out var s) ? s : 20;
        Out.WriteLine($"stats: looping, pid {Environment.ProcessId}");
        long a0 = Allocated();
        int calls = 0;
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed.TotalSeconds < seconds) { Game.CharacterPlayerStats(player); calls++; }
        Record("stats", "CharacterPlayerStats us per call", clock.Elapsed.TotalMilliseconds * 1000 / calls, "us");
        Record("stats", "CharacterPlayerStats KB per call", (Allocated() - a0) / 1024.0 / calls, "KB");
        player.Closed = true;
    }

    // One player's stat builds while one input flips at a time: poison stacks on and off in the
    // poison stage (never moving), and moving on and off in the moving stage (never poisoned).
    // Splitting the two apart is the point: the old shape flipped both together, so a fix for one
    // input could not show -- its misses hid behind the other's. One CharacterPlayerStats call per
    // build, as a frame's Sources step would run it: StepPlayerStats cannot be driven per call from
    // here (it needs the area's frames, which would take the graph over and stop
    // CharacterPlayerStats rebuilding). The sources sum's cache is what is measured: a rebuild
    // replaces StatSourcesCache, a hit keeps it, so its identity counts the rebuilds (no counter
    // for them exists in the materials). Run before and after the fixes: the misses should fall
    // from one per flip to near zero in the stage the fix covers. Plain gear only (this robe, or
    // Game_PERF_CHARACTER's): no conditional stat may read either input, or the flip must rebuild
    // by design. The key's own view of both inputs is read back on every rebuild, so a flip that
    // never reached the key shows up in the counts instead of silently measuring nothing.
    // Game_PERF_STATFLIPS_STAGE picks the stages: poison, moving, or both (default).
    public static void Statflips_stat_builds()
    {
        _ = ConsoleLog.Value;
        using var wire = new Wire();
        var layout = AreaLayouts.For(AreaTransitionCatalog.HashOf("1_1_2")!.Value, 1);
        var player = Player(layout, 1, wire);
        if (Environment.GetEnvironmentVariable("Game_PERF_CHARACTER") is not { Length: > 0 })
        {
            // One plain item, so the sources sum has something to fold and a miss costs more than a hit;
            // a bare robe carries no conditional stat, so neither flipped input is read by the build.
            var items = Game.ItemsFor(player);
            var robe = items.Find("Simple Robe") ?? throw new InvalidOperationException("no Simple Robe");
            var inventories = Game.ItemsOf(player);
            inventories.Put(PlayerInventories.BodyArmour, new InventoryItem(inventories.NewItemId(), 0, 0,
                ItemCatalog.Create(robe)));
        }
        for (int i = 0; i < 300; i++) Game.CharacterPlayerStats(player);
        int cycles = int.TryParse(Environment.GetEnvironmentVariable("Game_PERF_CYCLES"), out int c) ? c : 10;
        cycles = Math.Max(cycles, 1);
        string stage = Environment.GetEnvironmentVariable("Game_PERF_STATFLIPS_STAGE") ?? "both";
        const int BuildsPerPhase = 200;
        const double PoisonSeconds = 1.0;
        Out.WriteLine($"statflips: looping, pid {Environment.ProcessId}");
        using var self = Process.GetCurrentProcess();

        bool WaitUntil(Func<bool> done)
        {
            var deadline = Stopwatch.StartNew();
            while (!done())
            {
                if (deadline.Elapsed > TimeSpan.FromSeconds(10)) return false;
                Thread.Sleep(10);
            }
            return true;
        }

        // One stage: one input flips, the other stays put for the whole stage. The live inputs
        // before the stage's first measured build, so it is not counted as a flip. The miss
        // reasons are counted per stage (the Difference of the two keys on a rebuild), so each
        // stage names the input its own misses come from.
        void RunStage(string name, bool poisonStage)
        {
            long allocatedBefore = Allocated();
            var cpuBefore = self.TotalProcessorTime;
            var clock = Stopwatch.StartNew();
            long builds = 0, misses = 0, flipBuilds = 0, flipMisses = 0, steadyMisses = 0;
            var missReasons = new Dictionary<string, int>();
            double flipMs = 0, steadyMs = 0;
            long poisonedBuilds = 0, movingBuilds = 0, stillBuilds = 0;
            long noMovingFlagBuilds = 0, keyPoisonDisagrees = 0, keyMovingDisagrees = 0;
            long poisonAppliesFailed = 0, poisonExpiriesMissed = 0, movingStopsMissed = 0;
            int lastPoison = player.IncomingAilments.PoisonStacks(Game.AreaTime(player));
            bool lastWalking = player.Walk.Moving(Game.AreaTime(player));

            // One build: the snapshot, and whether the sources sum was rebuilt for it.
            void Build()
            {
                double now = Game.AreaTime(player);
                int poison = player.IncomingAilments.PoisonStacks(now);
                bool walking = player.Walk.Moving(now);
                bool flip = poison != lastPoison || walking != lastWalking;
                (lastPoison, lastWalking) = (poison, walking);
                var before = player.StatSourcesCache;
                long started = Stopwatch.GetTimestamp();
                Game.CharacterPlayerStats(player);
                double took = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                var cached = player.StatSourcesCache;
                bool miss = !ReferenceEquals(before, cached);
                builds++;
                if (flip)
                {
                    flipBuilds++;
                    flipMs += took;
                    if (miss) flipMisses++;
                }
                else
                {
                    steadyMs += took;
                    if (miss) steadyMisses++;
                }
                if (miss) misses++;
                if (miss && before is not null && cached is not null)
                {
                    string why = (flip ? "flip: " : "steady: ") + before.Key.Difference(cached.Key);
                    missReasons[why] = missReasons.GetValueOrDefault(why) + 1;
                }
                if (poison > 0) poisonedBuilds++;
                if (walking) movingBuilds++; else stillBuilds++;
                // On a rebuild the stored key is fresh: check it saw the same inputs this build did.
                // On a hit it is the old build's key, stale by design, so there is nothing to check.
                if (miss && cached is not null)
                {
                    if (cached.Key.PoisonStacks != poison) keyPoisonDisagrees++;
                    if (cached.Key.Flags is null) noMovingFlagBuilds++;
                    else if (!(cached.Key.Flags is Game.ReadFlags kept ? kept.TryGetValueUnrecorded("_while_moving", out bool keyMoving) : cached.Key.Flags.TryGetValue("_while_moving", out keyMoving))) noMovingFlagBuilds++;
                    else if (keyMoving != walking) keyMovingDisagrees++;
                }
            }

            // The walk targets, as the busy instance's path: 12 away from the spawn, walkable ones only.
            var (px, py) = (layout.SpawnX, layout.SpawnY);
            var steps = new[] { (px + 12, py), (px, py + 12), (px - 12, py), (px, py - 12) }
                .Where(p => layout.WalkableFor(p.Item1, p.Item2, 0)).DefaultIfEmpty((px, py)).ToArray();
            for (int cycle = 0; cycle < cycles; cycle++)
            {
                double now = Game.AreaTime(player);
                if (poisonStage)
                {
                    // Poison on, standing still: one poison that expires, and no walk order.
                    if (!player.IncomingAilments.ApplyPoison(now, 200.0, PoisonSeconds)) poisonAppliesFailed++;
                    for (int i = 0; i < BuildsPerPhase; i++) Build();
                    // Poison off: wait for the poison to expire, then build.
                    if (!WaitUntil(() => player.IncomingAilments.PoisonStacks(Game.AreaTime(player)) == 0)) poisonExpiriesMissed++;
                    for (int i = 0; i < BuildsPerPhase; i++) Build();
                }
                else
                {
                    // Moving on, never poisoned: a walk to the cycle's point.
                    var (tx, ty) = steps[cycle % steps.Length];
                    player.Walk.Order(tx, ty, now);
                    for (int i = 0; i < BuildsPerPhase; i++) Build();
                    // Moving off: wait for the walk to arrive, then build.
                    if (!WaitUntil(() => !player.Walk.Moving(Game.AreaTime(player)))) movingStopsMissed++;
                    for (int i = 0; i < BuildsPerPhase; i++) Build();
                }
            }
            double seconds = clock.Elapsed.TotalSeconds;
            self.Refresh();
            string scenario = "statflips-" + name;
            Record(scenario, "builds", builds, "");
            Record(scenario, "builds per second", builds / seconds, "/s");
            foreach (var (why, n) in missReasons.OrderByDescending(p => p.Value))
                Record(scenario, "miss reason " + why, n, "");
            Record(scenario, "cache misses", misses, "");
            Record(scenario, "cache misses per 1000 builds", misses * 1000.0 / builds, "");
            Record(scenario, "flip builds", flipBuilds, "");
            Record(scenario, "misses on flip builds", flipMisses, "");
            Record(scenario, "misses on steady builds", steadyMisses, "");
            Record(scenario, "CharacterPlayerStats us per call", (flipMs + steadyMs) * 1000 / builds, "us");
            Record(scenario, "us per call on flip builds", flipBuilds == 0 ? 0 : flipMs * 1000 / flipBuilds, "us");
            Record(scenario, "us per call on steady builds", builds - flipBuilds == 0 ? 0 : steadyMs * 1000 / (builds - flipBuilds), "us");
            Record(scenario, "allocated KB per call", (Allocated() - allocatedBefore) / 1024.0 / builds, "KB");
            Record(scenario, "scenario CPU ms", (self.TotalProcessorTime - cpuBefore).TotalMilliseconds, "ms");
            Record(scenario, "scenario allocated MB", (Allocated() - allocatedBefore) / 1048576.0, "MB");
            Record(scenario, "builds with poison", poisonedBuilds, "");
            Record(scenario, "builds moving", movingBuilds, "");
            Record(scenario, "builds standing still", stillBuilds, "");
            Record(scenario, "rebuilds with no _while_moving flag in the key", noMovingFlagBuilds, "");
            Record(scenario, "rebuilds where the key poison disagrees with the live poison", keyPoisonDisagrees, "");
            Record(scenario, "rebuilds where the key moving disagrees with the live moving", keyMovingDisagrees, "");
            Record(scenario, "poison applies failed", poisonAppliesFailed, "");
            Record(scenario, "poison expiries missed (10 s deadline)", poisonExpiriesMissed, "");
            Record(scenario, "moving stops missed (10 s deadline)", movingStopsMissed, "");
        }

        if (stage is "both" or "poison") RunStage("poison", poisonStage: true);
        if (stage is "both" or "moving") RunStage("moving", poisonStage: false);
        player.Closed = true;
    }

    public static async Task Area_sweep()
    {
        _ = ConsoleLog.Value;
        using var wire = new Wire();
        const string id = "1_1_2";
        var layout = AreaLayouts.For(AreaTransitionCatalog.HashOf(id)!.Value, 1);
        var player = Player(layout, 1, wire);
        var items = Game.ItemsFor(player);
        var (gemType, variant) = items.FindGem("Ice Nova") ?? throw new InvalidOperationException("no Ice Nova gem");
        var gem = Game.ItemsFor(player).Gem(gemType)!;
        var created = ItemCatalog.Create(gemType) with { GemExperience = gem.ExperienceFor(20), GemVariant = variant.Index };
        var robe = items.Find("Simple Robe") ?? throw new InvalidOperationException("no Simple Robe");
        var inventories = Game.ItemsOf(player);
        inventories.Put(PlayerInventories.BodyArmour, new InventoryItem(inventories.NewItemId(), 0, 0,
            ItemCatalog.Create(robe) with { Sockets = [new ItemSocket(SocketColour.Blue, created)] }));
        var nova = Game.GemSkillEffects(player).First(g => g.Name.Contains("Ice Nova"));
        var kind = Game.Monsters.Find("Metadata/Monsters/Zombies/ZombieBite") ?? Game.Packs.For(id)!.Scattered
            .SelectMany(p => p.Pack.Entries).Select(e => Game.Monsters.Find(e.Variety.ToString()))
            .OfType<MonsterCatalog.Variety>().First(v => v.Supported && Game.Emerges.Kind(v, MonsterRarity.Normal) == EmergeKind.None);
        var (px, py) = (layout.SpawnX, layout.SpawnY);
        var random = new Random(7);
        foreach (int crowd in (int[])[100, 300, 500])
        {
            while (player.Area.Monsters.Count < crowd)
            {
                // Inside the nova's radius (the aimed point is the player): spawning may push a monster a little out.
                int x = px + random.Next(-12, 13), y = py + random.Next(-12, 13);
                await Game.SpawnMonster(player, kind, 5, MonsterRarity.Normal, x, y, log: false);
            }
            // Crowded spawns push each other apart: put every monster back inside the radius (overlapping is fine here).
            double now0 = player.Clock.Elapsed.TotalSeconds;
            foreach (var m in player.Area.Monsters.Values)
            {
                m.Walk.Teleport(px + random.Next(-15, 16), py + random.Next(-15, 16), now0);
                m.Life = 1_000_000_000; m.FlinchUntil = 0;
            }
            const int Sweeps = 6;
            var times = new List<double>();
            long allocated = 0, sent = 0;
            for (int i = 0; i < Sweeps; i++)
            {
                foreach (var m in player.Area.Monsters.Values) m.FlinchUntil = 0;
                long a0 = Allocated(), s0 = Interlocked.Read(ref wire.Received);
                var clock = Stopwatch.StartNew();
                await Game.AreaSkillHit(player, nova.Id, nova, (px, py), (px, py), immediate: true);
                times.Add(clock.Elapsed.TotalMilliseconds);
                if (i > 0) { allocated += Allocated() - a0; sent += Interlocked.Read(ref wire.Received) - s0; }
            }
            // The first sweep is a fresh process's JIT; the median of the others.
            var warm = times.Skip(1).Order().ToList();
            Record("sweep", $"{crowd} monsters: first sweep ms", times[0], "ms");
            Record("sweep", $"{crowd} monsters: sweep ms (median)", warm[warm.Count / 2], "ms");
            Record("sweep", $"{crowd} monsters: allocated MB per sweep", allocated / 1048576.0 / (Sweeps - 1), "MB");
        }
        // The loop's inputs, one call each, as the sweep reads them per victim or per sweep.
        void Time(string name, Action call, int n = 2000)
        {
            for (int i = 0; i < 50; i++) call();
            long a0 = GC.GetAllocatedBytesForCurrentThread();
            var clock = Stopwatch.StartNew();
            for (int i = 0; i < n; i++) call();
            Record("sweep", $"{name} µs per call", clock.Elapsed.TotalMilliseconds * 1000 / n, "µs");
            Record("sweep", $"{name} KB per call", (GC.GetAllocatedBytesForCurrentThread() - a0) / 1024.0 / n, "KB");
        }
        var first = player.Area.Monsters.First();
        var playerStats = Game.CharacterPlayerStats(player);
        var skillStats = Game.SupportedSkill(player, nova.Id).Stats;
        var hit = Game.SupportedHit(player, nova.Id);
        var pen = Penetration.Of(playerStats, Program.Stats, attack: false, skillStats, null);
        var mods = Game.HitResistMods(playerStats, skillStats);
        Time("CharacterPlayerStats", () => Game.CharacterPlayerStats(player), 200);
        Time("SupportedHit", () => Game.SupportedHit(player, nova.Id), 200);
        Time("SupportedCriticalChance", () => Game.SupportedCriticalChance(player, nova.Id), 200);
        Time("SupportedCriticalMultiplier", () => Game.SupportedCriticalMultiplier(player, nova.Id), 200);
        Time("roll + mitigation (MonsterDamage)", () => Game.MonsterDamage(first.Value,
            hit.Roll(Random.Shared), false, attack: false, mods, pen, out _, false));
        Time("LifeUpdate packet", () => InstanceProtocol.LifeUpdate(first.Key, 5));
        Time("ChargesOnHit", () => Game.ChargesOnHit(player, first.Value, false, true, nova.Effect, nova.Level, nova.Quality), 200);
        var leech = Game.HitLeechSources(playerStats, false, skillStats, null);
        var rolledHit = hit.Roll(Random.Shared);
        Time("LeechOnHitLocked", () => Game.LeechOnHitLocked(player, first.Value, rolledHit, 10, leech), 200);
        Time("ApplyMonsterHitLocked", () => Game.ApplyMonsterHitLocked(player, first.Value, 1, null, null), 200);
        Time("HitRollsFor + WithOutgoingDamage", () => GameEvents.WithOutgoingDamage(player, playerStats, first.Value,
            (Game.HitRollsFor(playerStats) with { DamageMorePct = 0 }).RollOutgoingHit(hit, Random.Shared)), 200);
        Time("NotifyMonsterHit (sends)", () => Game.NotifyMonsterHit(player, first.Key, first.Value,
            new(1_000_000, false), 1, "bench", log: true).GetAwaiter().GetResult(), 200);
        player.Closed = true;
    }
}

// The runtime's GC AllocationTick events (about one per 100 KB allocated), summed by type: where a scenario's allocation goes.
sealed class AllocationSampler : System.Diagnostics.Tracing.EventListener
{
    readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> bytes = new();

    protected override void OnEventSourceCreated(System.Diagnostics.Tracing.EventSource source)
    {
        if (source.Name == "Microsoft-Windows-DotNETRuntime")
            EnableEvents(source, System.Diagnostics.Tracing.EventLevel.Verbose, (System.Diagnostics.Tracing.EventKeywords)0x1);
    }

    protected override void OnEventWritten(System.Diagnostics.Tracing.EventWrittenEventArgs e)
    {
        if (e.EventName is not "GCAllocationTick_V4" and not "GCAllocationTick_V3" || e.Payload is null) return;
        int type = e.PayloadNames!.IndexOf("TypeName"), amount = e.PayloadNames.IndexOf("AllocationAmount64"),
            kind = e.PayloadNames.IndexOf("AllocationKind");
        if (type < 0 || amount < 0) return;
        // A large object (85 KB and more) is collected only with generation 2: the full collections that pause everything.
        string name = (kind >= 0 && Convert.ToInt32(e.Payload[kind]) == 1 ? "LARGE " : "") + (string)e.Payload[type]!;
        bytes.AddOrUpdate(name, Convert.ToInt64(e.Payload[amount]), (_, v) => v + Convert.ToInt64(e.Payload[amount]));
    }

    public IEnumerable<(string Type, double Kb)> Top(int n) =>
        bytes.OrderByDescending(p => p.Value).Take(n).Select(p => (p.Key.Length > 46 ? p.Key[^46..] : p.Key, p.Value / 1024.0));
}

// The Game module's context as the Gateway's module host gives it (ModHost.Context), for the parts of its Load PerfBench runs.
sealed class PerfModuleContext(IModuleLifetime lifetime) : IModuleContext
{
    public ModManifest Manifest { get; } = new("Game", "1.0.0", ModKind.Feature, [], "bin/game.dll");
    public IModuleLifetime Lifetime => lifetime;
    public string Folder => "Modules/Core/Game";
    public void Log(string message) => Console.WriteLine($"[modules] Game: {message}");
    public void ProvideLeagueRules(Func<LeagueRules> rules) { }
    public T Settings<T>() => throw new NotSupportedException();
    public void SaveSettings<T>(T settings) => throw new NotSupportedException();
}

