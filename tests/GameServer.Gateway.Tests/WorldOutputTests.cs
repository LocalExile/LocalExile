using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using Xunit;
using GameServer.Modding;

namespace GameServer.Gateway.Tests;

public class WorldOutputTests : IDisposable
{
    readonly IGameLogic? previousLogic = Program.GameLogicOrNull;
    readonly IModuleLifetime? previousLifetime = Game.CombatLifetime;
    readonly ModuleLifetime lifetime = new("Game");
    public WorldOutputTests()
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
    public async Task Closing_a_connection_during_projection_refuses_the_unadmitted_batch()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var writer = new TcpClient();
        var accepting = listener.AcceptTcpClientAsync();
        await writer.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
        using var reader = await accepting;
        var ciphers = new ConnectionCiphers(new byte[64], isClient: false);
        var player = new Player(new(1, "world-retire", 300, 300, 100, 100, null, []), 1, 1)
        {
            Connection = { Session = new("world-retire", writer.GetStream(), ciphers) },
        };
        Assert.False(await Program.SendRecords(player, [], _ =>
        {
            player.Closed = true;
            return new OutputBatch([OutputRecord.Immediate(InstanceProtocol.FrameEnd())]);
        }));
        Assert.Empty(Program.HistoryFor(ciphers).Lines());

        var sharedPlayer = new Player(new(1, "world-retire-shared", 300, 300, 100, 100, null, []), 1, 1);
        sharedPlayer.Connection.WorldOutput.Begin(1);
        var previous = GameArea.Own(sharedPlayer.Area);
        try
        {
            Assert.False(await Program.SendRecords(sharedPlayer, [], _ =>
            {
                sharedPlayer.Closed = true;
                return new OutputBatch([OutputRecord.Immediate(InstanceProtocol.FrameEnd())]);
            }));
            Assert.Empty(sharedPlayer.Area.Delta.Take());
            Assert.False(sharedPlayer.Connection.WorldOutput.TryShared(OutputBatch.Empty,
                new WorldAdmission(sharedPlayer.Connection.WorldOutput, [])));
            Assert.Throws<ObjectDisposedException>(() => sharedPlayer.Connection.WorldOutput.Shared(OutputBatch.Empty,
                new WorldAdmission(sharedPlayer.Connection.WorldOutput, [])));
        }
        finally { GameArea.Own(previous); }
    }

    [Fact]
    public async Task Producers_publish_stable_native_order_and_release_births_only_after_the_delimiter()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var writer = new TcpClient();
        var accepting = listener.AcceptTcpClientAsync();
        await writer.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
        using var reader = await accepting;
        var ciphers = new ConnectionCiphers(new byte[64], isClient: false);
        var receiver = new ConnectionCiphers(new byte[64], isClient: true);
        var player = new Player(new(1, "world-order", 300, 300, 100, 100, null, []), 1, 1)
        {
            Connection = { LockstepClient = true, Session = new("world-order", writer.GetStream(), ciphers) }
        };
        try
        {
            await Program.BeginWorldFrames(player, .033);
            byte[] Record(uint id, uint key, ushort checkpoint)
            {
                var bytes = InstanceProtocol.ActionEnded(id);
                BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(6), key);
                BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(10), checkpoint);
                return bytes;
            }
            async Task Read(params byte[][] packets)
            {
                byte[] expected = [.. packets.SelectMany(p => p)];
                byte[] actual = new byte[expected.Length];
                await reader.GetStream().ReadExactlyAsync(actual);
                receiver.Receive.Process(actual);
                Assert.Equal(expected, actual);
            }
            var firstA = Record(1, 0, 0xffff);
            var firstB = Record(1, 0, 0);
            var low = Record(1, 0x7fffffff, 0x8000);
            var highA = Record(1, 0x80000000, 1);
            var highB = Record(1, 0x80000000, 0xffff);
            var global = Record(1, 0xfffffffe, 1);
            foreach (var record in new[] { highB, global, low, firstA, highA, firstB })
                await Game.SendWorldRecord(player, 1, record, null);
            Assert.Empty(Program.HistoryFor(ciphers).Lines());
            await Program.EndWorldFrame(player, (.033, .066));
            await Read(firstA, firstB, low, highA, highB, global, InstanceProtocol.FrameEnd());

            // A creation and its committed wake presentation are different frames. An action's factory must wait
            // outside the cipher gate, then observe state at admission rather than at the original call.
            const uint bornId = 9;
            var create = InstanceEntities.CreateObject(bornId, 0x1234, [0, 0]);
            var creating = Game.SendWorldCreate(player, bornId, () => create, wake: true);
            Assert.False(creating.IsCompleted);
            int version = 1, observed = 0;
            var acting = Game.SendWorldRecord(player, bornId, () =>
            {
                observed = version;
                return InstanceProtocol.LifeUpdate(bornId, (uint)version);
            });
            Assert.False(acting.IsCompleted);
            Assert.Equal(0, observed);
            version = 2;
            await Program.EndWorldFrame(player, (.066, .099));
            Assert.True(await creating);
            Assert.True(await acting);
            Assert.Equal(2, observed);
            var tailCreate = create.ToArray();
            BinaryPrimitives.WriteUInt32BigEndian(tailCreate.AsSpan(6), uint.MaxValue);
            Assert.Equal(0u, BinaryPrimitives.ReadUInt32BigEndian(create.AsSpan(6)));
            await Read(tailCreate, InstanceProtocol.FrameEnd());
            await Program.EndWorldFrame(player, (.099, .132));
            await Read(InstanceProtocol.EnterWakeZone(bornId), InstanceProtocol.LifeUpdate(bornId, 2), InstanceProtocol.FrameEnd());

            // Predictive keeps the declared records in producer order, without waiting for a frame.
            player.Connection.LockstepClient = false;
            await Game.SendWorldRecords(player, 1, () => [highB, firstA]);
            await Read(highB, firstA);

            // Death updates the existing player incarnation; it must never reserve a duplicate player birth.
            player.Connection.LockstepClient = true;
            var playerObject = player.Connection.WorldOutput.ExistingObject(player.Id);
            player.PlayerLife = 0;
            await Game.SendPlayerDeathPresentation(player, "death", player.IncomingHitEpoch);
            await Read(InstanceProtocol.DeathScreen(Game.DeathScreenFlags(player)));
            // The life at 0 and the hidden mini life bar are the Life and Stats components' (the area's frame end).
            await Program.EndWorldFrame(player, (.132, .165));
            await Read(InstanceProtocol.FrameEnd());
            Assert.Same(playerObject, player.Connection.WorldOutput.PublishedObject(player.Id));
            player.Connection.LockstepClient = false;
            await Game.SendPlayerDeathPresentation(player, "predictive death", player.IncomingHitEpoch);
            await Read(InstanceProtocol.DeathScreen(Game.DeathScreenFlags(player)));

        }
        finally { player.Closed = true; }
    }

    [Fact]
    public async Task Predictive_projectile_removal_keeps_the_final_update_and_removal_separate_in_the_area_frame()
    {
        var player = new Player(new(1, "predictive-projectile", 300, 300, 100, 100, null, []), 1, 1);
        player.Connection.LockstepClient = false;
        player.Connection.WorldOutput.Begin(1);
        const uint id = 100;
        var projectile = player.Connection.WorldOutput.ExistingObject(id);
        var finalUpdate = InstanceProtocol.Teleport(id, 20, 30);
        var removal = InstanceProtocol.RemoveObject(id);
        var send = typeof(Game.ProjectileSession).GetMethod("SendWorldRecordsNow",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var session = new Game.ProjectileSession(player);
        var previous = GameArea.Own(player.Area);
        try
        {
            Func<byte[][]?> records = () => [finalUpdate, removal];
            var projected = (Task)send.Invoke(session, [id, records, null, false, true, false])!;
            Assert.True(projected.IsCompleted);
            await projected;

            Assert.Collection(player.Area.Delta.Take(),
                update =>
                {
                    Assert.Equal(AreaDelta.Kind.World, update.Kind);
                    Assert.Equal(finalUpdate, update.Bytes);
                },
                removed =>
                {
                    Assert.Equal(AreaDelta.Kind.Remove, removed.Kind);
                    Assert.Equal(id, removed.Entity);
                    Assert.Equal(WorldOrder.Last, removed.Order);
                });
            Assert.Equal(WorldObject.BirthState.Retired, projectile.State);
            Assert.Equal(0u, BinaryPrimitives.ReadUInt32BigEndian(removal.AsSpan(6)));
        }
        finally
        {
            GameArea.Own(previous);
            session.Invalidate();
            await session.OutputCompleted;
        }
    }

    [Fact]
    public async Task Predictive_area_frame_completes_object_creation_and_removal()
    {
        var player = new Player(new(1, "predictive-object", 300, 300, 100, 100, null, []), 1, 1);
        player.Connection.LockstepClient = false;
        player.Connection.WorldOutput.Begin(1);
        const uint id = 101;
        var previous = GameArea.Own(player.Area);
        try
        {
            var create = InstanceEntities.CreateObject(id, 0x1234, [0, 0]);
            var creating = Game.SendWorldCreate(player, id, create);
            Assert.True(creating.IsCompleted);
            Assert.True(await creating);
            var created = Assert.Single(player.Area.Delta.Take());
            Assert.Equal(AreaDelta.Kind.Create, created.Kind);
            Assert.Equal(id, created.Entity);

            var target = player.Connection.WorldOutput.PublishedObject(id);
            Assert.True(target.IsReady);

            var removing = Game.SendWorldRemoval(player, id);
            Assert.True(removing.IsCompleted);
            await removing;
            var removed = Assert.Single(player.Area.Delta.Take());
            Assert.Equal(AreaDelta.Kind.Remove, removed.Kind);
            Assert.Equal(id, removed.Entity);
            Assert.Equal(WorldObject.BirthState.Retired, target.State);
        }
        finally
        {
            GameArea.Own(previous);
        }
    }

    [Fact]
    public async Task Birth_cancellation_and_synchronous_quest_capture_preserve_incarnations_and_owned_bytes()
    {
        var player = new Player(new(1, "world-lifetime", 300, 300, 100, 100, null, []), 1, 1) { Connection = { LockstepClient = true } };
        var output = player.Connection.WorldOutput;
        output.Begin(7);
        var first = output.NewObject(10);
        var second = output.NewObject(11);
        var waiting = output.AwaitBirths([first, second]);
        Assert.False(waiting.IsCompleted);
        second.Retire();
        Assert.False(await waiting);
        first.Retire();
        Assert.Throws<ArgumentException>(() => output.CheckOwner(new WorldOutput().NewObject(10)));

        var admission = new WorldAdmission(output, []);
        var bytes = InstanceProtocol.LifeUpdate(20, 5);
        var batch = Game.CaptureQuestWorldOutput(player, admission, async () =>
        {
            Assert.True(await Game.SendWorldCreate(player, 20,
                () => InstanceEntities.CreateObject(20, 0x1234, [0, 0]), wake: true));
            await Game.SendWorldRecord(player, 20, bytes, null);
        });
        batch = new([.. batch.Records.Select(r => r.AsSharedView())]);
        Assert.Empty(output.Admit(batch, admission, framed: true));
        admission.Finish(true);
        Array.Fill(bytes, (byte)0xff);
        var born = Assert.Single(admission.Births);
        var frame = output.TakeFrame();
        Assert.True(Assert.Single(frame.Packets).SharedView);
        Assert.False(born.IsReady);
        output.Written(frame.Births, frame.Removals, frameEnded: true);
        Assert.True(await output.AwaitBirths([born]));
        frame = output.TakeFrame();
        Assert.Equal(2, frame.Packets.Count);
        Assert.All(frame.Packets, packet => Assert.True(packet.SharedView));
        Assert.Equal(InstanceProtocol.EnterWakeZone(20), frame.Packets[0].Bytes);
        Assert.Equal(InstanceProtocol.LifeUpdate(20, 5), frame.Packets[1].Bytes);
        Assert.Throws<InvalidOperationException>(() => output.NewObject(20));

        var removal = InstanceProtocol.RemoveObject(20);
        BinaryPrimitives.WriteUInt32BigEndian(removal.AsSpan(6), uint.MaxValue);
        var removing = new WorldAdmission(output, []);
        output.Admit(new(OutputRecord.Remove(born, WorldOrder.Last, removal)), removing, framed: true);
        Assert.False(await output.AwaitBirths([born]));
        Assert.False(born.Removed.IsCompleted);
        // A second producer that notices the same object leave (a view's synchronisation after its taker) repeats that
        // removal: admitted as nothing, while the removal waits for its frame and once it is written. An update of the
        // leaving incarnation is still refused.
        Assert.Empty(output.Admit(new(OutputRecord.Remove(born, WorldOrder.Last, removal)), new WorldAdmission(output, []), framed: true));
        Assert.Throws<ArgumentException>(() => output.Admit(new(OutputRecord.World(born, WorldOrder.First, InstanceProtocol.LifeUpdate(20, 4))),
            new WorldAdmission(output, []), framed: true));
        frame = output.TakeFrame();
        Assert.Same(born, Assert.Single(frame.Removals));
        Assert.Single(frame.Packets);
        output.Written(frame.Births, frame.Removals, frameEnded: true);
        Assert.True(await born.Removed);
        Assert.Empty(output.Admit(new(OutputRecord.Remove(born, WorldOrder.Last, removal)), new WorldAdmission(output, []), framed: true));
        Assert.Empty(output.TakeFrame().Packets);
        var replacement = output.NewObject(20);
        Assert.NotSame(born, replacement);
        var closing = output.AwaitBirths([replacement]);
        player.Closed = true;
        Assert.False(await closing);

        var cancelledOutput = new WorldOutput();
        var cancelled = new WorldAdmission(cancelledOutput, []);
        var reservation = cancelled.NewObject(30);
        cancelled.Finish(false);
        Assert.False(await cancelledOutput.AwaitBirths([reservation]));
        Assert.NotSame(reservation, cancelledOutput.NewObject(30));
    }

    [Fact]
    public void A_placeholder_for_an_unseen_id_yields_to_the_create_that_follows_it()
    {
        var output = new WorldOutput();
        // A removal may name an ID whose create is still queued behind it: the record keeps a placeholder, not an
        // incarnation, so the create that follows replaces it instead of failing the connection.
        var placeholder = output.PublishedObject(40);
        Assert.True(placeholder.Placeholder);
        Assert.True(placeholder.IsReady);
        var birth = output.NewObject(40);
        Assert.NotSame(placeholder, birth);
        Assert.False(placeholder.IsReady);
        Assert.False(birth.IsReady);
        // A create that really reached the client still needs its explicit removal before replacement.
        Assert.Throws<InvalidOperationException>(() => output.NewObject(40));
        var established = output.ExistingObject(41);
        Assert.Throws<InvalidOperationException>(() => established.Retire());
    }

    [Fact]
    public async Task Predictive_create_flight_and_same_frame_removal_defer_after_birth_and_removal_to_next_frame()
    {
        var player = new Player(new(1, "predictive-flight", 300, 300, 100, 100, null, []), 1, 1);
        player.Connection.LockstepClient = false;
        player.Connection.WorldOutput.Begin(1);
        const uint id = 102;
        var create = InstanceEntities.CreateObject(id, 0x1234, [0, 0]);
        var move = InstanceProtocol.Teleport(id, 20, 30);
        var stop = InstanceProtocol.Teleport(id, 25, 30);
        var removal = InstanceProtocol.RemoveObject(id);
        var createFlight = typeof(Game.ProjectileSession).GetMethod("CreateFlightNow",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var send = typeof(Game.ProjectileSession).GetMethod("SendWorldRecordsNow",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var previous = GameArea.Own(player.Area);
        var session = new Game.ProjectileSession(player);
        try
        {
            var createdTask = (Task)createFlight.Invoke(session, [id, (Func<byte[]?>)(() => create), (Func<byte[][]?>)(() => [move]), null])!;
            Assert.True(createdTask.IsCompleted);
            await createdTask;
            // Shared AreaDelta creates are tracked in Delta while undrained and do not masquerade as directly
            // written creates in WorldOutput.HasDirect (which JoinArea relies on to skip already-sent monsters).
            Assert.True(player.Connection.WorldOutput.Has(id));
            Assert.False(player.Connection.WorldOutput.HasDirect(id));
            Assert.True(player.Area.Delta.HasCreateFor(id, player));
            player.Connection.WorldOutput.ExistingObject(103);
            Assert.True(player.Connection.WorldOutput.HasDirect(103));
            var removedTask = (Task)send.Invoke(session, [id, (Func<byte[][]?>)(() => [stop, removal]), null, false, true, false])!;
            Assert.True(removedTask.IsCompleted);
            await removedTask;

            // Frame N: only Create leaves; same-frame AfterBirth moves and Remove wait for Frame N+1.
            var frame1 = player.Area.Delta.Take();
            Assert.False(player.Area.Delta.HasCreateFor(id, player));
            var created = Assert.Single(frame1);
            Assert.Equal(AreaDelta.Kind.Create, created.Kind);
            Assert.Equal(id, created.Entity);

            // Frame N+1: moves precede removal cleanly instead of arriving after a same-frame Remove.
            var frame2 = player.Area.Delta.Take();
            Assert.Equal(3, frame2.Count);
            Assert.Equal(AreaDelta.Kind.World, frame2[0].Kind);
            Assert.Equal(AreaDelta.Kind.World, frame2[1].Kind);
            Assert.Equal(AreaDelta.Kind.Remove, frame2[2].Kind);
            Assert.Equal(id, frame2[2].Entity);
        }
        finally
        {
            GameArea.Own(previous);
            session.Invalidate();
            await session.OutputCompleted;
        }
    }
}
