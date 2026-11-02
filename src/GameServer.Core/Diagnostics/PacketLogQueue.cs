using System.Collections.Concurrent;
using System.Text;

namespace GameServer.Core.Diagnostics;

/// <summary>Best-effort packet summaries: a slow log sink must not hold a connection's send lock.</summary>
public sealed class PacketLogQueue
{
    readonly BlockingCollection<string> pending;
    readonly Action<string> write;
    long dropped;

    public PacketLogQueue(Action<string> write, int capacity = 1024)
    {
        this.write = write;
        pending = new(capacity);
        new Thread(Drain) { IsBackground = true, Name = "packet-log" }.Start();
    }

    public void Write(string line)
    {
        if (!pending.TryAdd(line)) Interlocked.Increment(ref dropped);
    }

    // Used at shutdown/tests without waiting on the sink on a gameplay thread.
    public void Complete() => pending.CompleteAdding();

    void Drain()
    {
        foreach (string line in pending.GetConsumingEnumerable())
        {
            try
            {
                long lost = Interlocked.Exchange(ref dropped, 0);
                if (lost != 0) write($"[packet-log] dropped {lost} summaries while the log sink was busy");
                if (pending.TryTake(out string? next))
                {
                    var batch = new StringBuilder(line);
                    batch.Append(Environment.NewLine).Append(next);
                    for (int count = 2; count < 32 && pending.TryTake(out next); count++)
                        batch.Append(Environment.NewLine).Append(next);
                    write(batch.ToString());
                }
                else write(line);
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException)
            { /* Diagnostic output must not terminate the server. */ }
        }
    }
}
