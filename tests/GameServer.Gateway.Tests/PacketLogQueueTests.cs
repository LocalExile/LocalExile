using GameServer.Core.Diagnostics;

namespace GameServer.Gateway.Tests;

public sealed class PacketLogQueueTests
{
    [Fact]
    public void Blocked_sink_does_not_block_producers_and_overflow_is_reported()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var finished = new ManualResetEventSlim();
        var lines = new List<string>();
        var queue = new PacketLogQueue(line =>
        {
            if (line == "first") { entered.Set(); release.Wait(); }
            lines.Add(line);
            if (line.EndsWith("third")) finished.Set();
        }, capacity: 2);
        queue.Write("first");
        entered.Wait();
        try
        {
            queue.Write("second");
            queue.Write("third");
            queue.Write("overflow");
        }
        finally { release.Set(); }
        finished.Wait();
        queue.Complete();
        Assert.Equal(new[] { "first", "[packet-log] dropped 1 summaries while the log sink was busy", "second" + Environment.NewLine + "third" }, lines);
    }

    [Fact]
    public void Failed_sink_does_not_stop_later_output()
    {
        using var finished = new ManualResetEventSlim();
        using var failed = new ManualResetEventSlim();
        var queue = new PacketLogQueue(line =>
        {
            if (line == "failure") { failed.Set(); throw new ObjectDisposedException("log destination"); }
            finished.Set();
        });
        queue.Write("failure");
        failed.Wait();
        queue.Write("next");
        finished.Wait();
        queue.Complete();
    }
}
