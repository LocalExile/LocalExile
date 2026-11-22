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
    public void Prepared_instance_reuses_cached_layout_on_enter_and_expires_if_abandoned()
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
        int builds = 0;
        var first = recreated.PreparedLayout(42, 0, () => { builds++; return (42u, walkableLayout); });
        var entered = store.Enter(9001, 100);
        Assert.Same(recreated, entered);
        Assert.Equal(1, entered.Connections);
        Assert.Null(entered.EmptySince);
        var second = entered.PreparedLayout(42, 0, () => { builds++; return (99u, walkableLayout); });
        Assert.Equal(1, builds);
        Assert.Equal(42u, second.Seed);
        Assert.Same(first.Layout, second.Layout);

        // Dropping terrain plugin states invalidates the prepared layout so the next lookup rebuilds it.
        entered.DropTerrainPluginStates();
        entered.PreparedLayout(42, 0, () => { builds++; return (42u, walkableLayout); });
        Assert.Equal(2, builds);

        // A layout without walkability (an uncaptured unsupported area) is not pinned across calls.
        var uncaptured = new AreaLayout(0x9999, "uncaptured", 0, 0, 1, 1, null, []);
        entered.ClearPreparedLayout();
        entered.PreparedLayout(7, 0, () => { builds++; return (7u, uncaptured); });
        entered.PreparedLayout(7, 0, () => { builds++; return (7u, walkableLayout); });
        Assert.Equal(4, builds);

        Assert.Equal("02B5", Convert.ToHexString(InstanceTransfer.CancelChangeArea()));
    }

    [GameFilesFact]
    public async Task Transfer_pregenerates_destination_area_before_0x27_and_cancels_on_refusal()
    {
        const ulong instanceId = 900_000_042;
        const long created = 1_795_349_327_000;
        const uint authSeed = 123_456_789;
        const uint newTicket = 0x55aa1234;
        var townLayout = new AreaLayout(AreaLayouts.LioneyesWatch, "1_1_town", 690, 414, 197, 261, (_, _) => true, []);
        var keyring = new InstanceKeyring(new byte[32]);

        try
        {
            // Success: Transfer pre-generates the destination area while the client shows "Entering <area>", before 0x27.
            using (var wire = new Wire())
            {
                var conn = new ClientState(townLayout, 1, 1)
                {
                    Character = "TransferPrepTester",
                    League = "Standard",
                    Keyring = keyring,
                    Wire = ("test", wire.Stream, wire.Ciphers),
                };
                var transfer = new PendingTransfer(AreaLayouts.TheCoast, 0, New: true);
                Assert.True(conn.TryBeginAreaChange(transfer, out _));

                await global::Game.Transfer("test", wire.Stream, wire.Ciphers, conn, transfer, _ => Task.FromResult(
                    new ControlResponse
                    {
                        Ok = true,
                        Area = AreaLayouts.TheCoast,
                        Instance = instanceId,
                        Created = created,
                        Seed = authSeed,
                        Ticket = newTicket,
                    }));

                byte[] expected = InstanceTransfer.ConnectInstance(
                    GatewayEndpoint.Host, GatewayEndpoint.Port, newTicket, AreaLayouts.TheCoast, keyring.Key(newTicket));
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

            // Refusal: Transfer restores LeavingArea and sends 0x2b5 so the client hides "Entering <area>".
            using (var wire = new Wire())
            {
                var conn = new ClientState(townLayout, 2, 1)
                {
                    Character = "TransferPrepTester",
                    League = "Standard",
                    Keyring = keyring,
                    Wire = ("test", wire.Stream, wire.Ciphers),
                };
                var transfer = new PendingTransfer(AreaLayouts.TheCoast, 999, New: false);
                Assert.True(conn.TryBeginAreaChange(transfer, out _));

                await global::Game.Transfer("test", wire.Stream, wire.Ciphers, conn, transfer, _ => Task.FromResult(
                    new ControlResponse { Ok = false, Error = "instance unavailable" }));

                Assert.False(conn.LeavingArea);
                Assert.Equal(InstanceTransfer.CancelChangeArea(), wire.Read(2));
            }
        }
        finally
        {
            AreaLayouts.CameFrom.TryRemove(newTicket, out _);
            AreaLayouts.ArrivalEntrances.TryRemove(newTicket, out _);
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
