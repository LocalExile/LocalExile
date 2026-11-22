using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public sealed class CorruptedSoulTests
{
    [Fact]
    public void Replica_soul_tether_grants_corrupted_soul_once_and_splits_non_chaos_damage()
    {
        using var fixture = new TriggeredSummonTests.Fixture();
        var conn = fixture.Conn;
        uint keystone = Program.Stats.Key("keystone_corrupted_defences")!.Value;
        var belt = Assert.IsType<Item>(new UniqueCatalog(Program.Items).Create("Replica Soul Tether", new Random(11)));
        Game.ItemsOf(conn).Put(PlayerInventories.Belt, new(100, 0, 0, belt));
        var pools = Game.BuildPoolsFor(conn, 90);
        Assert.True(pools.EnergyShield > 0);
        conn.PlayerStats[keystone] = 2;
        Assert.Equal(pools.EnergyShield, Game.BuildPoolsFor(conn, 90).EnergyShield);

        (int Life, uint Shield) Hit(DamageType type)
        {
            var stats = Game.CharacterPlayerStats(conn);
            conn.EnergyShield.SetMaximum(200, refill: true);
            var damage = DamagePacket.Of(type, 100, 100);
            int life = Game.DamagePlayerHitTyped(conn, type == DamageType.Physical ? 100 : 0,
                type == DamageType.Fire ? 100 : 0, type == DamageType.Chaos ? 100 : 0, 1, stats, damage);
            return (life, conn.EnergyShield.Current);
        }
        Assert.Equal((50, 150u), Hit(DamageType.Physical));
        Assert.Equal((50, 150u), Hit(DamageType.Fire));
        Assert.Equal((100, 200u), Hit(DamageType.Chaos));
    }
}
