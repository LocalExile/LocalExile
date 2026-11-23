using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public sealed class DelveChartFocusTests
{
    [Fact]
    public void Resource_snapshot_carries_native_signed_x_and_unsigned_y_without_changing_resources()
    {
        var baseline = InstanceDelve.DelveData(500, 1234, 3, 2, [1, 2]);
        var focused = InstanceDelve.DelveData(500, 1234, 3, 2, [1, 2], chartX: -17, chartY: 301);
        Assert.Equal(37, focused.Length);
        // Native instance +d878 is u16 Y, +d87a is i16 X: raw14 follows id, flag and raw20.
        Assert.Equal(new byte[] { 0x2d, 0x01, 0xef, 0xff }, focused[27..31]);
        focused.AsSpan(27, 4).Clear();
        Assert.Equal(baseline, focused);
    }
}
