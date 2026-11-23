namespace GameServer.Gateway.Tests;

public class ObjectIdAllocationTests
{
    // "NextObjectId++" read the instance's shared counter and wrote it back without a lock: the projectile pump spawning a
    // volley's arrows and the connection's own thread casting (or a bot in the same instance) could read one value and
    // build two objects with one id. The world output refuses the second creation ("World object ID is still reserved or
    // published") and closes the connection (live 2026-11-11, Lightning Arrow in a fight).
    [Fact]
    public void Concurrent_takes_never_repeat_an_id()
    {
        var world = new InstanceWorld();
        const int producers = 8, each = 500;
        var ids = new System.Collections.Concurrent.ConcurrentBag<uint>();

        Parallel.For(0, producers, _ =>
        {
            for (int i = 0; i < each; i++) ids.Add(world.TakeObjectId());
        });

        Assert.Equal(producers * each, ids.Distinct().Count());
    }

    // The first id is the counter's own value (0x04000000), as the post-increment gave before, and the next is one more.
    [Fact]
    public void The_first_take_is_the_counters_value()
    {
        var world = new InstanceWorld();

        Assert.Equal(0x04000000u, world.TakeObjectId());
        Assert.Equal(0x04000001u, world.TakeObjectId());
    }
}
