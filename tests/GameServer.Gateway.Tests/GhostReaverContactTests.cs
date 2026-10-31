using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public partial class ConditionalCriticalGatewayTests
{
    [Fact]
    public async Task Target_ES_leech_immunity_blocks_converted_and_native_ES_sources_but_not_mana()
    {
        using var f = new Contact(new Draws(), targetMods: new([], [],
            new(new Dictionary<string, int> { ["cannot_have_energy_shield_leeched_from"] = 1 })));
        f.Set("global_cannot_crit", 1); f.Set("leech_%_is_instant", 100);
        f.Set("keystone_ghost_reaver", 1);
        foreach (var pool in new[] { "life", "mana", "energy_shield" }) f.Set($"{pool}_leech_from_any_damage_permyriad", 100);
        f.Conn.Pools = f.Conn.Pools with { EnergyShield = 1000 };
        f.Conn.PlayerMana = 500;
        await f.Use();
        Assert.Equal(50u, f.Conn.PlayerLife);
        Assert.Equal(510u, f.Conn.PlayerMana);
        Assert.Equal(0u, f.Conn.EnergyShield.Current);
        Assert.Equal(0, Program.LeechOf(f.Conn).EnergyShield.Count);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(null, "Crude Bow")]
    [InlineData("Cyclone", null)]
    [InlineData("Fireball", null)]
    public async Task Actual_Ghost_Reaver_node_converts_two_source_categories_and_refund_restores_Life(
        string? gem, string? weapon)
    {
        using var f = new Contact(new Draws(), gem, weapon);
        f.Set("global_cannot_crit", 1);
        f.Set("life_leech_from_any_damage_permyriad", 100);
        f.Set("energy_shield_leech_from_any_damage_permyriad", 100);
        const ushort node = 24426;
        Assert.Equal(1, Program.Passives.Stats([node])[Program.Stats.Key("keystone_ghost_reaver")!.Value]);
        f.Conn.Passives.Add(node);
        f.Refresh(); f.Conn.Pools = f.Conn.Pools with { Life = 1000, EnergyShield = 1000 };
        var stats = Program.CharacterPlayerStats(f.Conn);
        Assert.Equal(1, stats[Program.Stats.Key("virtual_leech_energy_shield_instead_of_life")!.Value]);
        Assert.Equal(12000, stats[Program.Stats.Key("maximum_total_recovery_per_minute_from_energy_shield_leech")!.Value]);
        Assert.Equal(0, stats[Program.Stats.Key("energy_shield_recharge_rate_per_minute")!.Value]);
        await f.Use(); await f.HitLands();
        var s = Program.LeechOf(f.Conn);
        Assert.Equal(0, s.Life.Count);
        Assert.Equal(2, s.EnergyShield.Count);
        f.Conn.RefundPoints = 1;
        await f.Refund(node);
        Assert.DoesNotContain(node, f.Conn.Passives);
        // Refund recomputes real pools; restore the controlled pool fixture for comparisons, not the keystone.
        f.Conn.Pools = f.Conn.Pools with { Life = 1000, EnergyShield = 1000 };
        stats = Program.CharacterPlayerStats(f.Conn);
        Assert.Equal(0, stats.GetValueOrDefault(Program.Stats.Key("virtual_leech_energy_shield_instead_of_life")!.Value));
        Assert.Equal(6000, stats[Program.Stats.Key("maximum_total_recovery_per_minute_from_energy_shield_leech")!.Value]);
        Assert.Equal(20000, stats[Program.Stats.Key("energy_shield_recharge_rate_per_minute")!.Value]);
        Program.LeechOnHitLocked(f.Conn, f.Target, DamagePacket.Of(DamageType.Physical, 1000, 1000), 1000,
            Program.HitLeechSources(stats, true));
        Assert.Equal(1, s.Life.Count);
    }
}
