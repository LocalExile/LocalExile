namespace GameServer.Gateway.Tests;

// The two kill-driven effects of the belt: Rampage's rarity per streak and Soul Eater's eaten souls.
public sealed class RampageAndSoulEaterTests
{
    [Fact]
    public void Rampage_rarity_counts_whole_groups_of_kills()
    {
        Assert.Equal(0, Game.RampageRarity(0, 15));
        Assert.Equal(0, Game.RampageRarity(14, 15));
        Assert.Equal(1, Game.RampageRarity(15, 15));
        Assert.Equal(6, Game.RampageRarity(100, 15));
        Assert.Equal(66, Game.RampageRarity(1000, 15));
        Assert.Equal(0, Game.RampageRarity(100, 0));
    }

    [Fact]
    public void Soul_eater_gains_caps_and_decays_after_four_seconds()
    {
        var souls = new SoulEaterState();
        Assert.Equal(0, souls.Stacks(0));
        Assert.Equal(1, souls.Soul(0));
        Assert.Equal(2, souls.Soul(0.1));
        Assert.Equal(2, souls.Stacks(4.0));   // still Recently
        Assert.Equal(1, souls.Stacks(4.6));   // one lost after 4.1 s idle
        Assert.Equal(1, souls.Stacks(5.0));

        var full = new SoulEaterState();
        for (int i = 0; i < 60; i++) full.Soul(10);
        Assert.Equal(SoulEaterState.MaximumStacks, full.Stacks(10));
        Assert.Equal(SoulEaterState.MaximumStacks - 1, full.Stacks(14.5));

        souls.Clear();
        Assert.Equal(0, souls.Stacks(20));
        Assert.False(souls.Enabled);
    }
}
