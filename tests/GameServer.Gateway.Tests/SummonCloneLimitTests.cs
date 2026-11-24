namespace GameServer.Gateway.Tests;

// The Blink / Mirror Arrow clone cap read base_number_of_mirror_arrow_clones_allowed, which is not a
// 3.29.3c Stats row, so SummonLimit always fell back to 100. The real cap stat is
// base_number_of_clones_allowed (Stats 8217); the transfigured clones already had their own limit stats.
public sealed class SummonCloneLimitTests
{
    [Fact]
    public void Blink_and_Mirror_Arrow_clones_read_the_real_clone_cap_stat()
    {
        var catalog = new SummonCatalog();
        Assert.Equal(Stat.BaseNumberOfClonesAllowed, catalog.Find("BlinkArrow")!.LimitStat);
        Assert.Equal(Stat.BaseNumberOfClonesAllowed, catalog.Find("MirrorArrow")!.LimitStat);
        Assert.Equal(Stat.BaseNumberOfBlinkMirrorArrowRainOfArrowsClones, catalog.Find("BlinkArrowAltX")!.LimitStat);
        Assert.Equal(Stat.BaseNumberOfBlinkMirrorArrowElementalHitClones, catalog.Find("BlinkArrowAltY")!.LimitStat);
    }
}
