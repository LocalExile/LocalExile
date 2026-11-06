using System.Runtime.CompilerServices;
using GameServer.Core.Protocol.Instance;

static partial class QuestobjectsRules
{
    const string BeaconRefueled = "a6q1-RefueledBeacon", BeaconDelivered = "a6q1-DeliveredFlag";
    // The native payload's visible escort circle is roughly this radius; speed 13 is its .ot base_speed.
    const double BeaconEscortRadius = 22, BeaconSpeed = 13;
    static string BeaconCartFlag(GlyphQuest.Placement at) => Quests.InstanceFlag($"BeaconCart:{at.X}:{at.Y}");
    static readonly ConditionalWeakTable<ClientState, BeaconView> BeaconViews = new();
    static readonly ConditionalWeakTable<InstanceState, BeaconRun> BeaconRuns = new();

    sealed class BeaconCart(GlyphQuest.Placement start, RailEscort walk)
    {
        public readonly GlyphQuest.Placement Start = start;
        public readonly RailEscort Walk = walk;
        public bool Moving;
    }
    sealed class BeaconRun
    {
        public readonly SemaphoreSlim Gate = new(1);
        public readonly List<BeaconCart> Carts = [];
        public readonly List<WeakReference<ClientState>> Players = [];
        public List<GlyphQuest.Placement> Controllers = [], Spawners = [];
        public double LastAt;
        public bool Loaded;
    }
    sealed class BeaconView
    {
        public readonly List<uint> Carts = [];
        public readonly Dictionary<uint, (int Target, bool Moving)> ShownMoves = [];
        public readonly Dictionary<uint, byte> ShownStates = [];
        public bool Loaded;
    }

    internal static bool CanUseBeacon(ClientState conn, string type) => conn.Layout.Id != Act6Areas.Beacon || type switch
    {
        Act6Objects.BeaconLever => BeaconRuns.TryGetValue(conn.Instance, out var run) && run.Carts.Count == 2 && run.Carts.All(c => c.Walk.Complete),
        Act6Objects.BeaconStatue => conn.Instance.HasState(Act6Objects.BeaconIgnited),
        _ => true,
    };

    internal static async Task TickBeacon(ClientState conn)
    {
        if (conn.Layout.Id != Act6Areas.Beacon || !conn.PlayerSent || conn.Closed || conn.LeavingArea) return;
        var run = BeaconRuns.GetValue(conn.Instance, _ => new());
        await run.Gate.WaitAsync();
        try
        {
            var view = BeaconViews.GetValue(conn, _ => new());
            if (!view.Loaded) await CreateBeacon(conn, run, view);
            if (run.Carts.Count != 2) return;
            double now = conn.Clock.Elapsed.TotalSeconds;
            double seconds = Math.Clamp(now - run.LastAt, 0, 0.5);
            run.LastAt = now;
            var players = run.Players.Select(w => w.TryGetTarget(out var c) ? c : null)
                .Where(c => c is { Closed: false, LeavingArea: false }).Cast<ClientState>().ToArray();
            foreach (var cart in run.Carts)
            {
                var before = cart.Walk.Position;
                bool escorted = players.Any(c => c.PlayerLife > 0 && !c.Paused && c.Walk.Position(now) is var p
                    && Math.Pow(p.X - before.X, 2) + Math.Pow(p.Y - before.Y, 2) <= BeaconEscortRadius * BeaconEscortRadius);
                cart.Walk.Advance(seconds, cart.Moving);
                cart.Moving = escorted && !cart.Walk.Complete;
                if (cart.Walk.Complete)
                {
                    conn.Instance.Transition(Act6Objects.BeaconPayload, cart.Start.X, cart.Start.Y);
                    conn.Instance.SetState(BeaconCartFlag(cart.Start));
                }
                if (cart.Moving) await SpawnBeaconGuards(conn, run, cart.Walk.Position);
            }
            bool refueled = run.Carts.All(c => c.Walk.Complete);
            foreach (var player in players)
            {
                if (refueled && !ClientHasQuestFlag(player, BeaconRefueled))
                {
                    var (remote, stream, ciphers) = player.Wire;
                    await SetQuestFlags(remote, stream, ciphers, player, [BeaconRefueled]);
                }
                await ShowBeacon(player, run, BeaconViews.GetValue(player, _ => new()));
            }
        }
        finally { run.Gate.Release(); }
    }

    static async Task CreateBeacon(ClientState conn, BeaconRun run, BeaconView view)
    {
        var placed = new QuestObjectTypes.AreaPlacements(Act6Areas.Beacon, conn.Seed, []);
        var (remote, stream, ciphers) = conn.Wire;
        // Rebind the old quest rules and add missing objects when this module is hot-reloaded in an occupied area.
        foreach (var rule in QuestsOf(conn).Objects(Act6Areas.Beacon))
        {
            bool found = false;
            lock (conn.QuestObjects)
                foreach (var (id, o) in conn.QuestObjects.Where(p => p.Value.Rule.ObjectType == rule.ObjectType).ToArray())
                { conn.QuestObjects[id] = (rule, o.At, o.Info); found = true; }
            if (!found)
                foreach (var at in placed.Of(rule.ObjectType)) await CreateQuestObject(remote, stream, ciphers, conn, rule, at);
        }
        var starts = placed.Of(Act6Objects.BeaconPayload);
        if (!run.Loaded)
        {
            var rails = placed.Of(LabyrinthTrials.VisibleRail);
            var paths = LabyrinthTrials.RoombaPaths([.. rails.Select(p => (p.X, p.Y))], [.. starts.Select(p => (p.X, p.Y, p.Rotation))]);
            for (int i = 0; i < starts.Count; i++)
            {
                if (paths[i] is not { Ring: false } path) continue;
                var start = starts[i];
                var points = path.Rails.ToList();
                static double Distance((int X, int Y) p, GlyphQuest.Placement a) => Math.Pow(p.X - a.X, 2) + Math.Pow(p.Y - a.Y, 2);
                if (Distance(points[^1], start) < Distance(points[0], start)) points.Reverse();
                // Each route ends at its straight rail beside the tank's ramp, before the shared central gap.
                int end = points.FindIndex(1, p => rails.Any(r => r.X == p.X && r.Y == p.Y && r.Model.Contains("rail_straight", StringComparison.OrdinalIgnoreCase)));
                if (end < 1) continue;
                points = points.Take(end + 1).ToList();
                var walk = new RailEscort(points, BeaconSpeed);
                (int X, int Y)? old = null;
                lock (conn.QuestObjects)
                {
                    var payloads = conn.QuestObjects.Where(p => p.Value.Rule.ObjectType == Act6Objects.BeaconPayload).OrderBy(p => p.Key).ToArray();
                    if (i < payloads.Length) old = (payloads[i].Value.At.X, payloads[i].Value.At.Y);
                }
                if (conn.Instance.Transitioned(start.Type, start.X, start.Y) || ClientHasQuestFlag(conn, BeaconRefueled) || ClientHasQuestFlag(conn, BeaconDelivered))
                    walk.Advance(double.MaxValue, true);
                else if (old is { } at) walk.Restore(at);
                run.Carts.Add(new(start, walk));
            }
            run.Controllers = placed.Of("Metadata/QuestObjects/Act6/BeaconPayload_SpawnController");
            foreach (string suffix in new[] { "GhostPirateSpawner", "GhostPirateSpawnerMagic", "GhostPirateSpawnerRare", "GhostTotemSpawner" })
                run.Spawners.AddRange(placed.Of("Metadata/QuestObjects/Act6/BeaconPayload_" + suffix));
            run.LastAt = conn.Clock.Elapsed.TotalSeconds;
            run.Loaded = true;
            Console.WriteLine($"[gateway] Beacon: {run.Carts.Count}/2 fuel carts, {run.Controllers.Count} encounter triggers");
        }
        foreach (var cart in run.Carts)
        {
            uint id;
            lock (conn.QuestObjects)
                id = conn.QuestObjects.Where(p => p.Value.Rule.ObjectType == Act6Objects.BeaconPayload).OrderBy(p => p.Key).Skip(view.Carts.Count).Select(p => p.Key).FirstOrDefault();
            if (id == 0)
            {
                var at = cart.Walk.Position;
                var info = QuestObjectTypes.Find(Act6Objects.BeaconPayload) ?? throw new InvalidOperationException("BeaconPayload metadata missing");
                var position = cart.Start with { X = (int)Math.Round(at.X), Y = (int)Math.Round(at.Y) };
                lock (conn.QuestObjects)
                {
                    id = conn.NextObjectId++;
                    conn.QuestObjects[id] = (new Quests.ObjectRule(Act6Areas.Beacon, Act6Objects.BeaconPayload, BeaconCartFlag(cart.Start)), position, info);
                }
                await Send(remote, stream, ciphers, InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(Act6Objects.BeaconPayload),
                    new QuestObject(position.X, position.Y, info.Components, position.Rotation, position.Z,
                        TransitionState: (byte)(cart.Walk.Complete ? 2 : 1), Team: 4).Blob()), $"[Beacon] fuel cart at ({position.X}, {position.Y})");
                await Send(remote, stream, ciphers, InstanceProtocol.EnterWakeZone(id), null);
            }
            lock (conn.QuestObjects)
                if (conn.QuestObjects.TryGetValue(id, out var shown))
                    conn.QuestObjects[id] = (shown.Rule with { TransitionedBy = BeaconCartFlag(cart.Start) }, shown.At, shown.Info);
            view.Carts.Add(id);
        }
        if (ClientHasQuestFlag(conn, BeaconDelivered)) conn.Instance.SetState(Act6Objects.BeaconIgnited);
        if (!run.Players.Any(w => w.TryGetTarget(out var player) && !player.Closed && !player.LeavingArea))
        {
            run.LastAt = conn.Clock.Elapsed.TotalSeconds;
            foreach (var cart in run.Carts) cart.Moving = false;
        }
        run.Players.RemoveAll(w => !w.TryGetTarget(out var player) || player.Closed || player.LeavingArea);
        run.Players.Add(new(conn));
        view.Loaded = true;
    }

    static async Task ShowBeacon(ClientState conn, BeaconRun run, BeaconView view)
    {
        var (remote, stream, ciphers) = conn.Wire;
        int delivered = run.Carts.Count(c => c.Walk.Complete);
        bool flag = ClientHasQuestFlag(conn, BeaconDelivered), lit = flag || conn.Instance.HasState(Act6Objects.BeaconIgnited);
        for (int i = 0; i < view.Carts.Count; i++)
        {
            var cart = run.Carts[i]; uint id = view.Carts[i];
            var at = cart.Walk.Position;
            var cell = ((int)Math.Round(at.X), (int)Math.Round(at.Y));
            lock (conn.QuestObjects)
                if (conn.QuestObjects.TryGetValue(id, out var o)) conn.QuestObjects[id] = (o.Rule, o.At with { X = cell.Item1, Y = cell.Item2 }, o.Info);
            var movement = (cart.Walk.Target, cart.Moving);
            if (!view.ShownMoves.TryGetValue(id, out var shown) || shown != movement)
            {
                view.ShownMoves[id] = movement;
                var target = cart.Moving ? cart.Walk.Destination : (cell.Item1, cell.Item2);
                float seconds = cart.Moving ? (float)Math.Max(0.05, cart.Walk.RemainingSeconds) : 0.05f;
                var move = InstanceProtocol.MoveTo(id, target.Item1, target.Item2, seconds, InstanceProtocol.FollowTerrain);
                await Send(remote, stream, ciphers, cart.Moving
                    ? [.. InstanceProtocol.Orientation(id, (float)Math.Atan2(target.Item1 - at.X, at.Y - target.Item2)), .. move] : move,
                    $"[Beacon] cart {i + 1} {(cart.Moving ? "moving" : "stopped")} at {cell}, target {target}");
            }
            await BeaconState(conn, view, id, (byte)(cart.Walk.Complete ? 2 : 1));
        }
        (uint Id, string Type)[] objects;
        lock (conn.QuestObjects) objects = [.. conn.QuestObjects.Select(p => (p.Key, p.Value.Rule.ObjectType))];
        foreach (var (id, type) in objects)
        {
            byte state = type switch
            {
                Act6Objects.BeaconFuelTank => (byte)(1 + delivered),
                Act6Objects.BeaconLever => (byte)(lit ? 3 : delivered == 2 ? 2 : 1),
                Act6Objects.BeaconFlame or Act6Objects.BeaconStatue => (byte)(flag ? 3 : lit ? 2 : 1),
                Act6Objects.BeaconShip => (byte)(flag ? 2 : 1),
                _ => 0,
            };
            if (state > 0) await BeaconState(conn, view, id, state);
        }
    }

    static async Task BeaconState(ClientState conn, BeaconView view, uint id, byte state)
    {
        if (view.ShownStates.TryGetValue(id, out var previous) && previous == state) return;
        int component;
        lock (conn.QuestObjects) component = conn.QuestObjects.TryGetValue(id, out var o) ? QuestObject.TransitionableIndex(o.Info.Components) : -1;
        if (component < 0) return;
        var (remote, stream, ciphers) = conn.Wire;
        await Send(remote, stream, ciphers, InstanceEntities.TransitionableUpdate(id, (byte)component, state), $"[Beacon] 0x{id:x} state {state}");
        view.ShownStates[id] = state;
    }

    static async Task SpawnBeaconGuards(ClientState conn, BeaconRun run, (double X, double Y) at)
    {
        if (Environment.GetEnvironmentVariable("Game_MONSTERS") == "0") return;
        foreach (var trigger in run.Controllers.Where(p => Math.Pow(p.X - at.X, 2) + Math.Pow(p.Y - at.Y, 2) <= 14 * 14))
        {
            if (!conn.Instance.Transition(trigger.Type, trigger.X, trigger.Y)) continue;
            foreach (var spawn in run.Spawners.Where(p => ReferenceEquals(run.Controllers.MinBy(t => Math.Pow(t.X - p.X, 2) + Math.Pow(t.Y - p.Y, 2)), trigger)))
            {
                bool totem = spawn.Type.EndsWith("GhostTotemSpawner", StringComparison.Ordinal);
                string monster = totem ? "Metadata/Monsters/Totems/GhostPirateTotemTreasureHunter" : "Metadata/Monsters/GhostPirates/GhostPirateBeacon";
                if (Monsters.Find(monster) is not { Supported: true } variety) continue;
                var rarity = spawn.Type.EndsWith("Rare", StringComparison.Ordinal) ? MonsterRarity.Rare
                    : spawn.Type.EndsWith("Magic", StringComparison.Ordinal) ? MonsterRarity.Magic : MonsterRarity.Normal;
                var pos = conn.Layout.Near((spawn.X, spawn.Y));
                var (remote, stream, ciphers) = conn.Wire;
                await SpawnMonster(remote, stream, ciphers, conn, variety, 49, rarity, pos.X, pos.Y);
            }
            Console.WriteLine($"[gateway] Beacon encounter at ({trigger.X}, {trigger.Y})");
        }
    }
}
