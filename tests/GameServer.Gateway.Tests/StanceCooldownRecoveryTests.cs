namespace GameServer.Gateway.Tests;

public sealed class StanceCooldownRecoveryTests
{
    [Fact]
    public void Stance_recovery_speeds_up_stance_switches()
    {
        using var f = new TriggeredSummonTests.Fixture();
        var conn = f.Conn;
        ushort stance = Assert.Single(f.Equip(("Blood and Sand", 1)));
        SingleUseCooldown Plan(ushort skill) => Assert.IsType<SingleUseCooldown>(Game.CooldownForSkill(conn, skill));

        var baseline = Plan(stance);
        uint stanceRecovery = Program.Stats.Key("stance_skill_cooldown_speed_+%")!.Value;
        conn.PlayerStats[stanceRecovery] = 10;
        var accelerated = Plan(stance);
        Assert.Equal(baseline.RecoveryPercent + 10, accelerated.RecoveryPercent);
        Assert.Equal(baseline.BaseMilliseconds, accelerated.BaseMilliseconds);
        Assert.Equal(baseline.Uses, accelerated.Uses);

        ushort dash = Assert.Single(f.Equip(("Flame Dash", 1)));
        var movement = Plan(dash);
        conn.PlayerStats.Remove(stanceRecovery);
        Assert.Equal(movement, Plan(dash));
    }
}
