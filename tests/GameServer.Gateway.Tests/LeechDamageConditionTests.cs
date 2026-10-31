using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public class LeechDamageConditionTests
{
    [Theory]
    [InlineData("life", false, 240)]
    [InlineData("life", true, 190)]
    [InlineData("mana", false, 190)]
    [InlineData("mana", true, 140)]
    [InlineData("es", false, 200)]
    [InlineData("es", true, 150)]
    [InlineData("none", false, 100)]
    [InlineData("none", true, 100)]
    [InlineData("all", false, 380)]
    [InlineData("all", true, 330)]
    public void Scoped_increases_share_the_increased_bucket_and_inherit_physical_conversion(string pool, bool spell, int expected)
    {
        var raw = new Dictionary<string, int>
        {
            ["damage_+%_while_leeching"] = 10,
            ["damage_+%_while_life_leeching"] = 20,
            ["damage_+%_while_mana_leeching"] = 30,
            ["damage_+%_while_es_leeching"] = 40,
            ["attack_damage_+%_while_leeching"] = 50,
            ["physical_damage_+%_while_life_leeching"] = 60,
            ["damage_+%_on_full_energy_shield"] = 70,
        };
        foreach (string p in new[] { "life", "mana", "es" }) raw[$"is_{p}_leeching"] = pool == p || pool == "all" ? 1 : 0;
        raw["on_full_energy_shield"] = pool == "all" ? 1 : 0;
        var hit = PlayerHitDamage.Resolve(DamagePacket.Of(DamageType.Physical, 100, 100), n => raw.GetValueOrDefault(n),
            new DamageConversion().Add(DamageType.Physical, DamageType.Fire, 100), spell);
        Assert.Equal(0, hit[DamageType.Physical].Min);
        Assert.Equal(expected, hit[DamageType.Fire].Min, 8);
    }

    [Theory]
    [InlineData(false, false, 1)]
    [InlineData(true, false, 1.15)]
    [InlineData(false, true, 1.3)]
    [InlineData(true, true, 1.495)]
    public void Conditional_support_more_factors_multiply_independently(bool full, bool leech, double expected)
    {
        var value = GemSupportDamage.Multipliers(DamageTypeHistory.Fire,
            [("support_energy_shield_leech_damage_+%_on_full_energy_shield_final", 15),
                ("support_energy_shield_leech_damage_+%_while_leeching_energy_shield_final", 30)],
            new HashSet<int> { 1 }, full, leech);
        Assert.Equal(expected, value.GeneralOrPhysical, 8);
        Assert.Equal(1, value.ElementalOrChaos);
    }

    [Fact]
    public void Reading_expired_conditions_preserves_recovery_for_the_resource_writer()
    {
        var state = new LeechRecoveryState();
        state.Configure(0, new(1, 200, 1));
        state.Grant(0, 100, 1000, 100);
        Assert.True(state.Active(1));
        Assert.Equal(20, state.TakeRecovery(1));
        Assert.False(state.Active(6));
        Assert.Equal(80, state.TakeRecovery(6));
        state.Grant(7, 100, 1000, 100, instantPercent: 100);
        Assert.False(state.Active(7));
    }
}
