using GameServer.Core.World;
using Xunit;

public class BlockerSetTests
{
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
