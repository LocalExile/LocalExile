namespace GameServer.Gateway.Tests;

public sealed class MinionLeechTests
{
    [Fact]
    public void One_hit_recovers_two_percent_per_second_until_the_ten_percent_instance_cap()
    {
        var leech = new MinionLeech();
        leech.Add(0, 1000, 100, 9999);
        Assert.Equal(120u, leech.Recover(0, 1, 1000, 100));
        Assert.Equal(200u, leech.Recover(1, 8, 1000, 120));
        Assert.Equal(200u, leech.Recover(8, 9, 1000, 200));
    }

    [Fact]
    public void Total_cap_integrates_expiry_and_never_recovers_before_the_hit()
    {
        var leech = new MinionLeech();
        for (int i = 0; i < 20; i++) leech.Add(1, 1000, 1, 100);
        Assert.Equal(1u, leech.Recover(0, 1, 1000, 1));
        Assert.Equal(201u, leech.Recover(1, 2, 1000, 1));
        Assert.Equal(1000u, leech.Recover(2, 7, 1000, 201));
        Assert.Equal(10u, leech.Recover(7, 8, 1000, 10)); // full life removed all instances
    }

    [Fact]
    public void Fractional_recovery_is_retained_and_full_life_does_not_store_overleech()
    {
        var leech = new MinionLeech();
        leech.Add(0, 100, 1, 10);
        uint life = 1;
        for (int i = 1; i <= 4; i++) life = leech.Recover((i - 1) * .125, i * .125, 100, life);
        Assert.Equal(2u, life);
        leech.Clear(); leech.Add(1, 100, 100, 10);
        Assert.Equal(1u, leech.Recover(1, 2, 100, 1));
    }
}
