namespace GameServer.Gateway.Tests;

public partial class ConditionalCriticalGatewayTests
{
    [Theory]
    [InlineData("melee", "life", false)]
    [InlineData("melee", "life", true)]
    [InlineData("melee", "focus", false)]
    [InlineData("melee", "focus", true)]
    [InlineData("melee", "raw", false)]
    [InlineData("melee", "raw", true)]
    [InlineData("object-bow", "life", false)]
    [InlineData("object-bow", "life", true)]
    [InlineData("object-bow", "focus", false)]
    [InlineData("object-bow", "focus", true)]
    [InlineData("object-bow", "raw", false)]
    [InlineData("object-bow", "raw", true)]
    [InlineData("ground-bow", "life", false)]
    [InlineData("ground-bow", "life", true)]
    [InlineData("ground-bow", "focus", false)]
    [InlineData("ground-bow", "focus", true)]
    [InlineData("ground-bow", "raw", false)]
    [InlineData("ground-bow", "raw", true)]
    public async Task Ordinary_use_keeps_admitted_luck_while_its_confirmation_is_queued(
        string path, string source, bool initiallyLucky)
    {
        var random = new Draws(9000, 0);
        using var f = new Contact(random, weaponName: path == "melee" ? "Rusted Sword" : "Crude Bow",
            distance: path == "melee" ? 2 : 60);
        f.Set("extra_critical_rolls_while_on_low_life", source == "life" ? 1 : 0);
        f.Set("extra_critical_rolls_during_focus", source == "focus" ? 1 : 0);
        void SetLucky(bool active)
        {
            if (source == "life") f.Conn.PlayerLife = active ? 50u : 51u;
            if (source == "focus") f.Set("has_focus", active ? 1 : 0);
            if (source == "raw") f.Set("extra_critical_rolls", active ? 1 : 0);
        }
        SetLucky(initiallyLucky);
        await f.SendGate.WaitAsync();
        Task use;
        try
        {
            use = path == "ground-bow" ? f.GroundUse() : f.Use();
            Assert.True(f.Conn.NextAttackAt > 0); // Admission already reserved the swing; no hit has occurred.
            Assert.Equal(0, random.Calls);
            Assert.Equal(f.Target.MaxLife, f.Target.Life);
            lock (f.Conn.Monsters) SetLucky(!initiallyLucky);
        }
        finally { f.SendGate.Release(); }
        await use;
        await f.HitLands();
        Assert.Equal(initiallyLucky ? 2 : 1, random.Calls);
        Assert.Equal(initiallyLucky ? 1500u : 1000u, f.Target.MaxLife - f.Target.Life);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Ordinary_melee_captures_luck_after_approaching_before_the_swing(bool initiallyLucky)
    {
        var random = new Draws(9000, 0);
        using var f = new Contact(random, weaponName: "Rusted Sword", distance: 30);
        f.Conn.PlayerLife = initiallyLucky ? 50u : 51u;
        var use = f.Use();
        Assert.Equal(0, f.Conn.NextAttackAt); // The approach has begun, but the actual swing has not.
        Assert.Equal(0, random.Calls);
        f.Conn.PlayerLife = initiallyLucky ? 51u : 50u;
        await use;
        Assert.Equal(initiallyLucky ? 1 : 2, random.Calls);
        Assert.Equal(initiallyLucky ? 1000u : 1500u, f.Target.MaxLife - f.Target.Life);
    }
}
