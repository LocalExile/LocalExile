using System.Diagnostics;
using System.Reflection;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

namespace GameServer.Gateway.Tests;

public class DelveClientStreamIntegrationTests
{
    [Fact]
    public async Task A_fragmented_native_route_resets_lost_stream_detection_only_after_it_frames()
    {
        var conn = new ClientState(new AreaLayout(1, "stream-test", 100, 100, 20, 20, null, []), 1, 1);
        var inbox = new ClientInbox();
        using var wire = new VaalSoulGatewayTests.Wire();
        var peer = new ConnectionCiphers(new byte[64], isClient: true);
        var receive = typeof(Program).GetMethod("OnClientData", BindingFlags.NonPublic | BindingFlags.Static)!;
        async Task Feed(byte[] plain)
        {
            byte[] encrypted = plain.ToArray();
            peer.Send.Process(encrypted);
            await (Task)receive.Invoke(null,
                ["delve-stream-integration", wire.Stream, wire.Ciphers, encrypted, conn, inbox, Stopwatch.GetTimestamp()])!;
        }
        for (int i = 0; i < ClientInbox.LostAfterUnframedReads - 1; i++) await Feed([0xff, 0xff]);
        Assert.Null(conn.CloseReason);
        byte[] route = Convert.FromHexString("0241020000000000000000000000020000000000000000000000000000000200000001");
        await Feed(route[..1]);
        Assert.Equal(ClientInbox.LostAfterUnframedReads - 1, conn.UnframedRun);
        Assert.Equal(route[..1], conn.Carry);
        await Feed(route[1..]);
        Assert.Equal(0, conn.UnframedRun);
        Assert.Empty(conn.Carry);
        Assert.Null(conn.CloseReason);
        for (int i = 0; i < ClientInbox.LostAfterUnframedReads; i++) await Feed([0xff, 0xff]);
        Assert.Contains("stream no longer decodes", conn.CloseReason);
        inbox.Complete();
        var routes = new List<byte[]>();
        await foreach (var batch in inbox.ReadAllAsync())
            routes.AddRange(batch.Messages.Where(m => m.Id == InstanceDelve.RouteId).Select(m => m.Bytes));
        Assert.Equal(route, Assert.Single(routes));
    }
}
