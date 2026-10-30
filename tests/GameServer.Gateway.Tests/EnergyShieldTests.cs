public class EnergyShieldTests
{
    static int Get(string _) => 0;
    [Fact] public void AbsorptionOverflowChaosAndZeroDamage()
    {
        var es = new EnergyShield(); es.SetMaximum(100, true);
        Assert.Equal(0, es.Damage(60, false, 0)); Assert.Equal(40u, es.Current);
        Assert.Equal(10, es.Damage(10, true, 1)); Assert.Equal(40u, es.Current);
        Assert.Equal(20, es.Damage(60, false, 2)); Assert.Equal(0u, es.Current);
        es.Advance(2, 4, Get, true); Assert.Equal(0u, es.Current);
        es.Damage(0, false, 4); es.Advance(4, 5, Get, true); Assert.Equal(33u, es.Current);
    }
}
