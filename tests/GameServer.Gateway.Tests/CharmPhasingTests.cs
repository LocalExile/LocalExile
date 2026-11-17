namespace GameServer.Gateway.Tests;

public sealed class CharmPhasingTests
{
    [Fact]
    public void Timed_phasing_answers_the_while_phasing_condition()
    {
        var conn = new ClientState(new(1, "phasing", 100, 100, 100, 100, null, []), 1, 1)
            { Character = "Phasing" + Guid.NewGuid().ToString("N") };
        conn.PhasingUntil = conn.Clock.Elapsed.TotalSeconds + 4;
        Assert.True(conn.IsPhasing);
        Assert.True(Game.ItemConditionFlags(conn, conn.Clock.Elapsed.TotalSeconds)["_while_phasing"]);
    }
}
