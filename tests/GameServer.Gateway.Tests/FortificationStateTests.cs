namespace GameServer.Gateway.Tests;

public class FortificationStateTests
{
    [Fact]
    public void Real_Behemoth_nodes_supply_maximum_regeneration_and_speed_inputs()
    {
        var tree = new PassiveTree();
        var catalog = new StatCatalog();
        var stats = tree.Stats([28815, 37308]);
        int Get(string name) => stats.GetValueOrDefault(catalog.Key(name)!.Value);
        Assert.Equal(1, Get("fortify_on_hit"));
        Assert.Equal(10, Get(FortificationState.MaximumStat));
        Assert.Equal(12, Get(FortificationState.LifeRegenPerStack));
        Assert.Equal(1, Get(FortificationState.AttackSpeedPerStack));
        var state = new FortificationState();
        Assert.True(state.GrantExplicitStacks(0, 30));
        var value = state.Read(0, Get(FortificationState.MaximumStat));
        Assert.Equal(30, value.Current);
        Assert.Equal(360, value.LifeRegenerationPerMinutePercent(Get(FortificationState.LifeRegenPerStack)));
        Assert.Equal(30, value.IncreasedAttackSpeedPercent(Get(FortificationState.AttackSpeedPerStack)));
        Assert.Equal(.7, value.HitDamageFactor, 12);
        // Removing Tireless changes the effective limit; removing Dire Rampart removes its per-stack effects.
        Assert.Equal(20, state.Read(0).Current);
        Assert.Equal(0, value.LifeRegenerationPerMinutePercent(0));
        Assert.Equal(0, value.IncreasedAttackSpeedPercent(0));
    }

}
