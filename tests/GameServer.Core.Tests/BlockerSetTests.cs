using GameServer.Core.World;
using Xunit;

public class BlockerSetTests
{
    [Fact]
    public void Updated_exclusion_views_match_a_full_scan_across_buckets_and_hull_sizes()
    {
        var random = new Random(53);
        var bodies = Enumerable.Range(0, 40).Select(_ => new ClientPathfinder.Blocker(
            random.Next(-30, 100), random.Next(-30, 100), random.Next(0, 20))).ToArray();
        var set = new BlockerSet(bodies);
        var view = set.Without(2, 17);
        for (int step = 0; step < 30; step++)
        {
            int index = random.Next(bodies.Length);
            bodies[index] = new(random.Next(-30, 100), random.Next(-30, 100), random.Next(0, 30))
                { CollisionRadius = step % 2 == 0 ? 35 : null };
            set.Replace(index, bodies[index]);
            var expected = bodies.Where((_, i) => i is not 2 and not 17).ToArray();
            Assert.Equal(expected.Length, view.Count);
            Assert.Equal(expected, view.ToArray());
            for (int i = 0; i < expected.Length; i++) Assert.Equal(expected[i], view[i]);
            for (int q = 0; q < 300; q++)
            {
                int x = random.Next(-40, 130), y = random.Next(-40, 130), threshold = random.Next(1, 12), radius = random.Next(1, 30);
                Assert.Equal(ClientPathfinder.ObjectBlocks(expected, x, y, threshold, radius),
                    ClientPathfinder.ObjectBlocks(view, x, y, threshold, radius));
            }
        }
    }

    [Fact]
    public void Indexed_queries_do_not_allocate_an_iterator_per_cell()
    {
        var set = new BlockerSet([new(20, 20, 3), new(50, 50, 9)]);
        var view = set.Without(0);
        for (int i = 0; i < 100; i++) ClientPathfinder.ObjectBlocks(view, 48, 48, 2, 5);
        long before = GC.GetAllocatedBytesForCurrentThread();
        bool blocked = false;
        for (int i = 0; i < 1000; i++) blocked |= ClientPathfinder.ObjectBlocks(view, 48, 48, 2, 5);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(blocked);
        Assert.Equal(0, allocated);
    }

    [Fact]
    public void A_bucketed_set_answers_every_query_as_the_full_list_does()
    {
        var random = new Random(7);
        var blockers = new List<ClientPathfinder.Blocker>();
        for (int i = 0; i < 400; i++)
        {
            var b = new ClientPathfinder.Blocker(random.Next(0, 300), random.Next(0, 300), random.Next(-1, 12));
            blockers.Add(i % 9 == 0 ? b with { CollisionRadius = random.NextDouble() * 20 } : b);
        }
        var set = new BlockerSet(blockers);
        Assert.Equal(blockers.Count, set.Count);
        for (int q = 0; q < 20_000; q++)
        {
            int x = random.Next(-10, 310), y = random.Next(-10, 310), threshold = random.Next(1, 10), radius = random.Next(1, 30);
            Assert.Equal(ClientPathfinder.ObjectBlocks(blockers, x, y, threshold, radius),
                ClientPathfinder.ObjectBlocks(set, x, y, threshold, radius));
        }
    }
}
