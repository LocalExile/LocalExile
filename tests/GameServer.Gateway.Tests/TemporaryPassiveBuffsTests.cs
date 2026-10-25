namespace GameServer.Gateway.Tests;

public class TemporaryPassiveBuffsTests
{
    static PassiveEffectCatalog.Buff Buff => new(503, 1090, "Adrenaline", 0, true, [], []);

    [Fact]
    public void Active_adrenaline_does_not_refresh_and_expires_at_the_original_absolute_deadline()
    {
        var buffs = new TemporaryPassiveBuffs();
        var start = DateTimeOffset.UnixEpoch;
        Assert.True(buffs.Grant(Buff, TimeSpan.FromSeconds(20), start));
        Assert.False(buffs.Grant(Buff, TimeSpan.FromSeconds(20), start.AddSeconds(10)));
        Assert.Single(buffs.Active(start.AddSeconds(19.999)));
        Assert.Empty(buffs.Active(start.AddSeconds(20)));
        Assert.True(buffs.Grant(Buff, TimeSpan.FromSeconds(20), start.AddSeconds(20)));
        buffs.Clear(); Assert.Empty(buffs.Active(start.AddSeconds(20)));
    }

    [Fact]
    public void Area_connections_share_the_same_timed_effect_without_changing_its_expiry()
    {
        string character = Guid.NewGuid().ToString("N");
        var start = DateTimeOffset.UnixEpoch;
        var first = TemporaryPassiveBuffs.For(character);
        Assert.True(first.Grant(Buff, TimeSpan.FromSeconds(20), start));
        var nextArea = TemporaryPassiveBuffs.For(character.ToUpperInvariant());
        Assert.Same(first, nextArea);
        Assert.Equal(start.AddSeconds(20), Assert.Single(nextArea.Active(start.AddSeconds(5))).ExpiresAt);
    }
}
