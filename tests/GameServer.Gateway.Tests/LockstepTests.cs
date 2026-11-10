using System.Net;
using System.Net.Sockets;
using GameServer.Core.Projectiles;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;
using GameServer.Modding;
using Xunit;

namespace GameServer.Gateway.Tests;

public class LockstepTests : IDisposable
{
    readonly IGameLogic? previousLogic = Program.GameLogicOrNull;
    readonly IModuleLifetime? previousLifetime = Game.CombatLifetime;
    readonly ModuleLifetime lifetime = new("Game");
    public LockstepTests()
    {
        Game.CombatLifetime = lifetime;
        Program.SetGameLogic(new GameServer.Modules.Game.GameLogic());
    }
    public void Dispose()
    {
        lifetime.Stop(ex => throw ex);
        Game.CombatLifetime = previousLifetime;
        Program.SetGameLogic(previousLogic!);
    }

    [Fact]
    public void Instant_replies_share_the_lockstep_sequence_and_suppress_local_action_events()
    {
        var conn = new ClientState(new(1, "instant", 300, 300, 100, 100, null, []), 1, 1)
            { LockstepClient = true, LockstepSequence = 0x575, ActionCount = 17, LockstepBusyUntil = 12.5 };
        // The retained ground and object forms carry 0x0101 and 0x0121; their sequence joins ordinary actions.
        Assert.Equal("01CE0000100000000000000084098576010181F78223",
            Convert.ToHexString(Game.PlayerInstantAction(conn, 0x8409, (503, 547))));
        Assert.Equal("01CE000010000000000000008409857701210000300181F78223",
            Convert.ToHexString(Game.PlayerInstantAction(conn, 0x8409, (503, 547), 0x3001)));
        Assert.Equal(0x577, conn.LockstepSequence);
        Assert.Equal(17, conn.ActionCount); // An instant skill does not cancel an existing cast or channel.
        Assert.Equal(12.5, conn.LockstepBusyUntil);
        conn.LockstepSequence = 0x7fff;
        Assert.Equal("80000101", Convert.ToHexString(Game.PlayerInstantAction(conn, 0x8409, (503, 547))[14..18]));
        conn.LockstepClient = false;
        Assert.Equal("01CE000010000000000000008409FFFF00200000300181F78223",
            Convert.ToHexString(Game.PlayerInstantAction(conn, 0x8409, (503, 547), 0x3001)));
        Assert.Equal(0, conn.LockstepSequence);
        foreach (bool lockstep in new[] { false, true })
        {
            conn.LockstepClient = lockstep;
            conn.PlayerLife = 100;
            var state = new SingleUseCooldownState(0, 1);
            Assert.True(state.TryUse(0, new SingleUseCooldown(3000, 0)));
            var use = new CooldownUseTicket(1, conn.ActionCount, conn.ProjectileEpoch, Instant: true,
                State: state, UseVersion: state.UseVersion);
            Assert.True(use.CommitNativeUse(conn, 0x8409));
            // In lockstep the queued instant action must consume its still-ready native use itself.
            Assert.Equal(!lockstep, state.NeedsNativeRefresh);
            state.NativeRefreshed();
            state.Refresh(1, 2);
            Assert.True(state.NeedsNativeRefresh); // Later recovery-rate corrections still synchronize.
        }
    }

    [Fact]
    public void A_walker_on_frames_starts_on_the_frame_and_stops_after_its_steps()
    {
        // 100 cells a second: one cell every 10 ms, 3.3 cells a frame.
        double start = 0;
        PlayerWalk Walker(double speed = 100) => new(0, 0, speed) { FrameOf = at => (Math.Min(at, start), start + .033) };

        var walk = Walker();
        walk.Order(100, 0, .005);   // sent 5 ms into the frame, it starts at the frame's start
        start = .033;
        Assert.Equal((3, 0), walk.Position(.04));   // read during a frame, it is where the frame began
        Assert.Equal((3, 0), walk.Order(3, 0, .04));   // the stop reports where the client is now
        Assert.Equal((3, 0), walk.Position(.04));   // the frame's steps are still steps: the step pass checks each
        start = .066;
        Assert.Equal((6, 0), walk.Position(.066));   // and the walker stands where they took it
        Assert.False(walk.Moving(.066));

        start = 0;
        walk = Walker();
        walk.Order(100, 0, 0);
        start = .033;
        walk.Pause(.04, .1);   // a hold also starts after the frame's steps, and ends .1 s after the frame's start
        start = .099;
        Assert.Equal((6, 0), walk.Position(.099));
        start = .165;
        Assert.Equal((9, 0), walk.Position(.165));

        // A hit's recovery ends a lockstep player's walk after the steps of the frame its 0x1d1 goes out in, and nothing
        // takes the walk up again: the client stands until its next order. A predictive player's walk is not the
        // message's to end (it is held when the hit lands and goes on after).
        foreach (bool lockstep in new[] { true, false })
        {
            ClientState Player() => new(new(1, "recovery", 300, 300, 100, 100, null, []), 1, 1)
                { LockstepClient = lockstep, PlayerLife = 100, LockstepFrame = (0, .033) };
            var hit = Player();
            var free = Player();
            hit.Walk.Order(160, 100, 0);
            free.Walk.Order(160, 100, 0);
            hit.LockstepFrame = free.LockstepFrame = (.033, .066);
            lock (hit.Monsters)
                Assert.NotNull(Game.PlayerHitRecoveryRecordLocked(hit, new(hit.ProjectileEpoch, hit.ActionCount, Until: .4, Seconds: .35f), now: .04));
            hit.LockstepFrame = free.LockstepFrame = (.066, .099);
            var stood = hit.Walk.Position(.066);
            Assert.Equal(free.Walk.Position(.066), stood);
            Assert.NotEqual(hit.Walk.Position(0), stood);
            hit.LockstepFrame = free.LockstepFrame = (.495, .528);
            Assert.Equal(lockstep, !hit.Walk.Moving(.495));
            Assert.Equal(lockstep ? stood : free.Walk.Position(.495), hit.Walk.Position(.495));
        }

        // A stop whose tick steps the walker into a waypoint ends the walk there, as at a path's end: it does not go on into
        // the next piece, and nothing of the step time is kept, so the next walk from the stand steps a whole step after
        // it starts. Round a corner at 40 cells a second, cancelled in the frame of the step into the corner.
        foreach (bool order in new[] { false, true })
        {
            start = 0;
            walk = new PlayerWalk(0, 0, 40, walkable: (x, y) => y == 0 || x == 8) { FrameOf = at => (Math.Min(at, start), start + .033) };
            walk.Order(8, 9, 0);
            var corner = walk.Steps(0, 1).First(s => walk.EndsPiece(s.At));
            Assert.True(walk.Steps(corner.At, 1).Count > 0);   // the path goes on past it
            start = Math.Floor(corner.At / .033) * .033;
            if (start == corner.At) start -= .033;
            double tick = start;
            // A cancelled action's stop, and a Move onto the walker's own cell: the same stop.
            if (order) { var own = walk.Position(tick); walk.Order(own.X, own.Y, tick + .001); }
            else walk.Cancel(tick + .001);
            start = tick + .099;
            Assert.Equal(corner.Cell, walk.Position(start));
            Assert.False(walk.Moving(start));
            walk.Order(0, 0, start);
            float step = (float)(walk.Steps(start, start + 1)[1].At - walk.Steps(start, start + 1)[0].At);
            Assert.Equal(start + step, walk.Steps(start, start + 1)[0].At, 5);
        }

        // A stats message that names a movement stat lays the piece in progress again from the walker's cell, at an
        // unchanged speed too: the straight line from there steps sideways further on than the line from the piece's
        // start did. Any other stats message leaves the walk as it is.
        static int Sideways(PlayerWalk w, int y) => w.Steps(0, 9).First(s => s.Cell.Y == y).Cell.X;
        start = 0; walk = Walker(40);
        walk.Order(40, 1, 0);
        int turned = Sideways(walk, 1);
        start = .231; walk.Relay(.24);   // nine cells along, before the sideways step
        Assert.True(Sideways(walk, 1) > turned);
        Assert.Equal((40, 1), walk.Destination);
        var told = new ClientState(new(1, "stats", 300, 300, 100, 100, null, []), 1, 1)
            { LockstepClient = true, PlayerLife = 100, PlayerSent = true, LockstepFrame = (0, .033) };
        told.Walk.Order(140, 101, 0);
        turned = Sideways(told.Walk, 101);
        told.LockstepFrame = (.231, .264);
        lock (told.Monsters)
        {
            Game.RelayWalkForStats(told, InstanceEntities.StatsUpdate(0x1000, [(0x2fec, 0)]));
            Assert.Equal(turned, Sideways(told.Walk, 101));
            Game.RelayWalkForStats(told, InstanceEntities.StatsUpdate(0x1000, [(0x2fec, 0), (StatKeys.MovementVelocityPct, 0)]));
            Assert.True(Sideways(told.Walk, 101) > turned);
        }

        // A stop keeps the time since the last step, and the next walk from the stand starts with it (40 cells a second:
        // 25 ms a step; the client's accumulator 16 ms, + 33 ms is one step, + 33 ms two more).
        start = 0; walk = Walker(40);
        walk.Order(100, 0, 0);
        start = .033; Assert.Equal((1, 0), walk.Order(1, 0, .04));   // stands at (2, 0) after the frame, 16 ms into its step
        start = .099; walk.Order(100, 0, .099);
        start = .165; Assert.Equal((5, 0), walk.Position(.165));

        // The same through a walk-up found in reach where the walker stands, which stops where the frame's steps leave it,
        // and a forced set at the stand keeps it.
        start = 0; walk = Walker(40);
        walk.Order(100, 0, 0);
        start = .033; var (here, stop, _) = walk.Approach(11, 0, 10, .04);
        Assert.Equal(((1, 0), (2, 0)), (here, stop));
        start = .066; walk.Teleport(2, 0, .07);
        start = .099; walk.Order(100, 0, .099);
        start = .165; Assert.Equal((5, 0), walk.Position(.165));

        // Kept time longer than the new step (30.6 ms into a 35.4 ms diagonal step, then 25 ms straight ones): the step
        // already due comes in the order's frame, not before it; the client steps 2, 1 and 2 cells in the next frames.
        start = 0; walk = Walker(40);
        walk.Order(20, 20, 0);
        start = .033; Assert.Equal((0, 0), walk.Order(0, 0, .04));
        Assert.Equal((1, 1), walk.Destination);
        start = .099; Assert.Equal((1, 1), walk.Order(20, 1, .099));
        Assert.Equal((1, 1), walk.Position(.099));
        Assert.Equal([.1087, .1184, .1434], walk.Steps(.05, .16).Select(s => Math.Round(s.At, 4)));
        Assert.Equal([(2, 1), (3, 1)], walk.Steps(.099, .132).Select(s => s.Cell));
        start = .132; Assert.Equal((3, 1), walk.Position(.132));
        start = .165; Assert.Equal((4, 1), walk.Position(.165));
        start = .198; Assert.Equal((6, 1), walk.Position(.198));

        // A slow walker's too (10 cells a second: 132 ms kept, 100 ms steps): in the order's frame, not the next one.
        start = 0; walk = Walker(10);
        walk.Order(20, 20, 0);
        start = .099; walk.Order(0, 0, .1);
        start = .165; walk.Order(20, 0, .165);
        Assert.Equal([(1, 0)], walk.Steps(.165, .198).Select(s => s.Cell));
        start = .231; Assert.Equal((1, 0), walk.Position(.231));
        start = .264; Assert.Equal((2, 0), walk.Position(.264));

        // A speed doubled at the stand (12.5 ms steps against 30.6 ms kept) leaves two steps due: both come in the order's
        // frame, in order, before the walk's third step, and the client takes 5 steps in that frame's tick and 2 in the next.
        start = 0; walk = Walker(40);
        walk.Order(20, 20, 0);
        start = .033; walk.Order(0, 0, .04);
        walk.SetSpeed(80, .05);
        start = .099; Assert.Equal((1, 1), walk.Order(20, 1, .099));
        Assert.Equal((1, 1), walk.Position(.099));
        Assert.Equal([.1013, .1036, .1059, .1184, .1309], walk.Steps(.099, .132).Select(s => Math.Round(s.At, 4)));
        Assert.Equal([(2, 1), (3, 1), (4, 1), (5, 1), (6, 1)], walk.Steps(.099, .132).Select(s => s.Cell));
        start = .165; Assert.Equal((8, 1), walk.Position(.165));

        // A walk set up in the stop's own frame finds the walker still on its cell with the step in progress (the stop
        // waits for the tick, and the new MoveTo clears it): it goes on from there with the step's fraction, 33 of
        // 35.4 ms carried into a 25 ms step. Shield Charge takes no kept time at all (its first step from a stand is not
        // traced): it steps a whole step after it starts.
        start = 0; walk = Walker(40);
        walk.Order(20, 20, 0);
        start = .033; walk.Order(0, 0, .04);   // would stand at (1, 1) after the frame's step
        Assert.Equal((0, 0), walk.Order(1, 20, .05));
        Assert.Equal(.0347, walk.Steps(.033, .2)[0].At, 4);
        start = 0; walk = Walker(40);
        walk.Order(20, 20, 0);
        start = .033; walk.Order(0, 0, .04);
        walk.SetSpeed(100, .04);
        walk.Charge(30, 1, 100, .04);
        Assert.Equal(.05, walk.Steps(.033, .2)[0].At, 4);
        start = .099; Assert.Equal((6, 1), walk.Position(.099));
    }

    [Fact]
    public async Task A_player_attack_walks_up_to_the_weapon_range_less_one_and_swings_by_the_client_s_goal_test()
    {
        // A Drowned (size 2, the tolerance) 13 cells off and a sword of range 11: the client's MoveTo has max range 10, so
        // the player is in reach only 12 cells off, one step on (the whole range would have it swing where it stands).
        var conn = new ClientState(new(1, "walk-up", 300, 300, 100, 100, null, []), 1, 1);
        var drowned = new SpawnedMonster("Drowned", new PlayerWalk(113, 100, 8, size: 2), 0, 4, 100);
        var sword = new SkillCatalog.Unarmed(645, 1, 2, 11);
        var (here, stop, _, atOnce) = Game.WalkUpToSwing(conn, drowned, (113, 100), sword, 0, 1);
        Assert.Equal(((100, 100), (101, 100), false), (here, stop, atOnce));

        // A predictive walk-up retries every 50 ms (0x141e8eb60, 0x141e8ef50). Even after a late wake
        // beyond three seconds, an out-of-reach target does not start a swing or end the pursuit.
        var predictive = new ClientState(new(1, "predictive-follow", 300, 300, 100, 100, null, []), 1, 1);
        predictive.Clock.Reset();
        var previousLogic = Program.GameLogicOrNull;
        var previousLifetime = Game.CombatLifetime;
        var lifetime = new ModuleLifetime("Game");
        Game.CombatLifetime = lifetime;
        Program.SetGameLogic(new GameServer.Modules.Game.GameLogic());
        var session = new Game.ProjectileSession("predictive-follow", null!, null!, predictive);
        predictive.ProjectileRuntimeState = session;
        try
        {
            Assert.True(session.Active);
            var target = new SpawnedMonster("Drowned", new PlayerWalk(200, 100, 8, size: 2), 0, 4, 100);
            var waits = new List<double>();
            var followed = await Game.FollowMonsterToSwing(predictive, target, sword, () => true,
                (next, _) =>
                {
                    waits.Add(next);
                    if (waits.Count > 1) return Task.FromResult(false); // the next request cancels the pursuit
                    session.AdvanceTo(ProjectileRuntime.Time(3.05));
                    Assert.Equal(3.05, Game.ProjectileWorldTime(predictive), 9);
                    return Task.FromResult(true);
                }, (100, 100), 0, false, 0);
            Assert.Null(followed);
            Assert.Equal(2, waits.Count);
            Assert.Equal(.05, waits[0], 9);
            Assert.Equal(3.10, waits[1], 9);
            Assert.Equal((188, 100), predictive.Walk.Destination);
        }
        finally
        {
            session.Invalidate();
            predictive.ProjectileRuntimeState = null;
            lifetime.Stop(ex => throw ex);
            Game.CombatLifetime = previousLifetime;
            if (previousLogic is not null) Program.SetGameLogic(previousLogic);
        }

        // On a lockstep client the attack's walk-up is the client's MoveTo (07 §5, §6): placed where its 0x1ab goes out, at
        // the start of the frame being sent, toward the monster's own cell; tested by the step pass in the player's turn of
        // every frame after his steps, against the monster as the frame before left it, and at each waypoint; the swing in
        // the goal frame, the stand after the next tick's steps mid-piece; a search again only at the path's end or by the
        // search timer. The player walks 37 cells a second (a step every 27 ms), frames are 33 ms; reach 10, tolerance 2.
        ClientState Lockstep(SpawnedMonster target)
        {
            var c = new ClientState(new(1, "walk-up", 300, 300, 100, 100, null, []), 1, 1)
            {
                Pools = new(1, 1000, 100, 2, 0), PlayerLife = 500, PlayerSent = true, LockstepClient = true, LockstepFrame = (0, .033),
            };
            c.Monsters[0x3000] = target;
            return c;
        }
        var use = new InstanceTransfer.UseSkillOnObject(0x3000, SkillCatalog.FirstSkillId, 0x8001, 0x0400);
        double hold = .5;   // the swing's hold (LockstepAttackCutIn and the tail), read as the swing begins
        LockstepAttack Attack(ClientState c, SpawnedMonster target)
        {
            var attack = new LockstepAttack(++c.ActionCount, 0x3000, target, 10, 2, 1, hold: () => hold);
            c.LockstepAttack = attack;
            return attack;
        }
        byte[]? Gate(ClientState c, LockstepAttack attack, double start)
        {
            c.LockstepFrame = (start, start + .033);
            lock (c.Monsters) return Game.LockstepAttackAtGate("walk-up", c, attack, use);
        }
        void Replay(ClientState c, double to)   // the frames up to `to`, the start of the frame then being sent
        {
            c.LockstepFrame = (to, to + .033);
            lock (c.Monsters) Game.StepWalkers(c, to);
        }
        byte[] Start(int x, int y, int toX, int toY) =>
            InstanceProtocol.ActionStarted(0x1000, x, y, toX, toY, use.SkillId, use.Sequence, use.Flags, targetObjectId: 0x3000);

        // Walking away from one 12 cells behind: in goal where the order applies, so the swing begins in that frame, from
        // where the frame's steps leave him, 13 cells off, with no wait and no walk back.
        drowned = new SpawnedMonster("Drowned", new PlayerWalk(89, 100, 8, size: 2), 0, 4, 100);
        conn = Lockstep(drowned);
        conn.Walk.Order(200, 100, 0);
        var attack = Attack(conn, drowned);
        Assert.Equal(Start(101, 100, 89, 100), Gate(conn, attack, .033));
        Assert.Equal(new LockstepAttack.Swing(.033, .066, (102, 100)), await attack.Decided);
        Assert.Equal(.566, conn.LockstepBusyUntil, 9);
        Assert.Equal(((102, 100), .033, .033), await Game.FollowMonsterToSwing(conn, drowned, sword, () => true,
            (_, _) => Task.FromResult(false), (0, 0), 0, false, .04, lockstep: attack));

        // Asked in the frame from 0.033, its 0x1ab goes out a frame later (a frame end overtook it at the send gate):
        // the walk-up is placed at that frame's start, from the cell the player's steps have reached by then, and its
        // search timer starts there. The target then moves off: out of reach at the path's end, the walk-up searches
        // again in that frame's Positioned tick, so the timer counts from the frame's start.
        drowned = new SpawnedMonster("Drowned", new PlayerWalk(80, 100, 8, size: 2), 0, 4, 100);
        conn = Lockstep(drowned);
        conn.Walk.Order(200, 100, 0);
        conn.LockstepFrame = (.033, .066);
        using (var listener = new TcpListener(IPAddress.Loopback, 0))
        {
            listener.Start();
            using var writer = new TcpClient();
            var accepting = listener.AcceptTcpClientAsync();
            await writer.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
            using var reader = await accepting;
            var ciphers = new ConnectionCiphers(new byte[64], isClient: false);
            var gate = Program.SendGates.GetValue(ciphers, _ => new(1, 1));
            Assert.True(gate.Wait(0));   // the frame end holds the send gate
            var starting = Game.LockstepAttackStart("walk-up", writer.GetStream(), ciphers, conn, use, drowned, sword,
                1, ++conn.ActionCount, "attack");
            Assert.False(starting.IsCompleted);
            conn.LockstepFrame = (.066, .099);
            gate.Release();
            attack = (await starting)!;
            var sent = Start(102, 100, 80, 100);
            var wire = new byte[sent.Length];
            await reader.GetStream().ReadExactlyAsync(wire);
            new ConnectionCiphers(new byte[64], isClient: true).Receive.Process(wire);
            Assert.Equal(sent, wire);
        }
        Assert.True(attack.Open);
        Assert.Equal((92, 100), conn.Walk.Destination);
        Assert.Equal(.066, attack.TimerFrom, 9);
        drowned.Walk.Teleport(60, 100, .07);
        Replay(conn, .297);
        Assert.Equal((92, 100), conn.Walk.Destination);
        Replay(conn, .33);   // there at 0.324, 32 cells off
        Assert.Equal((72, 100), conn.Walk.Destination);
        Assert.Equal(.297, attack.TimerFrom, 9);

        // Toward a Drowned walking at him (a step every 0.143 s, to (132, 100) at 0.571): the path goes to (124, 100) and is
        // not searched again. In the frame from 0.561 he is 13 cells from the monster's cell at that frame's start, though
        // 12 from the cell its step leaves it on; in the frame from 0.594 his step to (121, 100) is in goal mid-piece. The
        // swing begins in that frame, and he takes the next tick's two due steps, then stands, where his next order starts.
        drowned = new SpawnedMonster("Drowned", new PlayerWalk(136, 100, 7, size: 2), 0, 4, 100);
        drowned.Walk.Order(100, 100, 0);
        conn = Lockstep(drowned);
        attack = Attack(conn, drowned);
        Assert.Equal(Start(100, 100, 136, 100), Gate(conn, attack, .033));
        Assert.Equal((124, 100), conn.Walk.Destination);
        Replay(conn, .594);
        Assert.False(attack.Decided.IsCompleted);
        Assert.Equal((124, 100), conn.Walk.Destination);
        Assert.Equal((120, 100), conn.Walk.Position(.594));
        hold = .6;   // slowed on the way (a chill): the swing holds him as long as the attack time it begins with
        Replay(conn, .627);
        Assert.Equal(new LockstepAttack.Swing(.594, .627, (123, 100)), await attack.Decided);
        Assert.Equal(.627 + .6, conn.LockstepBusyUntil, 9);
        Assert.Equal([(.6276, (122, 100)), (.6546, (123, 100))], conn.Walk.Steps(.627, .66).Select(s => (Math.Round(s.At, 4), s.Cell)));
        Replay(conn, .66);
        Replay(conn, .693);
        Assert.Equal((123, 100), conn.Walk.Position(.693));
        Assert.False(conn.Walk.Moving(.693));
        Assert.Equal(((123, 100), .594, .594), await Game.FollowMonsterToSwing(conn, drowned, sword, () => true,
            (_, _) => Task.FromResult(false), (0, 0), 0, false, .04, lockstep: attack));
        Assert.Equal((123, 100), Game.PlaceLockstepPlayerMove(conn, (140, 130), .693));

        // A target that has left the cell searched toward is searched for again mid-path 2.0 s after the search, or 0.7 s
        // after it once 64 cells from that cell (0x141e7bfb0). Here it goes off his line, out of reach of it: 16 and 13
        // cells from (190, 100), the walk-up searches again at the end of the frame from 2.013, a step from a waypoint in
        // goal, where it stands at once; 64 and 13 cells away, at the end of the frame from 0.726.
        (ClientState Conn, LockstepAttack Attack) Off(int x, int y)
        {
            var target = new SpawnedMonster("Drowned", new PlayerWalk(190, 100, 8, size: 2), 0, 4, 100);
            var c = Lockstep(target);
            var a = Attack(c, target);
            Assert.Equal(Start(100, 100, 190, 100), Gate(c, a, .033));
            Assert.Equal((178, 100), c.Walk.Destination);
            target.Walk.Teleport(x, y, .04);
            return (c, a);
        }
        var (late, lateAttack) = Off(174, 113);
        Replay(late, 2.013);
        Assert.Equal((178, 100), late.Walk.Destination);
        Replay(late, 2.046);
        Assert.Equal((174, 101), late.Walk.Destination);
        Assert.Equal(2.046, lateAttack.TimerFrom, 9);
        Replay(late, 2.079);
        Assert.Equal(new LockstepAttack.Swing(2.046, 2.079, (174, 101)), await lateAttack.Decided);
        var (far, farAttack) = Off(126, 113);
        Replay(far, .726);
        Assert.Equal((178, 100), far.Walk.Destination);
        Replay(far, .759);
        Assert.Equal((126, 101), far.Walk.Destination);
        Replay(far, .792);
        Assert.Equal(new LockstepAttack.Swing(.759, .792, (126, 101)), await farAttack.Decided);

        // A ground melee use walks to weapon range too, with no target-size tolerance. Native 3.29.3b:
        // an aim 17 cells right and 9 up stops 8 right and 5 up, then starts the swing there.
        conn = Lockstep(new SpawnedMonster("Drowned", new PlayerWalk(200, 200, 8, size: 2), 0, 4, 100));
        attack = new(++conn.ActionCount, 0, null, 10, -1, 1, () => .5)
        {
            GroundTarget = (117, 91), GroundAttackSeconds = () => .645,
        };
        conn.LockstepAttack = attack;
        Assert.Equal(((100, 100), (117, 91)), Game.PlaceLockstepAttack(conn, attack, 0));
        Assert.True(attack.Open);
        Assert.Equal(0, conn.LockstepBusyUntil);
        Replay(conn, .33);
        Assert.Equal((108, 95), conn.Walk.Position(.33));
        Assert.Equal((108, 95), (await attack.Decided)!.Value.From);
        Assert.True(conn.LockstepBusyUntil > .33);
        Assert.True(conn.NextAttackAt > .33);

        // The tutorial chest's interaction range is independent of its blocking size of 1. Native stops at y=555,
        // four cells before the old footprint-based walk-up; the next order must start at that same cell.
        var chestWalk = new PlayerWalk(797, 564, 37);
        Assert.Equal((797, 555), chestWalk.WalkUpTo(800, 547, TutorialObjects.ChestInteractionSize, 0).Stop);
    }

    // A create whose blob's first byte has 0x02 (a projectile's) wakes the object itself; a 0x1a9 after it only logs on the
    // client. An object created asleep (an effect's blob starts with 0) still gets its 0x1a9 right behind the create.
    // These are the whole sends the projectile session makes for a flight object's and an effect's create.
    [Fact]
    public void An_object_its_create_wakes_gets_no_wake_zone_message()
    {
        const uint id = 0x4000;
        var projectile = new ProjectileObject("Metadata/Projectiles/Fireball", 40, 30);
        Assert.Equal(InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(projectile.TypePath), projectile.Blob(),
            headerU32: 0xffffffff), Game.CreateProjectileObject(id, projectile));

        var effect = new EffectObject(40, 30, "a.ao");
        Assert.Equal([.. InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(EffectObject.TypePath), effect.Blob()),
            .. InstanceProtocol.EnterWakeZone(id)], Game.CreateEffectObject(id, effect));
    }

    [Fact]
    public void The_client_picks_its_mode_and_its_round_trip_only_aims()
    {
        // No server setting: a connection becomes lockstep only when its client shows it is, and at any round trip.
        var conn = new ClientState(new(1, "mode", 300, 300, 100, 100, null, []), 1, 1);
        Assert.False(conn.LockstepClient);
        // A resync (a stun's, a freeze's, a movement skill's) puts a predictive client's monsters on the server's cells.
        var near = new SpawnedMonster("Drowned", new PlayerWalk(110, 100, 8), aggroRange: 60, stopRange: 2, maxLife: 23);
        near.Walk.Order(100, 100, now: 0);
        conn.Monsters[0x3000] = near;
        lock (conn.Monsters)
            Assert.Equal(InstanceProtocol.Teleport(0x3000, 110, 100, 0x7fffffff), Game.ResyncPacketLocked(conn, 0));
        conn.NoteLockstepClient("test");
        conn.NoteLockstepClient("test");
        Assert.True(conn.LockstepClient);
        // A lockstep client skips GGG's resync message, and a teleport would cost its walkers their step's fraction.
        lock (conn.Monsters) Assert.Empty(Game.ResyncPacketLocked(conn, 0));
        conn.Monsters.Clear();

        var slow = new LockstepLatency();
        Assert.True(double.IsNaN(slow.Lowest));
        for (int i = 0; i < LockstepLatency.Enough - 1; i++) slow.Add(300);
        Assert.False(slow.Settled);
        slow.Add(250);
        Assert.True(slow.Settled);
        Assert.Equal(250, slow.Lowest);
        for (int i = 0; i < LockstepLatency.Window; i++) slow.Add(LockstepLatency.Lost);
        Assert.Equal(LockstepLatency.Lost, slow.Lowest);

        // A lockstep aim update is also activity for its running channel, as a predictive channel update is.
        conn.ChannelSkill = 0x0200;
        conn.ChannelSkillSequence = 0x8001;
        conn.LockstepSkill.Press((8, 8), null, 0x0200, 0x0401, repeats: false);
        conn.LockstepSkill.Channelling(0x8001);
        conn.LastChannelUpdateAt = -10;
        Game.OnLockstepRequest("test", conn, 0x192, Convert.FromHexString("01920000001400000005"));
        Assert.True(conn.LastChannelUpdateAt >= 0);
        Assert.Equal((120, 105), conn.LockstepSkill.Aim);
        Game.OnLockstepRequest("test", conn, 0x18e, Convert.FromHexString("018E00000014000000002909040000"));
        Assert.Equal((ushort)0x8001, conn.LockstepSkill.ChannelReleased());
        Assert.Null(conn.LockstepSkill.ChannelReleased());
        // A new Move may cancel at either end of a spell; a held repeat still waits for the full cast.
        conn.LockstepBusyUntil = 10.65;
        conn.LockstepCastWindow = new(10, .65, .17, .46);
        Assert.False(Game.LockstepPlayerFree(conn, 9.99));
        Assert.True(Game.LockstepPlayerFree(conn, 10.1));
        Assert.False(Game.LockstepPlayerFree(conn, 10.3));
        Assert.True(Game.LockstepPlayerFree(conn, 10.5));
        conn.LockstepHold.Clear();
        Assert.False(Game.LockstepPlayerFree(conn, 10.5));
        Assert.True(Game.LockstepPlayerFree(conn, 10.65));
    }

    [Fact]
    public void A_held_walk_follows_its_newest_target_and_tries_again_when_stopped()
    {
        byte[] requests = Convert.FromHexString("018F000001F10266040000" + "01960266");
        var framed = InstanceProtocol.SplitClientMessages(requests, out _, out var unknown);
        Assert.Equal(new ushort[] { 0x018f, 0x0196 }, framed.Select(m => m.Id));
        Assert.Empty(unknown);
        Assert.All(framed, m => Assert.True(InstanceProtocol.IsLockstepRequest(m.Id)));
        Assert.Equal(((uint)0x1f1, (ushort)0x266, (ushort)0x400, (byte)0), InstanceProtocol.ReadLockstepUseOnObject(framed[0].Bytes));

        var hold = new LockstepHold();
        var (here, walking) = ((0, 0), true);
        Assert.Null(hold.Due(walking, here));

        hold.Press((10, 0), 0x2909, 0x0400);
        Assert.Equal((10, 0), hold.Due(walking, here));
        Assert.Null(hold.Due(walking, here));

        hold.Update((10, 0));
        Assert.Null(hold.Due(walking, here));
        hold.Update((12, 0));
        Assert.Equal((12, 0), hold.Due(walking, here));

        // Stopped short while held: again every frame. Arrived: nothing.
        Assert.Equal((12, 0), hold.Due(false, (4, 0)));
        Assert.Equal((12, 0), hold.Due(false, (4, 0)));
        Assert.Null(hold.Due(false, (12, 0)));

        // A release does not cancel a target not yet started, and ends the retries.
        hold.Update((20, 0));
        hold.Release();
        Assert.Equal((20, 0), hold.Due(true, (4, 0)));
        Assert.Null(hold.Due(false, (4, 0)));

        hold.Press((30, 0), 0x2909, 0x0400);
        Assert.Equal((30, 0), hold.Due(true, here));
        hold.Release();
        hold.Update((31, 0));
        Assert.Null(hold.Due(false, here));

        // An object use takes over from a held walk.
        hold.Press((40, 0), 0x2909, 0x0400);
        hold.Clear();
        Assert.Null(hold.Due(false, here));

        // A skill waits for a free player, repeats while held and goes on at the ground when its target dies.
        var skill = new LockstepSkillHold();
        skill.Press((5, 5), 0x3007, 0x0102, 0x0401, repeats: true);
        Assert.False(skill.Due(free: false));
        Assert.True(skill.Due(free: true));
        Assert.True(skill.Due(free: true));
        skill.LoseTarget();
        skill.Update((6, 6));
        Assert.Null(skill.Target);
        Assert.Equal((6, 6), skill.Aim);
        skill.Release();
        Assert.False(skill.Due(free: true));

        // A click during a swing is kept through its own release.
        skill.Press((7, 7), null, 0x0102, 0x0001, repeats: true);
        skill.Release();
        Assert.False(skill.Due(free: false));
        Assert.True(skill.Due(free: true));

        // During an attack's open walk-up the held attack's next use waits for its swing, and a new click does not.
        skill.Press((9, 9), 0x3007, 0x0102, 0x0401, repeats: true);
        Assert.True(skill.Due(free: true, repeat: false));
        Assert.False(skill.Due(free: true, repeat: false));
        Assert.True(skill.Due(free: true));
        skill.Press((10, 10), null, 0x0200, 0x0401, repeats: true);
        Assert.True(skill.Due(free: true, repeat: false));
        skill.Clear();

        // A channel starts once and ends on the release.
        skill.Press((8, 8), null, 0x0200, 0x0401, repeats: false);
        Assert.True(skill.Due(free: true));
        skill.Channelling(0x8001);
        Assert.False(skill.Due(free: true));
        Assert.Null(skill.ChannelReleased());
        skill.Release();
        Assert.Equal((ushort)0x8001, skill.ChannelReleased());
        Assert.Equal("01AE00001000000000000000800101", Convert.ToHexString(InstanceProtocol.ChannelFinished(0x1000, 0x8001)));

        // A replacement skill must finish the old channel before its own start can replace the native action.
        skill.Press((8, 8), null, 0x0200, 0x0401, repeats: false);
        skill.Channelling(0x8002);
        skill.Press((9, 9), null, 0x0201, 0x0401, repeats: true);
        Assert.Equal((ushort)0x8002, skill.ChannelReleased());
        Assert.Null(skill.ChannelReleased());
        Assert.True(skill.Due(free: true));
    }
}
