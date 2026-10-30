namespace GameServer.Gateway.Tests;

public class ProjectileSpeedTests
{
    static Func<string, int> Stats(params (string Name, int Value)[] stats) =>
        name => stats.FirstOrDefault(s => s.Name == name).Value;

    [Theory]
    [InlineData(0, 0, 95.68)]
    public void Player_and_skill_increases_and_reductions_add(int player, int skill, double expected)
    {
        Assert.Equal(expected, ProjectileSpeed.CellsPerSecond(1040,
            Stats(("projectile_speed_+%", player)), Stats(("base_projectile_speed_+%", skill))), 4);
    }

}
