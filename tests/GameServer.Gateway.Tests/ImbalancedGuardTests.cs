using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public sealed class ImbalancedGuardTests
{
    [Fact]
    public void Imbalanced_guard_doubles_armour_and_caps_damage_reduction_before_damage_taken_modifiers()
    {
        using var f = new TriggeredSummonTests.Fixture();
        var conn = f.Conn;
        uint guard = Program.Stats.Key(PlayerHitMitigation.ImbalancedGuard)!.Value;
        int Hit(DamageType type, int armour, int resistance = 0)
        {
            conn.Pools = conn.Pools with { Armour = armour };
            return Game.MitigateTaken(conn, type, 100, resistance, 75);
        }

        Assert.Equal(33, Hit(DamageType.Physical, 1000));
        Assert.Equal(83, Hit(DamageType.Physical, 100));
        conn.PlayerStats[guard] = 1;
        Assert.Equal(50, Hit(DamageType.Physical, 1000));
        Assert.Equal(71, Hit(DamageType.Physical, 100));
        Assert.Equal(25, Hit(DamageType.Fire, 1000, resistance: 75));
        conn.PlayerStats[Program.Stats.Key(PlayerHitMitigation.ArmourPercentToElemental)!.Value] = 100;
        Assert.Equal(12, Hit(DamageType.Fire, 1000, resistance: 75));

        conn.PlayerStats[Program.Stats.Key("base_damage_taken_+%")!.Value] = -20;
        Assert.Equal(40, Hit(DamageType.Physical, 1000));
    }
}
