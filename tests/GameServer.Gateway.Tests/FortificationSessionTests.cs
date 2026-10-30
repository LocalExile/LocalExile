namespace GameServer.Gateway.Tests;

public class FortificationSessionTests
{
    [Fact]
    public void One_presentation_snapshot_cannot_expire_halfway_through_its_buff_and_stat_records()
    {
        var session = new FortificationSession();
        session.State.GrantExplicitStacks(0, 20);
        using (session.Capture(5))
        {
            Assert.Equal(20, session.Read(5).Current);
            Assert.Equal(20, session.Read(7).Current); // Later wall-clock sampling during the same send construction.
        }
        Assert.Equal(0, session.Read(7).Current);
    }

}
