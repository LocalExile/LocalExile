using System.Diagnostics;
using System.Net.Sockets;
using GameServer.Core.Diagnostics;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;

static partial class Game
{
    internal static async Task RunLivingCollisions(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
    {
        int frames = 0;
        try
        {
            while (!conn.Closed && conn.PendingTransfer is null)
            {
                if (!conn.Paused)
                {
                    await CorrectMonsterCollisions(remote, stream, ciphers, conn);
                    await PushOutOfCollision(remote, stream, ciphers, conn, living: true);
                    await ShowFreeze(remote, stream, ciphers, conn);
                    await ReleaseHeldMove(remote, stream, ciphers, conn);
                    await KeepWakeZone(remote, stream, ciphers, conn);
                }
                PublishPlayerPose(conn);
                if (ServerTrace.Sync) TraceSync(conn);
                if ((frames++ & 1) == 0) await SyncInstancePlayers(remote, stream, ciphers, conn);
                await Task.Delay(50);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException) { }
        finally { conn.Instance.RemovePlayer(conn.PeerId); }
    }

    // The client does not stop for is_frozen (live 2026-11-01), and virtual_action_speed_+% at -100, which would stop it,
    // divides its action clips by zero. A hit's recovery for as long as the freeze lasts stops it where it stands and lets
    // it go on with its walk after, as the server's walk does.
    internal static async Task ShowFreeze(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
    {
        lock (conn.Monsters)
        {
            if (conn.FreezeShown == conn.FreezeGeneration) return;
            conn.FreezeShown = conn.FreezeGeneration;
        }
        await Send(remote, stream, ciphers, [], "0x01d1 player frozen", atGate: () =>
        {
            lock (conn.Monsters)
            {
                double now = conn.Clock.Elapsed.TotalSeconds, left = conn.ElementalAilments.FreezeUntil(now) - now;
                if (left <= 0 || conn.PlayerLife == 0 || conn.LeavingArea || conn.Paused) return null;
                return [.. InstanceProtocol.ActorAnimation(PlayerId, (float)left, PlayerHitRecovery.TakeHitAnimation),
                    .. ResyncPacketLocked(conn, now)];
            }
        });
    }

    // A click that came during a hit's recovery or a freeze: the walk starts when that ends, as the client's does.
    internal static async Task ReleaseHeldMove(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
    {
        InstanceProtocol.UseSkillAtLocation use;
        (int X, int Y) from;
        int action;
        using (ProjectileMutation(conn))
        {
            if (conn.HeldMove is not { } held) return;
            double now = ProjectileWorldTime(conn);
            if (PlayerActionBlocked(conn, now, now)) return;
            conn.HeldMove = null;
            if (conn.Closed || conn.LeavingArea || conn.PlayerLife == 0) return;
            use = held;
            action = Interlocked.Increment(ref conn.ActionCount);
            from = conn.Walk.Order(use.X, use.Y, Math.Max(conn.HitRecovery.Until, now - MonsterAiCadence.StepSeconds));
            conn.ChannelSkill = 0;
        }
        await Send(remote, stream, ciphers, InstanceProtocol.ActionStarted(PlayerId, from.X, from.Y, use.X, use.Y, use.SkillId, use.Slot, use.Flags),
            $"0x01ab held move to ({use.X}, {use.Y}) from ({from.X}, {from.Y})", still: () => PlayerActionCurrent(conn, action));
    }

    // Game_TRACE=sync: where the server has the player and the monsters now, on the QPC clock of
    // scripts/re/frida/trace-sync.js. scripts/re/sync_report.py compares the two.
    internal static void TraceSync(ClientState conn)
    {
        if (!conn.PlayerSent) return;
        var line = new System.Text.StringBuilder();
        lock (conn.Monsters)
        {
            double now = conn.Clock.Elapsed.TotalSeconds;
            var p = conn.Walk.Position(now);
            line.Append($"[sync] qpc={System.Diagnostics.Stopwatch.GetTimestamp()} peer={conn.PeerId:x} speed={conn.Walk.Speed:0.###} ");
            line.Append($"moving={(conn.Walk.Moving(now) ? 1 : 0)} p={p.X},{p.Y} m=");
            foreach (var (id, m) in conn.Monsters)
            {
                if (m.Dead || m.Life <= 0) continue;
                var at = m.Walk.Position(now);
                line.Append($"{id:x}:{at.X},{at.Y};");
            }
        }
        Console.WriteLine(line);
    }

    internal static void PublishPlayerPose(ClientState conn)
    {
        lock (conn.Monsters)
        {
            if (conn.Closed || conn.LeavingArea) { conn.Instance.RemovePlayer(conn.PeerId); return; }
            if (conn.PeerTemplate is null || !conn.PlayerSent) return;
            RefreshPeerScale(conn, CharacterPlayerStats(conn)[ActorScale.Final]);
        }
    }

    // Called under the owner's lock. Replace only final scale in an immutable template, retaining creation stats,
    // equipment and skills. Observers read published snapshots and never lock another connection's simulation.
    internal static void RefreshPeerScale(ClientState conn, int scale)
    {
        if (conn.PeerTemplate is not { } template) return;
        if ((template.Stats ?? []).SingleOrDefault(s => s.Key == ActorScale.Final).Value != scale)
            conn.PeerTemplate = template = template with
            { Stats = [.. (template.Stats ?? []).Where(s => s.Key != ActorScale.Final), (ActorScale.Final, scale)] };
        if (!conn.PlayerSent || conn.Closed || conn.LeavingArea || conn.Instance is null) return;
        var p = conn.Walk.Position(conn.Clock.Elapsed.TotalSeconds);
        conn.Instance.PublishPlayer(new(conn.PeerId, conn.PeerType, template, p.X, p.Y, conn.PlayerLife, conn));
    }

    internal static async Task SyncInstancePlayers(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
    {
        var ids = conn.Instance.PlayersExcept(conn.PeerId).Select(p => p.Id)
            .Concat(conn.SeenPlayers.Keys).Distinct().ToArray();
        foreach (uint id in ids)
            await Send(remote, stream, ciphers, [], null, atGate: () =>
            {
                if (conn.Closed || conn.LeavingArea) return null;
                var packets = new List<byte[]>();
                // Read after waiting: queued creation/change must not restore an older scale or departed owner.
                if (conn.Instance.Player(id) is not { } peer)
                {
                    if (conn.SeenPlayers.Remove(id)) packets.Add(InstanceProtocol.RemoveObject(id));
                    return packets.SelectMany(p => p).ToArray();
                }
                bool exists = conn.SeenPlayers.TryGetValue(peer.Id, out var previous);
                if (!exists || previous!.Life == 0 && peer.Life > 0)
                {
                    if (exists) packets.Add(InstanceProtocol.RemoveObject(peer.Id));
                    // The beach wake-up is the owner's alone: observers get no action to end that stance with.
                    var player = peer.Template with { GridX = peer.X, GridY = peer.Y, Life = peer.Life,
                        Skills = [], QuestFlags = [], LeagueQuestFlags = [], WashedUp = false, Rotation = 0 };
                    packets.Add(InstanceEntities.CreateObject(peer.Id, peer.Type, player.Blob()));
                    packets.Add(InstanceProtocol.EnterWakeZone(peer.Id));
                }
                else
                {
                    if ((previous!.X, previous.Y) != (peer.X, peer.Y))
                        packets.Add(Math.Max(Math.Abs(previous.X - peer.X), Math.Abs(previous.Y - peer.Y)) > 20
                            ? InstanceProtocol.Teleport(peer.Id, peer.X, peer.Y, 0x7fffffff)
                            : InstanceProtocol.MoveTo(peer.Id, peer.X, peer.Y, .1f, 0x7fffffff));
                    if (previous.Life != peer.Life)
                        packets.Add(InstanceProtocol.LifeUpdate(peer.Id, peer.Life));
                    int scale = (peer.Template.Stats ?? []).SingleOrDefault(s => s.Key == ActorScale.Final).Value;
                    int before = (previous.Template.Stats ?? []).SingleOrDefault(s => s.Key == ActorScale.Final).Value;
                    if (scale != before)
                        packets.Add(InstanceEntities.StatsUpdate(peer.Id, [(ActorScale.Final, scale)]));
                }
                conn.SeenPlayers[peer.Id] = peer;
                return packets.SelectMany(p => p).ToArray();
            });
    }

    internal static Task CorrectMonsterCollisions(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
    {
        using (ProjectileMutation(conn, phase: "collision")) StepWalkers(conn, ProjectileWorldTime(conn));
        return Task.CompletedTask;
    }

    // The client steps every walker a cell at a time and checks each step (0x141e32710): the cell's walk value must be at
    // least the walker's size + 1, and no other body may be within reach (0x141e47a40 → 0x141e47ca0: the Chebyshev distance
    // less that body's size below the walker's size + 1). A blocked step ends the walk where it stands, with no new path
    // (0x141e7bc20); a chaser's next order paths it again. A walk whose path began on a blocked cell steps through bodies
    // until its first free step; terrain still stops it. A path cut short by the search is walked to its end and then paths
    // again from there at once (0x141e7bec0 → 0x141e7b920). The client moves nobody for any other reason, so this replays
    // the steps taken since the last pass, the player's and every monster's, each against the cells the others hold at
    // that moment: on a predictive client in time order; on a lockstep client frame by frame as its world ticks
    // (StepFrames). Called under conn.Monsters.
    internal static void StepWalkers(ClientState conn, double now)
    {
        double from = conn.WalkersSteppedTo;
        // A lockstep client's walkers move only in its frames, so whole frames are replayed, up to the start of the frame
        // being sent (its 0x2b5 is on the wire: the frame advances in that send's gate under this lock); the frame's own
        // steps wait for its end (PlayerWalk.Position reads the frame's start as well).
        (double Start, double End)? frame = conn.LockstepClient ? conn.LockstepFrame : null;
        if (frame is { } lockstep) now = lockstep.Start;
        if (now <= from) return;
        conn.WalkersSteppedTo = now;
        var walks = new Dictionary<uint, GameServer.Core.World.PlayerWalk>();
        foreach (var (id, m) in conn.Monsters)
            if (LivingCollision.Blocks(m)) walks[id] = m.Walk;
        if (conn.PlayerSent && conn.PlayerLife > 0) walks[PlayerId] = conn.Walk;
        if (frame is { } f)
        {
            StepFrames(conn, walks, from, now, f.End - f.Start);
            return;
        }
        var pending = new Dictionary<uint, double>();
        foreach (var (id, walk) in walks)
            if (walk.ArrivesAt > from) pending[id] = from;
        if (pending.Count == 0 && walks.Values.All(w => w.Reached)) return;
        using var bodies = conn.BeginMonsterBlockerFrame(from);
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
                if (!Step(bodies, id, walks[id], at, cell)) stopped.Add(id);
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
    // tree of 0x142183ce0, 0x142184680 per object; the player, 0x1000, before every monster, 0x3000 and up), and a walker's
    // Positioned (its vtable slot +0x40 = 0x141e32710) takes all of its steps due in the frame before the next object
    // ticks: its accumulator (+0x244) gains the frame's length (33 ms as float seconds, nothing rounded) and steps while
    // it holds a step time, each step checked against the other objects' current cells (0x141e47ca0 reads +0x294/+0x298),
    // so a walker ticked earlier in the frame is seen where this frame's steps put it and a later one where the frame
    // before left it (live 2026-11-03, scripts/re/frida/trace-lockstep-tick.js; 07 §5). The frame a step falls in is the
    // one the walk's times give (the
    // accumulator carries the remainder between steps and segments, and a new order mid-step keeps the step's fraction:
    // 0x141e4d180 rescales it by the new step time), so the same steps are taken here, grouped by frame and ordered by
    // object within it. A path the search cut short paths again at its end within the tick.
    static void StepFrames(ClientState conn, Dictionary<uint, GameServer.Core.World.PlayerWalk> walks, double from, double to, double length)
    {
        if (walks.Values.All(w => w.ArrivesAt <= from && w.Reached)) return;
        using var bodies = conn.BeginMonsterBlockerFrame(from);
        var order = walks.Keys.Order().ToArray();
        // Frame ends at to, to - length, ...; the oldest one replayed starts at from (a pass far behind lumps the rest).
        int frames = Math.Clamp((int)Math.Ceiling((to - from) / length - 1e-9), 1, 100);
        for (int i = frames; i >= 1; i--)
        {
            double end = to - (i - 1) * length, start = i == frames ? from : end - length;
            foreach (uint id in order)
            {
                var walk = walks[id];
                for (int round = 0; round < 8; round++)
                {
                    bool stopped = false;
                    foreach (var (at, cell) in walk.Steps(start, end))
                        if (!Step(bodies, id, walk, at, cell)) { stopped = true; break; }
                    if (stopped || walk.Reached || walk.ArrivesAt > end) break;
                    walk.Repath(walk.ArrivesAt);
                }
            }
            // Game_TRACE=sync: every walker's cell after the frame, with how far it is into its step and the step's length,
            // on the QPC clock of scripts/re/frida/trace-sync.js.
            if (ServerTrace.Sync)
            {
                var line = new System.Text.StringBuilder();
                line.Append($"[frame] qpc={Stopwatch.GetTimestamp()} n={conn.ServerFrame - 1 - (long)Math.Round((to - end) / length)} end={end:0.000} now={to:0.000} m=");
                foreach (uint id in order)
                {
                    var at = walks[id].Position(end);
                    var (into, step) = walks[id].StepPhase(end);
                    line.Append($"{id:x}:{at.X},{at.Y}/{into:0.0000}/{step:0.0000};");
                }
                Console.WriteLine(line);
            }
        }
    }

    // One step of a walk, as the client checks it: false when it was blocked and ended the walk.
    static bool Step(ClientState.MonsterBlockerFrame bodies, uint id, GameServer.Core.World.PlayerWalk walk, double at, (int X, int Y) cell)
    {
        bool floor = walk.Floor(cell.X, cell.Y);
        bool body = floor && ClientPathfinder.ObjectBlocks(id == PlayerId ? bodies.ForPlayer : bodies.For(id),
            cell.X, cell.Y, walk.Size + 1, walk.Size + 4);
        if (!floor || body && !walk.WalksThrough)
        {
            walk.StopAt(at);
            return false;
        }
        if (!body) walk.WalksThrough = false;
        if (id == PlayerId) bodies.MovePlayer(cell.X, cell.Y);
        else bodies.Move(id, cell.X, cell.Y);
        return true;
    }
}
