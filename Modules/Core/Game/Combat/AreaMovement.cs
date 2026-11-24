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
        public readonly Dictionary<uint, PlayerWalk> Walks = [];
        public readonly Dictionary<uint, (SpawnedMonster M, Player Target)> Chases = [];
        public readonly Dictionary<uint, (Player Player, LockstepAttack Attack)> Attacks = [];
        public readonly List<uint> Order = [];
        public readonly Dictionary<uint, int> Turn = [];
        public readonly AreaBodies Bodies = new();
    }
    static readonly ConditionalWeakTable<InstanceWorld, AreaWalkState> AreaWalks = new();

    /// <summary>Whether this connection's area steps its walkers itself: an instance whose frames run, with a lockstep
    /// player in it (an area of predictive clients only keeps their own time-ordered pass).</summary>
    public static bool AreaSteps(Player player)
    {
        if (player.Instance is null || !player.Frames.Running) return false;
        var players = player.Frames.Players();
        for (int i = 0; i < players.Length; i++)
            if (players[i].Connection.LockstepClient) return true;
        return false;
    }

    /// <summary>Puts every living walker where it stands now in the area's grid (with its body), and drops the dead and
    /// gone: true when one changed cell, came or went. In the area's frame.</summary>
    public static bool PlaceWalkers(Player[] players, double now, AreaGrid grid)
    {
        bool moved = false;
        var living = players[0].Area.SyncLivingMonsters();
        for (int i = 0; i < living.Count; i++)
        {
            var (id, m) = living[i];
            moved |= grid.Set(new(id), m.Walk.Position(now), m.StopRange);
        }
        for (int i = 0; i < players.Length; i++)
        {
            var p = players[i];
            if (p.Connection.PlayerSent) moved |= grid.Set(new(p.Id), p.Walk.Position(now));
        }
        return grid.EndPass() | moved;
    }

    public static void StepAreaWalkers(Player player, double now)
    {
        var world = player.Instance.World;
        var players = player.Frames.Players();
        if (players.Length == 0) return;
        var state = AreaWalks.GetValue(world, _ => new());
        if (player.Frames.Running && !double.IsNaN(player.Frames.Time)) now = Math.Min(now, player.Frames.Time);
        if (double.IsNaN(state.SteppedTo))
        {
            double maxStepped = now;
            for (int i = 0; i < players.Length; i++)
                if (players[i].WalkersSteppedTo > maxStepped) maxStepped = players[i].WalkersSteppedTo;
            state.SteppedTo = maxStepped;
            return;
        }
        double from = state.SteppedTo;
        if (now <= from) return;
        state.SteppedTo = now;
        bool anyCorpses = false;
        for (int i = 0; i < players.Length; i++)
        {
            players[i].WalkersSteppedTo = now;
            if (players[i].HasCorpses) anyCorpses = true;
        }
        var first = players[0];
        var living = first.Area.SyncLivingMonsters();
        var walks = state.Walks;
        walks.Clear();
        for (int i = 0; i < living.Count; i++)
        {
            var (id, m) = living[i];
            if (!ClientEntityGrid.Lists(first, id)) ClientEntityGrid.Stepped(first, id, m.Walk.Position(from));
            if (LivingCollision.StepsWithBodies(m)) walks[id] = m.Walk;
        }
        if (anyCorpses)
        {
            for (int pi = 0; pi < players.Length; pi++)
            {
                var p = players[pi];
                if (!p.HasCorpses) continue;
                foreach (uint id in p.KeptCorpseIds)
                {
                    if (walks.ContainsKey(id)) continue;
                    if (!first.Area.Monsters.TryGetValue(id, out var m)) continue;
                    if (!ClientEntityGrid.Lists(first, id)) ClientEntityGrid.Stepped(first, id, m.Walk.Position(from));
                    if (p.CorpseWalks(id, m)) walks[id] = m.Walk;
                }
            }
        }
        for (int i = 0; i < players.Length; i++)
        {
            var p = players[i];
            if (p.Connection.PlayerSent && p.PlayerLife > 0) walks[p.Id] = p.Walk;
        }
        StepAreaFrames(first, players, state, living, anyCorpses, from, now, Program.FrameMs / 1000d);
    }

    static bool ContainsPlayer(Player[] players, Player target)
    {
        for (int i = 0; i < players.Length; i++)
            if (ReferenceEquals(players[i], target)) return true;
        return false;
    }

    static void StepAreaFrames(Player first, Player[] players, AreaWalkState state,
        List<(uint Id, SpawnedMonster Monster)> living, bool anyCorpses,
        double from, double to, double length)
    {
        var walks = state.Walks;
        var chases = state.Chases;
        chases.Clear();
        for (int i = 0; i < living.Count; i++)
        {
            var (id, m) = living[i];
            if (m.Target is { } target && ContainsPlayer(players, target) && WalkUpOpen(target, m))
                chases[id] = (m, target);
        }
        var attacks = state.Attacks;
        attacks.Clear();
        for (int i = 0; i < players.Length; i++)
        {
            var p = players[i];
            if (PlayerWalkUpOpen(p) is { } attack) attacks[p.Id] = (p, attack);
        }
        if (chases.Count == 0 && attacks.Count == 0)
        {
            bool anyMoving = false;
            foreach (var w in walks.Values)
                if (w.ArrivesAt > from || !w.Reached) { anyMoving = true; break; }
            if (!anyMoving) return;
        }
        // The turn order: ascending object id, matching the client's object tree (0x14218a650: monsters 0x08000000+ tick
        // before players 0x20000000+). Reversing that order flips every player-vs-monster collision and chase goal test.
        var order = state.Order;
        order.Clear();
        foreach (uint id in walks.Keys) order.Add(id);
        foreach (uint id in chases.Keys) if (!walks.ContainsKey(id)) order.Add(id);
        foreach (uint id in attacks.Keys) if (!walks.ContainsKey(id) && !chases.ContainsKey(id)) order.Add(id);
        order.Sort();
        var turn = state.Turn;
        turn.Clear();
        for (int i = 0; i < order.Count; i++) turn[order[i]] = i;
        var bodies = state.Bodies;
        bodies.Reset(first, players, living, anyCorpses, from);
        var world = first.Instance.World;
        world.AreaBodies = bodies.For;
        try
        {
            int frames = Math.Clamp((int)Math.Ceiling((to - from) / length - 1e-9), 1, 100);
            for (int i = frames; i >= 1; i--)
            {
                double end = to - (i - 1) * length, start = i == frames ? from : end - length;
                for (int oi = 0; oi < order.Count; oi++)
                {
                    uint id = order[oi];
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
                        bool targetFirst = turn.TryGetValue(chase.Target.Id, out int at) && at < turn[id];
                        var (result, timer) = StepAreaWalkUp(first, bodies, id, chaser.Walk, walks.ContainsKey(id),
                            walks.GetValueOrDefault(chase.Target.Id) ?? chase.Target.Walk, targetFirst,
                            (int)(chaser.Attack!.Range - 1), LivingCollision.PlayerSize, chaser.ClientChaseTimerFrom, default, start, end, length);
                        chaser.ClientChaseTimerFrom = timer;
                        if (result != WalkUpResult.Walking) WalkUpEnds(chaser, start, result == WalkUpResult.Swings);
                        continue;
                    }
                    if (!walks.TryGetValue(id, out var walk)) continue;
                    SpawnedMonster? monster = null;
                    if (first.Area.Monsters.TryGetValue(id, out monster)
                        && monster.ClientActionWalkStop is { } stopAt && start >= stopAt - 1e-6)
                    {
                        monster.ClientActionWalkStop = null;
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
                    if (anyCorpses && monster is not null && (monster.Dead || monster.Life == 0))
                    {
                        bool someClears = false, allClear = true;
                        for (int pi = 0; pi < players.Length; pi++)
                        {
                            var p = players[pi];
                            bool clears = p.CorpseClears(id, start);
                            if (clears) someClears = true;
                            if (p.Connection.LockstepClient && p.CorpseWalks(id, monster) && !clears) allClear = false;
                        }
                        if (someClears && allClear) bodies.Remove(id);
                    }
                }
                if (ServerTrace.Sync) TraceAreaFrame(first, order, walks, end, to, length);
            }
        }
        finally { world.AreaBodies = null; }
    }

    static void TraceAreaFrame(Player first, List<uint> order, Dictionary<uint, PlayerWalk> walks, double end, double to, double length)
    {
        var line = new System.Text.StringBuilder();
        line.Append($"[frame] qpc={Stopwatch.GetTimestamp()} n={first.Connection.ServerFrame - 1 - (long)Math.Round((to - end) / length)} end={end:0.000} now={to:0.000} m=");
        foreach (uint id in order)
        {
            if (!walks.TryGetValue(id, out var traced)) continue;
            var at = traced.Position(end);
            var (into, step) = traced.StepPhase(end);
            // The trace names a player as its own client does (0x1000), so the sync scripts read the solo case as before.
            line.Append($"{(id == first.Id ? first.Id : id):x}:{at.X},{at.Y}/{into:0.0000}/{step:0.0000};");
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
        if (!floor)
        {
            walk.StopAt(at);
            return false;
        }
        if (walk.ChargesThrough && !walk.Slides)
        {
            if (ShieldChargeStepBlocked(first, id, walk, at, cell, bodies.Fixed))
            {
                walk.StopAt(at);
                return false;
            }
            if (!ClientPathfinder.ObjectBlocks(bodies.Fixed, cell.X, cell.Y, walk.Size + 1, walk.Size + 4))
                walk.WalksThrough = false;
            bodies.Move(id, cell.X, cell.Y);
            ClientEntityGrid.Stepped(first, id, cell);
            return true;
        }
        bool body = ClientPathfinder.ObjectBlocks(bodies.For(id), cell.X, cell.Y, walk.Size + 1, walk.Size + 4);
        if (body && !walk.WalksThrough && !walk.Slides)
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
        readonly BlockerSet current = new(), fixedSet = new();
        public IReadOnlyList<ClientPathfinder.Blocker> Fixed => fixedSet;
        readonly Dictionary<uint, int> index = [];
        readonly Dictionary<uint, IReadOnlyList<ClientPathfinder.Blocker>> views = [];
        readonly HashSet<uint> passThrough = [];
        IReadOnlyList<ClientPathfinder.Blocker>? lastStationary;
        int lastStationaryCount = -1;

        public void Reset(Player first, Player[] players, List<(uint Id, SpawnedMonster Monster)> living, bool anyCorpses, double now)
        {
            var stationary = first.StationaryBlockers;
            if (!ReferenceEquals(stationary, lastStationary) || stationary.Count != lastStationaryCount)
            {
                fixedSet.Reset(stationary);
                lastStationary = stationary;
                lastStationaryCount = stationary.Count;
            }
            current.Reset(stationary);
            index.Clear();
            views.Clear();
            passThrough.Clear();
            for (int i = 0; i < players.Length; i++)
            {
                var p = players[i];
                if (!p.Connection.PlayerSent || p.PlayerLife == 0) continue;
                if (p.IsPhasing) { passThrough.Add(p.Id); continue; }
                var at = p.Walk.Position(now);
                index[p.Id] = current.Add(new(at.X, at.Y, LivingCollision.PlayerSize));
            }
            for (int i = 0; i < living.Count; i++)
            {
                var (id, m) = living[i];
                if (LivingCollision.StepsWithBodies(m) && LivingCollision.Phases(m)) passThrough.Add(id);
                if (LivingCollision.Blocks(m))
                {
                    var p = m.Walk.Position(now);
                    index[id] = current.Add(new(p.X, p.Y, LivingCollision.Size(m)));
                }
            }
            if (anyCorpses)
            {
                for (int pi = 0; pi < players.Length; pi++)
                {
                    var p = players[pi];
                    if (!p.HasCorpses) continue;
                    foreach (uint id in p.KeptCorpseIds)
                    {
                        if (index.ContainsKey(id)) continue;
                        if (!first.Area.Monsters.TryGetValue(id, out var m)) continue;
                        if (p.MonsterBody(id, m, now) is { } found) index[id] = current.Add(found);
                    }
                }
            }
            if (first.Area.DormantMonsters.Count > 0)
            {
                foreach (var (id, m) in first.Area.DormantMonsters)
                {
                    if (LivingCollision.StepsWithBodies(m) && LivingCollision.Phases(m)) passThrough.Add(id);
                    for (int pi = 0; pi < players.Length; pi++)
                        if (players[pi].MonsterBody(id, m, now) is { } found) { index[id] = current.Add(found); break; }
                }
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
