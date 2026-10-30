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

}
