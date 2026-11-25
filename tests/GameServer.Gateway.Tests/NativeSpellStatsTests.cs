namespace GameServer.Gateway.Tests;

public sealed class NativeSpellStatsTests
{
    [Fact]
    public void Manabond_requires_and_spends_a_share_of_maximum_mana()
    {
        using var fixture = new TriggeredSummonTests.Fixture();
        var player = fixture.Player;
        ushort skill = Assert.Single(fixture.Equip(("Manabond", 1)));
        Assert.Equal(5, Game.SupportedSkill(player, skill).Stats.GetValueOrDefault(Stat.BaseManaCostPct));
        uint cost = player.Pools.Mana / 20;
        Assert.True(cost > 0);

        player.PlayerMana = cost - 1;
        Assert.False(Game.PayMana(player, skill, out _, out _));
        Assert.Equal(cost - 1, player.PlayerMana);

        player.PlayerMana = cost;
        Assert.True(Game.PayMana(player, skill, out uint mana, out uint life));
        Assert.Equal(cost, mana);
        Assert.Equal(0u, life);
        Assert.Equal(0u, player.PlayerMana);
    }

    [Fact]
    public void Vortex_of_Projection_adds_the_adjustment_to_its_resolved_cold_damage_rate()
    {
        int effect = Program.Skills.EffectRow("VortexAltX");
        int RateAt(int level) => Assert.Single(Program.Skills.EffectStats(effect, level),
            stat => stat.Item1 == Stat.BaseColdDamageToDealPerMinute).Item2;

        Assert.Equal(4591, RateAt(1));
        Assert.Equal(126719, RateAt(20));
    }
}
