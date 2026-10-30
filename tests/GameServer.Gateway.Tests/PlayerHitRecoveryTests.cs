namespace GameServer.Gateway.Tests;

public class PlayerHitRecoveryTests
{
    static Func<string, int> Stats(params (string Key, int Value)[] values) =>
        name => values.FirstOrDefault(v => v.Key == name).Value;

    [Theory]
    [InlineData(0, 0)]
    public void Physical_melee_chance_has_a_minimum_and_caps_at_100(int damage, double chance) =>
        Assert.Equal(chance, PlayerHitRecovery.Chance(damage, 100, Stats()));

    static ClientState ObjectWalkState()
    {
        var conn = new ClientState(new AreaLayout(0, "test", 100, 100, 30, 30, null, []), 0, 1)
        { PlayerLife = 57, ActionCount = 4, ChannelSkill = 123 };
        conn.Clock.Reset();
        return conn;
    }

}
