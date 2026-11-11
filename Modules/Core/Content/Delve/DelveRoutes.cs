using System.Collections.Concurrent;
using System.Text.Json;
using GameServer.Core.Protocol.Instance;
using GameServer.Modding;
using GameServer.Core;

namespace GameServer.Modules.Delve;

public sealed partial class DelveModule
{
    static string DestinationFlag(IPlayer player) => $"Delve.Destination.{CharacterName(ConnOf(player))}";
    sealed class Attempt(DelveMine.Plan plan, ISavedData account)
    {
        public readonly DelveMine.Plan Plan = plan;
        public readonly ISavedData Account = account;
        public IPlayer? Player;
        public string Owner = "";
        public volatile bool Cancelled;
        public readonly HashSet<ClientState> Departures = [];
        public uint Seed => DelveTerrain.Seed(Plan.From, Plan.To);
    }
    readonly ConcurrentDictionary<Guid, Attempt> _attempts = new();
    static readonly Lazy<int[]> TripPrices = new(() =>
    {
        var doc = ClientTables.Table("DelveLevelScaling");
        return doc.OrderBy(r => r.Int("Depth"))
            .Select(r => r.Int("SulphiteCost")).ToArray();
    });
    static int Price(int depth) => TripPrices.Value[Math.Clamp(depth - 1, 0, TripPrices.Value.Length - 1)];

    // Each native window starts at the current checkpoint; only the initial route contains the camp entrance.
    byte MineLevel(IModuleContext module, IPlayer player)
    {
        var mine = Mine(module, player);
        int depth = mine.Pending is { } plan && _attempts.ContainsKey(plan.Id) ? plan.Depth : mine.Current.Depth;
        return DelveMine.MonsterLevel(depth);
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
        module.Lifetime.Own(GameEvents.TransferResolved.Add(module.Manifest.Id, async (conn, _, response) =>
        {
            if (response.Ok) return;
            foreach (var attempt in _attempts.Values.Where(a => ReferenceEquals(a.Player, PlayerOf(conn))))
                await AbortTrip(module, PlayerOf(conn), attempt, "instance transfer failed");
        }));
        GameEvents.PlayerLeaving.Add(module.Manifest.Id, async (connection, _) =>
        {
            var player = PlayerOf(connection);
            foreach (var attempt in _attempts.Values.Where(a => ReferenceEquals(a.Player, player)))
            {
                lock (attempt.Departures) if (attempt.Departures.Contains(ConnOf(player))) continue;
                await AbortTrip(module, player, attempt, "left the mine");
            }
        });
        module.Lifetime.Own(module.Lifetime.Stopping.Register(() =>
        {
            foreach (var attempt in _attempts.Values)
            {
                attempt.Cancelled = true;
                DelveMine.Change(attempt.Account, m => m.Pending?.Id == attempt.Plan.Id ? m with { Pending = null } : null);
            }
        }));
        GameEvents.Tick.Add(module.Manifest.Id, async (connection, _) =>
        {
            var player = PlayerOf(connection);
            var ready = ConnOf(player);
            if (ready.PlayerSent && !ready.Closed && !ready.LeavingArea && player.AreaId is "Delve_Hub" or "Delve_Main")
            {
                // Without DelveOwner the native 0x7b handler opens only the current terrain's local preview in a mine.
                if (!ready.HasQuestFlag("DelveOwner"))
                {
                    var w = ready.Wire; await SetQuestFlags(w.Remote, w.Stream, w.Ciphers, ready, ["DelveOwner"]);
                }
                await SpawnCrawler(module, player);
            }
            if (player.AreaId != "Delve_Main") return;
            var conn = ConnOf(player);
            if (_states.TryGetValue(player, out var deadState) && conn.PlayerLife == 0
                && (deadState.Journey?.Completed == true || conn.Instance.HasState(DestinationFlag(player))))
                deadState.ResetNeeded = true;
            if (_states.TryGetValue(player, out var state) && state.ResetNeeded && conn.PlayerLife > 0)
            {
                await ResetScene(module, player, state); state.ResetNeeded = false;
            }
            foreach (var attempt in _attempts.Values.Where(a => ReferenceEquals(a.Player, player)))
                if (conn.PlayerLife == 0) await AbortTrip(module, player, attempt, "player died");
        });
        GameEvents.Drops.Add(module.Manifest.Id, roll =>
        {
            if (roll.Player.AreaId != "Delve_Main" || !_states.TryGetValue(roll.Player, out var state) || state.Journey is not { } journey) return;
            var policy = DelveRewards.Current();
            roll.Quantity *= 1 + (journey.Depth - 1L) * policy.QuantityPerDepth / 100;
            roll.Rarity *= 1 + (journey.Depth - 1L) * policy.RarityPerDepth / 100;
        });
        module.Lifetime.Own(GameEvents.StateBuilding.Add(module.Manifest.Id, (conn, json) =>
        {
            if (conn.Layout.Hash is not (Hub or InstanceDelve.MineArea)) return;
            var player = PlayerOf(conn); var mine = Mine(module, player);
            json["delveMine"] = JsonSerializer.SerializeToNode(mine);
            if (_states.TryGetValue(player, out var state) && state.Journey is { } journey && state.Rail is { } rail)
            {
                var next = journey.At(Math.Min(journey.Length, journey.Travelled + 40)).At;
                int defenders;
                lock (conn.Monsters) defenders = journey.DestinationMonsters.Count(id => conn.Monsters.TryGetValue(id, out var m)
                    && m.Hostile && !m.Dead && m.Life > 0);
                json["delveTrip"] = JsonSerializer.SerializeToNode(new { journey.Depth, journey.Travelled, journey.Length,
                    journey.Cost, journey.Charged, journey.Completed, FinishDefendersAlive = defenders,
                    Crawler = new[] { state.CrawlerCell.X, state.CrawlerCell.Y },
                    Start = new[] { rail.Points[0].X, rail.Points[0].Y }, End = new[] { rail.Points[^1].X, rail.Points[^1].Y },
                    Next = new[] { next.X, next.Y } });
            }
        }));
    }

    async Task AbortTrip(IModuleContext module, IPlayer player, Attempt attempt, string reason)
    {
        if (!_attempts.TryRemove(attempt.Plan.Id, out _)) return;
        attempt.Cancelled = true;
        DelveMine.Change(attempt.Account, m => m.Pending?.Id == attempt.Plan.Id ? m with { Pending = null } : null);
        if (_states.TryGetValue(player, out var state))
        {
            state.ResetNeeded = true;
            if (state.Journey is { } journey)
            {
                var conn = ConnOf(player); uint[] monsters;
                lock (conn.Monsters)
                {
                    monsters = journey.EncounterMonsters.ToArray();
                    foreach (uint id in monsters)
                    {
                        if (conn.Monsters.Remove(id, out var m)) { m.Life = 0; m.Dead = true; m.ActionGeneration++; }
                        conn.AsleepMonsters.Remove(id); conn.MonsterViews.Remove(id);
                        conn.Instance.World.MonsterOrigins.TryRemove(id, out _);
                    }
                    state.Journey = null;
                }
                if (!conn.Closed && !conn.LeavingArea)
                    foreach (uint id in monsters) await RemoveSceneObject(player, id);
            }
            if (!ConnOf(player).Closed && !ConnOf(player).LeavingArea)
                foreach (uint id in state.Trail) await RemoveSceneObject(player, id);
            state.Trail.Clear();
        }
        module.Log($"Delve attempt {attempt.Plan.Id} reset to saved checkpoint: {reason}");
    }

    static async Task ResetScene(IModuleContext module, IPlayer player, State state)
    {
        var conn = ConnOf(player);
        if ((state.Rail ??= DelveRailRoute.Load(player.AreaId, conn.Seed)) is not { } rail) return;
        foreach (uint id in state.Trail) await RemoveSceneObject(player, id);
        state.Trail.Clear();
        bool completed = state.Journey?.Completed == true || conn.Instance.HasState(DestinationFlag(player));
        var start = completed ? rail.Points[^1] : rail.Points[0];
        foreach (uint id in new[] { state.Crawler }.Concat(state.Attachments))
            await global::Game.SendWorldRecord(player, id, InstanceProtocol.Teleport(id, start.X, start.Y));
        state.CrawlerCell = start;
        lock (conn.Monsters) { Interlocked.Increment(ref conn.ActionCount); conn.Walk.Teleport(start.X + 10, start.Y, conn.Clock.Elapsed.TotalSeconds); }
        await global::Game.SendWorldRecord(player, PlayerId, InstanceProtocol.Teleport(PlayerId, start.X + 10, start.Y));
        await global::Game.SendWorldRecord(player, state.Crawler, InstanceProtocol.AnimationUpdate(state.Crawler, 0, 0, 1));
        var presentation = new DelveJourney(rail.Points, 0);
        await BlockCrawler(module, player, state, start.X, start.Y, presentation.Heading(completed ? presentation.Length : 0));
    }

    async Task TransferChunk(IModuleContext module, IPlayer player, Attempt attempt)
    {
        var conn = ConnOf(player); var (remote, stream, ciphers) = conn.Wire;
        lock (attempt.Departures) attempt.Departures.Add(conn);
        await ChangeArea(remote, stream, ciphers, conn,
            new PendingTransfer(InstanceDelve.MineArea, 0, true, AtSpawn: true) { Seed = attempt.Seed }, "Delve route segment");
        if (!conn.LeavingArea) await AbortTrip(module, player, attempt, "departure refused");
    }
}
