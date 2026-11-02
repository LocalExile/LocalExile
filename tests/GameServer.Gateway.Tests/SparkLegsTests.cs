namespace GameServer.Gateway.Tests;

public sealed class SparkLegsTests
{
    // Live 3.29.3b sparks (Frida 2026-10-31, scripts/re/frida/trace-spark-legs.js): the use sequence, the spark's index in
    // its volley, its start and aim cells, and the target of each leg the client flew without touching terrain.
    [Theory]
    [InlineData(5310, 0, 1226, 395, 1242, 397, new[] { 1249, 398, 1269, 400, 1286, 405, 1306, 408, 1321, 423, 1323, 445 })]
    [InlineData(4278, 1, 636, 784, 618, 795, new[] { 620, 794, 615, 808, 601, 819, 583, 825, 560, 828 })]
    [InlineData(4434, 0, 635, 751, 626, 776, new[] { 629, 768, 626, 785, 625, 804, 623, 824, 613, 837, 614, 854 })]
    public void Legs_follow_the_clients_own_sparks(int sequence, int index, int x, int y, int aimX, int aimY, int[] targets)
    {
        var legs = new SparkLegs((ushort)sequence, index, Program.PlayerId, 3.8, 0, 38.64);
        Assert.Equal(3.8, legs.Lifetime, 5);
        var leg = legs.First(new(x, y), new(aimX, aimY));
        var cells = new List<int>();
        while (true)
        {
            cells.Add((int)leg.Target.X); cells.Add((int)leg.Target.Y);
            Assert.InRange(leg.Speed, .8 * 38.64 - 1e-4, 1.2 * 38.64 + 1e-4);
            if (cells.Count == targets.Length) break;
            leg = legs.Next(leg.Target, blocked: false);
        }
        Assert.Equal(targets, cells);
    }
}
