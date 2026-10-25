namespace GameServer.Gateway.Tests;

public class ProjectileSpeedTests
{
    static Func<string, int> Stats(params (string Name, int Value)[] stats) =>
        name => stats.FirstOrDefault(s => s.Name == name).Value;

    [Theory]
    [InlineData(0, 0, 95.68)]
    [InlineData(100, 0, 191.36)]
    [InlineData(30, 40, 162.656)]
    [InlineData(-50, 0, 47.84)]
    [InlineData(30, -50, 76.544)]
    public void Player_and_skill_increases_and_reductions_add(int player, int skill, double expected)
    {
        Assert.Equal(expected, ProjectileSpeed.CellsPerSecond(1040,
            Stats(("projectile_speed_+%", player)), Stats(("base_projectile_speed_+%", skill))), 4);
    }

    [Fact]
    public void Player_base_is_already_in_the_panel_total_and_is_not_counted_twice()
    {
        var raw = Stats(("base_projectile_speed_+%", 30), ("projectile_speed_+%", 20));
        var final = PanelStats.Compute(raw).ToDictionary(s => s.Stat, s => s.Value);
        final["base_projectile_speed_+%"] = 30; // CharacterPlayerStats also retains raw inputs.
        Assert.Equal(50, final["projectile_speed_+%"]);
        Assert.Equal(181.792, ProjectileSpeed.CellsPerSecond(1040, n => final.GetValueOrDefault(n),
            Stats(("base_projectile_speed_+%", 40))), 4); // 50 + 40, not 50 + 30 + 40.
    }

    [Fact]
    public void Final_player_stat_override_wins_over_its_raw_base_input()
    {
        Assert.Equal(95.68, ProjectileSpeed.CellsPerSecond(1040,
            Stats(("projectile_speed_+%", 0), ("base_projectile_speed_+%", 200)), Stats()), 4);
    }

    [Theory]
    [InlineData("10.5", 10.5f)]
    [InlineData("0.001", 0.001f)]
    public void Valid_test_override_is_exact_and_does_not_receive_bonuses(string value, float expected)
    {
        float speed = ProjectileSpeed.CellsPerSecond(1040,
            Stats(("projectile_speed_+%", 200)), Stats(("base_projectile_speed_+%", 100)), value);
        Assert.Equal(expected, speed);
        Assert.Equal(expected, ProjectileSpeed.ForFlightDistance(speed, 121));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("invalid")]
    [InlineData("0")]
    [InlineData("-10")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("1e100")]
    [InlineData("10,5")]
    public void Invalid_test_override_falls_back_to_the_modified_table_speed(string? value)
    {
        Assert.Equal(143.52, ProjectileSpeed.CellsPerSecond(1040,
            Stats(("projectile_speed_+%", 50)), Stats(), value), 4);
    }

    [Theory]
    [InlineData(1040, -100, 0, null)]
    [InlineData(1040, int.MinValue, int.MinValue, null)]
    [InlineData(0, 0, 0, null)]
    [InlineData(-1, 0, 0, null)]
    [InlineData(1040, 0, 0, "1e-30")]
    public void Only_an_unsafe_flight_delay_gets_a_distance_dependent_speed_floor(int basis, int player, int skill, string? value)
    {
        float speed = ProjectileSpeed.CellsPerSecond(basis,
            Stats(("projectile_speed_+%", player)), Stats(("base_projectile_speed_+%", skill)), value);
        float safe = ProjectileSpeed.ForFlightDistance(speed, 121);
        Assert.True(float.IsFinite(safe));
        Assert.True(safe > speed);
        var duration = TimeSpan.FromSeconds(121.0 / safe);
        Assert.InRange(duration.TotalMilliseconds, 1, 4294967294);
        // Validate the actual API boundary without leaving a timer pending for 49 days.
        Assert.True(Task.Delay(duration, new CancellationToken(canceled: true)).IsCanceled);
        Assert.True(ProjectileSpeed.ForFlightDistance(speed, 241) > safe);
    }

    [Fact]
    public void Extreme_positive_inputs_cannot_overflow_the_additive_sum_or_speed()
    {
        float speed = ProjectileSpeed.CellsPerSecond(int.MaxValue,
            Stats(("projectile_speed_+%", int.MaxValue)), Stats(("base_projectile_speed_+%", int.MaxValue)));
        Assert.True(float.IsFinite(speed));
        Assert.True(speed > 1040);
        Assert.True(float.IsFinite(120 / speed));
    }

    [Fact]
    public void Real_gem_quality_remains_additive_with_the_players_final_bonus()
    {
        string data = BenchCraftingTests.Data();
        var skills = new SkillCatalog(data);
        var catalog = new StatCatalog(data);
        var own = skills.EffectStats(skills.EffectRow("FreezingPulse"), 1, quality: 20)
            .ToDictionary(s => s.Item1, s => s.Item2);
        int Skill(string name) => catalog.Key(name) is uint key ? own.GetValueOrDefault(key) : 0;
        Assert.Equal(40, Skill("base_projectile_speed_+%"));
        Assert.Equal(162.656, ProjectileSpeed.CellsPerSecond(1040,
            Stats(("projectile_speed_+%", 30)), Skill), 4);
    }
}
