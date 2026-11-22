using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public sealed class BattlemageTests
{
    [Fact]
    public void Battlemage_adds_the_main_hand_damage_once_at_the_spells_added_effectiveness()
    {
        using var f = new TriggeredSummonTests.Fixture();
        var conn = f.Conn;
        ushort skill = Assert.Single(f.Equip(("Fireball", 1)));
        var gem = Game.SupportedSkill(conn, skill);
        var weapon = Assert.IsType<DamagePacket>(Game.WeaponFor(conn).LocalDamage);
        double weaponPhysical = weapon[DamageType.Physical].Max;
        Assert.True(weaponPhysical > 0);
        double PhysicalBase() => Game.SupportedHitInputsFor(conn, skill)!.Raw[DamageType.Physical].Max;
        Assert.Equal(0, PhysicalBase());

        uint battlemage = Program.Stats.Key("keystone_battlemage")!.Value;
        conn.PlayerStats[battlemage] = 1;
        Assert.Equal(weaponPhysical * Game.AddedEffectiveness(conn, gem.Effect, gem.Level), PhysicalBase(), 6);
        Assert.True(Game.SupportedHitFor(conn, skill, suppress: false)[DamageType.Physical].Max > 0);

        conn.PlayerStats.Remove(battlemage);
        Assert.Equal(0, PhysicalBase());
    }
}
