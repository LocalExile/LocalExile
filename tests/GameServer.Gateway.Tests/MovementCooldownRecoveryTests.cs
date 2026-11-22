namespace GameServer.Gateway.Tests;

public sealed class MovementCooldownRecoveryTests
{
    [Fact]
    public void Flame_dash_combines_movement_and_travel_recovery_and_preserves_its_blink_group()
    {
        using var f = new TriggeredSummonTests.Fixture();
        var conn = f.Conn;
        ushort dash = Assert.Single(f.Equip(("Flame Dash", 1)));
        SingleUseCooldown Plan(ushort skill) => Assert.IsType<SingleUseCooldown>(Game.CooldownForSkill(conn, skill));

        var baseline = Plan(dash);
        Assert.NotNull(baseline.SharedGroup);
        uint movement = Program.Stats.Key("movement_skills_cooldown_speed_+%")!.Value;
        conn.PlayerStats[movement] = 15;
        var accelerated = Plan(dash);
        Assert.Equal(baseline.RecoveryPercent + 15, accelerated.RecoveryPercent);
        Assert.Equal(baseline.BaseMilliseconds, accelerated.BaseMilliseconds);
        Assert.Equal(baseline.Uses, accelerated.Uses);
        Assert.Equal(baseline.SharedGroup, accelerated.SharedGroup);

        conn.PlayerStats[Program.Stats.Key("travel_skill_cooldown_speed_+%")!.Value] = 20;
        Assert.Equal(baseline.RecoveryPercent + 35, Plan(dash).RecoveryPercent);
        ushort stance = Assert.Single(f.Equip(("Blood and Sand", 1)));
        var stationary = Plan(stance);
        conn.PlayerStats.Remove(movement);
        Assert.Equal(stationary, Plan(stance));
    }
}
