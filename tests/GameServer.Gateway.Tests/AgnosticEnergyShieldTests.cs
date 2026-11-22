namespace GameServer.Gateway.Tests;

public sealed class AgnosticEnergyShieldTests
{
    [Fact]
    public void The_agnostic_zeroes_current_and_maximum_energy_shield_until_it_is_removed()
    {
        using var f = new TriggeredSummonTests.Fixture();
        var conn = f.Conn;
        uint baseShield = Program.Stats.Key("base_maximum_energy_shield")!.Value;
        uint agnostic = Program.Stats.Key("keystone_miracle_of_thaumaturgy")!.Value;
        var stats = new Dictionary<uint, int> { [baseShield] = 100 };
        var character = Game.SkillsFor(conn).ClassFor(conn.ClassRow);
        CharacterStats.Pools Pools() => CharacterStats.For(character, 90,
            Game.PassiveBonus(stats, new WeaponProfile(false, false, false, false, false), default));

        conn.Pools = Pools();
        uint maximum = conn.Pools.EnergyShield;
        Assert.True(maximum > 0);
        conn.EnergyShield.SetMaximum(maximum, refill: true);
        Assert.Equal(maximum, conn.EnergyShield.Current);

        stats[agnostic] = 1;
        conn.Pools = Pools();
        Assert.Equal(0u, conn.Pools.EnergyShield);
        Assert.Equal(0u, conn.EnergyShield.Maximum);
        Assert.Equal(0u, conn.EnergyShield.Current);

        stats.Remove(agnostic);
        conn.Pools = Pools();
        Assert.Equal(maximum, conn.EnergyShield.Maximum);
    }
}
