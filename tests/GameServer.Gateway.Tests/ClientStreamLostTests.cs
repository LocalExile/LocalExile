namespace GameServer.Gateway.Tests;

/// <summary>A client stream that no longer frames is closed after a run of such reads (ClientInbox.UnframedRun).</summary>
public class ClientStreamLostTests
{
    [Fact]
    public void Only_an_unbroken_run_of_unframed_reads_counts_toward_closing()
    {
        int run = 0;
        // A healthy area change: one unframed read (0x2cb), then framed messages again.
        run = ClientInbox.UnframedRun(run, framed: 0, unknownBytes: 870);
        Assert.Equal(1, run);
        run = ClientInbox.UnframedRun(run, framed: 2, unknownBytes: 0);
        Assert.Equal(0, run);
        // A read with framed messages and an unframed rest is still a working stream.
        Assert.Equal(0, ClientInbox.UnframedRun(5, framed: 1, unknownBytes: 3));
        // An empty read changes nothing.
        Assert.Equal(4, ClientInbox.UnframedRun(4, framed: 0, unknownBytes: 0));
        // A lost stream: every read garbage.
        for (int i = 0; i < ClientInbox.LostAfterUnframedReads; i++) run = ClientInbox.UnframedRun(run, framed: 0, unknownBytes: 7);
        Assert.Equal(ClientInbox.LostAfterUnframedReads, run);
    }
}
