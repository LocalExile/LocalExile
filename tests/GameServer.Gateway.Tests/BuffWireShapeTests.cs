namespace GameServer.Gateway.Tests;

public sealed class BuffWireShapeTests
{
    [Fact]
    public void A_non_recovery_row_does_not_send_values_or_u82()
    {
        // convergence (1699): a caller used to send 30 as a value; the client read it as a 30-entry list
        // header and over-read the messages that followed.
        var wire = Convert.ToHexString(new Buff(1699, 0x20000003, Values: [30], U68: Buff.Seconds(4), U82: 7)
            .Add(0x20000003, 0x66d));
        Assert.Equal("01C120000003000000000000066D06A3200000030000024080000000000000", wire);
    }

    [Fact]
    public void A_recovery_row_keeps_values_and_u82()
    {
        // chilled (row 1) has both tails: the +0xff u16 and its one GrantedStats value.
        var wire = Convert.ToHexString(new Buff(1, 0x1000, Values: [30], U82: 30).Add(0x1000, 3));
        Assert.Equal("01C1000010000000000000000003000100001000000000001E0000001E00000000", wire);
    }
}
