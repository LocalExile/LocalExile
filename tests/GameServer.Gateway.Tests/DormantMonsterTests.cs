using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;
using GameServer.Modding;
using Xunit;
using MonsterRarity = GameServer.Core.Protocol.Instance.MonsterRarity;

namespace GameServer.Gateway.Tests;

public class DormantMonsterTests
{
    [Fact]
    public void Untargetable_monsters_keep_their_native_blocking_bodies()
    {
        var conn = new ClientState(new(1, "dormant-body", 300, 300, 100, 100, null, []), 1, 1)
        {
            LockstepClient = true, LockstepFrame = (0, .033),
        };
        static SpawnedMonster Body(int x, int y) => new("buried", new PlayerWalk(x, y, 8), 60, 2, 23);
        var first = Body(53, 50);
        var second = Body(52, 47);
        lock (conn.Monsters)
        {
            MonsterEmerge.AddLocked(conn, 0x303f, first, EmergeKind.Buried, unrisen: true);
            MonsterEmerge.AddLocked(conn, 0x3040, second, EmergeKind.Buried, unrisen: true);
            Assert.Empty(conn.Monsters);
            Assert.Equal(2, MonsterEmerge.Buried(conn).Count);
            Assert.Contains(new ClientPathfinder.Blocker(53, 50, 2), conn.Blockers);
            Assert.Contains(new ClientPathfinder.Blocker(52, 47, 2), conn.Blockers);

            // Native 141e61540 blocks on these untargetable neighbours.
            // Translate its exact native six-waypoint route; an empty-body control is straight.
            static List<(int X, int Y)> Path(IReadOnlyList<ClientPathfinder.Blocker> bodies) =>
                ClientPathfinder.FindPath((50, 50), (50, 39), (_, _) => true, 300, 300, bodies,
                    moverSize: 2, limit: ClientPathfinder.SearchLimit.Expansions100);
            Assert.Equal([(50, 50), (50, 39)], Path([]));
            conn.Monsters[0x3041] = Body(50, 50);
            using (var frame = conn.BeginMonsterBlockerFrame(0))
            {
                Assert.Equal([(50, 50), (49, 51), (48, 52), (47, 51), (47, 43), (50, 39)], Path(frame.For(0x3041)));
                Assert.DoesNotContain(new ClientPathfinder.Blocker(50, 50, 2), frame.For(0x3041));
            }

            var catalog = new MonsterCatalog();
            var drowned = Assert.IsType<MonsterCatalog.Variety>(catalog.Find("Metadata/Monsters/ZombieDismember/ZombieDrownedStrandWake1"));
            var ghost = Assert.IsType<MonsterCatalog.Variety>(catalog.Find("Metadata/Monsters/Skeletons/SkeletonBossFairgraves"));
            Assert.True(drowned.PositionedBlocking);
            // The resolved SkeletonBossEmerge type has Positioned.blocking = "false" in object-types.json.
            Assert.False(ghost.PositionedBlocking);
            var nonblocking = new SpawnedMonster("nonblocking", new PlayerWalk(60, 50, 8), 60, 2, 23) { Variety = ghost };
            MonsterEmerge.AddLocked(conn, 0x3042, nonblocking, EmergeKind.Buried, unrisen: true);
            Assert.DoesNotContain(new ClientPathfinder.Blocker(60, 50, 2), conn.Blockers);
            using (var frame = conn.BeginMonsterBlockerFrame(0))
                Assert.DoesNotContain(frame.Bodies, b => b.Id == 0x3042);

            // Phasing removes living bodies only from the player's collision view; terrain objects remain,
            // and a different monster still sees the dormant bodies but no longer sees the phased player.
            conn.SetBlocker(0x2000, 70, 70, 1);
            conn.PlayerSent = true;
            conn.PlayerLife = 23;
            conn.Walk.Teleport(100, 100, 0);
            using (var frame = conn.BeginMonsterBlockerFrame(0))
                Assert.Contains(new ClientPathfinder.Blocker(100, 100, LivingCollision.PlayerSize), frame.For(0x3041));
            conn.Phasing = true;
            Assert.Equal(new[] { new ClientPathfinder.Blocker(70, 70, 1) }, conn.Blockers);
            using (var frame = conn.BeginMonsterBlockerFrame(0))
            {
                Assert.Equal(new[] { new ClientPathfinder.Blocker(70, 70, 1) }, frame.ForPlayer);
                Assert.Contains(new ClientPathfinder.Blocker(53, 50, 2), frame.For(0x3041));
                Assert.DoesNotContain(new ClientPathfinder.Blocker(100, 100, LivingCollision.PlayerSize), frame.For(0x3041));
            }
            conn.Phasing = false;
            conn.PlayerSent = false;
            conn.RemoveBlocker(0x2000);

            var emerge = new MonsterEmerge();
            var risen = emerge.RiseLocked(conn, (50, 50), 1, out bool watching);
            Assert.Equal(new uint[] { 0x303f, 0x3040, 0x3042 }, risen.Select(r => r.Id));
            Assert.False(watching);
            Assert.Empty(MonsterEmerge.Buried(conn));
            Assert.Empty(MonsterEmerge.Unrisen(conn));
            Assert.Same(first, conn.Monsters[0x303f]);
            Assert.Same(second, conn.Monsters[0x3040]);
            Assert.Same(nonblocking, conn.Monsters[0x3042]);
            using (var frame = conn.BeginMonsterBlockerFrame(1))
                Assert.Equal(new uint[] { 0x303f, 0x3040, 0x3041 }, frame.Bodies.Select(b => b.Id));
            Assert.Empty(emerge.RiseLocked(conn, (50, 50), 2, out watching));
            Assert.False(watching);
            Assert.Equal(4, conn.Monsters.Count);

            // Emerge ends with its clip in both modes (141e933b0, 141e934b0); there is no extra idle tail.
            var tide = Assert.IsType<MonsterCatalog.Variety>(catalog.Find("Metadata/Monsters/WaterElemental/WaterElementalTidal"));
            foreach (bool lockstep in new[] { false, true })
            {
                var rising = new ClientState(new(1, "emerge-duration", 300, 300, 100, 100, null, []), 1, 1)
                {
                    LockstepClient = lockstep,
                };
                var strider = new SpawnedMonster("Tide Strider", new PlayerWalk(50, 50, 25), 60, 2, 23) { Variety = tide };
                MonsterEmerge.AddLocked(rising, 0x3000, strider, EmergeKind.Lurking, unrisen: true);
                var rise = Assert.Single(emerge.RiseLocked(rising, (60, 50), 1, out _));
                Assert.Equal(.866, rise.Seconds);
                Assert.Equal(1.866, strider.BusyUntil);
                Assert.False(MonsterAiCadence.Due(1.865, strider.BusyUntil));
                Assert.True(MonsterAiCadence.Due(1.866, strider.BusyUntil));
            }

            first.Life = 0;
            Assert.True(conn.Monsters.Remove(0x3040));
            Assert.DoesNotContain(new ClientPathfinder.Blocker(53, 50, 2), conn.Blockers);
            Assert.DoesNotContain(new ClientPathfinder.Blocker(52, 47, 2), conn.Blockers);
            using (var frame = conn.BeginMonsterBlockerFrame(2))
                Assert.Equal(new uint[] { 0x3041 }, frame.Bodies.Select(b => b.Id));

            // Life and Positioned's blocking lifetime differ: a post-kill path still sees the death cell until
            // the clear is sent. A replacement at the same ID must not inherit the old corpse's body.
            conn.RetainCorpseBody(0x303f, first, 2);
            Assert.Contains(new ClientPathfinder.Blocker(53, 50, 2), conn.Blockers);
            Assert.False(LivingCollision.StepsWithBodies(first));
            using (var frame = conn.BeginMonsterBlockerFrame(2))
                Assert.Contains(new ClientPathfinder.Blocker(53, 50, 2), frame.ForPlayer);
            Assert.Null(conn.MonsterBody(0x303f, new("replacement", new(80, 80, 8), 60, 2, 0), 2));
            conn.ReleaseCorpseBody(0x303f, first);
            Assert.DoesNotContain(new ClientPathfinder.Blocker(53, 50, 2), conn.Blockers);
            conn.LockstepClient = false;
            conn.RetainCorpseBody(0x303f, first, 2);
            Assert.DoesNotContain(new ClientPathfinder.Blocker(53, 50, 2), conn.Blockers);
            conn.LockstepClient = true;

            // A nonblocking mover must still take collision-checked movement turns. Without StepWalkers admitting
            // it, Position would advance analytically through the dormant neighbour instead of stopping at 0.1 s.
            foreach (bool lockstep in new[] { false, true })
            {
                var moving = new ClientState(new(1, "nonblocking-step", 300, 300, 100, 100, null, []), 1, 1)
                {
                    LockstepClient = lockstep, LockstepFrame = (0.132, 0.165),
                };
                var mover = new SpawnedMonster("nonblocking", new PlayerWalk(100, 100, 10, size: 2), 60, 2, 23) { Variety = ghost };
                mover.Walk.Order(100, 120, now: 0);
                lock (moving.Monsters)
                {
                    moving.Monsters[0x3000] = mover;
                    MonsterEmerge.AddLocked(moving, 0x3001, Body(100, 105), EmergeKind.Buried, unrisen: true);
                    Game.StepWalkers(moving, now: 0.132);
                    Assert.Equal((100, 100), mover.Walk.Position(0.132));
                    Assert.False(mover.Walk.Moving(0.132));
                    Assert.True(MonsterEmerge.Buried(moving).Remove(0x3001));
                    mover.Walk.Order(100, 120, now: 0.132);
                    moving.LockstepFrame = (0.264, 0.297);
                    Game.StepWalkers(moving, now: 0.264);
                    Assert.Equal((100, 101), mover.Walk.Position(0.264));
                    Assert.True(mover.Walk.Moving(0.264));
                    using var frame = moving.BeginMonsterBlockerFrame(0.264);
                    Assert.Empty(frame.Bodies);
                }
            }
        }

        // Both connections retain the same dormant roster and watch when the instance runner leaves.
        var instance = new InstanceState();
        var firstClient = new ClientState(new(1, "shared-dormant", 300, 300, 100, 100, null, []), 1, 1)
        {
            Instance = instance, PlayerSent = true, PlayerLife = 100,
        };
        var secondClient = new ClientState(new(1, "shared-dormant", 300, 300, 100, 100, null, []), 1, 1)
        {
            Instance = instance,
        };
        instance.PublishPlayer(new(firstClient.PeerId, 7, new("Peer", 1, 80, 80, 100, 100),
            80, 80, 100, firstClient));
        var sharedBody = Body(53, 50);
        lock (firstClient.Monsters)
        {
            Assert.Same(firstClient.Monsters, secondClient.Monsters);
            Assert.Same(firstClient.DormantMonsters, secondClient.DormantMonsters);
            MonsterEmerge.AddLocked(firstClient, 0x303f, sharedBody, EmergeKind.Buried, unrisen: true);
            Assert.Same(sharedBody, Assert.Single(MonsterEmerge.Buried(secondClient)).Value);
            using (var frame = secondClient.BeginMonsterBlockerFrame(0))
                Assert.DoesNotContain(new ClientPathfinder.Blocker(53, 50, 2), frame.ForPlayer);
            secondClient.MonsterViews[0x303f] = new((53, 50), (53, 50), sharedBody.Life);
            using (var frame = secondClient.BeginMonsterBlockerFrame(0))
            {
                Assert.Contains(new ClientPathfinder.Blocker(53, 50, 2), frame.ForPlayer);
                Assert.Contains(new ClientPathfinder.Blocker(80, 80, LivingCollision.PlayerSize), frame.ForPlayer);
            }
            Assert.True(MonsterEmerge.Unwatched(secondClient));
            Assert.True(MonsterEmerge.StartWatch(firstClient));
            Assert.False(MonsterEmerge.Unwatched(secondClient));
            firstClient.Closed = true;
            MonsterEmerge.StopWatch(firstClient);
            Assert.True(MonsterEmerge.Unwatched(secondClient));
            Assert.True(MonsterEmerge.StartWatch(secondClient));
            Assert.Same(sharedBody, Assert.Single(new MonsterEmerge().RiseLocked(secondClient, (50, 50), 1, out bool watching)).Monster);
            Assert.False(watching);
            Assert.Empty(firstClient.DormantMonsters);
            Assert.Same(sharedBody, firstClient.Monsters[0x303f]);
            Assert.False(MonsterEmerge.Unwatched(firstClient));
        }
    }

    [Fact]
    public async Task Saved_monsters_are_recreated_at_their_established_cells()
    {
        string database = Path.Combine(Path.GetTempPath(), $"localexile-monster-restore-{Guid.NewGuid():N}.db");
        var previousLogic = Program.GameLogicOrNull;
        var previousLifetime = Game.CombatLifetime;
        var previousOutgoing = Program.Outgoing;
        ClientState? peer = null;
        var lifetime = new ModuleLifetime("Game");
        var conn = new ClientState(new(1, "restore-cells", 300, 300, 100, 100, null, []), 1, 1)
        {
            LockstepClient = true, Instance = new(), Character = $"restore-cells-{Guid.NewGuid():N}",
        };

        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var writer = new TcpClient();
        var accepting = listener.AcceptTcpClientAsync();
        await writer.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
        using var reader = await accepting;
        var stream = writer.GetStream();
        var ciphers = new ConnectionCiphers(new byte[64], isClient: false);
        bool openedSaves = false;
        try
        {
            Saves.Open(database);
            openedSaves = true;
            Game.CombatLifetime = lifetime;
            Program.SetGameLogic(new GameServer.Modules.Game.GameLogic());
            // This test drives creation only. Reserve the watch owner without starting its real-time loop.
            lock (conn.Monsters) Assert.True(MonsterEmerge.StartWatch(conn));
            var variety = Assert.IsType<MonsterCatalog.Variety>(new MonsterCatalog().Find("Metadata/Monsters/ZombieDismember/ZombieDrownedStrandWake1"));
            // SavedMonster keeps active actors before the dormant set. The native Positioned reader (141e2f0b0)
            // restores their recorded cells directly, even when their footprints overlap.
            var active = new SpawnedMonster("active", new PlayerWalk(50, 50, 8), 60, 2, 23)
            {
                Variety = variety, Level = 1, Rarity = MonsterRarity.Normal,
            };
            var buried = new SpawnedMonster("buried", new PlayerWalk(53, 50, 8), 60, 2, 23)
            {
                Variety = variety, Level = 1, Rarity = MonsterRarity.Normal,
            };
            conn.Instance.Monsters = SavedMonster.Save([active], 0, [buried]);
            await Game.PopulateArea("restore-cells", stream, ciphers, conn);
            var activeEntry = Assert.Single(conn.Monsters);
            var buriedEntry = Assert.Single(MonsterEmerge.Buried(conn));
            Assert.Equal((50, 50), activeEntry.Value.Walk.Position(0));
            Assert.Equal((53, 50), buriedEntry.Value.Walk.Position(0));
            Assert.Equal(2, conn.Monsters.Count + MonsterEmerge.Buried(conn).Count);

            // PacketHistory owns the plaintext before encryption. Its 48-byte prefix includes the entire initial
            // Positioned XY: common create header18, object/base prefix2, then little-endian X/Y.
            var creates = Program.HistoryFor(ciphers).Lines().Where(line => line.Contains("=> 0x02b9 ")).Select(line =>
            {
                var bytes = Convert.FromHexString(line[(line.IndexOf(" B ", StringComparison.Ordinal) + 3)..].TrimEnd('…'));
                return (Id: BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(2)),
                    X: BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(20)), Y: BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(24)));
            }).ToArray();
            Assert.Equal([(activeEntry.Key, 50, 50), (buriedEntry.Key, 53, 50)], creates);

            // A newly proposed spawn still uses the existing placement policy; restoration is a distinct contract.
            uint fresh = await Game.SpawnMonster("restore-cells", stream, ciphers, conn, variety, 1,
                MonsterRarity.Normal, 50, 50, log: false, mods: MonsterModSet.None, unrisen: false);
            Assert.NotEqual((50, 50), conn.Monsters[fresh].Walk.Position(0));

            // A later client receives the still-buried create before that body joins its collision view. Capture
            // the real send bytes before encryption so the entire initial Transitionable state is checked too.
            var peerCiphers = new ConnectionCiphers(new byte[64], isClient: false);
            var sent = new List<byte[]>();
            Program.Outgoing = (wire, bytes) =>
            {
                if (ReferenceEquals(wire, peerCiphers)) sent.Add((byte[])bytes.Clone());
            };
            peer = new ClientState(new(1, "restore-cells", 300, 300, 100, 100, null, []), 1, 1)
            {
                Instance = conn.Instance, Character = $"restore-peer-{Guid.NewGuid():N}", PlayerSent = true,
                PlayerLife = 100, LockstepClient = true, LockstepFrame = (.99, 1.023),
            };
            conn.PlayerSent = true;
            Program.InGame.TryAdd(conn, 0);
            Program.InGame.TryAdd(peer, 0);
            var bodyCell = new ClientPathfinder.Blocker(53, 50, LivingCollision.Size(buriedEntry.Value));
            Assert.DoesNotContain(bodyCell, peer.Blockers);
            var origin = conn.Instance.World.MonsterOrigins[buriedEntry.Key];
            Assert.Equal(MonsterEmerge.UnrisenState, origin.Object.TransitionState);
            byte[] dormantCreate = GameServer.Core.Protocol.Instance.InstanceEntities.CreateObject(buriedEntry.Key,
                origin.Type, (origin.Object with { Life = buriedEntry.Value.Life }).Blob());
            await Game.SyncInstanceMonsters("restore-peer", stream, peerCiphers, peer);
            Assert.Contains(sent, bytes => bytes.AsSpan().IndexOf(dormantCreate) >= 0);
            Assert.Contains(buriedEntry.Key, peer.MonsterViews.Keys);
            Assert.Contains(buriedEntry.Key, peer.AwakeMonsters);
            Assert.Contains(bodyCell, peer.Blockers);
            Game.PublishAwakeMonsters(peer);
            Assert.Contains(buriedEntry.Key, peer.AwakeMonsters);
            Assert.DoesNotContain(buriedEntry.Key, peer.RunMonsters);
            sent.Clear();
            await Game.SyncInstanceMonsters("restore-peer", stream, peerCiphers, peer);
            await Game.SyncInstanceMonsters("restore-cells", stream, ciphers, conn);
            Assert.Empty(sent);
            Assert.Contains(buriedEntry.Key, conn.MonsterViews.Keys);
            peer.AsleepMonsters.Add(buriedEntry.Key);
            await Game.SyncInstanceMonsters("restore-peer", stream, peerCiphers, peer);
            Assert.DoesNotContain(buriedEntry.Key, peer.AwakeMonsters);
            Assert.Contains(buriedEntry.Key, peer.MonsterViews.Keys);
            Assert.Empty(sent);
            peer.AsleepMonsters.Remove(buriedEntry.Key);

            // A runner handoff must rebind even dormant walkers to the new connection's collision/frame view.
            var oldWalk = buriedEntry.Value.Walk;
            conn.Closed = true;
            Assert.False(Game.ClaimMonsterHost("restore-peer", peer));
            Assert.Same(peer, conn.Instance.World.Runner);
            Assert.NotSame(oldWalk, buriedEntry.Value.Walk);
            Assert.Equal((53, 50), buriedEntry.Value.Walk.Position(1));
            Assert.Equal(peer.LockstepFrame, buriedEntry.Value.Walk.FrameOf!(1));
            await Game.SyncInstanceMonsters("restore-peer", stream, peerCiphers, peer);
            Assert.Contains(buriedEntry.Key, peer.AwakeMonsters);

            // Rising keeps the already-created object. Its actual component/action sends precede a sync that
            // neither recreates nor removes it; the unchanged awake admission permits peers to receive the rise.
            (uint Id, SpawnedMonster Monster, (int X, int Y) At, double Seconds) risen;
            bool watching;
            lock (peer.Monsters)
                risen = Assert.Single(new MonsterEmerge().RiseLocked(peer, (53, 50), 1, out watching));
            Assert.False(watching);
            Assert.Same(buriedEntry.Value, risen.Monster);
            await Game.Emerge("restore-peer", stream, peerCiphers, peer, risen.Id, risen.Monster, risen.At, risen.Seconds);
            Assert.Equal(2, sent.Count);
            Assert.Equal(GameServer.Core.Protocol.Instance.InstanceEntities.TransitionableUpdate(risen.Id,
                (byte)variety.ComponentsOrBase.ToList().IndexOf("Transitionable"), MonsterEmerge.RisenState(variety)), sent[0]);
            Assert.Equal(0x01ab, BinaryPrimitives.ReadUInt16BigEndian(sent[1]));
            Assert.Equal(risen.Id, BinaryPrimitives.ReadUInt32BigEndian(sent[1].AsSpan(2)));
            sent.Clear();
            await Game.SyncInstanceMonsters("restore-peer", stream, peerCiphers, peer);
            Assert.Empty(sent);
            Assert.Same(buriedEntry.Value, peer.Monsters[risen.Id]);
            Assert.Contains(risen.Id, peer.MonsterViews.Keys);

            // A real removal still leaves the peer's view once, using the upstream non-runner removal rule.
            conn.Closed = false;
            conn.Instance.World.Runner = conn;
            lock (conn.Monsters) Assert.True(conn.Monsters.Remove(risen.Id));
            await Game.SyncInstanceMonsters("restore-peer", stream, peerCiphers, peer);
            Assert.Equal(GameServer.Core.Protocol.Instance.InstanceProtocol.RemoveObject(risen.Id), Assert.Single(sent));
            Assert.DoesNotContain(risen.Id, peer.MonsterViews.Keys);
            Assert.DoesNotContain(risen.Id, peer.AwakeMonsters);
        }
        finally
        {
            conn.Closed = true;
            Program.InGame.TryRemove(conn, out _);
            if (peer is not null)
            {
                peer.Closed = true;
                Program.InGame.TryRemove(peer, out _);
                PlayerInventories.Forget(peer.Character!);
            }
            Program.Outgoing = previousOutgoing;
            lifetime.Stop(ex => throw ex);
            PlayerInventories.Forget(conn.Character!);
            Game.CombatLifetime = previousLifetime;
            Program.SetGameLogic(previousLogic!);
            if (openedSaves) TestEnvironment.ReleaseSaves(database);
        }
    }
}
