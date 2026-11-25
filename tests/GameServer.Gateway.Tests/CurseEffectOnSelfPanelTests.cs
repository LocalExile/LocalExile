namespace GameServer.Gateway.Tests;

public sealed class CurseEffectOnSelfPanelTests
{
    [Fact]
    public void Curse_effect_on_self_is_published_beside_its_base_stat()
    {
        int Stats(uint stat) => stat == Stat.CurseEffectOnSelfPlusPct ? -23 : 0;

        var panel = PanelStats.Compute(Stats);

        Assert.Contains((Stat.VirtualCurseEffectOnSelfPlusPct, -23), panel);
        Assert.Equal(-23, PanelStats.Of(Stat.VirtualCurseEffectOnSelfPlusPct, Stats));
        Assert.DoesNotContain(PanelStats.Compute(_ => 0), s => s.Stat == Stat.VirtualCurseEffectOnSelfPlusPct);
    }
}
