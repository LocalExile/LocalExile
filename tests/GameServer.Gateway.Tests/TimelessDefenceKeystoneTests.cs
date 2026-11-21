using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public sealed class TimelessDefenceKeystoneTests
{
    const ushort Socket = 36634, Keystone = 34098;

    static void Put(ClientState conn, string name, int seed, int conqueror)
    {
        var item = Assert.IsType<Item>(TimelessJewelCatalog.Create(Program.Items, name, seed, conqueror, random: new Random(7)));
        var tree = Game.PassivesFor(conn);
        Assert.Equal(Socket, tree.JewelNodeForSlot(1));
        conn.Passives = [Socket, Keystone];
        Game.ItemsOf(conn).Add(PlayerInventories.PassiveJewels, 1, 0, item);
    }

    [Fact]
    public void Transcendence_reduces_elemental_caps_and_moves_armour_from_physical_hits()
    {
        using var f = new MinionUtilityTests.Fixture();
        var conn = f.Conn;
        conn.Passives = [Socket, Keystone];
        var ordinary = Game.PoolsFor(conn, 100);
        Put(conn, "Militant Faith", 3304, 1);
        var pools = Game.PoolsFor(conn, 100) with { Armour = 1000 };
        var stats = Game.CharacterPlayerStats(conn);
        int Get(string name) => Program.Stats.Key(name) is uint key ? stats.GetValueOrDefault(key) : 0;
        Assert.Equal(1, Get(PlayerHitMitigation.Transcendence));
        Assert.Equal(ordinary.FireMaxResistance - 15, pools.FireMaxResistance);
        Assert.Equal(ordinary.ColdMaxResistance - 15, pools.ColdMaxResistance);
        Assert.Equal(ordinary.LightningMaxResistance - 15, pools.LightningMaxResistance);
        Assert.Equal(ordinary.ChaosMaxResistance, pools.ChaosMaxResistance);
        foreach (var type in new[] { DamageType.Fire, DamageType.Cold, DamageType.Lightning })
            Assert.Equal(DamageModel.MitigatePhysical(100, 1000, 0),
                PlayerHitMitigation.AfterResistance(pools, type, 100, armourPercent: PlayerHitMitigation.ArmourPercent(pools, type, Get)));
        Assert.Equal(80, PlayerHitMitigation.AfterResistance(pools, DamageType.Physical, 100, physicalReduction: 20,
            armourPercent: PlayerHitMitigation.ArmourPercent(pools, DamageType.Physical, Get)));
    }

    [Fact]
    public void Corrupted_soul_adds_energy_shield_and_routes_half_non_chaos_damage_to_life()
    {
        using var f = new MinionUtilityTests.Fixture();
        var conn = f.Conn;
        conn.Passives = [Socket, Keystone];
        var ordinary = Game.PoolsFor(conn, 100);
        Put(conn, "Glorious Vanity", 2000, 3);
        var pools = Game.PoolsFor(conn, 100);
        var gain = (uint)((decimal)pools.Life * 15 * (100 + pools.EnergyShieldPct) / 10000);
        Assert.Equal(ordinary.EnergyShield + gain, pools.EnergyShield);
        conn.Pools = pools;
        var stats = Game.CharacterPlayerStats(conn);
        Assert.Equal(50, stats[Program.Stats.Key(IncomingResourceDefences.BypassStat)!.Value]);
        conn.EnergyShield.SetMaximum(300, refill: true);
        var loss = Game.DamagePlayerHitTyped(conn, 100, 0, 0, 1, stats, DamagePacket.Of(DamageType.Physical, 100, 100));
        Assert.Equal(50, loss);
        Assert.Equal(250u, conn.EnergyShield.Current);
    }
}
