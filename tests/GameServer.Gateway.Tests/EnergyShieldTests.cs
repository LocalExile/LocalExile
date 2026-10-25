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
    [Fact] public void DelayedTickOnlyRechargesAfterBoundary()
    {
        var es = new EnergyShield(); es.SetMaximum(300, true); es.Damage(300, false, 10);
        es.Advance(10, 13, Get, true); Assert.Equal(100u, es.Current);
        es.Damage(1, true, 13); es.Advance(13, 14.99, Get, true); Assert.Equal(100u, es.Current);
    }
    [Fact] public void FractionsEqualAcrossCadencesAndRefillAndDeath()
    {
        var fast = new EnergyShield(); var slow = new EnergyShield();
        foreach (var es in new[] { fast, slow }) { es.SetMaximum(13, true); es.Damage(13, false, 0); }
        for (int i=0;i<100;i++) fast.Advance(i * .04, (i+1)*.04, Get, true);
        slow.Advance(0, 4, Get, true); Assert.InRange(Math.Abs((int)fast.Current-(int)slow.Current),0,1);
        fast.SetMaximum(5); Assert.True(fast.Current<=5); fast.SetMaximum(10,true); Assert.Equal(10u,fast.Current);
        fast.Advance(4,5,Get,false); Assert.Equal(0u,fast.Current);
    }
    [Fact] public void IncreasedEnergyShieldAndFasterStart()
    {
        Assert.Equal(178u, EnergyShield.Max(100,78)); Assert.Equal(0u,EnergyShield.Max(100,-200));
        Assert.Equal(1, EnergyShield.Delay(s=>s=="energy_shield_delay_-%"?100:0));
        Assert.Equal(2000, EnergyShield.RechargePerMinute(100,Get));
    }
    [Fact] public void NativePredictionIncludesRechargeOnlyDuringItsActivePhase()
    {
        int Stats(string key)=>key=="base_energy_shield_regeneration_rate_per_minute"?60:0;
        Assert.Equal(60,EnergyShield.PredictionPerMinute(300,Stats,false));
        Assert.Equal(6060,EnergyShield.PredictionPerMinute(300,Stats,true));
        int Disabled(string key)=>key=="cannot_recharge_energy_shield"?1:Stats(key);
        Assert.Equal(60,EnergyShield.PredictionPerMinute(300,Disabled,true));
    }
    [Fact] public void WithoutRegenerationDamageDuringRechargeRestartsTheWholeDelay()
    {
        var es = new EnergyShield(); es.SetMaximum(300, true); es.Damage(200, false, 0);
        es.Advance(0, 1.99, Get, true); Assert.Equal(100u, es.Current);
        es.Advance(1.99, 2.5, Get, true); Assert.Equal(150u, es.Current);
        es.Damage(30, false, 2.5); Assert.False(es.Recharging);
        es.Advance(2.5, 4.49, Get, true); Assert.Equal(120u, es.Current);
        Assert.Equal(0, EnergyShield.PredictionPerMinute(300, Get, es.Recharging));
        es.Advance(4.49, 4.625, Get, true); Assert.Equal(132u, es.Current);
    }
    [Fact] public void TwoPercentMasteryKeepsRegeneratingWhileRechargeIsInterrupted()
    {
        int Mastery(string key) => key == "base_energy_shield_regeneration_rate_per_minute_%" ? 120 : 0;
        var es = new EnergyShield(); es.SetMaximum(185, true); es.Damage(100, false, 0);
        Assert.Equal(222, EnergyShield.RegenPerMinute(185, Mastery));
        es.Advance(0, 1, Mastery, true); Assert.Equal(88u, es.Current); Assert.False(es.Recharging);
        es.Damage(10, false, 1);
        es.Advance(1, 2, Mastery, true); Assert.Equal(82u, es.Current); Assert.False(es.Recharging);
    }
}
