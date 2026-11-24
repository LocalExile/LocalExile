namespace GameServer.Gateway.Tests;

public sealed class ProjectileVisualOwnershipTests
{
    [Fact]
    public void Visual_births_and_retirements_preserve_ownership_across_workers()
    {
        using var fixture = new TriggeredSummonTests.Fixture();
        var player = fixture.Player;
        const int workers = 4;
        const int objectsPerWorker = 1000;
        using var start = new Barrier(workers);
        var failures = new System.Collections.Concurrent.ConcurrentQueue<uint>();
        var threads = new Thread[workers];

        for (int worker = 0; worker < workers; worker++)
        {
            int first = worker * objectsPerWorker + 1;
            threads[worker] = new Thread(() =>
            {
                start.SignalAndWait();
                for (int offset = 0; offset < objectsPerWorker; offset++)
                {
                    uint id = (uint)(first + offset);
                    player.TrackProjectileObject(id);
                    if (offset % 2 == 0 && !player.UntrackProjectileObject(id)) failures.Enqueue(id);
                }
            }) { IsBackground = true };
            threads[worker].Start();
        }

        foreach (var thread in threads) thread.Join();
        Assert.Empty(failures);
        Assert.Equal(workers * objectsPerWorker / 2, player.ProjectileObjects.Count);
    }
}
