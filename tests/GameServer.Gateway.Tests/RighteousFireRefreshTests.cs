namespace GameServer.Gateway.Tests;

public class RighteousFireRefreshTests
{
    [Fact]
    public void Effective_gem_changes_refresh_the_running_fire_without_replacing_its_identity_or_auras()
    {
        var running = new RighteousFireState(1, 2, 3, 4, 4200, 5400, 18);
        var original = running;
        running.Auras[123] = 7;

        Assert.True(running.Refresh(4500, 5500, 21));
        Assert.Same(original, running);
        Assert.Equal((4500, 5500, 21), (running.DamagePctPerMinute, running.BurnPctPerMinute, running.Radius));
        Assert.Equal((ushort)1, running.Buff);
        Assert.Equal((ushort)2, running.SelfAura);
        Assert.Equal((uint)3, running.Ring);
        Assert.Equal((ushort)4, running.Skill);
        Assert.Equal((ushort)7, running.Auras[123]);
        Assert.False(running.Refresh(4200, 5400, 21));
        Assert.Equal((4200, 5400), (running.DamagePctPerMinute, running.BurnPctPerMinute));
    }
}
