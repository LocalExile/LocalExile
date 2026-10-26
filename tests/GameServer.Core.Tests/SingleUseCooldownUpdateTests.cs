using GameServer.Core.Protocol.Instance;

namespace GameServer.Core.Tests;

public class SingleUseCooldownUpdateTests
{
    [Fact]
    public void Native_cooldown_update_has_no_skill_records_or_consumed_action_and_keeps_base_time_separate_from_rate()
    {
        byte[] bytes = InstanceEntities.PlayerSingleUseCooldowns(0x1000, [new(0x103, 2, 2.5f, 2)]);
        // Object header/blob length BE; Actor flags, counts, skill id and four entry fields LE.
        string body = "09" + "4000" + "00" + "00" + "01000000" + "0301" + "01000000"
            + "00000040" + "00002040" + "00000040" + "FFFFFFFF";
        Assert.Equal("02BA00001000000000000000" + "001F" + body, Convert.ToHexString(bytes));
    }

    [Fact]
    public void An_expired_budget_clears_only_its_existing_entry_vector()
    {
        byte[] bytes = InstanceEntities.PlayerSingleUseCooldowns(0x1000, [new(0x103, 0, 2.5f, 2)]);
        Assert.Equal("02BA00001000000000000000" + "000F" + "094000000001000000030100000000",
            Convert.ToHexString(bytes));
    }

    [Fact]
    public void A_zero_rate_is_a_stalled_existing_budget_not_a_new_use()
    {
        byte[] bytes = InstanceEntities.PlayerSingleUseCooldowns(0x1000, [new(0x103, 2, 2.5f, 0)]);
        Assert.EndsWith("000000400000204000000000FFFFFFFF", Convert.ToHexString(bytes));
    }

    [Theory]
    [InlineData(-1, 2.5f, 2)]
    [InlineData(float.NaN, 2.5f, 2)]
    [InlineData(2, 0, 2)]
    [InlineData(2, float.PositiveInfinity, 2)]
    [InlineData(2, 2.5f, -1)]
    [InlineData(2, 2.5f, float.NaN)]
    public void Invalid_native_float_fields_cannot_be_serialized(float remaining, float total, float rate) =>
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            InstanceEntities.PlayerSingleUseCooldowns(0x1000, [new(0x103, remaining, total, rate)]));
}
