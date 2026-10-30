using GameServer.Core.Protocol.Instance;

public class HideoutUnlockTests
{
    [Fact]
    public void Campaign_unlocks_are_a_fixed_raw_bitmask_not_a_purchased_row_list()
    {
        Assert.Equal("01F6" + "01000080010000000000000000000080",
            Convert.ToHexString(InstanceMicrotransactions.HideoutUnlocks([0, 31, 32, 127, 0])));
        Assert.Equal("01F6" + new string('0', 32), Convert.ToHexString(InstanceMicrotransactions.HideoutUnlocks([])));
    }

}
