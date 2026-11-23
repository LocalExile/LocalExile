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
        var player = new Player(new(1, "instant", 300, 300, 100, 100, null, []), 1, 1)
            { Connection = { LockstepClient = true }, LockstepSequence = 0x575, ActionCount = 17, LockstepBusyUntil = 12.5 };
        // The retained ground and object forms carry 0x0101 and 0x0121; their sequence joins ordinary actions.
        Assert.Equal($"01CE{player.Id:X8}00000000000084098576010181F78223",
            Convert.ToHexString(Game.PlayerInstantAction(player, 0x8409, (503, 547))));
        Assert.Equal($"01CE{player.Id:X8}0000000000008409857701210000300181F78223",
            Convert.ToHexString(Game.PlayerInstantAction(player, 0x8409, (503, 547), 0x3001)));
        Assert.Equal(0x577, player.LockstepSequence);
        Assert.Equal(17, player.ActionCount); // An instant skill does not cancel an existing cast or channel.
        Assert.Equal(12.5, player.LockstepBusyUntil);
        player.LockstepSequence = 0x7fff;
        Assert.Equal("80000101", Convert.ToHexString(Game.PlayerInstantAction(player, 0x8409, (503, 547))[14..18]));
        player.Connection.LockstepClient = false;
        Assert.Equal($"01CE{player.Id:X8}0000000000008409FFFF00200000300181F78223",
            Convert.ToHexString(Game.PlayerInstantAction(player, 0x8409, (503, 547), 0x3001)));
        Assert.Equal(0, player.LockstepSequence);
        foreach (bool lockstep in new[] { false, true })
        {
            player.Connection.LockstepClient = lockstep;
            player.PlayerLife = 100;
            var state = new SingleUseCooldownState(0, 1);
            Assert.True(state.TryUse(0, new SingleUseCooldown(3000, 0)));
            var use = new CooldownUseTicket(1, player.ActionCount, player.ProjectileEpoch, Instant: true,
                State: state, UseVersion: state.UseVersion);
            Assert.True(use.CommitNativeUse(player, 0x8409));
            // In lockstep the queued instant action must consume its still-ready native use itself.
            Assert.Equal(!lockstep, state.NeedsNativeRefresh);
            state.NativeRefreshed();
            state.Refresh(1, 2);
            Assert.True(state.NeedsNativeRefresh); // Later recovery-rate corrections still synchronize.
        }
    }

    [Fact]
    public async Task A_player_attack_walks_up_to_the_weapon_range_less_one_and_swings_by_the_client_s_goal_test()
    {
        // A Drowned (size 2, the tolerance) 13 cells off and a sword of range 11: the client's MoveTo has max range 10, so
        // the player is in reach only 12 cells off, one step on (the whole range would have it swing where it stands).
        var player = new Player(new(1, "walk-up", 300, 300, 100, 100, null, []), 1, 1);
        var drowned = new SpawnedMonster("Drowned", new PlayerWalk(113, 100, 8, size: 2), 0, 4, 100);
        var sword = new SkillCatalog.Unarmed(645, 1, 2, 11);
        var (here, stop, _, atOnce) = Game.WalkUpToSwing(player, drowned, (113, 100), sword, 0, 1);
        Assert.Equal(((100, 100), (101, 100), false), (here, stop, atOnce));

        // A predictive walk-up retries every 50 ms (0x141e95400, 0x141e957f0). Even after a late wake
        // beyond three seconds, an out-of-reach target does not start a swing or end the pursuit.
        var predictive = new Player(new(1, "predictive-follow", 300, 300, 100, 100, null, []), 1, 1);
        predictive.Clock.Reset();
        var previousLogic = Program.GameLogicOrNull;
        var previousLifetime = Game.CombatLifetime;
        var lifetime = new ModuleLifetime("Game");
        Game.CombatLifetime = lifetime;
        Program.SetGameLogic(new GameServer.Modules.Game.GameLogic());
        var session = new Game.ProjectileSession(predictive);
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
        Player Lockstep(SpawnedMonster target)
        {
            var c = new Player(new(1, "walk-up", 300, 300, 100, 100, null, []), 1, 1)
            {
                // Below the monsters' ids, so it ticks first in a frame (the client ticks objects in ascending id).
                Id = 0x1000,
                Pools = new(1, 1000, 100, 2, 0),
                PlayerLife = 500,
                Connection = { PlayerSent = true, LockstepClient = true, LockstepFrame = (0, .033) }
            };
            c.Area.Monsters[0x3000] = target;
            return c;
        }
        var use = new InstanceTransfer.UseSkillOnObject(0x3000, SkillCatalog.FirstSkillId, 0x8001, 0x0400);
        double hold = .5;   // the swing's hold (LockstepAttackCutIn and the tail), read as the swing begins
        LockstepAttack Attack(Player c, SpawnedMonster target)
        {
            var attack = new LockstepAttack(++c.ActionCount, 0x3000, target, 10, 2, 1, hold: () => hold);
            c.LockstepAttack = attack;
            return attack;
        }
        byte[]? Gate(Player c, LockstepAttack attack, double start)
        {
            c.Connection.LockstepFrame = (start, start + .033);
            return Game.LockstepAttackAtGate(c, attack, use);
        }
        void Replay(Player c, double to)   // the frames up to `to`, the start of the frame then being sent
        {
            c.Connection.LockstepFrame = (to, to + .033);
            Game.StepWalkers(c, to);
        }
        byte[] Start(int x, int y, int toX, int toY) =>
            InstanceProtocol.ActionStarted(0x1000, x, y, toX, toY, use.SkillId, use.Sequence, use.Flags, targetObjectId: 0x3000);

        // Walking away from one 12 cells behind: in goal where the order applies, so the swing begins in that frame, from
        // where the frame's steps leave him, 13 cells off, with no wait and no walk back.
        drowned = new SpawnedMonster("Drowned", new PlayerWalk(89, 100, 8, size: 2), 0, 4, 100);
        player = Lockstep(drowned);
        player.Walk.Order(200, 100, 0);
        var attack = Attack(player, drowned);
        Assert.Equal(Start(101, 100, 89, 100), Gate(player, attack, .033));
        Assert.Equal(new LockstepAttack.Swing(.033, .066, (102, 100)), await attack.Decided);
        Assert.Equal(.566, player.LockstepBusyUntil, 9);
        Assert.Equal(((102, 100), .033, .033), await Game.FollowMonsterToSwing(player, drowned, sword, () => true,
            (_, _) => Task.FromResult(false), (0, 0), 0, false, .04, lockstep: attack));

        // Asked with flag 0x0001 (attack in place) of one 41 cells behind him: no walk-up. The swing begins in the order's
        // frame all the same, from where the frame's steps leave him, and he stands there through it.
        drowned = new SpawnedMonster("Drowned", new PlayerWalk(60, 100, 8, size: 2), 0, 4, 100);
        player = Lockstep(drowned);
        player.Walk.Order(200, 100, 0);
        attack = new(++player.ActionCount, 0x3000, drowned, 10, 2, 1, hold: () => hold) { InPlace = true };
        player.LockstepAttack = attack;
        Assert.Equal(Start(101, 100, 60, 100), Gate(player, attack, .033));
        Assert.False(attack.Open);
        Assert.Equal(new LockstepAttack.Swing(.033, .066, (102, 100)), await attack.Decided);
        Replay(player, .066);
        Replay(player, .099);
        Assert.False(player.Walk.Moving(.099));
        Assert.Equal((102, 100), player.Walk.Position(.099));

        // Asked in the frame from 0.033, its 0x1ab goes out a frame later (a frame end overtook it at the send gate):
        // the walk-up is placed at that frame's start, from the cell the player's steps have reached by then, and its
        // search timer starts there. The target then moves off: out of reach at the path's end, the walk-up searches
        // again in that frame's Positioned tick, so the timer counts from the frame's start.
        drowned = new SpawnedMonster("Drowned", new PlayerWalk(80, 100, 8, size: 2), 0, 4, 100);
        player = Lockstep(drowned);
        player.Walk.Order(200, 100, 0);
        player.Connection.LockstepFrame = (.033, .066);
        using (var listener = new TcpListener(IPAddress.Loopback, 0))
        {
            listener.Start();
            using var writer = new TcpClient();
            var accepting = listener.AcceptTcpClientAsync();
            await writer.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
            using var reader = await accepting;
            var ciphers = new ConnectionCiphers(new byte[64], isClient: false);
            player.Connection.Session = new("lockstep-test", writer.GetStream(), ciphers);
            var gate = Program.SendGates.GetValue(ciphers, _ => new(1, 1));
            Assert.True(gate.Wait(0));   // the frame end holds the send gate
            var starting = Game.LockstepAttackStart(player, use, drowned, sword,
                1, ++player.ActionCount, "attack");
            Assert.False(starting.IsCompleted);
            player.Connection.LockstepFrame = (.066, .099);
            gate.Release();
            attack = (await starting)!;
            var sent = Start(102, 100, 80, 100);
            var wire = new byte[sent.Length];
            await reader.GetStream().ReadExactlyAsync(wire);
            new ConnectionCiphers(new byte[64], isClient: true).Receive.Process(wire);
            Assert.Equal(sent, wire);
        }
        Assert.True(attack.Open);
        Assert.Equal((92, 100), player.Walk.Destination);
        Assert.Equal(.066, attack.TimerFrom, 9);
        drowned.Walk.Teleport(60, 100, .07);
        Replay(player, .297);
        Assert.Equal((92, 100), player.Walk.Destination);
        Replay(player, .33);   // there at 0.324, 32 cells off
        Assert.Equal((72, 100), player.Walk.Destination);
        Assert.Equal(.297, attack.TimerFrom, 9);

        // Toward a Drowned walking at him (a step every 0.143 s, to (132, 100) at 0.571): the path goes to (124, 100) and is
        // not searched again. In the frame from 0.561 he is 13 cells from the monster's cell at that frame's start, though
        // 12 from the cell its step leaves it on; in the frame from 0.594 his step to (121, 100) is in goal mid-piece. The
        // swing begins in that frame, and he takes the next tick's two due steps, then stands, where his next order starts.
        drowned = new SpawnedMonster("Drowned", new PlayerWalk(136, 100, 7, size: 2), 0, 4, 100);
        drowned.Walk.Order(100, 100, 0);
        player = Lockstep(drowned);
        attack = Attack(player, drowned);
        Assert.Equal(Start(100, 100, 136, 100), Gate(player, attack, .033));
        Assert.Equal((124, 100), player.Walk.Destination);
        Replay(player, .594);
        Assert.False(attack.Decided.IsCompleted);
        Assert.Equal((124, 100), player.Walk.Destination);
        Assert.Equal((120, 100), player.Walk.Position(.594));
        hold = .6;   // slowed on the way (a chill): the swing holds him as long as the attack time it begins with
        Replay(player, .627);
        Assert.Equal(new LockstepAttack.Swing(.594, .627, (123, 100)), await attack.Decided);
        Assert.Equal(.627 + .6, player.LockstepBusyUntil, 9);
        Assert.Equal([(.6276, (122, 100)), (.6546, (123, 100))], player.Walk.Steps(.627, .66).Select(s => (Math.Round(s.At, 4), s.Cell)));
        Replay(player, .66);
        Replay(player, .693);
        Assert.Equal((123, 100), player.Walk.Position(.693));
        Assert.False(player.Walk.Moving(.693));
        Assert.Equal(((123, 100), .594, .594), await Game.FollowMonsterToSwing(player, drowned, sword, () => true,
            (_, _) => Task.FromResult(false), (0, 0), 0, false, .04, lockstep: attack));
        Assert.Equal((123, 100), Game.PlaceLockstepPlayerMove(player, (140, 130), .693));

        // A target that has left the cell searched toward is searched for again mid-path 2.0 s after the search, or 0.7 s
        // after it once 64 cells from that cell (0x141e826a0). Here it goes off his line, out of reach of it: 16 and 13
        // cells from (190, 100), the walk-up searches again at the end of the frame from 2.013, a step from a waypoint in
        // goal, where it stands at once; 64 and 13 cells away, at the end of the frame from 0.726.
        (Player Player, LockstepAttack Attack) Off(int x, int y)
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
        player = Lockstep(new SpawnedMonster("Drowned", new PlayerWalk(200, 200, 8, size: 2), 0, 4, 100));
        attack = new(++player.ActionCount, 0, null, 10, -1, 1, () => .5)
        {
            GroundTarget = (117, 91), GroundAttackSeconds = () => .645,
        };
        player.LockstepAttack = attack;
        Assert.Equal(((100, 100), (117, 91)), Game.PlaceLockstepAttack(player, attack, 0));
        Assert.True(attack.Open);
        Assert.Equal(0, player.LockstepBusyUntil);
        Replay(player, .33);
        Assert.Equal((108, 95), player.Walk.Position(.33));
        Assert.Equal((108, 95), (await attack.Decided)!.Value.From);
        Assert.True(player.LockstepBusyUntil > .33);
        Assert.True(player.NextAttackAt > .33);

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
        // No server setting: a connection is lockstep until its client sends a mode-only message, then takes that one's.
        Assert.True(new Player(new(1, "mode", 300, 300, 100, 100, null, []), 1, 1).Connection.LockstepClient);
        var player = new Player(new(1, "mode", 300, 300, 100, 100, null, []), 1, 1) { Connection = { LockstepClient = false } };
        Assert.False(player.Connection.LockstepClient);
        // A resync (a stun's, a freeze's, a movement skill's) puts a predictive client's monsters on the server's cells.
        var near = new SpawnedMonster("Drowned", new PlayerWalk(110, 100, 8), aggroRange: 60, stopRange: 2, maxLife: 23);
        near.Walk.Order(100, 100, now: 0);
        player.Area.Monsters[0x3000] = near;
        Assert.Equal(InstanceProtocol.Teleport(0x3000, 110, 100, 0x7fffffff), Game.ResyncPacketLocked(player, 0));
        Assert.Null(player.Connection.NotePacket("test", InstanceProtocol.LockstepMoveStartId));
        Assert.True(player.Connection.LockstepClient);
        // A lockstep client's hit and the life it leaves go into the attacker's own turn of the frame, keyed by the
        // attacker at GGG's checkpoints, so they never come before the attacker's order of that frame; a predictive
        // client applies a message on arrival and keeps the frame's start.
        Assert.Equal(default, Game.HitOrder(new Player(new(1, "mode", 300, 300, 100, 100, null, []), 1, 1) { Connection = { LockstepClient = false } }, 0x1000));
        var hit = Game.HitOrder(player, 0x1000);
        Assert.Equal((0x1000u, (ushort)0x7000, (ushort)0x7002), hit);
        Assert.Equal("01AF0000100000001000700080010000000030004201030000", Convert.ToHexString(
            InstanceProtocol.ActionHit(0x1000, 0x8001, 0x3000, flags: 0x4201, orderKey: hit.Key, checkpoint: hit.Hit)));
        Assert.Equal("01B1000030000000100070020000000500000000000000" + "00", Convert.ToHexString(
            InstanceProtocol.LifeUpdate(0x3000, 5, orderKey: hit.Key, checkpoint: hit.Life)));
        // A lockstep client skips GGG's resync message, and a teleport would cost its walkers their step's fraction.
        Assert.Empty(Game.ResyncPacketLocked(player, 0));
        player.Area.Monsters.Clear();

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
        player.ChannelSkill = 0x0200;
        player.ChannelSkillSequence = 0x8001;
        player.LockstepSkill.Press((8, 8), null, 0x0200, 0x0401, repeats: false);
        player.LockstepSkill.Channelling(0x8001);
        player.LastChannelUpdateAt = -10;
        Game.OnLockstepRequest(player, 0x192, Convert.FromHexString("01920000001400000005"));
        Assert.True(player.LastChannelUpdateAt >= 0);
        Assert.Equal((120, 105), player.LockstepSkill.Aim);
        Game.OnLockstepRequest(player, 0x18e, Convert.FromHexString("018E00000014000000002909040000"));
        Assert.Equal((ushort)0x8001, player.LockstepSkill.ChannelReleased());
        Assert.Null(player.LockstepSkill.ChannelReleased());
        // A new Move may cancel at either end of a spell; a held repeat still waits for the full cast.
        player.LockstepBusyUntil = 10.65;
        player.LockstepCastWindow = new(10, .65, .17, .46);
        Assert.False(Game.LockstepPlayerFree(player, 9.99));
        Assert.True(Game.LockstepPlayerFree(player, 10.1));
        Assert.False(Game.LockstepPlayerFree(player, 10.3));
        Assert.True(Game.LockstepPlayerFree(player, 10.5));
        player.LockstepHold.Clear();
        Assert.False(Game.LockstepPlayerFree(player, 10.5));
        Assert.True(Game.LockstepPlayerFree(player, 10.65));
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

    [Fact]
    public void Close_range_held_move_continues_smoothly_and_nimis_randomizes_attack_volleys()
    {
        // Close-range held Move (4 cells left) must project from here when LockstepClientCell lags behind or the
        // short walk is about to finish within 2 frames, and stop cleanly once released or held on feet.
        var player = new Player(new(1, "close-hold", 300, 300, 100, 100, null, []), 1, 1)
        {
            Connection = { LockstepClient = true }
        };
        player.LockstepHold.Press((96, 100), 0x2909, 0x0400, (-4, 0));
        Assert.Equal((96, 100), Game.LockstepMoveDue(player, 0));
        player.Walk.Order(96, 100, 0);
        // 66 ms in: walk finishes at ~108 ms (within 2 frames of 66 ms) -> seamlessly projects (-4, 0) from here.
        var extended = Game.LockstepMoveDue(player, 0.066);
        Assert.NotNull(extended);
        var here = player.Walk.Position(0.066);
        Assert.Equal((here.X - 4, here.Y), extended.Value);
        // Lagged 0x192 whose target (98, 100) lands within 2 cells of here still projects (-4, 0) from here.
        player.LockstepHold.Update((here.X - 1, here.Y), (-4, 0));
        Assert.Equal((here.X - 4, here.Y), Game.LockstepMoveDue(player, 0.066));
        // Cursor moved directly onto feet (< 3 cells) stops cleanly.
        player.LockstepHold.Update((here.X - 1, here.Y), (-1, 0));
        Assert.Null(Game.LockstepMoveDue(player, 0.066));
        // Releasing clears offset and stops cleanly.
        player.LockstepHold.Update((here.X - 4, here.Y), (-4, 0));
        player.LockstepHold.Release();
        Assert.Null(player.LockstepHold.Offset);
        Assert.Equal((here.X - 4, here.Y), Game.LockstepMoveDue(player, 0.066));
        Assert.Null(Game.LockstepMoveDue(player, 0.066));

        // Nimis (random_projectile_direction) randomizes non-ClientPredicted attack volleys using the client's
        // seeded angles, and IceShot is natively client-drawn so no duplicate server arrow is spawned.
        var baseProfile = new GameServer.Core.Projectiles.ProjectileProfile("ice-arrow")
        {
            Count = 5, Speed = 326, Range = 150, Lifetime = GameServer.Core.Projectiles.ProjectileRuntime.Time(2)
        };
        var randomized = Game.NativeVolley(baseProfile, key => key == Stat.RandomProjectileDirection ? 1 : 0,
            sequence: 7, aim: new(20, 0), emitter: 0x1000);
        Assert.NotNull(randomized.NativeVolley?.Angles);
        Assert.Equal(Game.RandomVolleyAngles(7, 5, 0x1000, new(20, 0)), randomized.NativeVolley!.Angles);
        Assert.True(Game.ClientDrawsPrimaryProjectile(predicted: false, "IceShot"));

        // 1:1 3.29.3c spawn forward offset (0x141ba7840 -> 0x141ba7c30): default 30 world units (~2.76 cells),
        // zero when projectiles_not_offset / trap / mine, override when set, contact offset only if stat 0x1230 != 0.
        var bowContact = new ContactPoint(0, -60f, -125f);
        Assert.Equal(30.0 / WorldUnits.PerCell, Game.NativeProjectileForwardCells(_ => 0, bowContact), 6);
        Assert.Equal(0, Game.NativeProjectileForwardCells(k => k == Stat.ProjectilesNotOffset ? 1 : 0, bowContact));
        Assert.Equal(0, Game.NativeProjectileForwardCells(k => k == Stat.IsTrapObject ? 1 : 0, bowContact));
        Assert.Equal(0, Game.NativeProjectileForwardCells(k => k == Stat.IsRemoteMine ? 1 : 0, bowContact));
        Assert.Equal(50.0 / WorldUnits.PerCell, Game.NativeProjectileForwardCells(
            k => k == Stat.ProjectileFiringForwardDistanceOverride ? 50 : 0, bowContact), 6);
        Assert.Equal(bowContact.ForwardCells, Game.NativeProjectileForwardCells(
            k => k == Stat.ProjectileUsesContactPosition ? 1 : 0, bowContact), 6);

        // 1:1 3.29.3c 3D sloped height ray (0x141bac460 / 0x141bb31a0 / 0x141bb5570 / 0x141e35a50):
        // Walkable rising slope (0 -> +20 over 10 cells) never blocks an uphill arrow; flat shot at cliff base blocks
        // when (int)projSteps < terrain (16 < 23); aiming at cliff top clears it; return preserves turnaround Z and slopes
        // by (terrain(anchor) - terrain(turnaround)) / dist.
        int? Ramp(int x, int _) => Math.Clamp(x * 2, 0, 20);
        var uphill = ProjectileAimHeight.Primary(new(0, 0), new(10, 0), launchSteps: 16, Ramp);
        Assert.NotNull(uphill);
        for (int x = 0; x <= 10; x++) Assert.False(uphill!.Value.Blocks(x, 0, Ramp));
        Assert.Equal(36, uphill!.Value.MarkerAt(new(10, 0)).Marker);

        int? Cliff(int x, int _) => x >= 10 ? 23 : 0;
        var flatAtBase = ProjectileAimHeight.Primary(new(0, 0), new(8, 0), launchSteps: 16, Cliff);
        Assert.True(flatAtBase!.Value.Blocks(10, 0, Cliff));
        var aimedAtTop = ProjectileAimHeight.Primary(new(0, 0), new(10, 0), launchSteps: 16, Cliff);
        Assert.False(aimedAtTop!.Value.Blocks(10, 0, Cliff));

        int? OceanAndBeach(int x, int _) => x >= 20 ? -15 : 0;
        var returning = ProjectileAimHeight.Return(new(20, 0), new(0, 0), currentSteps: 10, OceanAndBeach);
        Assert.NotNull(returning);
        Assert.Equal(25, returning!.Value.MarkerAt(new(0, 0)).Marker);
    }

    [Fact]
    public void Shield_charge_lockstep_distance_threshold_speed_stats_and_travel_hold()
    {
        // Forward raycast stops before a wall at x=115 (15 cells away < 24 -> no-charge, stands in place).
        var walled = new PlayerWalk(100, 100, 37, (x, y) => x < 115);
        walled.Charge(160, 100, 75, 0, extraDistance: 0, minDistance: Game.ShieldChargeMinDistance, out bool wallCharged);
        Assert.False(wallCharged);
        Assert.Equal((100, 100), walled.Position(1));

        // Open floor < 24 cells (23 cells, dx²+dy² = 529 < 576): no-charge. At 24 cells (576): charges.
        var open = new PlayerWalk(100, 100, 37);
        open.Charge(123, 100, 75, 0, extraDistance: 0, minDistance: Game.ShieldChargeMinDistance, out bool shortCharged);
        Assert.False(shortCharged);
        Assert.Equal((100, 100), open.Position(1));
        open.Charge(124, 100, 75, 0, extraDistance: 0, minDistance: Game.ShieldChargeMinDistance, out bool exactCharged);
        Assert.True(exactCharged);
        Assert.Equal((124, 100), open.Destination);

        // Equipped character with a sword, shield, Shield Charge gem, and increased attack speed:
        // verify published off-hand speed stats (0x127 / 0x211e / 0x531e), travel speed multiplier, dynamic
        // LockstepPlayerFree hold through travel, and immediate release + bonus cleanup when stopped early.
        using var fixture = new TriggeredSummonTests.Fixture();
        var player = fixture.Player;
        player.Connection.PlayerSent = true;
        player.Connection.LockstepClient = true;
        player.Connection.LockstepFrame = (1.0, 1.033);
        player.Walk.Teleport(100, 100, 1.0);
        var items = Game.ItemsOf(player);
        items.Change(PlayerInventories.Weapon1, items.Items(PlayerInventories.Weapon1).Single().Id,
            _ => ItemCatalog.Create(Program.Items.Find("Driftwood Club")!));
        items.Add(PlayerInventories.Offhand1, 0, 0, ItemCatalog.Create(Program.Items.Find("Splintered Tower Shield")!));
        ushort chargeId = Assert.Single(fixture.Equip(("Shield Charge", 1)));
        player.Pools = Game.PoolsFor(player, 20) with { AttackSpeedPct = 50 };

        var scGem = Game.GemSkillEffects(player).Single(g => g.Id == chargeId);
        var scSkill = Game.PlayerSkills(player).Single(s => s.Id == chargeId);
        var scStats = scSkill.Stats!.ToDictionary(s => s.Item1, s => s.Item2);
        Assert.True(scStats.TryGetValue(Stat.OffHandAttackSpeedPlusPct, out int offFinal));
        Assert.True(scStats.TryGetValue(Stat.CombinedOffHandAttackSpeedPlusPct, out int offInc));
        Assert.True(scStats.ContainsKey(Stat.CombinedOffHandAttackSpeedPlusPctFinal));
        Assert.True(offInc >= 50);
        Assert.Equal(SkillAttackTiming.FinalSpeedPercent(offInc, 1.0), offFinal);

        int bonusPct = Game.ShieldChargeBonusPct(player, chargeId, scGem.Effect);
        Assert.True(bonusPct > 0);
        Assert.Equal(0, Game.LockstepActionHold(player, chargeId, 0x8001));

        int action = ++player.ActionCount;
        double baseSpeed = player.Walk.Speed;
        Game.BeginShieldCharge(player, chargeId, bonusPct, 175, 100, 1.0, action, out bool startedCharge);
        Assert.True(startedCharge);
        Assert.True(player.Walk.Speed > baseSpeed * 1.5, $"Expected charge speed > {baseSpeed * 1.5:0.0} (with +90% move and off-hand attack speed), got {player.Walk.Speed:0.0}");
        Assert.False(Game.LockstepPlayerFree(player, 1.033));

        // Early stop on a wall/body immediately ends the travel hold and clears the +90% movement bonus.
        player.Walk.StopAt(1.12);
        Assert.True(Game.LockstepPlayerFree(player, 1.132));
        Assert.Equal(0, Game.ShieldChargeMovementPct(player));
        Assert.Equal(1.0, Game.MovementSpeedMultiplier(player));
    }

    [Fact]
    public async Task Lockstep_leap_slam_hold_published_speed_sync_and_no_silent_collision_teleport()
    {
        // Leap Slam in Lockstep holds until startup-skipped landing (timing.Land), not unskipped AttackCutIn.
        using var fixture = new TriggeredSummonTests.Fixture();
        var player = fixture.Player;
        player.Connection.PlayerSent = true;
        player.Connection.LockstepClient = true;
        player.Connection.LockstepFrame = (2.0, 2.033);
        player.Walk.Teleport(100, 100, 2.0);
        var items = Game.ItemsOf(player);
        items.Change(PlayerInventories.Weapon1, items.Items(PlayerInventories.Weapon1).Single().Id,
            _ => ItemCatalog.Create(Program.Items.Find("Driftwood Club")!));
        ushort leapId = Assert.Single(fixture.Equip(("Leap Slam", 1)));
        var leapGem = Game.GemSkillEffects(player).Single(g => g.Id == leapId);

        double attackSeconds = Game.SupportedAttackSeconds(player, leapId);
        double expectedLand = Game.LeapSlamTiming(player, leapId, leapGem.Effect, attackSeconds).Land;
        double unskippedCutIn = Game.LockstepAttackCutIn(player, leapId, 0x8001);
        double lockstepHold = Game.LockstepActionHold(player, leapId, 0x8001);
        Assert.Equal(expectedLand, lockstepHold, 6);
        Assert.True(lockstepHold < unskippedCutIn - 0.15, $"Expected startup-skipped hold ({lockstepHold:0.000}s) well below unskipped cut-in ({unskippedCutIn:0.000}s).");

        // PushOutOfCollision(tell: false) must not silently teleport a lockstep player off its cell.
        player.SetBlocker(0x5000, 100, 100, size: 2);
        await Game.PushOutOfCollision(player, living: true, tell: false);
        Assert.Equal((100, 100), player.Walk.Position(2.0));

        // AreaStats tracks published movement speed for lockstep clients.
        player.Area.Stats.Created(player.Id, [(Stat.MovementVelocityPlusPct, 0)]);
        Assert.True(player.Area.Stats.HasWritten(player.Id));
        Assert.Equal(0, player.Area.Stats.WrittenValue(player.Id, Stat.MovementVelocityPlusPct));
        player.Area.Stats.Set(player.Id, Stat.MovementVelocityPlusPct, 40);
        // Before Publish(), WrittenValue still reflects what the client currently holds (0%).
        Assert.Equal(0, player.Area.Stats.WrittenValue(player.Id, Stat.MovementVelocityPlusPct));
        player.Area.Stats.Publish();
        Assert.Equal(40, player.Area.Stats.WrittenValue(player.Id, Stat.MovementVelocityPlusPct));
    }
}
