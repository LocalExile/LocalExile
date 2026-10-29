namespace GameServer.Gateway.Tests;

public class VerifiedGamblerGraphTests
{
    [Fact]
    public void Unverified_installed_graph_bytes_are_refused_without_fallback()
    {
        Assert.Throws<AscendancyRuleException>(() => VerifiedGamblerGraph.Verify([], []));
        Assert.Throws<AscendancyRuleException>(() => VerifiedGamblerGraph.Verify([1, 2, 3], [4, 5, 6]));
    }
}
