using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text.Json;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

partial class Program
{
    // The Program type already exists during hot reload; a new static initializer would not run again.
    static ConcurrentDictionary<string, byte>? pendingDelveTrips;
    static ConcurrentDictionary<string, byte> PendingDelveTrips => LazyInitializer.EnsureInitialized(ref pendingDelveTrips,
        () => new ConcurrentDictionary<string, byte>(StringComparer.Ordinal));

    static async Task RequestDelveTrip(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        IReadOnlyList<InstanceDelve.RoutePoint> route)
    {
        if (!conn.PlayerSent || conn.LeavingArea || conn.Closed || conn.PlayerLife == 0
            || conn.Layout.Hash is not (StashCatalog.DelveHub or InstanceDelve.MineArea)) return;
        // Only the first quest cell has terrain and an encounter implementation. Never charge an arbitrary chart path.
        if (route.Count != 2 || route[0] != new InstanceDelve.RoutePoint(0, 0, 2, 0)
            || route[1] != new InstanceDelve.RoutePoint(0, 0, 2, 1))
        {
            await conn.Commands!.Reply("Only the first Azurite quest route is available."); return;
        }
        if (conn.Layout.Hash == StashCatalog.DelveHub)
        {
            if (!PendingDelveTrips.TryAdd(CharacterName(conn), 0)) return;
            try
            {
                await ChangeArea(remote, stream, ciphers, conn,
                    new PendingTransfer(InstanceDelve.MineArea, 0, false, AtSpawn: true) { Seed = 1 }, "Selected first Delve route");
            }
            finally { if (!conn.LeavingArea) PendingDelveTrips.TryRemove(CharacterName(conn), out _); }
        }
        else await StartDelveTrip(remote, stream, ciphers, conn);
    }

    static async Task StartPendingDelveTrip(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
    {
        if (conn.Layout.Hash == InstanceDelve.MineArea && PendingDelveTrips.TryRemove(CharacterName(conn), out _))
            await StartDelveTrip(remote, stream, ciphers, conn);
    }

    static async Task StartDelveTrip(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
    {
        if (conn.DelveJourney is not null || conn.DelveCrawlerObject == 0 || conn.Layout.Walkable is null) return;
        if (!File.Exists("data/tables/DelveLevelScaling.json"))
        {
            await conn.Commands!.Reply("Delve trip scaling data is unavailable; no sulphite was spent."); return;
        }
        var rail = DelveRailRoute.Load(conn.Layout.Id, conn.Seed);
        if (rail is null) { await conn.Commands!.Reply("The mine rail capture is unavailable; no sulphite was spent."); return; }
        using var scaling = JsonDocument.Parse(File.ReadAllBytes("data/tables/DelveLevelScaling.json"));
        // 0x141ea3300 prices path points after the origin. BlockY*8 + CellY + 1 = 2 for the first quest destination.
        var depth = scaling.RootElement.EnumerateArray().First(r => r.GetProperty("Depth").GetInt32() == 2);
        int cost = depth.GetProperty("SulphiteCost").GetInt32();
        byte level = depth.GetProperty("MonsterLevel").GetByte();
        var trip = new DelveJourney(rail.Points, cost);
        if ((CharacterSaves.Load(CharacterName(conn))?.Sulphite ?? 0) < cost)
        {
            await conn.Commands!.Reply($"This trip requires {cost} sulphite."); return;
        }
        lock (conn.Monsters)
        {
            if (conn.DelveJourney is not null || conn.LeavingArea || conn.Closed || conn.PlayerLife == 0) return;
            if ((CharacterSaves.Load(CharacterName(conn))?.Sulphite ?? 0) < cost) return;
            conn.DelveJourney = trip;
        }
        // Moving blockers are removed before departure. The authored Crawler type itself is nonblocking.
        foreach (uint blocker in conn.DelveCrawlerBlockers)
        {
            conn.RemoveBlocker(blocker);
            await Send(remote, stream, ciphers, InstanceProtocol.RemoveObject(blocker), null);
        }
        conn.DelveCrawlerBlockers.Clear();
        var start = trip.At(0).At;
        conn.DelveCrawlerAt = start;
        foreach (uint id in new[] { conn.DelveCrawlerObject }.Concat(conn.DelveCrawlerAttachments))
            await Send(remote, stream, ciphers, InstanceProtocol.Teleport(id, start.X, start.Y, 0x7fffffff), "Place Crawler at rail start");
        await Send(remote, stream, ciphers, InstanceProtocol.Orientation(conn.DelveCrawlerObject, trip.Heading(0), locked: true), null);
        await Send(remote, stream, ciphers, InstanceWaypoints.OpenUi(InstanceNpcs.DelveMapUi, 0), "Close chart for Delve departure");
        Console.WriteLine($"[gateway] {remote} Delve trip armed (smooth motion + trail): {rail.Points.Count} rail points, {trip.Length:0.0} cells, cost {cost}, monster level {level}");
        _ = RunDelveTrip(remote, stream, ciphers, conn, trip, level, rail.Encounters);
    }

    static async Task RunDelveTrip(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, DelveJourney trip, byte level, IReadOnlyList<DelveRailRoute.Encounter> encounters)
    {
        int epoch = conn.ProjectileEpoch;
        var spawned = new HashSet<int>();
        // The tutorial is an Azurite quest room. Use its authored Weta/Spider pack pool for the initial route;
        // Delve_Mines has no ordinary WorldAreas pack weights. General biome pools need a separate implementation.
        var questPacks = encounters.Select(e => e.Pack).Where(n => n.Length > 0).Distinct()
            .Select(Packs.Find).OfType<PackCatalog.Pack>()
            .Where(p => p.Entries.Any(e => Monsters.Find(e.Variety.ToString()) is { Supported: true })).ToArray();
        double previous = conn.Clock.Elapsed.TotalSeconds;
        bool moving = false;
        double nextLamp = 0;
        uint previousLamp = 0;
        Task spawnWork = Task.CompletedTask;
        var spawnQueue = new Queue<DelveRailRoute.Encounter>();
        float heading = trip.Heading(0);
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(50));
            while (await timer.WaitForNextTickAsync())
            {
                double now = conn.Clock.Elapsed.TotalSeconds;
                double seconds = Math.Clamp(now - previous, 0, .25); previous = now;
                bool valid() => !conn.Closed && !conn.LeavingArea && conn.ProjectileEpoch == epoch && ReferenceEquals(conn.DelveJourney, trip);
                if (!valid() || conn.PlayerLife == 0) return;
                if (conn.Paused || seconds <= 0) continue;
                var at = trip.At(trip.Travelled).At;
                (int X, int Y) player;
                lock (conn.Monsters) player = conn.Walk.Position(now);
                // Wait for an escort who is behind; accelerate when the player leads. The target remains the rail path.
                double separation = DelveRailRoute.Distance(at, player);
                var ahead = trip.At(Math.Min(trip.Length, trip.Travelled + 40)).At;
                bool leading = DelveRailRoute.Distance(ahead, player) < separation;
                const float speed = 24;
                bool advance = DelveJourney.EscortMoving(moving, separation, leading);
                if (moving != advance)
                {
                    moving = advance;
                    await Send(remote, stream, ciphers, InstanceProtocol.AnimationUpdate(conn.DelveCrawlerObject, 0, advance ? 1u : 0u, 1), null, valid);
                }
                if (!advance) continue;
                ushort before = 0, after = 0;
                bool paid;
                lock (conn.Monsters)
                {
                    if (!valid() || conn.PlayerLife == 0) return;
                    if (conn.Paused) continue;
                    paid = trip.Advance(seconds * speed, amount => Saves.Atomically(() =>
                    {
                        var save = CharacterSaves.Load(CharacterName(conn));
                        if (save?.Sulphite is not ushort balance || balance < amount) return false;
                        before = balance; after = (ushort)(before - amount);
                        CharacterSaves.SetSulphite(CharacterName(conn), after); return true;
                    }));
                    conn.DelveCrawlerAt = trip.At(trip.Travelled).At;
                }
                if (!paid) return;
                var target = conn.DelveCrawlerAt;
                if (before != after)
                    await Send(remote, stream, ciphers, InstanceDelve.SulphiteChanged(before, after),
                        $"Delve sulphite {before}->{after}; route {trip.Travelled:0.0}/{trip.Length:0.0}; Crawler {target}", valid);
                // Positioned uses atan2(dx,-dy). Follow the current rail tangent without a delayed turn.
                heading = trip.Heading(trip.Travelled);
                await Send(remote, stream, ciphers, InstanceProtocol.Orientation(conn.DelveCrawlerObject, heading, locked: true), null, valid);
                // Bone-attached parts follow the body; separate MoveTo packets can fight their attachment transform.
                await Send(remote, stream, ciphers, InstanceProtocol.MoveTo(conn.DelveCrawlerObject, target.X, target.Y, .12f, 0x7fffffff), null, valid);
                while (nextLamp <= trip.Travelled)
                {
                    var lampAt = trip.At(nextLamp).At;
                    uint lamp;
                    lock (conn.Monsters) lamp = conn.NextObjectId++;
                    await Send(remote, stream, ciphers, InstanceEntities.CreateObject(lamp,
                        InstanceEntities.ObjectTypeHash(DelveTrailLightObject.MetadataPath),
                        new DelveTrailLightObject(lampAt.X, lampAt.Y, previousLamp).Blob()), $"Delve trail lamp {lamp} at {lampAt}, link {previousLamp}", valid);
                    await Send(remote, stream, ciphers, InstanceProtocol.EnterWakeZone(lamp), null, valid);
                    previousLamp = lamp; nextLamp += 30;
                }
                // Reveal authored room spawn points as the Crawler lights them, once per journey.
                for (int encounterIndex = 0; encounterIndex < encounters.Count; encounterIndex++)
                {
                    var encounter = encounters[encounterIndex];
                    if (spawned.Contains(encounterIndex) || DelveRailRoute.Distance(target, (encounter.X, encounter.Y)) > 60) continue;
                    spawned.Add(encounterIndex);
                    spawnQueue.Enqueue(encounter);
                }
                // A whole pack can take several ticks to create. Keep it out of the motion clock, in one ordered worker.
                if (spawnWork.IsCompleted)
                {
                    await spawnWork; // observe failures rather than silently losing the encounter worker
                    var pending = spawnQueue.ToArray(); spawnQueue.Clear();
                    spawnWork = Task.Run(() => SpawnEncounters(pending));
                }
                async Task SpawnEncounters(IEnumerable<DelveRailRoute.Encounter> pending)
                {
                  foreach (var encounter in pending)
                  {
                    PackCatalog.Pack? pack = encounter.Pack.Length > 0 ? Packs.Find(encounter.Pack) : null;
                    if (encounter.Pack.Length == 0 && questPacks.Length > 0) pack = questPacks[Random.Shared.Next(questPacks.Length)];
                    if (pack is not null)
                        for (int i = 0, count = Random.Shared.Next(pack.MinCount, Math.Max(pack.MinCount, pack.MaxCount) + 1); i < count; i++)
                            if (valid() && Monsters.Find(pack.PickVariety(Random.Shared).ToString()) is { Supported: true } variety)
                            {
                                var spawn = conn.Layout.Near((encounter.X, encounter.Y));
                                uint monster = await SpawnMonster(remote, stream, ciphers, conn, variety, level, MonsterRarity.Normal, spawn.X, spawn.Y);
                                lock (conn.Monsters) trip.EncounterMonsters.Add(monster);
                            }
                  }
                }
                if (trip.Finished)
                {
                    await spawnWork;
                    await SpawnEncounters(spawnQueue);
                    await Send(remote, stream, ciphers, InstanceProtocol.AnimationUpdate(conn.DelveCrawlerObject, 0, 0, 1), "Delve Crawler arrived", valid);
                    Console.WriteLine($"[gateway] {remote} Delve trip finished at {target}, sulphite {trip.Charged}/{trip.Cost}");
                    await BlockDelveCrawler(remote, stream, ciphers, conn, target.X, target.Y, heading);
                    // Arrival alone is not victory: finish the route's own encounter before opening its reward.
                    while (valid() && conn.PlayerLife > 0)
                    {
                        bool alive;
                        lock (conn.Monsters) alive = trip.EncounterMonsters.Any(id => conn.Monsters.TryGetValue(id, out var m) && m.Life > 0);
                        if (!conn.Paused && !alive)
                        {
                            await CompleteDelveTrip(remote, stream, ciphers, conn, trip, level, valid);
                            if (trip.Completed) break;
                        }
                        await Task.Delay(200);
                    }
                    return;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            Console.WriteLine($"[gateway] {remote} Delve trip stopped: {ex.Message}");
        }
        finally
        {
            // Keep the terminal journey on the connection so duplicate requests cannot charge the trip twice.
            if (!conn.Closed && !conn.LeavingArea)
                try { await Send(remote, stream, ciphers, InstanceProtocol.AnimationUpdate(conn.DelveCrawlerObject, 0, 0, 1), null); }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
        }
    }

    static async Task CompleteDelveTrip(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        DelveJourney trip, byte level, Func<bool> valid)
    {
        var reward = DelveRewards.Current().Roll(trip.Depth, Random.Shared);
        var drops = Environment.GetEnvironmentVariable("Game_DROPS") == "0" ? []
            : DropsFor(conn).Container(level, reward.Count, reward.Rarity, Random.Shared);
        var groundItems = new List<(uint Id, WorldItemObject Ground)>();
        lock (conn.Monsters)
        {
            if (!valid() || conn.PlayerLife == 0 || conn.Paused || trip.Completed || !trip.Finished
                || trip.EncounterMonsters.Any(id => conn.Monsters.TryGetValue(id, out var m) && m.Life > 0)) return;
            CharacterSaves.CompleteDelveDepth(CharacterName(conn), trip.Depth);
            if (!trip.TryComplete(false)) return;
            var at = conn.DelveCrawlerAt;
            for (int i = 0; i < drops.Count; i++)
            {
                double angle = i * 2.399963229728653, radius = 7 + Math.Sqrt(i) * 2;
                var position = conn.Layout.Near((at.X + (int)Math.Round(Math.Cos(angle) * radius),
                    at.Y + (int)Math.Round(Math.Sin(angle) * radius)));
                var ground = new WorldItemObject(position.X, position.Y, drops[i].Item);
                groundItems.Add((conn.Instance.Ground.Add(ground), ground));
            }
        }
        foreach (var (id, ground) in groundItems)
        {
            await Send(remote, stream, ciphers, InstanceEntities.CreateObject(id,
                InstanceEntities.ObjectTypeHash(WorldItemObject.MetadataPath), ground.Blob()), "Delve completion reward", valid);
            await Send(remote, stream, ciphers, InstanceProtocol.EnterWakeZone(id), null, valid);
        }
        Console.WriteLine($"[gateway] {remote} Delve depth {trip.Depth} completed: {groundItems.Count} items, +{reward.QuantityPercent}% quantity, +{reward.Rarity}% rarity");
    }
}
