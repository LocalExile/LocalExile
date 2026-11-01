using System.Collections.Concurrent;
using System.Text.Json;
using GameServer.Core.Protocol.Instance;
using GameServer.Modding;

namespace GameServer.Modules.Delve;

public sealed partial class DelveModule
{
    /// <summary>The characters who chose a route in the encampment: their trip starts when the mine is generated.</summary>
    readonly ConcurrentDictionary<string, byte> _pendingTrips = new(StringComparer.Ordinal);

    // Two events settle the paused interpolation's previous/target values while leaving its current value for the
    // next UI tick. A third event overwrites current too, suppressing the fill-change event that repositions the label.
    static async Task RefreshGauge(IPlayer player, IPlayerWorld world)
    {
        ushort amount = Sulphite(world.CharacterData);
        for (int i = 0; i < 2; i++)
            await player.Send(InstanceDelve.SulphiteChanged(amount, amount), "Synchronize Delve sulphite gauge");
    }

    async Task RequestTrip(IModuleContext module, IPlayer player, IReadOnlyList<InstanceDelve.RoutePoint> route)
    {
        var conn = ConnOf(player); var (remote, stream, ciphers) = conn.Wire;
        if (!conn.PlayerSent || conn.LeavingArea || conn.Closed || conn.PlayerLife == 0
            || conn.Layout.Hash is not (Hub or InstanceDelve.MineArea)) return;
        // Only the first quest cell has terrain and an encounter implementation. Never charge an arbitrary chart path.
        if (route.Count != 2 || route[0] != new InstanceDelve.RoutePoint(0, 0, 2, 0)
            || route[1] != new InstanceDelve.RoutePoint(0, 0, 2, 1))
        {
            await player.Tell("Only the first Azurite quest route is available."); return;
        }
        if (conn.Layout.Hash == Hub)
        {
            if (!_pendingTrips.TryAdd(CharacterName(conn), 0)) return;
            try
            {
                await ChangeArea(remote, stream, ciphers, conn,
                    new PendingTransfer(InstanceDelve.MineArea, 0, false, AtSpawn: true) { Seed = 1 }, "Selected first Delve route");
            }
            finally { if (!conn.LeavingArea) _pendingTrips.TryRemove(CharacterName(conn), out _); }
        }
        else await StartTrip(module, player);
    }

    async Task StartPendingTrip(IModuleContext module, IPlayer player)
    {
        var conn = ConnOf(player);
        if (conn.Layout.Hash == InstanceDelve.MineArea && _pendingTrips.TryRemove(CharacterName(conn), out _))
            await StartTrip(module, player);
    }

    async Task StartTrip(IModuleContext module, IPlayer player)
    {
        var conn = ConnOf(player); var (remote, stream, ciphers) = conn.Wire;
        var world = module.World(player);
        var state = _states.GetOrCreateValue(player);
        if (state.Journey is not null || state.Crawler == 0 || conn.Layout.Walkable is null) return;
        if (!File.Exists("data/tables/DelveLevelScaling.json"))
        {
            await player.Tell("Delve trip scaling data is unavailable; no sulphite was spent."); return;
        }
        var rail = DelveRailRoute.Load(conn.Layout.Id, conn.Seed);
        if (rail is null) { await player.Tell("The mine rail capture is unavailable; no sulphite was spent."); return; }
        using var scaling = JsonDocument.Parse(File.ReadAllBytes("data/tables/DelveLevelScaling.json"));
        // 0x141ea3300 prices path points after the origin. BlockY*8 + CellY + 1 = 2 for the first quest destination.
        var depth = scaling.RootElement.EnumerateArray().First(r => r.GetProperty("Depth").GetInt32() == 2);
        int cost = depth.GetProperty("SulphiteCost").GetInt32();
        byte level = depth.GetProperty("MonsterLevel").GetByte();
        var trip = new DelveJourney(rail.Points, cost);
        if (Sulphite(world.CharacterData) < cost)
        {
            await player.Tell($"This trip requires {cost} sulphite."); return;
        }
        lock (conn.Monsters)
        {
            if (state.Journey is not null || conn.LeavingArea || conn.Closed || conn.PlayerLife == 0) return;
            if (Sulphite(world.CharacterData) < cost) return;
            state.Journey = trip;
        }
        // Moving blockers are removed before departure. The authored Crawler type itself is nonblocking.
        foreach (uint blocker in state.Blockers)
        {
            world.RemoveBlocker(blocker);
            await Send(remote, stream, ciphers, InstanceProtocol.RemoveObject(blocker), null);
        }
        state.Blockers.Clear();
        var start = trip.At(0).At;
        state.CrawlerCell = start;
        foreach (uint id in new[] { state.Crawler }.Concat(state.Attachments))
            await Send(remote, stream, ciphers, InstanceProtocol.Teleport(id, start.X, start.Y, 0x7fffffff), "Place Crawler at rail start");
        await Send(remote, stream, ciphers, InstanceProtocol.Orientation(state.Crawler, trip.Heading(0), locked: true), null);
        await Send(remote, stream, ciphers, InstanceWaypoints.OpenUi(InstanceNpcs.DelveMapUi, 0), "Close chart for Delve departure");
        Console.WriteLine($"[gateway] {remote} Delve trip armed (smooth motion + trail): {rail.Points.Count} rail points, {trip.Length:0.0} cells, cost {cost}, monster level {level}");
        _ = RunTrip(module, player, state, trip, level, rail.Encounters);
    }

    static async Task RunTrip(IModuleContext module, IPlayer player, State state, DelveJourney trip, byte level, IReadOnlyList<DelveRailRoute.Encounter> encounters)
    {
        var conn = ConnOf(player); var (remote, stream, ciphers) = conn.Wire;
        var character = module.World(player).CharacterData;
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
                bool valid() => !conn.Closed && !conn.LeavingArea && conn.ProjectileEpoch == epoch && ReferenceEquals(state.Journey, trip);
                if (!valid() || conn.PlayerLife == 0) return;
                if (conn.Paused || seconds <= 0) continue;
                var at = trip.At(trip.Travelled).At;
                (int X, int Y) position;
                lock (conn.Monsters) position = conn.Walk.Position(now);
                // Wait for an escort who is behind; accelerate when the player leads. The target remains the rail path.
                double separation = DelveRailRoute.Distance(at, position);
                var ahead = trip.At(Math.Min(trip.Length, trip.Travelled + 40)).At;
                bool leading = DelveRailRoute.Distance(ahead, position) < separation;
                const float speed = 24;
                bool advance = DelveJourney.EscortMoving(moving, separation, leading);
                if (moving != advance)
                {
                    moving = advance;
                    await Send(remote, stream, ciphers, InstanceProtocol.AnimationUpdate(state.Crawler, 0, advance ? 1u : 0u, 1), null, valid);
                }
                if (!advance) continue;
                ushort before = 0, after = 0;
                bool paid;
                lock (conn.Monsters)
                {
                    if (!valid() || conn.PlayerLife == 0) return;
                    if (conn.Paused) continue;
                    paid = trip.Advance(seconds * speed, amount => character.Change(SulphiteKey, text =>
                    {
                        if (!ushort.TryParse(text, out ushort balance) || balance < amount) return null;
                        before = balance; after = (ushort)(before - amount);
                        return after.ToString();
                    }));
                    state.CrawlerCell = trip.At(trip.Travelled).At;
                }
                if (!paid) return;
                var target = state.CrawlerCell;
                if (before != after)
                    await Send(remote, stream, ciphers, InstanceDelve.SulphiteChanged(before, after),
                        $"Delve sulphite {before}->{after}; route {trip.Travelled:0.0}/{trip.Length:0.0}; Crawler {target}", valid);
                // Positioned uses atan2(dx,-dy). Follow the current rail tangent without a delayed turn.
                heading = trip.Heading(trip.Travelled);
                await Send(remote, stream, ciphers, InstanceProtocol.Orientation(state.Crawler, heading, locked: true), null, valid);
                // Bone-attached parts follow the body; separate MoveTo packets can fight their attachment transform.
                await Send(remote, stream, ciphers, InstanceProtocol.MoveTo(state.Crawler, target.X, target.Y, .12f, 0x7fffffff), null, valid);
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
                                    uint monster = await SpawnMonster(remote, stream, ciphers, conn, variety, level, GameServer.Core.Protocol.Instance.MonsterRarity.Normal, spawn.X, spawn.Y);
                                    lock (conn.Monsters) trip.EncounterMonsters.Add(monster);
                                }
                    }
                }
                if (trip.Finished)
                {
                    await spawnWork;
                    await SpawnEncounters(spawnQueue);
                    await Send(remote, stream, ciphers, InstanceProtocol.AnimationUpdate(state.Crawler, 0, 0, 1), "Delve Crawler arrived", valid);
                    Console.WriteLine($"[gateway] {remote} Delve trip finished at {target}, sulphite {trip.Charged}/{trip.Cost}");
                    await BlockCrawler(module, player, state, target.X, target.Y, heading);
                    // Arrival alone is not victory: finish the route's own encounter before opening its reward.
                    while (valid() && conn.PlayerLife > 0)
                    {
                        bool alive;
                        lock (conn.Monsters) alive = trip.EncounterMonsters.Any(id => conn.Monsters.TryGetValue(id, out var m) && m.Life > 0);
                        if (!conn.Paused && !alive)
                        {
                            await CompleteTrip(player, character, state, trip, level, valid);
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
                try { await Send(remote, stream, ciphers, InstanceProtocol.AnimationUpdate(state.Crawler, 0, 0, 1), null); }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
        }
    }

    static async Task CompleteTrip(IPlayer player, ISavedData character, State state, DelveJourney trip, byte level, Func<bool> valid)
    {
        var conn = ConnOf(player); var (remote, stream, ciphers) = conn.Wire;
        var reward = DelveRewards.Current().Roll(trip.Depth, Random.Shared);
        var drops = Environment.GetEnvironmentVariable("Game_DROPS") == "0" ? []
            : DropsFor(conn).Container(level, reward.Count, reward.Rarity, Random.Shared);
        var groundItems = new List<(uint Id, WorldItemObject Ground)>();
        lock (conn.Monsters)
        {
            if (!valid() || conn.PlayerLife == 0 || conn.Paused || trip.Completed || !trip.Finished
                || trip.EncounterMonsters.Any(id => conn.Monsters.TryGetValue(id, out var m) && m.Life > 0)) return;
            character.Change(DepthKey, text => Math.Max(int.TryParse(text, out int deepest) ? deepest : 0, trip.Depth).ToString());
            if (!trip.TryComplete(false)) return;
            var at = state.CrawlerCell;
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
