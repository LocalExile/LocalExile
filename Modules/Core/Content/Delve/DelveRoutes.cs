using System.Collections.Concurrent;
using System.Text.Json;
using GameServer.Core.Protocol.Instance;
using GameServer.Modding;
using GameServer.Core;

namespace GameServer.Modules.Delve;

public sealed partial class DelveModule
{
    /// <summary>Fills this type's game data at startup (the world loader, before the Gateway listens).</summary>
    public static void LoadDelveRoutesData()
    {
        TripPrices = LoadTripPrices();
    }

    static string DestinationFlag(IPlayer player) => $"Delve.Destination.{CharacterName(PlayerOf(player))}";
    sealed class Attempt(DelveMine.Plan plan, ISavedData account)
    {
        public readonly DelveMine.Plan Plan = plan;
        public readonly ISavedData Account = account;
        public IPlayer? Player;
        public string Owner = "";
        public volatile bool Cancelled;
        public readonly HashSet<Player> Departures = [];
        public uint Seed => Plan.SceneSeed == 0 ? DelveTerrain.Seed(Plan.From, Plan.To) : Plan.SceneSeed;
    }
    readonly ConcurrentDictionary<Guid, Attempt> _attempts = new();
    static int[] TripPrices = default!;

    static int[] LoadTripPrices()
    {
        var doc = ClientTables.Table("DelveLevelScaling");
        return doc.OrderBy(r => r.Int("Depth"))
            .Select(r => r.Int("SulphiteCost")).ToArray();
    }
    static int Price(int depth) => TripPrices[Math.Clamp(depth - 1, 0, TripPrices.Length - 1)];

    // Each native window starts at the current checkpoint; only the initial route contains the camp entrance.
    byte MineLevel(IModuleContext module, IPlayer player)
    {
        var mine = Mine(module, player);
        int depth = mine.Pending is { } plan && _attempts.ContainsKey(plan.Id) ? plan.Depth : mine.Current.Depth;
        return DelveMine.MonsterLevel(depth);
    }

    AreaHeader MineHeader(IModuleContext module, IPlayer player)
    {
        var mine = Mine(module, player);
        byte level = MineLevel(module, player);
        if (ServerLevels.DelveTerrain(player.Seed) is null && mine.LastScene is { Terrain: { } saved } scene && scene.SceneSeed == player.Seed)
        {
            if (ServerLevels.PrepareDelve(player.Seed, level, saved) is not null)
                PlayerOf(player).Use(AreaLayouts.For(InstanceDelve.MineArea, player.Seed));
        }
        return new(ServerLevels.DelveTerrain(player.Seed) ?? DelveTerrain.Data(player.Seed), [2]) { Level = level };
    }

    DelveMine Mine(IModuleContext module, IPlayer player)
    {
        var account = WorldOf(player).AccountData;
        var mine = DelveMine.Load(account);
        if (mine.Pending is { } abandoned && !_attempts.ContainsKey(abandoned.Id))
        {
            DelveMine.Change(account, m => m.Pending?.Id == abandoned.Id ? m with { Pending = null } : null);
            mine = DelveMine.Load(account);
        }
        return mine;
    }

    void RegisterTrips(IModuleContext module)
    {
        module.Lifetime.Own(GameEvents.TransferResolved.Add(module.Manifest.Id, async (player, _, response) =>
        {
            if (response.Ok) return;
            foreach (var attempt in _attempts.Values.Where(a => ReferenceEquals(a.Player, PlayerOf(player))))
                await AbortTrip(module, PlayerOf(player), attempt, "instance transfer failed");
        }));
        GameEvents.PlayerLeaving.Add(module.Manifest.Id, (Func<Player, string, Task>)(async (leaving, _) =>
        {
            var player = PlayerOf(leaving);
            foreach (var attempt in _attempts.Values.Where(a => ReferenceEquals(a.Player, player)))
            {
                lock (attempt.Departures) if (attempt.Departures.Contains(PlayerOf(player))) continue;
                await AbortTrip(module, player, attempt, "left the mine");
            }
        }));
        module.Lifetime.Own(module.Lifetime.Stopping.Register(() =>
        {
            foreach (var attempt in _attempts.Values)
            {
                attempt.Cancelled = true;
                DelveMine.Change(attempt.Account, m => m.Pending?.Id == attempt.Plan.Id ? m with { Pending = null } : null);
            }
        }));
        GameEvents.AreaTick.Add(module.Manifest.Id, (Func<Player, Func<Player, double, Task>?>)(start => (Func<Player, double, Task>?)(!start.Layout.Id.StartsWith("Delve", StringComparison.Ordinal) ? null : async (player, _) =>
        {
            var modPlayer = PlayerOf(player);
            var ready = player;
            if (ready.Connection.PlayerSent && !ready.Closed && !ready.LeavingArea && modPlayer.AreaId is "Delve_Hub" or "Delve_Main")
            {
                // Without DelveOwner the native 0x7b handler opens only the current terrain's local preview in a mine.
                if (!ready.Character.HasQuestFlag("DelveOwner"))
                {
                    await SetQuestFlags(ready, ["DelveOwner"]);
                }
                await SpawnCrawler(module, modPlayer);
            }
            if (modPlayer.AreaId != "Delve_Main") return;
            if (_states.TryGetValue(modPlayer, out var deadState) && player.PlayerLife == 0
                && (deadState.Journey?.Completed == true || player.Instance.HasState(DestinationFlag(modPlayer))))
                deadState.ResetNeeded = true;
            if (_states.TryGetValue(modPlayer, out var state) && state.ResetNeeded && player.PlayerLife > 0)
            {
                await ResetScene(module, modPlayer, state); state.ResetNeeded = false;
            }
            foreach (var attempt in _attempts.Values.Where(a => ReferenceEquals(a.Player, modPlayer)))
                if (player.PlayerLife == 0) await AbortTrip(module, modPlayer, attempt, "player died");
        })));
        GameEvents.Drops.Add(module.Manifest.Id, (Action<DropRoll>)(roll =>
        {
            if (roll.ModPlayer.AreaId != "Delve_Main" || !_states.TryGetValue(roll.ModPlayer, out var state) || state.Journey is not { } journey) return;
            var policy = DelveRewards.Current();
            roll.Quantity *= 1 + (journey.Depth - 1L) * policy.QuantityPerDepth / 100;
            roll.Rarity *= 1 + (journey.Depth - 1L) * policy.RarityPerDepth / 100;
        }));
        module.Lifetime.Own(GameEvents.StateBuilding.Add(module.Manifest.Id, (player, json) =>
        {
            if (player.Layout.Hash is not (Hub or InstanceDelve.MineArea)) return;
            var modPlayer = PlayerOf(player); var mine = Mine(module, modPlayer);
            json["delveMine"] = JsonSerializer.SerializeToNode(mine);
            if (_states.TryGetValue(modPlayer, out var state) && state.Journey is { } journey && state.Rail is { } rail)
            {
                var next = journey.At(Math.Min(journey.Length, journey.Travelled + 40)).At;
                int defenders;
                defenders = journey.DestinationMonsters.Count(id => player.Area.Monsters.TryGetValue(id, out var m)
                    && m.Hostile && !m.Dead && m.Life > 0);
                json["delveTrip"] = JsonSerializer.SerializeToNode(new { journey.Depth, journey.Travelled, journey.Length,
                    journey.Cost, journey.Charged, journey.Started, journey.Completed, FinishDefendersAlive = defenders,
                    ZoneMonsterLevel = DelveMine.MonsterLevel(journey.Depth), journey.WavesScheduled, journey.WavesSpawned, journey.FinaleElapsed,
                    Crawler = new[] { state.CrawlerCell.X, state.CrawlerCell.Y },
                    Start = new[] { rail.Points[0].X, rail.Points[0].Y }, End = new[] { rail.Points[^1].X, rail.Points[^1].Y },
                    Next = new[] { next.X, next.Y } });
            }
        }));
    }

    async Task AbortTrip(IModuleContext module, IPlayer modPlayer, Attempt attempt, string reason)
    {
        if (!_attempts.TryRemove(attempt.Plan.Id, out _)) return;
        attempt.Cancelled = true;
        DelveMine.Change(attempt.Account, m => m.Pending?.Id == attempt.Plan.Id ? m with { Pending = null } : null);
        if (_states.TryGetValue(modPlayer, out var state))
        {
            if (!PlayerOf(modPlayer).Closed && !PlayerOf(modPlayer).LeavingArea) await RemoveStartBoundary(modPlayer, state);
            state.ResetNeeded = true;
            if (state.Journey is { } journey)
            {
                var player = PlayerOf(modPlayer); uint[] monsters;
                monsters = journey.EncounterMonsters.ToArray();
                foreach (uint id in monsters)
                {
                    if (player.Area.Monsters.Remove(id, out var m)) { m.Life = 0; m.Dead = true; m.ActionGeneration++; }

                    player.Instance.World.MonsterOrigins.TryRemove(id, out _);
                }
                state.Journey = null;
                if (!player.Closed && !player.LeavingArea)
                    foreach (uint id in monsters) await RemoveSceneObject(modPlayer, id);
            }
            if (!PlayerOf(modPlayer).Closed && !PlayerOf(modPlayer).LeavingArea)
                foreach (uint id in state.Trail) await RemoveSceneObject(modPlayer, id);
            state.Trail.Clear();
        }
        module.Log($"Delve attempt {attempt.Plan.Id} reset to saved checkpoint: {reason}");
    }

    static async Task ResetScene(IModuleContext module, IPlayer modPlayer, State state)
    {
        var player = PlayerOf(modPlayer);
        if ((state.Rail ??= DelveRailRoute.Load(modPlayer.AreaId, player.Seed)) is not { } rail) return;
        if (state.Trail.Contains(state.DestinationLight)) state.DestinationLight = 0;
        foreach (uint id in state.Trail) await RemoveSceneObject(modPlayer, id);
        state.Trail.Clear();
        bool completed = state.Journey?.Completed == true || player.Instance.HasState(DestinationFlag(modPlayer));
        var start = completed ? rail.Points[^1] : rail.Points[0];
        if (completed) await EnsureDestinationLight(modPlayer, state, start);
        foreach (uint id in new[] { state.Crawler }.Concat(state.Attachments))
            await global::Game.SendWorldRecord(modPlayer, id, InstanceProtocol.Teleport(id, start.X, start.Y));
        state.CrawlerCell = start;
        Interlocked.Increment(ref player.ActionCount); player.Walk.Teleport(start.X + 10, start.Y, AreaTime(player));
        await global::Game.SendWorldRecord(modPlayer, player.Id, InstanceProtocol.Teleport(player.Id, start.X + 10, start.Y));
        await global::Game.SendWorldRecord(modPlayer, state.Crawler, InstanceProtocol.AnimationUpdate(state.Crawler, 0, 0, 1));
        var presentation = new DelveJourney(rail.Points, 0);
        await BlockCrawler(module, modPlayer, state, start.X, start.Y, presentation.Heading(completed ? presentation.Length : 0));
    }

    async Task TransferChunk(IModuleContext module, IPlayer modPlayer, Attempt attempt)
    {
        var player = PlayerOf(modPlayer);
        lock (attempt.Departures) attempt.Departures.Add(player);
        await ChangeArea(player,
            new PendingTransfer(InstanceDelve.MineArea, 0, true, AtSpawn: true) { Seed = attempt.Seed }, "Delve route segment");
        if (!player.LeavingArea) await AbortTrip(module, modPlayer, attempt, "departure refused");
    }
}
