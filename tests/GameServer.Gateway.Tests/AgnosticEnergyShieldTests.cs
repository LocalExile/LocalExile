namespace GameServer.Gateway.Tests;

public sealed class AgnosticEnergyShieldTests
{
    [Fact]
    public void The_agnostic_zeroes_current_and_maximum_energy_shield_until_it_is_removed()
    {
        using var f = new TriggeredSummonTests.Fixture();
        var conn = f.Conn;
        const ushort agnostic = 19732;
        uint extraShield = Program.Stats.Key("maximum_life_%_to_add_as_maximum_energy_shield")!.Value;
        conn.PlayerStats[extraShield] = 20;
        CharacterStats.Pools Pools() => Game.BuildPoolsFor(conn, 90);

        conn.Pools = Pools();
        uint maximum = conn.Pools.EnergyShield;
        Assert.True(maximum > 0);
        conn.EnergyShield.SetMaximum(maximum, refill: true);
        Assert.Equal(maximum, conn.EnergyShield.Current);

        conn.Passives.Add(agnostic);
        conn.Pools = Pools();
        Assert.Equal(0u, conn.Pools.EnergyShield);
        Assert.Equal(0u, conn.EnergyShield.Maximum);
        Assert.Equal(0u, conn.EnergyShield.Current);

        conn.Passives.Remove(agnostic);
        conn.Pools = Pools();
        Assert.Equal(maximum, conn.EnergyShield.Maximum);
    }
}
