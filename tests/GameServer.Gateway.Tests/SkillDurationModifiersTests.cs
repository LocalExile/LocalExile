namespace GameServer.Gateway.Tests;

public class SkillDurationModifiersTests
{
    static uint Key(string name) => Program.Stats.Key(name)!.Value;
    static int Duration(ClientState state) =>
        Program.CharacterPlayerStats(state)[Key(SkillDurationModifiers.IncreasedDuration)];

    [Theory]
    [InlineData(20, -10, 1, 20)]
    public void Complete_groups_add_to_raw_duration_and_signed_totals_saturate(int raw, int strength, int perTen, int expected) =>
        Assert.Equal(expected, SkillDurationModifiers.Total(raw, strength, perTen));

}
