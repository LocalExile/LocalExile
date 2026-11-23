using System.Runtime.CompilerServices;
using GameServer.Core.Protocol.Instance;

static partial class QuestobjectsRules
{
    const string BeaconRefueled = "a6q1-RefueledBeacon", BeaconDelivered = "a6q1-DeliveredFlag";
    const double BeaconSpeed = 13;
    const string BeaconCircle = "Metadata/Effects/Spells/monsters_effects/labyrinth/skeleton_king/circles_of_virtue/aoe_circle.ao";
    // BeaconPayload attaches the PedestalBuffZone art. MiscAnimated stores its radius in world units.
    static double BeaconEscortRadius => (global::Game.Art.ArtRadius(BeaconCircle) ?? 250) * 23d / 250;
    static string BeaconCartFlag(GlyphQuest.Placement at) => Quests.InstanceFlag($"BeaconCart:{at.X}:{at.Y}");
    static readonly ConditionalWeakTable<Player, BeaconView> BeaconViews = new();
    static readonly ConditionalWeakTable<InstanceState, BeaconRun> BeaconRuns = new();

    internal sealed class BeaconCart(GlyphQuest.Placement start, RailEscort walk)
    {
        public readonly GlyphQuest.Placement Start = start;
        public readonly RailEscort Walk = walk;
        public bool Moving { get; private set; }

        public (int X, int Y) CommandTarget { get; private set; }
        public double CommandSeconds { get; private set; }

        public void Update(double elapsed, IReadOnlyList<(int X, int Y)> escorts)
        {
            // Only simulate the part of this leg which was actually sent to the client.
            if (Moving) Walk.AdvanceCurrentLeg(Math.Min(elapsed, CommandSeconds));
            // An order that ran to the leg's end can leave the cart a hair short of the corner (its seconds times the
            // speed, rounded). That is the corner: the cart turns it, or the hair would count as not moving and the cart
            // would never take the next leg (seen live at the Beacon, 2026-11-14).
            var (cx, cy) = Walk.Position;
            if (!Walk.Complete && Math.Abs(Walk.Destination.X - cx) + Math.Abs(Walk.Destination.Y - cy) < 0.01)
                Walk.AdvanceCurrentLeg(0.02 / Walk.Speed);
            var (x, y) = Walk.Position;
            var (tx, ty) = Walk.Destination;
            double dx = tx - x, dy = ty - y, length = Math.Sqrt(dx * dx + dy * dy);
            double reach = 0, radius = BeaconEscortRadius;
            if (length > 0)
                foreach (var p in escorts)
                {
                    double px = p.X - x, py = p.Y - y, squared = px * px + py * py;
                    if (squared > radius * radius) continue;
                    // Exit of this straight rail from the escort's visible circle.
                    double projection = (px * dx + py * dy) / length;
                    double exit = projection + Math.Sqrt(Math.Max(0, projection * projection + radius * radius - squared));
                    reach = Math.Max(reach, Math.Min(length, exit));
                }
            Moving = reach > 0.001;
            CommandSeconds = reach / Walk.Speed;
            CommandTarget = Moving
                ? ((int)Math.Round(x + dx * reach / length), (int)Math.Round(y + dy * reach / length))
                : ((int)Math.Round(x), (int)Math.Round(y));
        }
    }
    sealed class BeaconRun
    {
        public readonly SemaphoreSlim Gate = new(1);
        public readonly List<BeaconCart> Carts = [];
        public readonly List<WeakReference<Player>> Players = [];
        public List<GlyphQuest.Placement> Controllers = [], Spawners = [];
        public double LastAt;
        public bool Loaded;
    }
    sealed class BeaconView
    {
        public readonly List<uint> Carts = [];
        public readonly Dictionary<uint, ((int X, int Y) Target, bool Moving)> ShownMoves = [];
        public readonly Dictionary<uint, byte> ShownStates = [];
        public bool Loaded;
    }

    internal static bool CanUseBeacon(Player player, string type) => player.Layout.Id != Act6Areas.Beacon || type switch
    {
        Act6Objects.BeaconLever => BeaconRuns.TryGetValue(player.Instance, out var run) && run.Carts.Count == 2 && run.Carts.All(c => c.Walk.Complete),
        Act6Objects.BeaconStatue => player.Instance.HasState(Act6Objects.BeaconIgnited),
        _ => true,
    };

    internal static async Task TickBeacon(Player player)
    {
        if (player.Layout.Id != Act6Areas.Beacon || !player.Connection.PlayerSent || player.Closed || player.LeavingArea) return;
        var run = BeaconRuns.GetValue(player.Instance, _ => new());
        await run.Gate.WaitAsync();
        try
        {
            var view = BeaconViews.GetValue(player, _ => new());
            if (!view.Loaded) await CreateBeacon(player, run, view);
            if (run.Carts.Count != 2) return;
            double now = AreaTime(player);
            double seconds = Math.Max(0, now - run.LastAt);
            run.LastAt = now;
            var players = run.Players.Select(w => w.TryGetTarget(out var c) ? c : null)
                .Where(c => c is { Closed: false, LeavingArea: false }).Cast<Player>().ToArray();
            var escorts = players.Where(c => c.PlayerLife > 0 && !c.Paused).Select(c => c.Walk.Position(now)).ToArray();
            foreach (var cart in run.Carts)
            {
                cart.Update(seconds, escorts);
                if (cart.Walk.Complete)
                {
                    player.Instance.Transition(Act6Objects.BeaconPayload, cart.Start.X, cart.Start.Y);
                    player.Instance.SetState(BeaconCartFlag(cart.Start));
                }
                if (cart.Moving) await SpawnBeaconGuards(player, run, cart.Walk.Position);
            }
            bool refueled = run.Carts.All(c => c.Walk.Complete);
            foreach (var peer in players)
            {
                if (refueled && !ClientHasQuestFlag(peer, BeaconRefueled))
                {
                    await SetQuestFlags(peer, [BeaconRefueled]);
                }
                await ShowBeacon(peer, run, BeaconViews.GetValue(peer, _ => new()));
            }
        }
        finally { run.Gate.Release(); }
    }

    static async Task CreateBeacon(Player player, BeaconRun run, BeaconView view)
    {
        var placed = new QuestObjectTypes.AreaPlacements(Act6Areas.Beacon, player.Seed, []);
        // Rebind the old quest rules and add missing objects when this module is hot-reloaded in an occupied area.
        foreach (var rule in QuestsOf(player).Objects(Act6Areas.Beacon))
        {
            bool found = false;
            lock (player.QuestObjects)
                foreach (var (id, o) in player.QuestObjects.Where(p => p.Value.Rule.ObjectType == rule.ObjectType).ToArray())
                { player.QuestObjects[id] = (rule, o.At, o.Info); found = true; }
            if (!found)
                foreach (var at in placed.Of(rule.ObjectType)) await CreateQuestObject(player, rule, at);
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
                lock (player.QuestObjects)
                {
                    var payloads = player.QuestObjects.Where(p => p.Value.Rule.ObjectType == Act6Objects.BeaconPayload).OrderBy(p => p.Key).ToArray();
                    if (i < payloads.Length) old = (payloads[i].Value.At.X, payloads[i].Value.At.Y);
                }
                if (player.Instance.Transitioned(start.Type, start.X, start.Y) || ClientHasQuestFlag(player, BeaconRefueled) || ClientHasQuestFlag(player, BeaconDelivered))
                    walk.Advance(double.MaxValue, true);
                else if (old is { } at) walk.Restore(at);
                run.Carts.Add(new(start, walk));
            }
            run.Controllers = placed.Of("Metadata/QuestObjects/Act6/BeaconPayload_SpawnController");
            foreach (string suffix in new[] { "GhostPirateSpawner", "GhostPirateSpawnerMagic", "GhostPirateSpawnerRare", "GhostTotemSpawner" })
                run.Spawners.AddRange(placed.Of("Metadata/QuestObjects/Act6/BeaconPayload_" + suffix));
            run.LastAt = AreaTime(player);
            run.Loaded = true;
            Console.WriteLine($"[gateway] Beacon: {run.Carts.Count}/2 fuel carts, {run.Controllers.Count} encounter triggers");
        }
        foreach (var cart in run.Carts)
        {
            uint id;
            lock (player.QuestObjects)
                id = player.QuestObjects.Where(p => p.Value.Rule.ObjectType == Act6Objects.BeaconPayload).OrderBy(p => p.Key).Skip(view.Carts.Count).Select(p => p.Key).FirstOrDefault();
            if (id == 0)
            {
                var at = cart.Walk.Position;
                var info = QuestObjectTypes.Find(Act6Objects.BeaconPayload) ?? throw new InvalidOperationException("BeaconPayload metadata missing");
                var position = cart.Start with { X = (int)Math.Round(at.X), Y = (int)Math.Round(at.Y) };
                lock (player.QuestObjects)
                {
                    id = player.TakeObjectId();
                    player.QuestObjects[id] = (new Quests.ObjectRule(Act6Areas.Beacon, Act6Objects.BeaconPayload, BeaconCartFlag(cart.Start)), position, info);
                }
                await global::Game.SendWorldCreate(player, id, () => InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(Act6Objects.BeaconPayload),
                    new QuestObject(position.X, position.Y, info.Components, position.Rotation, position.Z,
                        TransitionState: (byte)(cart.Walk.Complete ? 2 : 1), Team: 4).Blob()),
                            $"[Beacon] fuel cart at ({position.X}, {position.Y})", wake: true, awaitBirth: false);
            }
            lock (player.QuestObjects)
                if (player.QuestObjects.TryGetValue(id, out var shown))
                    player.QuestObjects[id] = (shown.Rule with { TransitionedBy = BeaconCartFlag(cart.Start) }, shown.At, shown.Info);
            view.Carts.Add(id);
        }
        if (ClientHasQuestFlag(player, BeaconDelivered)) player.Instance.SetState(Act6Objects.BeaconIgnited);
        if (!run.Players.Any(w => w.TryGetTarget(out var peer) && !peer.Closed && !peer.LeavingArea))
        {
            run.LastAt = AreaTime(player);
            foreach (var cart in run.Carts) cart.Update(0, []);
        }
        run.Players.RemoveAll(w => !w.TryGetTarget(out var peer) || peer.Closed || peer.LeavingArea);
        run.Players.Add(new(player));
        view.Loaded = true;
    }

    static async Task ShowBeacon(Player player, BeaconRun run, BeaconView view)
    {
        int delivered = run.Carts.Count(c => c.Walk.Complete);
        bool flag = ClientHasQuestFlag(player, BeaconDelivered), lit = flag || player.Instance.HasState(Act6Objects.BeaconIgnited);
        for (int i = 0; i < view.Carts.Count; i++)
        {
            var cart = run.Carts[i]; uint id = view.Carts[i];
            var at = cart.Walk.Position;
            var cell = ((int)Math.Round(at.X), (int)Math.Round(at.Y));
            lock (player.QuestObjects)
                if (player.QuestObjects.TryGetValue(id, out var o)) player.QuestObjects[id] = (o.Rule, o.At with { X = cell.Item1, Y = cell.Item2 }, o.Info);
            var movement = (cart.CommandTarget, cart.Moving);
            if (!view.ShownMoves.TryGetValue(id, out var shown) || shown != movement)
            {
                view.ShownMoves[id] = movement;
                var target = cart.CommandTarget;
                float seconds = cart.Moving ? (float)Math.Max(0.05, cart.CommandSeconds) : 0.05f;
                var move = InstanceProtocol.MoveTo(id, target.Item1, target.Item2, seconds, InstanceProtocol.FollowTerrain);
                await Send(player, cart.Moving
                    ? [.. InstanceProtocol.Orientation(id, (float)Math.Atan2(target.Item1 - at.X, at.Y - target.Item2)), .. move] : move,
                    $"[Beacon] cart {i + 1} {(cart.Moving ? "moving" : "stopped")} at {cell}, target {target}");
            }
            await BeaconState(player, view, id, (byte)(cart.Walk.Complete ? 2 : 1));
        }
        (uint Id, string Type)[] objects;
        lock (player.QuestObjects) objects = [.. player.QuestObjects.Select(p => (p.Key, p.Value.Rule.ObjectType))];
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
            if (state > 0) await BeaconState(player, view, id, state);
        }
    }

    static async Task BeaconState(Player player, BeaconView view, uint id, byte state)
    {
        if (view.ShownStates.TryGetValue(id, out var previous) && previous == state) return;
        int component;
        lock (player.QuestObjects) component = player.QuestObjects.TryGetValue(id, out var o) ? QuestObject.TransitionableIndex(o.Info.Components) : -1;
        if (component < 0) return;
        await global::Game.SendWorldRecord(player, id, InstanceEntities.TransitionableUpdate(id, (byte)component, state), $"[Beacon] 0x{id:x} state {state}");
        view.ShownStates[id] = state;
    }

    static async Task SpawnBeaconGuards(Player player, BeaconRun run, (double X, double Y) at)
    {
        if (Environment.GetEnvironmentVariable("Game_MONSTERS") == "0") return;
        foreach (var trigger in run.Controllers.Where(p => Math.Pow(p.X - at.X, 2) + Math.Pow(p.Y - at.Y, 2) <= 14 * 14))
        {
            if (!player.Instance.Transition(trigger.Type, trigger.X, trigger.Y)) continue;
            foreach (var spawn in run.Spawners.Where(p => ReferenceEquals(run.Controllers.MinBy(t => Math.Pow(t.X - p.X, 2) + Math.Pow(t.Y - p.Y, 2)), trigger)))
            {
                bool totem = spawn.Type.EndsWith("GhostTotemSpawner", StringComparison.Ordinal);
                string monster = totem ? "Metadata/Monsters/Totems/GhostPirateTotemTreasureHunter" : "Metadata/Monsters/GhostPirates/GhostPirateBeacon";
                if (Monsters.Find(monster) is not { Supported: true } variety) continue;
                var rarity = spawn.Type.EndsWith("Rare", StringComparison.Ordinal) ? MonsterRarity.Rare
                    : spawn.Type.EndsWith("Magic", StringComparison.Ordinal) ? MonsterRarity.Magic : MonsterRarity.Normal;
                var pos = player.Layout.Near((spawn.X, spawn.Y));
                await SpawnMonster(player, variety, 49, rarity, pos.X, pos.Y);
            }
            Console.WriteLine($"[gateway] Beacon encounter at ({trigger.X}, {trigger.Y})");
        }
    }
}
