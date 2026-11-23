using GameServer.Core.Protocol.Instance;
using GameServer.Modding;
using GameServer.Core;

namespace GameServer.Modules.Delve;

public sealed partial class DelveModule
{
    // Two events settle the paused interpolation's previous/target values while leaving its current value for the
    // next UI tick. A third event overwrites current too, suppressing the fill-change event that repositions the label.
    static async Task RefreshGauge(IPlayer player, IPlayerWorld world)
    {
        ushort amount = Sulphite(world.CharacterData);
        for (int i = 0; i < 2; i++)
            await player.Send(InstanceDelve.SulphiteChanged(amount, amount), "Synchronize Delve sulphite gauge");
    }

    async Task RequestTrip(IModuleContext module, IPlayer modPlayer, IReadOnlyList<InstanceDelve.RoutePoint> route)
    {
        var player = PlayerOf(modPlayer);
        if (!player.Connection.PlayerSent || player.LeavingArea || player.Closed || player.PlayerLife == 0
            || player.Layout.Hash is not (Hub or InstanceDelve.MineArea)) return;
        var world = WorldOf(modPlayer);
        var mine = Mine(module, modPlayer);
        var plan = mine.Select(route, Price, out string why);
        if (plan is null) { await modPlayer.Tell(why); return; }
        if (mine.Pending?.Id == plan.Id) return;
        if (Sulphite(world.CharacterData) < plan.Cost)
        { await modPlayer.Tell($"This trip requires {plan.Cost} sulphite."); return; }
        // Validate the generated layout and rails before transferring, including the first trip.
        IEnumerable<string> previousShapes = mine.SeenRoutes ?? [];
        if (mine.LastScene is { SceneSeed: > 0 } last && last.RailShape?.StartsWith("v1:", StringComparison.Ordinal) != true
            && DelveRailRoute.Load("Delve_Main", last.SceneSeed) is { } previousRail)
            previousShapes = previousShapes.Append(previousRail.Shape());
        var shapes = previousShapes.ToArray();
        DelveMine.Plan? prepared = null;
        for (int retry = 0; retry < 12; retry++)
        {
            DelveMine.Plan candidate;
            try { candidate = DelveTerrain.Procedural(mine, plan, AreaRng(player)); }
            catch (ArgumentException ex) { await modPlayer.Tell(ex.Message); return; }
            if (ServerLevels.PrepareDelve(candidate.SceneSeed, DelveMine.MonsterLevel(plan.Depth), candidate.Terrain!) is not { } scene) continue;
            var rails = DelveRailRoute.Load("Delve_Main", candidate.SceneSeed);
            if (rails is null || (mine.SeenTerrain ?? []).Contains(scene.TileHash))
            { ServerLevels.ForgetDelve(candidate.SceneSeed); continue; }
            string shape = rails.Shape();
            if (shapes.Any(old => DelveRailRoute.Similar(old, shape))) { ServerLevels.ForgetDelve(candidate.SceneSeed); continue; }
            prepared = candidate with { TerrainHash = scene.TileHash, RailShape = shape };
            break;
        }
        if (prepared is null) { await modPlayer.Tell("A new connected mine route could not be generated. No sulphite was spent."); return; }
        plan = prepared;
        var attempt = new Attempt(plan, world.AccountData) { Player = modPlayer, Owner = CharacterName(player) };
        _attempts[plan.Id] = attempt;
        if (!DelveMine.Change(world.AccountData, m => m.Pending is null && m.Current == plan.From
            ? m with { Pending = plan, SceneSeeds = plan.SceneSeed == 0 ? m.SceneSeeds : [.. (m.SceneSeeds ?? []).Append(plan.SceneSeed)] } : null))
        { _attempts.TryRemove(plan.Id, out _); return; }
        try { await TransferChunk(module, modPlayer, attempt); }
        catch { await AbortTrip(module, modPlayer, attempt, "departure failed"); throw; }
    }

    async Task StartPendingTrip(IModuleContext module, IPlayer player)
    {
        if (player.AreaId != "Delve_Main") return;
        var mine = Mine(module, player);
        if (mine.Pending is not { } plan || !_attempts.TryGetValue(plan.Id, out var attempt) || attempt.Seed != PlayerOf(player).Seed) return;
        if (attempt.Owner != CharacterName(PlayerOf(player))) return;
        attempt.Player = player;
        try { await StartTrip(module, player, attempt); }
        catch { await AbortTrip(module, player, attempt, "route could not start"); throw; }
    }

    async Task StartTrip(IModuleContext module, IPlayer modPlayer, Attempt attempt)
    {
        var player = PlayerOf(modPlayer);
        var world = WorldOf(modPlayer);
        var state = _states.GetOrCreateValue(modPlayer);
        var rail = state.Rail ??= DelveRailRoute.Load(player.Layout.Id, player.Seed);
        if (state.Journey is not null || state.Crawler == 0 || player.Layout.Walkable is null || rail is null)
        {
            await AbortTrip(module, modPlayer, attempt, "native rail geometry unavailable");
            await modPlayer.Tell("The mine geometry is unavailable. The trip was reset; no additional sulphite was spent.");
            if (!player.Closed && !player.LeavingArea)
                await ChangeArea(player, new PendingTransfer(Hub, 0, false, AtWaypoint: true), "Return to camp: mine rails unavailable");
            return;
        }
        int depth = attempt.Plan.Depth, cost = attempt.Plan.Cost;
        byte level = DelveMine.MonsterLevel(depth);
        var trip = new DelveJourney(rail.Points, cost, depth);
        if (attempt.Cancelled || player.LeavingArea || player.Closed || player.PlayerLife == 0) return;
        state.Journey = trip;
        await ResetScene(module, modPlayer, state);
        await ShowStartBoundary(modPlayer, state, trip.At(0).At);
        // Moving blockers are removed before departure. The authored Crawler type itself is nonblocking.
        foreach (uint blocker in state.Blockers)
        {
            world.RemoveBlocker(blocker);
            await global::Game.SendWorldRemoval(player, blocker, null);
        }
        state.Blockers.Clear();
        var start = trip.At(0).At;
        state.CrawlerCell = start;
        foreach (uint id in new[] { state.Crawler }.Concat(state.Attachments))
            await global::Game.SendWorldRecord(player, id, InstanceProtocol.Teleport(id, start.X, start.Y, 0x7fffffff), "Place Crawler at rail start");
        await global::Game.SendWorldRecord(player, state.Crawler, InstanceProtocol.Orientation(state.Crawler, trip.CableHeading(0), locked: false), null);
        await global::Game.SendWorldRecord(player, state.Crawler, InstanceProtocol.Orientation(state.Crawler, trip.CableHeading(0), locked: true), null);
        Console.WriteLine($"[gateway] {player.Connection.Remote} Delve trip armed (smooth motion + trail): {rail.Points.Count} rail points, {trip.Length:0.0} cells, cost {cost}, monster level {level}");
        _ = RunTrip(module, modPlayer, state, trip, level, rail.Encounters, attempt);
    }

    async Task RunTrip(IModuleContext module, IPlayer modPlayer, State state, DelveJourney trip, byte level, IReadOnlyList<DelveRailRoute.Encounter> encounters, Attempt attempt)
    {
        var player = PlayerOf(modPlayer);
        var character = WorldOf(modPlayer).CharacterData;
        int epoch = player.ProjectileEpoch;
        var spawned = new HashSet<int>();
        // Ordinary tier-one Azurite rooms leave their spawn names empty. Reuse the native Mines quest-room
        // Weta/Spider pool there; both the route and its defence draw from this same supported pool.
        var questPacks = encounters.Select(e => e.Pack).Where(n => n.Length > 0).Distinct()
            .Select(Packs.Find).OfType<PackCatalog.Pack>()
            .Where(p => p.Entries.Any(e => Monsters.Find(e.Variety.ToString()) is { Supported: true })).ToArray();
        if (questPacks.Length == 0)
            questPacks = new[] { "delve-azurite-weta-pack", "delve-azurite-spider-pack" }
                .Select(Packs.Find).OfType<PackCatalog.Pack>()
                .Where(p => p.Entries.Any(e => Monsters.Find(e.Variety.ToString()) is { Supported: true })).ToArray();
        double previous = AreaTime(player);
        bool moving = false;
        double nextLamp = 30;
        double lastLampAt = -1;
        uint previousLamp = 0;
        Task spawnWork = Task.CompletedTask;
        Task finalWaveWork = Task.CompletedTask;
        var spawnQueue = new Queue<DelveRailRoute.Encounter>();
        float heading = float.NaN;
        bool valid() => module.Lifetime.IsActive && !attempt.Cancelled && !player.Closed && !player.LeavingArea && player.ProjectileEpoch == epoch && ReferenceEquals(state.Journey, trip);
        async Task PlaceLamp(double along)
        {
            var at = trip.At(along).At;
            uint lamp;
            if (along >= trip.Length - .001) { lamp = state.Crawler + 7; state.DestinationLight = lamp; }
            else lamp = player.TakeObjectId();
            await global::Game.SendWorldCreate(player, lamp, InstanceEntities.CreateObject(lamp,
                InstanceEntities.ObjectTypeHash(DelveTrailLightObject.MetadataPath),
                new DelveTrailLightObject(at.X, at.Y, previousLamp).Blob()), $"Delve trail lamp {lamp} at {at}, link {previousLamp}", valid);
            await global::Game.SendWorldRecord(player, lamp, InstanceProtocol.EnterWakeZone(lamp), null, valid);
            state.Trail.Add(lamp); previousLamp = lamp; lastLampAt = along;
        }
        try
        {
            await PlaceLamp(0);
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(50));
            while (await timer.WaitForNextTickAsync(module.Lifetime.Stopping))
            {
                double now = AreaTime(player);
                double seconds = Math.Clamp(now - previous, 0, .25); previous = now;

                if (!valid() || player.PlayerLife == 0) return;
                if (player.Paused || seconds <= 0) continue;
                double departed = trip.Travelled;
                (int X, int Y) position;
                position = player.Walk.Position(now);
                if (!trip.Started)
                {
                    if (!trip.TryStart(position)) continue;
                    await RemoveStartBoundary(modPlayer, state);
                    module.Log($"Delve trip started: player left the {DelveJourney.StartRadius}-cell starting circle");
                }
                double step = trip.EscortStep(position, seconds, moving);
                bool advance = step > .001;
                if (moving != advance)
                {
                    moving = advance;
                    await global::Game.SendWorldRecord(player, state.Crawler, InstanceProtocol.AnimationUpdate(state.Crawler, 0, advance ? 1u : 0u, 1), null, valid);
                }
                if (!advance) continue;
                ushort before = 0, after = 0;
                bool paid;
                if (!valid() || player.PlayerLife == 0) return;
                if (player.Paused) continue;
                paid = trip.Advance(step, amount => character.Change(SulphiteKey, text =>
                {
                    if (!ushort.TryParse(text, out ushort balance) || balance < amount) return null;
                    before = balance; after = (ushort)(before - amount);
                    return after.ToString();
                }));
                state.CrawlerCell = trip.At(trip.Travelled).At;
                if (!paid) { await AbortTrip(module, modPlayer, attempt, "insufficient sulphite"); return; }
                var target = state.CrawlerCell;
                if (before != after)
                    await Send(player, InstanceDelve.SulphiteChanged(before, after),
                        $"Delve sulphite {before}->{after}; route {trip.Travelled:0.0}/{trip.Length:0.0}; Crawler {target}", valid);
                float nextHeading = trip.CableHeading(departed);
                if (!float.IsFinite(heading) || MathF.Abs(nextHeading - heading) > .001f)
                {
                    // SetOrientation ignores an already locked object; unlock first, then set and lock the new heading.
                    await global::Game.SendWorldRecord(player, state.Crawler, InstanceProtocol.Orientation(state.Crawler, nextHeading, locked: false), null, valid);
                    await global::Game.SendWorldRecord(player, state.Crawler, InstanceProtocol.Orientation(state.Crawler, nextHeading, locked: true), null, valid);
                    heading = nextHeading;
                }
                // Bone-attached parts follow the body; separate MoveTo packets can fight their attachment transform.
                await global::Game.SendWorldRecord(player, state.Crawler, InstanceProtocol.MoveTo(state.Crawler, target.X, target.Y, (float)seconds, 0x7fffffff), null, valid);
                while (nextLamp <= trip.Travelled)
                {
                    await PlaceLamp(nextLamp); nextLamp += 30;
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
                    spawnWork = AreaFrames.Start(() => SpawnEncounters(pending));
                }
                async Task<int> SpawnEncounters(IEnumerable<DelveRailRoute.Encounter> pending)
                {
                    int created = 0;
                    foreach (var encounter in pending)
                    {
                        PackCatalog.Pack? pack = encounter.Pack.Length > 0 ? Packs.Find(encounter.Pack) : null;
                        if (encounter.Pack.Length == 0 && questPacks.Length > 0) pack = questPacks[AreaRng(player).Next(questPacks.Length)];
                        if (pack is not null)
                            for (int i = 0, count = (int)Math.Min(100, Math.Ceiling(AreaRng(player).Next(pack.MinCount, Math.Max(pack.MinCount, pack.MaxCount) + 1) * DelveMine.PackMultiplier(trip.Depth))); i < count; i++)
                                if (valid() && Monsters.Find(pack.PickVariety(AreaRng(player)).ToString()) is { Supported: true } variety)
                                {
                                    var spawn = player.Layout.Near((encounter.X, encounter.Y));
                                    uint monster = await SpawnMonster(player, variety, level, GameServer.Core.Protocol.Instance.MonsterRarity.Normal, spawn.X, spawn.Y);
                                    created++;
                                    trip.EncounterMonsters.Add(monster);
                                    if (encounter.Destination) trip.DestinationMonsters.Add(monster);
                                    if (!valid())
                                    {
                                        if (player.Area.Monsters.Remove(monster, out var retired)) { retired.Life = 0; retired.Dead = true; retired.ActionGeneration++; }
                                        player.Instance.World.MonsterOrigins.TryRemove(monster, out _);
                                        if (!player.Closed && !player.LeavingArea) await RemoveSceneObject(modPlayer, monster);
                                        return created;
                                    }
                                }
                    }
                    return created;
                }
                if (trip.Finished)
                {
                    if (lastLampAt < trip.Length - .001) await PlaceLamp(trip.Length);
                    await spawnWork;
                    await SpawnEncounters(spawnQueue);
                    await global::Game.SendWorldRecord(player, state.Crawler, InstanceProtocol.AnimationUpdate(state.Crawler, 0, 0, 1), "Delve Crawler arrived", valid);
                    Console.WriteLine($"[gateway] {player.Connection.Remote} Delve trip finished at {target}, sulphite {trip.Charged}/{trip.Cost}");
                    await BlockCrawler(module, modPlayer, state, target.X, target.Y, heading);
                    if (questPacks.Length == 0) throw new InvalidOperationException("The Delve route has no supported encounter packs for its final waves.");
                    int queuedWaves = 0;
                    previous = AreaTime(player);
                    // Arrival starts a timed defence, not a save. All three waves must finish spawning before any
                    // last-kill check can commit; a gap between waves must never look like a completed encounter.
                    while (valid() && player.PlayerLife > 0)
                    {
                        now = AreaTime(player);
                        seconds = Math.Clamp(now - previous, 0, .25); previous = now;
                        if (!player.Paused) queuedWaves += trip.AdvanceFinale(seconds);
                        if (finalWaveWork.IsCompleted)
                        {
                            await finalWaveWork;
                            if (queuedWaves > 0 && !player.Paused)
                            {
                                queuedWaves--;
                                var pack = questPacks[AreaRng(player).Next(questPacks.Length)];
                                position = player.Walk.Position(now);
                                var waveAt = player.Layout.Near((position.X + AreaRng(player).Next(-25, 26), position.Y + AreaRng(player).Next(-25, 26)));
                                finalWaveWork = SpawnWave();
                                async Task SpawnWave()
                                {
                                    int created = await SpawnEncounters([new DelveRailRoute.Encounter(pack.Id, waveAt.X, waveAt.Y, Destination: true)]);
                                    if (created == 0) throw new InvalidOperationException("A Delve final wave spawned no monsters; the checkpoint cannot be saved.");
                                    trip.WaveSpawned();
                                    module.Log($"Delve final wave {trip.WavesSpawned}/{DelveJourney.FinaleWaveCount}, depth {trip.Depth}, level {level}");
                                }
                            }
                        }
                        bool alive;
                        alive = !trip.DestinationCleared(id => player.Area.Monsters.TryGetValue(id, out var m)
                            && m.Hostile && !m.Dead && m.Life > 0);
                        // A menu may pause combat after the last kill. Committing an already won encounter must
                        // still refresh the chart while it is open; no movement or combat advances here.
                        if (!alive)
                        {
                            await CompleteTrip(module, modPlayer, character, state, trip, level, valid, attempt);
                            if (trip.Completed) { _attempts.TryRemove(attempt.Plan.Id, out _); break; }
                        }
                        await AreaDelay(player, 0.2, module.Lifetime.Stopping);
                    }
                    return;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException or InvalidOperationException)
        {
            Console.WriteLine($"[gateway] {player.Connection.Remote} Delve trip stopped: {ex.Message}");
        }
        finally
        {
            try { await Task.WhenAll(spawnWork, finalWaveWork); }
            catch (Exception ex) { module.Log($"Delve encounter worker stopped: {ex.Message}"); }
            if (!trip.Completed && !player.LeavingArea)
                await AbortTrip(module, modPlayer, attempt, player.PlayerLife == 0 ? "player died" : "journey interrupted");
            if (!player.Closed && !player.LeavingArea)
                try { await global::Game.SendWorldRecord(player, state.Crawler, InstanceProtocol.AnimationUpdate(state.Crawler, 0, 0, 1), null); }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException) { }
        }
    }

    async Task CompleteTrip(IModuleContext module, IPlayer modPlayer, ISavedData character, State state, DelveJourney trip, byte level, Func<bool> valid, Attempt attempt)
    {
        var player = PlayerOf(modPlayer);
        var reward = attempt.Plan.Reward ? DelveRewards.Current().Roll(trip.Depth, AreaRng(player)) : (Count: 0, Rarity: 0, QuantityPercent: 0d);
        var drops = Environment.GetEnvironmentVariable("Game_DROPS") == "0" ? []
            : DropsFor(player).Container(level, reward.Count, reward.Rarity, AreaRng(player));
        var groundItems = new List<(uint Id, WorldItemObject Ground)>();
        if (!valid() || player.PlayerLife == 0 || trip.Completed || !trip.Finished
            || !trip.DestinationCleared(id => player.Area.Monsters.TryGetValue(id, out var m)
                && m.Hostile && !m.Dead && m.Life > 0)) return;
        if (!DelveMine.Change(attempt.Account, m => m.Pending?.Id == attempt.Plan.Id ? m.Finish(attempt.Plan.Id) : null)) return;
        character.Change(DepthKey, text => Math.Max(int.TryParse(text, out int deepest) ? deepest : 0, trip.Depth).ToString());
        if (!trip.TryComplete(false)) return;
        if (attempt.Plan.Reward)
        {
            uint azurite = (uint)(30 + Math.Max(0, trip.Depth - 1) * 10);
            ChangeEquipment(attempt.Account, p => p with { Azurite = p.Azurite + azurite });
        }
        player.Instance.SetState(DestinationFlag(modPlayer));
        var at = state.CrawlerCell;
        for (int i = 0; i < drops.Count; i++)
        {
            double angle = i * 2.399963229728653, radius = 7 + Math.Sqrt(i) * 2;
            var position = player.Layout.Near((at.X + (int)Math.Round(Math.Cos(angle) * radius),
                at.Y + (int)Math.Round(Math.Sin(angle) * radius)));
            var ground = new WorldItemObject(position.X, position.Y, drops[i].Item);
            groundItems.Add((player.Instance.Ground.Add(ground, player), ground));
        }
        await global::Game.SendWorldRecord(player, state.Crawler + 4, InstanceEntities.TransitionableUpdate(state.Crawler + 4, 3, 2),
            "Activate completed Delve checkpoint", valid);
        // The roaming native type has no Targetable. Once parked, give the same cart its stationary chart interaction.
        await EnsureDestinationLight(modPlayer, state, state.CrawlerCell);
        await CreateCrawlerBody(modPlayer, state, state.CrawlerCell.X, state.CrawlerCell.Y, trip.Heading(trip.Length), roaming: false);
        await PlayCompletionEffect(modPlayer, state, state.CrawlerCell);
        if (attempt.Plan.Reward && Equipment(attempt.Account).Azurite >= 30 && !player.Character.HasQuestFlag("NikoHaveEnoughAzurite"))
            await SetQuestFlags(player, ["NikoHaveEnoughAzurite"]);
        await PublishChart(module, modPlayer);
        foreach (var (id, ground) in groundItems)
        {
            await global::Game.SendWorldCreate(player, id, InstanceEntities.CreateObject(id,
                InstanceEntities.ObjectTypeHash(WorldItemObject.MetadataPath), ground.Blob(justDropped: true)), "Delve completion reward", valid);
            await global::Game.SendWorldRecord(player, id, InstanceProtocol.EnterWakeZone(id), null, valid);
        }
        Console.WriteLine($"[gateway] {player.Connection.Remote} Delve depth {trip.Depth} completed: {groundItems.Count} items, +{reward.QuantityPercent}% quantity, +{reward.Rarity}% rarity");
    }
}
