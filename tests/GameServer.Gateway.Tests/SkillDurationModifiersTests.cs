namespace GameServer.Gateway.Tests;

public class SkillDurationModifiersTests
{
    static uint Key(string name) => Program.Stats.Key(name)!.Value;
    static int Duration(ClientState state) =>
        Program.CharacterPlayerStats(state)[Key(SkillDurationModifiers.IncreasedDuration)];

    [Theory]
    [InlineData(20, -10, 1, 20)]
    [InlineData(20, 0, 1, 20)]
    [InlineData(20, 9, 1, 20)]
    [InlineData(20, 10, 1, 21)]
    [InlineData(20, 19, 1, 21)]
    [InlineData(20, 20, 1, 22)]
    [InlineData(20, 455, 1, 65)]
    [InlineData(20, 455, -2, -70)]
    [InlineData(int.MaxValue, 10, 1, int.MaxValue)]
    [InlineData(int.MinValue, 10, -1, int.MinValue)]
    [InlineData(int.MinValue, int.MaxValue, int.MaxValue, int.MaxValue)]
    [InlineData(int.MaxValue, int.MaxValue, int.MinValue, int.MinValue)]
    public void Complete_groups_add_to_raw_duration_and_signed_totals_saturate(int raw, int strength, int perTen, int expected) =>
        Assert.Equal(expected, SkillDurationModifiers.Total(raw, strength, perTen));

    [Fact]
    public void Real_node_derives_once_from_final_pools_and_removal_leaves_no_cached_bonus()
    {
        TestSaves.Ensure();
        var node = new PassiveTree().Find(27339)!;
        Assert.Equal("Exhibit 39: Whaletooth Bangle", node.Name);
        Assert.True(node.IsAscendancy);
        Assert.Equal(2817u, Key(SkillDurationModifiers.PerTenStrength));
        Assert.Contains((Key(SkillDurationModifiers.PerTenStrength), 1), node.Stats);
        var state = new ClientState(new(0, "Duration", 128, 128, 32, 32, null, []), 0, 1)
        { ClassRow = 0, Pools = CharacterStats.For(Program.Skills.ClassFor(0), 1,
            new(Strength: 332, Attributes: new(StrengthPct: 25))) };
        Assert.Equal(455, state.Pools.Strength); // (Marauder's 32 + 332) * 1.25, already resolved by pools.
        Assert.Equal(0, Duration(state));
        state.Passives.Add(27339);
        Assert.Equal(45, Duration(state));
        Assert.Equal(45, Duration(state));
        state.Passives.Add(7614); // Ordinary Skill Duration: +10%, additive with the derived +45%.
        Assert.Equal(55, Duration(state));
        Assert.Equal(55, Duration(state));
        state.Passives.Remove(7614);
        Assert.DoesNotContain(Key(SkillDurationModifiers.IncreasedDuration), state.PlayerStats.Keys);
        state.PlayerStats[Key("strength")] = 459;
        Assert.Equal(45, Duration(state));
        state.PlayerStats[Key("strength")] = 460;
        Assert.Equal(46, Duration(state));
        state.PlayerStats[Key(SkillDurationModifiers.PerTenStrength)] = 0;
        Assert.Equal(0, Duration(state));
        state.PlayerStats.Remove(Key(SkillDurationModifiers.PerTenStrength));
        state.Passives.Clear();
        Assert.Equal(0, Duration(state));
    }

    [Fact]
    public void Final_duration_override_is_authoritative_including_zero_and_negative_values()
    {
        TestSaves.Ensure();
        var state = new ClientState(new(0, "Duration", 128, 128, 32, 32, null, []), 0, 1)
        { Pools = new(100, 50, 1, 0, 0, Strength: 455) };
        state.Passives.Add(27339);
        foreach (int value in new[] { 20, 0, -20 })
        {
            state.PlayerStats[Key(SkillDurationModifiers.IncreasedDuration)] = value;
            Assert.Equal(value, Duration(state));
        }
        state.PlayerStats.Remove(Key(SkillDurationModifiers.IncreasedDuration));
        Assert.Equal(45, Duration(state));
    }
}
