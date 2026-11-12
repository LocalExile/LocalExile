namespace GameServer.Gateway.Tests;

public class NaturalSocketLayoutTests
{
    [Fact]
    public void Ordinary_armour_drops_vary_within_the_level_and_base_limits()
    {
        var drops = new ItemDrops(Program.Items);
        var rng = new Random(20323);
        foreach (var (level, max) in new[] { (24, 3), (35, 5), (60, 6) })
        {
            var counts = new HashSet<int>();
            foreach (var drop in drops.Container(level, 2000, 0, rng).Where(d => d.Type.Class == "Body Armour"))
            {
                int n = drop.Item.Sockets.Count;
                Assert.InRange(n, 1, max);
                Assert.Equal(n, drop.Item.Links!.Sum(group => (int)group));
                counts.Add(n);
            }
            Assert.True(counts.Count > 1, $"level {level}, socket counts: {string.Join(",", counts)}");
        }
    }
}
