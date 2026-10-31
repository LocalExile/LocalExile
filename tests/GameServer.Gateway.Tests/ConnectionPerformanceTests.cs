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

    [Fact]
    public void A_messages_handling_reaches_the_meter_with_its_id()
    {
        var seen = new System.Collections.Concurrent.ConcurrentBag<(string? Phase, string? Operation)>();
        using var listener = new System.Diagnostics.Metrics.MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == "LocalExile.Gateway" && instrument.Name == "localexile.gateway.duration") l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<double>((_, _, tags, _) =>
        {
            string? phase = null, operation = null;
            foreach (var t in tags)
                if (t.Key == "phase") phase = t.Value as string;
                else if (t.Key == "operation") operation = t.Value as string;
            seen.Add((phase, operation));
        });
        listener.Start();
        var performance = new ConnectionPerformance();

        using (performance.Message(0xfffe)) { }
        ConnectionPerformance.Server("LevelGen", TimeSpan.FromMilliseconds(3), "TestArea_fffe");

        Assert.Equal(1, performance.Read(LatencyPhase.Message).Count);
        Assert.Contains(("Message", "0xfffe"), seen);
        Assert.Contains(("LevelGen", "TestArea_fffe"), seen);
    }

}
