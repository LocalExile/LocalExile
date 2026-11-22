using System.Diagnostics;
using System.Net.Sockets;
using GameServer.Core.Diagnostics;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;

public static partial class Game
{
    // The player's collisions, every frame of the area while the player's updates run and no transfer is pending. A pause
    // skips them.
    static void StartLivingCollisions(Player player, PlayerUpdatesState updates)
    {
        EveryFrameAsPlayer(player, () =>
        {
            if (PlayerUpdatesCurrent(player, updates) && player.PendingTransfer is null) return true;
            if (player.Closed || player.LeavingArea) player.Instance.RemovePlayer(player.Id);
            return false;
        }, async player =>
        {
            if (!player.Paused)
            {
                if (IsMonsterRunner(player)) await CorrectMonsterCollisions(player);
                if (!PlayerUpdatesCurrent(player, updates)) return;
                await PushOutOfCollision(player, living: true);
                if (!PlayerUpdatesCurrent(player, updates)) return;
                await ShowFreeze(player);
                if (!PlayerUpdatesCurrent(player, updates)) return;
                await ReleaseHeldMove(player);
                if (!PlayerUpdatesCurrent(player, updates)) return;
            }
            if (ServerTrace.Sync) TraceSync(player);
            await ApplyKillShares(player);
            // The area's runner watches the monsters waiting to rise and reveals the held-back ones near a player.
            if (IsMonsterRunner(player))
            {
                if (MonsterEmerge.Unwatched(player)) WatchBuried(player);
                await RevealHeldBack(player);
            }

        });
    }

    // The client does not stop for is_frozen (live 2026-11-01), and virtual_action_speed_+% at -100, which would stop it,
    // divides its action clips by zero. A hit's recovery for as long as the freeze lasts stops it where it stands. A
    // predictive client goes on with its walk after, as the server's walk does; a lockstep one stands until its next order
    // (PlayerHitRecoveryRecordLocked: the same message and handler; the freeze itself is not traced).
    public static async Task ShowFreeze(Player player)
    {
        if (!CurrentGameWork || player.FreezeShown == player.FreezeGeneration) return;
        await Send(player, [], "0x01d1 player frozen", atGate: () =>
        {
            if (!CurrentGameWork || player.Closed || player.FreezeShown == player.FreezeGeneration) return null;
            double now = AreaTime(player), left = player.ElementalAilments.FreezeUntil(now) - now;
            if (left <= 0 || player.PlayerLife == 0 || player.LeavingArea || player.Paused) return null;
            player.FreezeShown = player.FreezeGeneration;
            if (player.Connection.LockstepClient) player.Walk.Cancel(now);
            return [.. InstanceProtocol.ActorAnimation(player.Id, (float)left, PlayerHitRecovery.TakeHitAnimation),
                .. ResyncPacketLocked(player, now)];
        });
    }

    // A click that came during a hit's recovery or a freeze: the walk starts when that ends, as the client's does.
    public static async Task ReleaseHeldMove(Player player)
    {
        if (!CurrentGameWork || player.HeldMove is null) return;
        // Accept the order and its cipher output together; otherwise leave the held click for replacement.
        await Send(player, [], "0x01ab held move", atGate: () =>
        {
            using (ProjectileMutation(player))
            {
                if (!CurrentGameWork || player.Closed || player.LeavingArea || player.PlayerLife == 0
                    || player.HeldMove is not { } use) return null;
                double now = ProjectileWorldTime(player);
                if (PlayerActionBlocked(player, now, now)) return null;
                player.HeldMove = null;
                Interlocked.Increment(ref player.ActionCount);
                var from = player.Walk.Order(use.X, use.Y, Math.Max(player.HitRecovery.Until, now - MonsterAiCadence.StepSeconds));
                player.ChannelSkill = 0;
                return InstanceProtocol.ActionStarted(player.Id, from.X, from.Y, use.X, use.Y, use.SkillId, use.Slot, use.Flags);
            }
        });
    }

    // Game_TRACE=sync: where the server has the player and the monsters now, on the QPC clock of
    // scripts/re/frida/trace-sync.js. scripts/re/sync_report.py compares the two.
    public static void TraceSync(Player player)
    {
        if (!player.Connection.PlayerSent) return;
        var line = new System.Text.StringBuilder();
        double now = AreaTime(player);
        var p = player.Walk.Position(now);
        line.Append($"[sync] qpc={System.Diagnostics.Stopwatch.GetTimestamp()} peer={player.Id:x} speed={player.Walk.Speed:0.###} ");
        line.Append($"moving={(player.Walk.Moving(now) ? 1 : 0)} p={p.X},{p.Y} m=");
        foreach (var (id, m) in player.Area.Monsters)
        {
            if (m.Dead || m.Life <= 0) continue;
            var at = m.Walk.Position(now);
            line.Append($"{id:x}:{at.X},{at.Y};");
        }
        Console.WriteLine(line);
    }

    public static void PublishPlayerPose(Player player)
    {
        if (!CurrentGameWork) return;
        if (player.Closed || player.LeavingArea) { player.Instance.RemovePlayer(player.Id); return; }
        if (player.PeerTemplate is null || !player.Connection.PlayerSent) return;
        RefreshPeerScale(player, CharacterPlayerStats(player)[ActorScale.Final]);
    }

    // Called under the owner's lock. Publish experience and final scale in an immutable template, retaining other creation stats,
    // equipment and skills. Observers read published snapshots and never lock another connection's simulation.
    public static void RefreshPeerScale(Player player, int scale)
    {
        if (player.PeerTemplate is not { } template) return;
        if (template.Experience != player.Character.Experience)
            player.PeerTemplate = template = template with { Experience = player.Character.Experience };
        if (Stat.Level is var levelKey)
        {
            int level = Progression.LevelFor(player.Character.Experience, CharacterStats.LevelStarts);
            if ((template.Stats ?? []).SingleOrDefault(s => s.Key == levelKey).Value != level)
                player.PeerTemplate = template = template with
                { Stats = [.. (template.Stats ?? []).Where(s => s.Key != levelKey), (levelKey, level)] };
        }
        if ((template.Stats ?? []).SingleOrDefault(s => s.Key == ActorScale.Final).Value != scale)
            player.PeerTemplate = template = template with
            { Stats = [.. (template.Stats ?? []).Where(s => s.Key != ActorScale.Final), (ActorScale.Final, scale)] };
        if (!player.Connection.PlayerSent || player.Closed || player.LeavingArea || player.Instance is null) return;
        player.Instance.AddPlayer(player);
        // The auras shared with allies (readers skip a dead or in-town giver); their zones follow the player.
        player.SharedAuras = [.. player.Auras.Values.Where(a => SharedPlayerAura(a.EffectId)).Select(a => new SharedAura(a,
                AuraRadius.Of(a, player.Pools.AuraAreaPct, SkillsFor(player), Stats),
                player.Pools.AuraEffectPct + a.SupportEffectPct)).Concat(SharedAvianAuras(player))];
        SyncSharedAuraZones(player, player.SharedAuras);
    }

    public static Task CorrectMonsterCollisions(Player player)
    {
        using (ProjectileMutation(player, phase: "collision"))
        {
            if (!CurrentGameWork) return Task.CompletedTask;
            StepWalkers(player, ProjectileWorldTime(player));
        }
        return Task.CompletedTask;
    }

    // The client steps every walker a cell at a time and checks each step (0x141e35a50): the cell's walk value must be at
    // least the walker's size + 1, and no other body may be within reach (0x141e4b140 → 0x141e4b3a0: the Chebyshev distance
    // less that body's size below the walker's size + 1). A blocked step ends the walk where it stands, with no new path
    // (0x141e82310); a chaser's next order paths it again. A walk whose path began on a blocked cell steps through bodies
    // until its first free step; terrain still stops it. A path cut short by the search is walked to its end and then paths
    // again from there at once (0x141e825b0 → 0x141e82010). The client moves nobody for any other reason, so this replays
    // the steps taken since the last pass, the player's and every monster's, each against the cells the others hold at
    // that moment: on a predictive client in time order; on a lockstep client frame by frame as its world ticks
    // (StepFrames). Called under conn.Monsters.
    public static void StepWalkers(Player player, double now)
    {
        // In an instance whose frames run, the area steps its walkers itself, once for everyone (AreaMovement).
        if (AreaSteps(player))
        {
            StepAreaWalkers(player, now);
            return;
        }
        double from = player.WalkersSteppedTo;
        // A lockstep client's walkers move only in its frames, so whole frames are replayed, up to the start of the frame
        // being sent (its 0x2b7 is on the wire: the frame advances in that send's gate under this lock); the frame's own
        // steps wait for its end (PlayerWalk.Position reads the frame's start as well).
        (double Start, double End)? frame = player.Connection.LockstepClient ? player.Connection.LockstepFrame : null;
        if (frame is { } lockstep) now = lockstep.Start;
        if (now <= from) return;
        player.WalkersSteppedTo = now;
        var walks = new Dictionary<uint, GameServer.Core.World.PlayerWalk>();
        // A lockstep client's killed monster walks until its death reaches the client (Player.Corpse). A monster the
        // grid has not listed yet joins it before this pass's steps (ClientEntityGrid: in the order the server added its
        // monsters, where it stands).
        foreach (var (id, m) in player.Area.Monsters)
        {
            if (!ClientEntityGrid.Lists(player, id)) ClientEntityGrid.Stepped(player, id, m.Walk.Position(from));
            if (LivingCollision.StepsWithBodies(m) || player.CorpseWalks(id, m)) walks[id] = m.Walk;
        }
        if (player.Connection.PlayerSent && player.PlayerLife > 0) walks[player.Id] = player.Walk;
        if (frame is { } f)
        {
            StepFrames(player, walks, from, now, f.End - f.Start);
            return;
        }
        var pending = new Dictionary<uint, double>();
        foreach (var (id, walk) in walks)
            if (walk.ArrivesAt > from) pending[id] = from;
        if (pending.Count == 0 && walks.Values.All(w => w.Reached)) return;
        using var bodies = player.BeginMonsterBlockerFrame(from);
        for (int round = 0; round < 8; round++)
        {
            var steps = new List<(double At, uint Id, (int X, int Y) Cell)>();
            foreach (var (id, since) in pending)
                foreach (var (at, cell) in walks[id].Steps(since, now)) steps.Add((at, id, cell));
            steps.Sort((a, b) => a.At != b.At ? a.At.CompareTo(b.At) : a.Id.CompareTo(b.Id));
            var stopped = new HashSet<uint>();
            foreach (var (at, id, cell) in steps)
            {
                if (stopped.Contains(id)) continue;
                if (!Step(player, bodies, id, walks[id], at, cell)) stopped.Add(id);
            }
            pending.Clear();
            foreach (var (id, walk) in walks)
            {
                if (walk.Reached || walk.ArrivesAt > now) continue;
                double at = walk.ArrivesAt;
                walk.Repath(at);
                pending[id] = at;
            }
            if (pending.Count == 0) break;
        }
    }

    // A lockstep client's world ticks each server frame with every object in turn, in ascending object id (the object
    // tree of 0x14218a650, 0x14218aff0 per object; the player, 0x1000, before every monster, 0x3000 and up), and a walker's
    // Positioned (its vtable slot +0x40 = 0x141e35a50) takes all of its steps due in the frame before the next object
    // ticks: its accumulator (+0x244) gains the frame's length (33 ms as float seconds, nothing rounded) and steps while
    // it holds a step time, each step checked against the other objects' current cells (0x141e4b3a0 reads +0x294/+0x298),
    // so a walker ticked earlier in the frame is seen where this frame's steps put it and a later one where the frame
    // before left it (live 2026-11-03, scripts/re/frida/trace-lockstep-tick.js; 07 §5). The frame a step falls in is the
    // one the walk's times give (the
    // accumulator carries the remainder between steps and segments, and a new order mid-step keeps the step's fraction:
    // 0x141e50880 rescales it by the new step time), so the same steps are taken here, grouped by frame and ordered by
    // object within it. A path the search cut short paths again at its end within the tick. A walk-up the client runs
    // (a monster's chase, the player's attack on a monster) also tests its goal in its own turn of each frame
    // (StepWalkUp), whether its body blocks or not.
    static void StepFrames(Player player, Dictionary<uint, GameServer.Core.World.PlayerWalk> walks, double from, double to, double length)
    {
        var chases = new Dictionary<uint, SpawnedMonster>();
        foreach (var (id, m) in player.Area.Monsters)
            if (WalkUpOpen(player, m)) chases[id] = m;
        var attack = PlayerWalkUpOpen(player);
        if (chases.Count == 0 && attack is null && walks.Values.All(w => w.ArrivesAt <= from && w.Reached)) return;
        using var bodies = player.BeginMonsterBlockerFrame(from);
        var order = walks.Keys.Union(chases.Keys).Order().ToArray();
        // Frame ends at to, to - length, ...; the oldest one replayed starts at from (a pass far behind lumps the rest).
        int frames = Math.Clamp((int)Math.Ceiling((to - from) / length - 1e-9), 1, 100);
        for (int i = frames; i >= 1; i--)
        {
            double end = to - (i - 1) * length, start = i == frames ? from : end - length;
            foreach (uint id in order)
            {
                if (id == player.Id && attack is not null && PlayerWalkUpOpen(player) == attack)
                {
                    var (result, timer) = StepWalkUp(player, bodies, new(player.Id, player.Walk, true, attack.TargetId, attack.Target?.Walk,
                        attack.Reach, attack.Tolerance, attack.TimerFrom, attack.GroundTarget), start, end, length);
                    attack.TimerFrom = timer;
                    if (result == WalkUpResult.Swings) LockstepAttackSwings(player, attack, start, end);
                    else if (result == WalkUpResult.Fails) attack.Fails();
                    continue;
                }
                if (chases.TryGetValue(id, out var chaser) && WalkUpOpen(player, chaser))
                {
                    var (result, timer) = StepWalkUp(player, bodies, new(id, chaser.Walk, walks.ContainsKey(id), player.Id,
                        walks.GetValueOrDefault(player.Id) ?? player.Walk, (int)(chaser.Attack!.Range - 1), LivingCollision.PlayerSize,
                        chaser.ClientChaseTimerFrom), start, end, length);
                    chaser.ClientChaseTimerFrom = timer;
                    if (result != WalkUpResult.Walking) WalkUpEnds(chaser, start, result == WalkUpResult.Swings);
                    continue;
                }
                if (!walks.TryGetValue(id, out var walk)) continue;
                // The client's Actor ended an in-place action in the frame before: its stop ends the walk that was left
                // going under the action, with this tick's steps (SpawnedMonster.ClientActionWalkStop).
                if (player.Area.Monsters.GetValueOrDefault(id) is { ClientActionWalkStop: { } stopAt } acting && start >= stopAt - 1e-6)
                {
                    acting.ClientActionWalkStop = null;
                    if (walk.Moving(start)) walk.StopAfter(end, start);
                }
                for (int round = 0; round < 8; round++)
                {
                    bool stopped = false;
                    foreach (var (at, cell) in walk.Steps(start, end))
                        if (!Step(player, bodies, id, walk, at, cell)) { stopped = true; break; }
                    if (stopped || walk.Reached || walk.ArrivesAt > end) break;
                    walk.Repath(walk.ArrivesAt);
                }
                // A corpse's death clip clears its body in its own turn: the walkers after it step without it.
                if (player.CorpseClears(id, start)) bodies.Remove(id);
            }
            // Game_TRACE=sync: every walker's cell after the frame, with how far it is into its step and the step's length,
            // on the QPC clock of scripts/re/frida/trace-sync.js.
            if (ServerTrace.Sync)
            {
                var line = new System.Text.StringBuilder();
                line.Append($"[frame] qpc={Stopwatch.GetTimestamp()} n={player.Connection.ServerFrame - 1 - (long)Math.Round((to - end) / length)} end={end:0.000} now={to:0.000} m=");
                foreach (uint id in order)
                {
                    if (!walks.TryGetValue(id, out var traced)) continue;
                    var at = traced.Position(end);
                    var (into, step) = traced.StepPhase(end);
                    line.Append($"{id:x}:{at.X},{at.Y}/{into:0.0000}/{step:0.0000};");
                }
                Console.WriteLine(line);
            }
        }
    }

    // A lockstep client's monster walking up to the player on its chase order (the attack aimed at him) that has neither
    // swung nor failed yet: the order went out (PlaceLockstepChase) and nothing the client gets has replaced it. A planned
    // order carries it to its own generation until the gate places it, and a give-up or a freeze, which send nothing,
    // keep it (NextPlannedAction); a swing order, a flinch, a stage and a transfer move the action generation on and end
    // it. Out of the wake zone its copy does not tick.
    public static bool WalkUpOpen(Player player, SpawnedMonster m) =>
        player.Connection.LockstepClient && m.ClientChase == m.ActionGeneration && !m.Dead && m.Life > 0 && !m.Asleep;

    // A lockstep client's attack on a monster that is walking up (placed at its gate, goal not met) while it is still the
    // player's action: an order the client takes ends it (a Move within two cells is none: PlaceLockstepPlayerMove places
    // nothing), as do a hit's recovery and a transfer; a target gone from the area ends it too (not traced). A closed one
    // is failed here, so the attack's task wakes. Under conn.Monsters.
    public static LockstepAttack? PlayerWalkUpOpen(Player player)
    {
        if (!player.Connection.LockstepClient || player.LockstepAttack is not { Open: true } attack) return null;
        if (player.ActionCount == attack.Action && player.Connection.PlayerSent && player.PlayerLife > 0 && !player.Closed && !player.LeavingArea
            && (attack.Target is null || ReferenceEquals(player.Area.Monsters.GetValueOrDefault(attack.TargetId), attack.Target))) return attack;
        attack.Fails();
        return null;
    }

    // A walk-up a lockstep client's MoveTo runs toward an object, in its walker's turn of a frame: a monster's chase at the
    // player (WalkUpOpen) or the player's attack on a monster (PlayerWalkUpOpen).
    readonly record struct WalkUpTurn(uint Id, GameServer.Core.World.PlayerWalk Walk, bool Blocks, uint TargetId,
        GameServer.Core.World.PlayerWalk? Target, int Reach, int Tolerance, double TimerFrom, (int X, int Y) GroundTarget = default);
    enum WalkUpResult { Walking, Swings, Fails }

    // A walk-up's turn in a lockstep frame (07 §5). Its steps are checked as any walker's (none for a body that blocks
    // nothing), and its MoveTo's goal (0x141e81950: within the reach less one, widened by the target's size, with the line
    // to it) is tested against the target object's own cell (0x14123f9c0) as the tick order leaves it: a target with a
    // lower id has ticked this frame, one with a higher id stands where the frame before left it (objects tick in
    // ascending id: 2 951 of 2 951 traced monster tests used the player's cell after his steps,
    // 181 of 181 of the player's on the monster's before its own). It is tested at each waypoint the walker steps into
    // (0x141e825b0) and after the frame's steps (the Pathfinding tick, 0x141e826a0). Met, the swing starts in this frame
    // (0x141e82310 → 0x141e9ab90 → 0x141e9afc0, which waits for no 0x1ac in lockstep): at a waypoint or the path's end the
    // walker stands there at once; mid-piece its stop (0x141e38420) waits for the next tick's steps. A blocked step tests
    // the goal at the cell it stopped on (its callback is 0x141e82310 too): met, it swings; else the action ends. Out of
    // reach at the path's end, cut short by the search or not, it searches again from there toward the target's cell in
    // the same tick (0x141e82010); no path ends the action. Mid-path, out of goal, it searches again only toward a target
    // that has left the cell it last searched toward, 2.0 s after that search, or 0.7 s after it once the target is 64
    // cells from that cell (0x141e826a0: each tick adds its frame to +0x55c before the test, and 0x141e82010 zeroes it;
    // decompiled, not reached live). Returns the turn's outcome and when the search timer last started.
    static (WalkUpResult Result, double TimerFrom) StepWalkUp(Player player, Player.MonsterBlockerFrame bodies, WalkUpTurn up,
        double start, double end, double length)
    {
        var walk = up.Walk;
        var target = up.Target?.Position(up.Id < up.TargetId ? start : end) ?? up.GroundTarget;
        double timerFrom = up.TimerFrom;
        bool InGoal((int X, int Y) cell) => walk.InReachOf(cell, target, up.Reach, up.Tolerance);
        for (int round = 0; round < 8; round++)
        {
            foreach (var (at, cell) in walk.Steps(start, end))
            {
                if (up.Blocks && !Step(player, bodies, up.Id, walk, at, cell))
                    return (InGoal(walk.Position(end)) ? WalkUpResult.Swings : WalkUpResult.Fails, timerFrom);
                if (!up.Blocks) ClientEntityGrid.Stepped(player, up.Id, cell);
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
                walk.Approach(target.X, target.Y, up.Reach, end, up.Tolerance, walk.Precision);
                // Searched after this tick's add: the timer counts again from the frame's end.
                if (walk.ArrivesAt > end) return (WalkUpResult.Walking, end);
                // No step to take (no path, or the goal at precision 1 where he stands): 0x141e82010 → 0x141e82310, a stop
                // after the next tick's steps in this replayed frame (the order's own stop would wait for the end of the
                // frame being sent).
                bool met = walk.ReachedGoal;
                walk.Restore(before);
                walk.StopAfter(end + length, end);
                return (met ? WalkUpResult.Swings : WalkUpResult.Fails, end);
            }
            walk.Approach(target.X, target.Y, up.Reach, Math.Max(walk.ArrivesAt, start), up.Tolerance, walk.Precision);
            // Searched inside the Positioned tick, before this frame's add.
            timerFrom = start;
            if (walk.Reached && !walk.ReachedGoal) return (WalkUpResult.Fails, timerFrom);
        }
        return (WalkUpResult.Walking, timerFrom);
    }

    // A walk-up that swung, from the frame's start, for the AI to take (PlanMonsterAi), or failed: the client's action
    // ends with no swing.
    static void WalkUpEnds(SpawnedMonster m, double start, bool swings)
    {
        if (swings) (m.ClientSwingAt, m.ClientSwingAction) = (start, m.ActionGeneration);
        else m.LastTarget = null;
        m.ClientChase = null;
    }

    // One step of a walk, as the client checks it: false when it was blocked and ended the walk.
    static bool Step(Player player, Player.MonsterBlockerFrame bodies, uint id, GameServer.Core.World.PlayerWalk walk, double at, (int X, int Y) cell)
    {
        // A Dash's slide crosses gaps and bodies (PlayerWalk.Slide): none of its steps is checked.
        bool floor = walk.Slides || walk.Floor(cell.X, cell.Y);
        bool body = floor && ClientPathfinder.ObjectBlocks(id == player.Id ? bodies.ForPlayer : bodies.For(id),
            cell.X, cell.Y, walk.Size + 1, walk.Size + 4);
        // A monster's charge runs through bodies (sync trace 2026-11-17: Jonah Unchained's ran past monsters on its line,
        // also ones beside its start).
        if (!floor || body && !walk.WalksThrough && !walk.ChargesThrough && !walk.Slides)
        {
            walk.StopAt(at);
            return false;
        }
        if (!body) walk.WalksThrough = false;
        if (id == player.Id) bodies.MovePlayer(cell.X, cell.Y);
        else bodies.Move(id, cell.X, cell.Y);
        ClientEntityGrid.Stepped(player, id, cell);
        return true;
    }
}
