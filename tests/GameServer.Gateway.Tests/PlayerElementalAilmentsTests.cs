using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public class PlayerElementalAilmentsTests
{
    static int None(string _) => 0;
    static DamagePacket Fire(int damage) => DamagePacket.Of(DamageType.Fire, damage, damage);

    [Fact]
    public void Strongest_ignite_is_integrated_through_expiry_and_the_weaker_longer_one_resumes()
    {
        var ailments = new PlayerElementalAilments();
        ailments.Apply(Fire(200), Fire(200), true, 1000, 0, None, new Random(1));
        ailments.Apply(Fire(100), Fire(100), true, 1000, 2, None, new Random(1));
        Assert.Equal(234 * 4 + 117 * 2, ailments.Advance(0, 10, None, 0, 75, false));
        Assert.Equal(0, ailments.Advance(10, 11, None, 0, 75, false));
        Assert.False(ailments.Snapshot(10).Ignite);
    }

    [Fact]
    public void Ordinary_resistance_is_live_and_immunity_removes_an_existing_ignite()
    {
        var ailments = new PlayerElementalAilments();
        ailments.Apply(Fire(100), Fire(100), true, 1000, 0, None, new Random(1));
        Assert.Equal(29, ailments.Advance(0, 1, None, 75, 75, false));
        Assert.Equal(117, ailments.Advance(1, 2, None, 0, 75, false)); // Fraction carried, no 90% hit override.
        int Immune(string key) => key == "immune_to_ignite" ? 1 : 0;
        ailments.Reconcile(Immune);
        Assert.False(ailments.Snapshot(2).Ignite);
        Assert.Equal(0, ailments.Advance(2, 4, None, 0, 75, false));
    }

    [Fact]
    public void Ordinary_noncritical_cold_chills_without_freezing_or_shocking_and_expires()
    {
        var ailments = new PlayerElementalAilments();
        var cold = DamagePacket.Of(DamageType.Cold, 100, 100);
        ailments.Apply(cold, cold, false, 1000, 0, None, new Random(1));
        Assert.True(ailments.ActionFactor(1) < 1);
        Assert.False(ailments.Snapshot(1).Freeze);
        Assert.Equal(1, ailments.DamageTakenFactor(1));
        Assert.Equal(1, ailments.ActionFactor(2));
        ailments.Clear();
        Assert.Equal((false, 0, false, 0), ailments.Snapshot(1));
    }

    [Fact]
    public void Gateway_ignite_uses_energy_shield_and_is_lethal_and_cleared_on_death()
    {
        var conn = new ClientState(new(ushort.MaxValue, "ailments", 128, 128, 32, 32, null, []), 0, 1)
        { PlayerLife = 100, Pools = new(1, 100, 100, 0, 0, EnergyShield: 20) };
        conn.EnergyShield.SetMaximum(20, refill: true);
        conn.ElementalAilments.Apply(Fire(200), Fire(200), true, 100, 0, None, new Random(1));
        Assert.Equal(234, Program.AdvancePlayerAilmentsLocked(conn, 0, 1, new Dictionary<uint, int>()));
        Assert.Equal(0u, conn.EnergyShield.Current);
        Assert.Equal(0u, conn.PlayerLife);
        Assert.False(conn.ElementalAilments.Snapshot(1).Ignite);
    }
}
