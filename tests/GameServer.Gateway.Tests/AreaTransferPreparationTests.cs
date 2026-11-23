using System.Net;
using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

namespace GameServer.Gateway.Tests;

public sealed class AreaTransferPreparationTests
{
    sealed class FakeClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public async Task Prepared_instance_reuses_cached_layout_on_enter_and_expires_if_abandoned()
    {
        var clock = new FakeClock();
        var store = new InstanceStore(clock, TimeSpan.FromMinutes(6));

        // Prepare creates the instance without claiming a connection slot, so an abandoned transfer still expires.
        var abandoned = store.Prepare(9001, 100);
        Assert.Equal(0, abandoned.Connections);
        Assert.NotNull(abandoned.EmptySince);
        clock.Now += TimeSpan.FromMinutes(7);
        var recreated = store.Prepare(9001, 100);
        Assert.NotSame(abandoned, recreated);

        // A prepared layout with walkability is cached and reused when the client connects (Enter).
        var walkableLayout = new AreaLayout(AreaLayouts.TheCoast, "1_1_2", 10, 10, 5, 5, (_, _) => true, []);
        var overriddenLayout = new AreaLayout(AreaLayouts.TheCoast, "1_1_2", 12, 12, 6, 6, (_, _) => true, []);
        int builds = 0;
        int rebuilds = 0;
        var first = recreated.PreparedLayout(42, 0, () => { builds++; return (42u, walkableLayout); },
            seed => { rebuilds++; return walkableLayout; }, clock.Now);
        Assert.True(recreated.HasPreparedLayout);

        // Stale unentered prepared layouts expire after PreparedLayoutLifetime without pruning the instance itself.
        clock.Now += InstanceStore.PreparedLayoutLifetime + TimeSpan.FromSeconds(1);
        Assert.Same(recreated, store.Prepare(9001, 100));
        Assert.False(recreated.HasPreparedLayout);

        // Prepare on an existing empty instance refreshes EmptySince so it is not pruned between Prepare and Enter.
        clock.Now += TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(50);
        Assert.Same(recreated, store.Prepare(9001, 100));
        clock.Now += TimeSpan.FromSeconds(20);
        var entered = store.Enter(9001, 100);
        Assert.Same(recreated, entered);
        Assert.Equal(1, entered.Connections);
        Assert.Null(entered.EmptySince);

        // Rebuilding after unentered expiry uses the remembered seed instead of re-running build (PlayableSeed).
        var afterExpiry = entered.PreparedLayout(42, 0, () => { builds++; return (99u, walkableLayout); },
            seed => { rebuilds++; return walkableLayout; }, clock.Now);
        Assert.Equal(1, builds);
        Assert.Equal(1, rebuilds);
        Assert.Equal(42u, afterExpiry.Seed);
        Assert.Same(first.Layout, afterExpiry.Layout);

        // AreaHeader overrides (Simulacrum/Delve) update the cached layout under the original request area level key,
        // so party joins hit while occupied and re-entries after Leave keep the same seed without calling build().
        entered.UpdatePreparedLayout(42, 0, 42u, overriddenLayout, clock.Now);
        var partyJoin = entered.PreparedLayout(42, 0, () => { builds++; return (99u, walkableLayout); },
            seed => { rebuilds++; return overriddenLayout; }, clock.Now);
        Assert.Equal(1, builds);
        Assert.Equal(1, rebuilds);
        Assert.Same(overriddenLayout, partyJoin.Layout);

        // Leaving releases the ~11 MB layout pin while keeping the lightweight seed for re-entry.
        store.Leave(entered);
        Assert.Equal(0, entered.Connections);
        Assert.NotNull(entered.EmptySince);
        Assert.False(entered.HasPreparedLayout);
        var rejoined = store.Enter(9001, 100);
        Assert.Same(entered, rejoined);
        var afterLeave = rejoined.PreparedLayout(42, 0, () => { builds++; return (99u, walkableLayout); },
            seed => { rebuilds++; return overriddenLayout; }, clock.Now);
        Assert.Equal(1, builds);
        Assert.Equal(2, rebuilds);
        Assert.Equal(42u, afterLeave.Seed);
        Assert.Same(overriddenLayout, afterLeave.Layout);

        // Dropping terrain plugin states invalidates both the prepared layout and remembered seed so build() reruns.
        rejoined.DropTerrainPluginStates();
        rejoined.PreparedLayout(42, 0, () => { builds++; return (42u, walkableLayout); },
            seed => { rebuilds++; return walkableLayout; }, clock.Now);
        Assert.Equal(2, builds);

        // A layout without walkability (an uncaptured unsupported area) is not pinned across calls.
        var uncaptured = new AreaLayout(0x9999, "uncaptured", 0, 0, 1, 1, null, []);
        rejoined.ClearPreparedLayout();
        rejoined.PreparedLayout(7, 0, () => { builds++; return (7u, uncaptured); });
        Assert.False(rejoined.HasPreparedLayout);
        rejoined.PreparedLayout(7, 0, () => { builds++; return (7u, walkableLayout); });
        Assert.Equal(4, builds);

        // AreaFrames.Run synchronized with Detach/Stop never hangs posted continuations after the last member leaves.
        using (var reading = new CancellationTokenSource())
        {
            var player = new Player(walkableLayout, 77, 1) { Instance = new InstanceState() };
            var inbox = new ClientInbox();
            player.Frames.Attach(player, inbox, reading);
            await player.Frames.Run(() => Task.CompletedTask);
            player.Closed = true;
            inbox.Complete();
            player.Frames.Detach(player);
            var runs = Enumerable.Range(0, 16).Select(i => player.Frames.Run(() => Task.FromResult(i))).ToArray();
            Assert.Equal(Enumerable.Range(0, 16), await Task.WhenAll(runs));
        }

        Assert.Equal("02B5", Convert.ToHexString(InstanceTransfer.CancelChangeArea()));
    }

    [GameFilesFact]
    public async Task Transfer_pregenerates_destination_area_before_0x27_and_cancels_on_refusal()
    {
        const ulong instanceId = 900_000_042;
        const ulong abandonedInstanceId = 900_000_043;
        const long created = 1_795_349_327_000;
        const uint authSeed = 123_456_789;
        const uint newTicket = 0x55aa1234;
        const uint abandonedTicket = 0x55aa1235;
        var townLayout = new AreaLayout(AreaLayouts.LioneyesWatch, "1_1_town", 690, 414, 197, 261, (_, _) => true, []);
        var keyring = new InstanceKeyring(new byte[32]);

        try
        {
            // Success: Transfer publishes TransferredTo, pre-generates the destination area off-frame while the client
            // shows "Entering <area>", then returns to AreaFrames to invoke Done, retire the source connection and send 0x27.
            using (var wire = new Wire())
            using (var reading = new CancellationTokenSource())
            {
                var sourceInstance = new InstanceState();
                var player = new Player(townLayout, 1, 1)
                {
                    Instance = sourceInstance,
                    Character = { Name = "TransferPrepTester", League = "Standard" },
                    Connection = { Session = new("test", wire.Stream, wire.Ciphers) { Keyring = keyring } },
                };
                var inbox = new ClientInbox();
                player.Frames.Attach(player, inbox, reading);
                try
                {
                    bool? doneOk = null;
                    bool doneOnFrame = false;
                    bool preparedBeforeDone = false;
                    var transfer = new PendingTransfer(AreaLayouts.TheCoast, 0, New: true)
                    {
                        Done = r =>
                        {
                            doneOk = r.Ok;
                            doneOnFrame = player.Area.Owned;
                            preparedBeforeDone = Program.Instances.Find(instanceId, created)?.HasPreparedLayout == true;
                        },
                    };
                    Assert.True(await player.Frames.Run(() => Task.FromResult(player.TryBeginAreaChange(transfer, out _))));

                    // While LeavingArea is true, gameplay packets are ignored by OnClientMessages.
                    await player.Frames.Run(() => global::Game.OnClientMessages(
                        [(InstanceProtocol.SwapWeaponsId, [0x00, (byte)InstanceProtocol.SwapWeaponsId, 1])], [], player));
                    Assert.Equal(0, wire.Available);

                    await player.Frames.Run(() => global::Game.Transfer(player, transfer, _ => Task.FromResult(
                        new ControlResponse
                        {
                            Ok = true,
                            Area = AreaLayouts.TheCoast,
                            Instance = instanceId,
                            Created = created,
                            Seed = authSeed,
                            Ticket = newTicket,
                        })));

                    Assert.True(doneOk);
                    Assert.True(doneOnFrame);
                    Assert.True(preparedBeforeDone);
                    Assert.True(player.TransferAccepted);
                    Assert.Equal((AreaLayouts.TheCoast, instanceId), player.TransferredTo);

                    byte[] expected = InstanceTransfer.ConnectInstance(
                        GatewayEndpoint.Host, GatewayEndpoint.Port, newTicket, AreaLayouts.TheCoast, keyring.Key(newTicket), keyring.Data8(newTicket));
                    Assert.Equal(expected, wire.Read(expected.Length));

                    // Before the new TCP connection arrives, the destination instance already holds the generated layout.
                    var preparedInstance = Program.Instances.Find(instanceId, created);
                    Assert.NotNull(preparedInstance);
                    Assert.Equal(0, preparedInstance.Connections);

                    int rebuilds = 0;
                    var cached = preparedInstance.PreparedLayout(authSeed, 0, () => { rebuilds++; throw new InvalidOperationException("must reuse"); });
                    Assert.Equal(0, rebuilds);
                    Assert.True(cached.Layout.ServerGenerated);
                    Assert.NotNull(cached.Layout.Walkable);
                    Assert.NotNull(cached.Layout.Level);

                    var onConnect = Program.PrepareInstanceArea(preparedInstance, newTicket, AreaLayouts.TheCoast, instanceId,
                        authSeed, "TransferPrepTester", "Standard", null);
                    Assert.Same(cached.Layout, onConnect.Layout);
                    Assert.Equal(cached.Seed, onConnect.Seed);
                }
                finally
                {
                    player.Closed = true;
                    inbox.Complete();
                    player.Frames.Detach(player);
                }
            }

            // Abandonment: PendingCharacterSelectExit arriving while LeavingArea cancels the in-flight transfer,
            // invokes Done(Ok=false) on-frame, clears TransferredTo, releases the destination layout and sends Auth "leave".
            using (var wire = new Wire())
            using (var reading = new CancellationTokenSource())
            {
                var player = new Player(townLayout, 3, 1)
                {
                    Instance = new InstanceState(),
                    Character = { Name = "TransferPrepTester", League = "Standard" },
                    Connection = { Session = new("test", wire.Stream, wire.Ciphers) { Keyring = keyring } },
                };
                var inbox = new ClientInbox();
                player.Frames.Attach(player, inbox, reading);
                try
                {
                    bool? doneOk = null;
                    bool doneOnFrame = false;
                    uint leftTicket = 0;
                    var transfer = new PendingTransfer(AreaLayouts.TheCoast, 0, New: true)
                    {
                        Done = r => { doneOk = r.Ok; doneOnFrame = player.Area.Owned; },
                    };
                    Assert.True(await player.Frames.Run(() => Task.FromResult(player.TryBeginAreaChange(transfer, out _))));

                    // Exit to Character Selection (0x153) is still accepted while LeavingArea is true.
                    await player.Frames.Run(() => global::Game.OnClientMessages(
                        [(InstanceProtocol.ExitToCharacterSelectId, [0x01, 0x53])], [], player));
                    Assert.True(player.PendingCharacterSelectExit);
                    byte[] charSelectBanner = InstanceTransfer.ChangeArea(InstanceProtocol.CharacterSelectAreaHash, 0);
                    Assert.Equal(charSelectBanner, wire.Read(charSelectBanner.Length));

                    // Even if PendingCharacterSelectExit lands after Auth "transfer" succeeds, Transfer abandons cleanly
                    // and tolerates a failing best-effort Auth "leave".
                    player.PendingCharacterSelectExit = false;
                    await player.Frames.Run(() => global::Game.Transfer(player, transfer, req =>
                    {
                        if (req.Op == "leave")
                        {
                            leftTicket = req.Ticket;
                            throw new IOException("auth unreachable");
                        }
                        player.PendingCharacterSelectExit = true;
                        return Task.FromResult(new ControlResponse
                        {
                            Ok = true,
                            Area = AreaLayouts.TheCoast,
                            Instance = abandonedInstanceId,
                            Created = created,
                            Seed = authSeed,
                            Ticket = abandonedTicket,
                        });
                    }));

                    Assert.Equal(abandonedTicket, leftTicket);
                    Assert.False(doneOk);
                    Assert.True(doneOnFrame);
                    Assert.Null(player.TransferredTo);
                    Assert.False(player.TransferAccepted);
                    Assert.Equal(0, wire.Available);
                    Assert.Null(Program.Instances.Find(abandonedInstanceId, created));
                }
                finally
                {
                    player.Closed = true;
                    inbox.Complete();
                    player.Frames.Detach(player);
                }
            }

            // Refusal: Transfer invokes Done(Ok=false), restores LeavingArea and sends 0x2b5 so the client hides "Entering <area>".
            using (var wire = new Wire())
            {
                var player = new Player(townLayout, 2, 1)
                {
                    Instance = new InstanceState(),
                    Character = { Name = "TransferPrepTester", League = "Standard" },
                    Connection = { Session = new("test", wire.Stream, wire.Ciphers) { Keyring = keyring } },
                };
                bool? doneOk = null;
                var transfer = new PendingTransfer(AreaLayouts.TheCoast, 999, New: false)
                {
                    Done = r => doneOk = r.Ok,
                };
                Assert.True(player.TryBeginAreaChange(transfer, out _));

                await global::Game.Transfer(player, transfer, _ => Task.FromResult(
                    new ControlResponse { Ok = false, Error = "instance unavailable" }));

                Assert.False(doneOk);
                Assert.False(player.LeavingArea);
                Assert.Equal(InstanceTransfer.CancelChangeArea(), wire.Read(2));
            }
        }
        finally
        {
            Program.Instances.Remove(instanceId, created);
            Program.Instances.Remove(abandonedInstanceId, created);
            AreaLayouts.CameFrom.TryRemove(newTicket, out _);
            AreaLayouts.ArrivalEntrances.TryRemove(newTicket, out _);
            AreaLayouts.CameFrom.TryRemove(abandonedTicket, out _);
            AreaLayouts.ArrivalEntrances.TryRemove(abandonedTicket, out _);
        }
    }

    sealed class Wire : IDisposable
    {
        readonly TcpListener listener = new(IPAddress.Loopback, 0);
        readonly TcpClient writer = new();
        readonly TcpClient reader;
        readonly ConnectionCiphers decrypt = new(new byte[64], true);
        public readonly ConnectionCiphers Ciphers = new(new byte[64], false);
        public NetworkStream Stream => writer.GetStream();
        public int Available => reader.Available;

        public Wire()
        {
            listener.Start();
            var accepting = listener.AcceptTcpClientAsync();
            writer.Connect(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
            reader = accepting.GetAwaiter().GetResult();
            reader.ReceiveTimeout = 5000;
        }

        public byte[] Read(int count)
        {
            var bytes = new byte[count];
            reader.GetStream().ReadExactly(bytes);
            decrypt.Receive.Process(bytes);
            return bytes;
        }

        public void Dispose()
        {
            reader.Dispose();
            writer.Dispose();
            listener.Stop();
        }
    }
}
