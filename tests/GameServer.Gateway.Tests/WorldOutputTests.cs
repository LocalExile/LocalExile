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
        var conn = new ClientState(new(1, "world-order", 300, 300, 100, 100, null, []), 1, 1)
        {
            LockstepClient = true, Wire = ("world-order", writer.GetStream(), ciphers),
        };
        try
        {
            await Program.BeginWorldFrames(conn, .033);
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
                await Game.SendWorldRecord("world-order", writer.GetStream(), ciphers, conn, 1, record, null);
            Assert.Empty(Program.HistoryFor(ciphers).Lines());
            await Program.EndWorldFrame(conn, (.033, .066));
            await Read(firstA, firstB, low, highA, highB, global, InstanceProtocol.FrameEnd());

            // A creation and its committed wake presentation are different frames. An action's factory must wait
            // outside the cipher gate, then observe state at admission rather than at the original call.
            const uint bornId = 9;
            var create = InstanceEntities.CreateObject(bornId, 0x1234, [0, 0]);
            var creating = Game.SendWorldCreate(conn, bornId, () => create, wake: true);
            Assert.False(creating.IsCompleted);
            int version = 1, observed = 0;
            var acting = Game.SendWorldRecord(conn, bornId, () =>
            {
                observed = version;
                return InstanceProtocol.LifeUpdate(bornId, (uint)version);
            });
            Assert.False(acting.IsCompleted);
            Assert.Equal(0, observed);
            version = 2;
            await Program.EndWorldFrame(conn, (.066, .099));
            Assert.True(await creating);
            Assert.True(await acting);
            Assert.Equal(2, observed);
            var tailCreate = create.ToArray();
            BinaryPrimitives.WriteUInt32BigEndian(tailCreate.AsSpan(6), uint.MaxValue);
            Assert.Equal(0u, BinaryPrimitives.ReadUInt32BigEndian(create.AsSpan(6)));
            await Read(tailCreate, InstanceProtocol.FrameEnd());
            await Program.EndWorldFrame(conn, (.099, .132));
            await Read(InstanceProtocol.EnterWakeZone(bornId), InstanceProtocol.LifeUpdate(bornId, 2), InstanceProtocol.FrameEnd());

            // Predictive keeps the declared records in producer order, without waiting for a frame.
            conn.LockstepClient = false;
            await Game.SendWorldRecords("world-order", writer.GetStream(), ciphers, conn, 1, () => [highB, firstA]);
            await Read(highB, firstA);

            // Death updates the existing player incarnation; it must never reserve a duplicate player birth.
            conn.LockstepClient = true;
            var player = conn.WorldOutput.ExistingObject(Program.PlayerId);
            conn.PlayerLife = 0;
            await Game.SendPlayerDeathPresentation("world-order", writer.GetStream(), ciphers, conn, "death", conn.IncomingHitEpoch);
            await Read(InstanceProtocol.DeathScreen(Game.DeathScreenFlags(conn)));
            await Program.EndWorldFrame(conn, (.132, .165));
            await Read(InstanceProtocol.LifeUpdate(Program.PlayerId, 0, conn.PlayerReservedLife),
                InstanceEntities.StatsUpdate(Program.PlayerId, [(StatKeys.HideMiniLifeBar, 1)]), InstanceProtocol.FrameEnd());
            Assert.Same(player, conn.WorldOutput.PublishedObject(Program.PlayerId));
            conn.LockstepClient = false;
            await Game.SendPlayerDeathPresentation("world-order", writer.GetStream(), ciphers, conn, "predictive death", conn.IncomingHitEpoch);
            await Read(InstanceProtocol.LifeUpdate(Program.PlayerId, 0, conn.PlayerReservedLife),
                InstanceEntities.StatsUpdate(Program.PlayerId, [(StatKeys.HideMiniLifeBar, 1)]),
                InstanceProtocol.DeathScreen(Game.DeathScreenFlags(conn)));

        }
        finally { conn.Closed = true; }
    }

    [Fact]
    public async Task Birth_cancellation_and_synchronous_quest_capture_preserve_incarnations_and_owned_bytes()
    {
        var conn = new ClientState(new(1, "world-lifetime", 300, 300, 100, 100, null, []), 1, 1) { LockstepClient = true };
        var output = conn.WorldOutput;
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
        var batch = Game.CaptureQuestWorldOutput(conn, admission, async () =>
        {
            Assert.True(await Game.SendWorldCreate(conn, 20,
                () => InstanceEntities.CreateObject(20, 0x1234, [0, 0]), wake: true));
            await Game.SendWorldRecord("quest", null!, null!, conn, 20, bytes, null);
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
        conn.Closed = true;
        Assert.False(await closing);

        var cancelledOutput = new WorldOutput();
        var cancelled = new WorldAdmission(cancelledOutput, []);
        var reservation = cancelled.NewObject(30);
        cancelled.Finish(false);
        Assert.False(await cancelledOutput.AwaitBirths([reservation]));
        Assert.NotSame(reservation, cancelledOutput.NewObject(30));
    }
}
