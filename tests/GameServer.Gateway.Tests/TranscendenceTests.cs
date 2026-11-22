using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public sealed class TranscendenceTests
{
    [Fact]
    public void Transcendence_reassigns_hit_armour_to_elements_and_lowers_the_elemental_maximums()
    {
        using var fixture = new TriggeredSummonTests.Fixture();
        var conn = fixture.Conn;
        uint k = Program.Stats.Key(PlayerHitMitigation.Transcendence)!.Value;
        conn.PlayerStats[k] = 1;
        conn.Pools = Game.BuildPoolsFor(conn, 90) with { Armour = 1000 };
        var stats = Game.CharacterPlayerStats(conn);
        Assert.Equal(60, conn.Pools.FireMaxResistance);
        Assert.Equal(60, conn.Pools.ColdMaxResistance);
        Assert.Equal(60, conn.Pools.LightningMaxResistance);
        Assert.Equal(75, conn.Pools.ChaosMaxResistance);
        int Get(string name) => Program.Stats.Key(name) is uint key ? stats.GetValueOrDefault(key) : 0;
        Assert.Equal(80, PlayerHitMitigation.AfterResistance(conn.Pools, DamageType.Physical, 100,
            physicalReduction: 20, armourPercent: PlayerHitMitigation.ArmourPercent(conn.Pools, DamageType.Physical, Get)));
        foreach (var type in new[] { DamageType.Fire, DamageType.Cold, DamageType.Lightning })
            Assert.Equal(7, Game.MitigateTakenWithArmour(conn, type, 100, 75, 60, 1000, stats));
        Assert.Equal(100, Game.MitigateTakenWithArmour(conn, DamageType.Chaos, 100, 0, 75, 1000, stats));
    }
}
