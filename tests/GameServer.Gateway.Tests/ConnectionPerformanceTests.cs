using System.Diagnostics;

namespace GameServer.Gateway.Tests;

public class ConnectionPerformanceTests
{
    [Fact]
    public async Task Abandoning_a_closed_connections_backlog_clears_its_depth()
    {
        var inbox = new ClientInbox(new ConnectionPerformance());
        inbox.Post([(0x25, new byte[] { 0, 0x25 })], []);
        inbox.Complete(); inbox.DiscardPending();
        Assert.Equal(0, inbox.PendingBatches);
        inbox.Post([(0x25, new byte[] { 0, 0x25 })], []);
        Assert.Equal(0, inbox.PendingBatches);
        await foreach (var _ in inbox.ReadAllAsync()) Assert.Fail("A discarded batch survived connection shutdown.");
    }

}
