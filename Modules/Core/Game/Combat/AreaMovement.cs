using System.Diagnostics;
using System.Runtime.CompilerServices;
using GameServer.Core.Diagnostics;
using GameServer.Core.World;

/// <summary>
/// The area's own movement: every walker of an instance (each player's walk, every monster's, a lockstep player's kept
/// corpses) stepped once per frame by the area, not by one connection's view. It is the lockstep tick (StepFrames) for the
/// whole area: frame by frame, each walker taking its steps in its turn, checked against the bodies as the turns before it
/// left them. The turn order is the players' first, in the order they joined (each client ticks its own player first),
/// then every other walker by id. A solo area ticks exactly as its client; with several players each client agrees on its
/// own player and the monsters and differs only on the other players, which the position syncs correct.
/// </summary>
public static partial class Game
{
    sealed class AreaWalkState
    {
        public double SteppedTo = double.NaN;
    }
    static readonly ConditionalWeakTable<InstanceWorld, AreaWalkState> AreaWalks = new();

    /// <summary>Whether this connection's area steps its walkers itself: an instance whose frames run, with a lockstep
    /// player in it (an area of predictive clients only keeps their own time-ordered pass).</summary>
    public static bool AreaSteps(Player conn) =>
        conn.Instance is not null && conn.Frames.Running && conn.Frames.Players() is { Length: > 0 } players
        && players.Any(p => p.LockstepClient);

    /// <summary>Steps the area's walkers up to <paramref name="now"/> (whole frames: up to the start of the frame being
    /// sent). Stepping again to the same time does nothing, so whoever needs the walkers current calls it. Under the
    /// instance's Monsters lock.</summary>
    /// <summary>Where each walker of an area stood after the last movement step, by cell: a walker in another cell (or one
    /// that came or went) is what the nearby presentations follow. Under the Monsters lock.</summary>
    public sealed class WalkerCells
    {
        Dictionary<EntityId, (int X, int Y)> last = [], next = [];

        /// <summary>Where each living walker stood after the last movement step.</summary>
        public IReadOnlyDictionary<EntityId, (int X, int Y)> Positions => last;

        public bool Moved(Player[] players, double now)
        {
            bool moved = false;
            void See(uint id, (int X, int Y) at)
            {
                next[new(id)] = at;
                if (!last.TryGetValue(new(id), out var was) || was != at) moved = true;
            }
            foreach (var (id, m) in players[0].Monsters)
                if (!m.Dead && m.Life > 0) See(id, m.Walk.Position(now));
            foreach (var p in players)
                if (p.PlayerSent) See(p.PeerId, p.Walk.Position(now));
            moved |= next.Count != last.Count;
            (last, next) = (next, last);
            next.Clear();
            return moved;
        }
    }

    public static void StepAreaWalkers(Player conn, double now)
    {
        var world = conn.Instance.World;
        var players = conn.Frames.Players();
        if (players.Length == 0) return;
        var state = AreaWalks.GetValue(world, _ => new());
        if (conn.Frames.Running && !double.IsNaN(conn.Frames.Time)) now = Math.Min(now, conn.Frames.Time);
        if (double.IsNaN(state.SteppedTo))
        {
            state.SteppedTo = Math.Max(now, players.Max(p => p.WalkersSteppedTo));
            return;
        }
        double from = state.SteppedTo;
        if (now <= from) return;
        state.SteppedTo = now;
        foreach (var p in players) p.WalkersSteppedTo = now;
        var first = players[0];
        var walks = new Dictionary<uint, PlayerWalk>();
        foreach (var (id, m) in first.Monsters)
        {
            if (!ClientEntityGrid.Lists(first, id)) ClientEntityGrid.Stepped(first, id, m.Walk.Position(from));
            if (LivingCollision.StepsWithBodies(m) || players.Any(p => p.CorpseWalks(id, m))) walks[id] = m.Walk;
        }
        foreach (var p in players)
            if (p.PlayerSent && p.PlayerLife > 0) walks[p.PeerId] = p.Walk;
        StepAreaFrames(first, players, walks, from, now, Program.FrameMs / 1000d);
    }

    static void StepAreaFrames(Player first, Player[] players, Dictionary<uint, PlayerWalk> walks,
        double from, double to, double length)
    {
        var chases = new Dictionary<uint, (SpawnedMonster M, Player Target)>();
        foreach (var (id, m) in first.Monsters)
            if (m.Target is { } target && players.Contains(target) && WalkUpOpen(target, m)) chases[id] = (m, target);
        var attacks = new Dictionary<uint, (Player Player, LockstepAttack Attack)>();
        foreach (var p in players)
            if (PlayerWalkUpOpen(p) is { } attack) attacks[p.PeerId] = (p, attack);
        if (chases.Count == 0 && attacks.Count == 0 && walks.Values.All(w => w.ArrivesAt <= from && w.Reached)) return;
        // The turn order: the players as they joined, then every other walker by id.
        var playerIds = players.Select(p => p.PeerId).ToArray();
        var order = playerIds.Where(id => walks.ContainsKey(id) || attacks.ContainsKey(id))
            .Concat(walks.Keys.Union(chases.Keys).Except(playerIds).Order()).ToArray();
        var turn = new Dictionary<uint, int>();
        for (int i = 0; i < order.Length; i++) turn[order[i]] = i;
        var bodies = new AreaBodies(first, players, from);
        var world = first.Instance.World;
        world.AreaBodies = bodies.For;
        try
        {
            int frames = Math.Clamp((int)Math.Ceiling((to - from) / length - 1e-9), 1, 100);
            for (int i = frames; i >= 1; i--)
            {
                double end = to - (i - 1) * length, start = i == frames ? from : end - length;
                foreach (uint id in order)
                {
                    if (attacks.TryGetValue(id, out var player) && PlayerWalkUpOpen(player.Player) == player.Attack)
                    {
                        var attack = player.Attack;
                        bool targetFirst = attack.TargetId is var t && turn.TryGetValue(t, out int at) && at < turn[id];
                        var (result, timer) = StepAreaWalkUp(first, bodies, id, player.Player.Walk, true, attack.Target?.Walk,
                            targetFirst, attack.Reach, attack.Tolerance, attack.TimerFrom, attack.GroundTarget, start, end, length);
                        attack.TimerFrom = timer;
                        if (result == WalkUpResult.Swings) LockstepAttackSwings(player.Player, attack, start, end);
                        else if (result == WalkUpResult.Fails) attack.Fails();
                        continue;
                    }
                    if (chases.TryGetValue(id, out var chase) && WalkUpOpen(chase.Target, chase.M))
                    {
                        var chaser = chase.M;
                        bool targetFirst = turn.TryGetValue(chase.Target.PeerId, out int at) && at < turn[id];
                        var (result, timer) = StepAreaWalkUp(first, bodies, id, chaser.Walk, walks.ContainsKey(id),
                            walks.GetValueOrDefault(chase.Target.PeerId) ?? chase.Target.Walk, targetFirst,
                            (int)(chaser.Attack!.Range - 1), LivingCollision.PlayerSize, chaser.ClientChaseTimerFrom, default, start, end, length);
                        chaser.ClientChaseTimerFrom = timer;
                        if (result != WalkUpResult.Walking) WalkUpEnds(chaser, start, result == WalkUpResult.Swings);
                        continue;
                    }
                    if (!walks.TryGetValue(id, out var walk)) continue;
                    if (first.Monsters.GetValueOrDefault(id) is { ClientActionWalkStop: { } stopAt } acting && start >= stopAt - 1e-6)
                    {
                        acting.ClientActionWalkStop = null;
                        if (walk.Moving(start)) walk.StopAfter(end, start);
                    }
                    for (int round = 0; round < 8; round++)
                    {
                        bool stopped = false;
                        foreach (var (stepAt, cell) in walk.Steps(start, end))
                            if (!AreaStep(first, bodies, id, walk, stepAt, cell)) { stopped = true; break; }
                        if (stopped || walk.Reached || walk.ArrivesAt > end) break;
                        walk.Repath(walk.ArrivesAt);
                    }
                    // A corpse's death clip clears its body in its own turn, once it has on every lockstep client.
                    if (first.Monsters.ContainsKey(id) && players.Any(p => p.CorpseClears(id, start))
                        && players.All(p => !p.LockstepClient || !p.CorpseWalks(id, first.Monsters[id]) || p.CorpseClears(id, start)))
                        bodies.Remove(id);
                }
                // Each lockstep client's copies of the other players (PeerWalks): the orders its owner was sent, walked
                // from the copy's cell on that client; checked against the area's bodies but the player's own.
                foreach (var p in players)
                    foreach (var (peer, copy) in p.PeerWalks)
                        for (int round = 0; round < 8; round++)
                        {
                            bool stopped = false;
                            foreach (var (stepAt, cell) in copy.Steps(start, end))
                            {
                                bool floor = copy.Slides || copy.Floor(cell.X, cell.Y);
                                if (!floor || !copy.Slides && ClientPathfinder.ObjectBlocks(bodies.For(peer), cell.X, cell.Y, copy.Size + 1, copy.Size + 4))
                                {
                                    copy.StopAt(stepAt);
                                    stopped = true;
                                    break;
                                }
                            }
                            if (stopped || copy.Reached || copy.ArrivesAt > end) break;
                            copy.Repath(copy.ArrivesAt);
                        }
                if (ServerTrace.Sync) TraceAreaFrame(first, order, walks, end, to, length);
            }
        }
        finally { world.AreaBodies = null; }
    }

    static void TraceAreaFrame(Player first, uint[] order, Dictionary<uint, PlayerWalk> walks, double end, double to, double length)
    {
        var line = new System.Text.StringBuilder();
        line.Append($"[frame] qpc={Stopwatch.GetTimestamp()} n={first.ServerFrame - 1 - (long)Math.Round((to - end) / length)} end={end:0.000} now={to:0.000} m=");
        foreach (uint id in order)
        {
            if (!walks.TryGetValue(id, out var traced)) continue;
            var at = traced.Position(end);
            var (into, step) = traced.StepPhase(end);
            // The trace names a player as its own client does (0x1000), so the sync scripts read the solo case as before.
            line.Append($"{(id == first.PeerId ? PlayerId : id):x}:{at.X},{at.Y}/{into:0.0000}/{step:0.0000};");
        }
        Console.WriteLine(line);
    }

    // StepWalkUp for the area: the target's cell is the one its turn this frame left (it ticked first) or the frame before.
    static (WalkUpResult Result, double TimerFrom) StepAreaWalkUp(Player first, AreaBodies bodies, uint id, PlayerWalk walk,
        bool blocks, PlayerWalk? targetWalk, bool targetFirst, int reach, int tolerance, double timerFrom, (int X, int Y) ground,
        double start, double end, double length)
    {
        var target = targetWalk?.Position(targetFirst ? end : start) ?? ground;
        bool InGoal((int X, int Y) cell) => walk.InReachOf(cell, target, reach, tolerance);
        for (int round = 0; round < 8; round++)
        {
            foreach (var (at, cell) in walk.Steps(start, end))
            {
                if (blocks && !AreaStep(first, bodies, id, walk, at, cell))
                    return (InGoal(walk.Position(end)) ? WalkUpResult.Swings : WalkUpResult.Fails, timerFrom);
                if (!blocks) ClientEntityGrid.Stepped(first, id, cell);
                if (walk.EndsPiece(at) && InGoal(cell))
                {
                    walk.StopAfter(at);
                    return (WalkUpResult.Swings, timerFrom);
                }
            }
            bool walking = walk.ArrivesAt > end;
            if (InGoal(walk.Position(end)))
            {
                walk.StopAfter(walking ? end + length : walk.ArrivesAt, end);
                return (WalkUpResult.Swings, timerFrom);
            }
            if (walking)
            {
                var searched = walk.SearchedFor;
                long dx = searched.X - target.X, dy = searched.Y - target.Y;
                double timer = end - timerFrom;
                if (searched == target || timer < 2.0 && (timer < 0.7 || dx * dx + dy * dy < 4096))
                    return (WalkUpResult.Walking, timerFrom);
                var before = walk.Save();
                walk.Approach(target.X, target.Y, reach, end, tolerance, walk.Precision);
                if (walk.ArrivesAt > end) return (WalkUpResult.Walking, end);
                bool met = walk.ReachedGoal;
                walk.Restore(before);
                walk.StopAfter(end + length, end);
                return (met ? WalkUpResult.Swings : WalkUpResult.Fails, end);
            }
            walk.Approach(target.X, target.Y, reach, Math.Max(walk.ArrivesAt, start), tolerance, walk.Precision);
            timerFrom = start;
            if (walk.Reached && !walk.ReachedGoal) return (WalkUpResult.Fails, timerFrom);
        }
        return (WalkUpResult.Walking, timerFrom);
    }

    // One step, as the client checks it (Step), against the area's bodies.
    static bool AreaStep(Player first, AreaBodies bodies, uint id, PlayerWalk walk, double at, (int X, int Y) cell)
    {
        bool floor = walk.Slides || walk.Floor(cell.X, cell.Y);
        bool body = floor && ClientPathfinder.ObjectBlocks(bodies.For(id), cell.X, cell.Y, walk.Size + 1, walk.Size + 4);
        if (!floor || body && !walk.WalksThrough && !walk.ChargesThrough && !walk.Slides)
        {
            walk.StopAt(at);
            return false;
        }
        if (!body) walk.WalksThrough = false;
        bodies.Move(id, cell.X, cell.Y);
        ClientEntityGrid.Stepped(first, id, cell);
        return true;
    }

    /// <summary>The bodies of an area's walkers in one pass: the area's fixed blockers, every living player that is not
    /// phasing, and every monster with a body (a kept corpse while it blocks on a lockstep client). A walker is checked
    /// against all but itself; a monster that phases, or a phasing player, only against the fixed ones.</summary>
    sealed class AreaBodies
    {
        readonly BlockerSet current, fixedSet;
        readonly Dictionary<uint, int> index = [];
        readonly Dictionary<uint, IReadOnlyList<ClientPathfinder.Blocker>> views = [];
        readonly HashSet<uint> passThrough = [];

        public AreaBodies(Player first, Player[] players, double now)
        {
            fixedSet = new BlockerSet(first.StationaryBlockers);
            current = new BlockerSet(first.StationaryBlockers);
            foreach (var p in players)
            {
                if (!p.PlayerSent || p.PlayerLife == 0) continue;
                if (p.IsPhasing) { passThrough.Add(p.PeerId); continue; }
                var at = p.Walk.Position(now);
                index[p.PeerId] = current.Add(new(at.X, at.Y, LivingCollision.PlayerSize));
            }
            foreach (var (id, m) in first.CollisionMonstersView)
            {
                if (LivingCollision.StepsWithBodies(m) && LivingCollision.Phases(m)) passThrough.Add(id);
                ClientPathfinder.Blocker? body = null;
                foreach (var p in players)
                    if (p.MonsterBody(id, m, now) is { } b) { body = b; break; }
                if (body is { } found) index[id] = current.Add(found);
            }
        }

        public IReadOnlyList<ClientPathfinder.Blocker> For(uint self)
        {
            if (passThrough.Contains(self)) return fixedSet;
            if (!index.TryGetValue(self, out int i)) return current;
            if (!views.TryGetValue(self, out var view)) views[self] = view = current.Without(i);
            return view;
        }

        public void Move(uint id, int x, int y)
        {
            if (!index.TryGetValue(id, out int i)) return;
            current.Replace(i, new(x, y, current[i].Size));
        }

        public void Remove(uint id)
        {
            if (index.Remove(id, out int i)) current.Remove(i);
        }
    }
}
