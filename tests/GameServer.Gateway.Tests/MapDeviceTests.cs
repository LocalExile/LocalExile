namespace GameServer.Gateway.Tests;

public class MapDeviceTests
{
    static readonly MapDevice.MapArea Beach = new(0x1111, "MapWorldsBeach", "Beach", 1);
    static readonly MapDevice Device = new(new Dictionary<uint, MapDevice.MapArea> { [0xbeac] = Beach });

    static MapDevice.Slot Map(uint type = 0xbeac) => new(type, MapDevice.MapClass, "Beach Map");
    static MapDevice.Slot Fragment() => new(0xf7a6, MapDevice.FragmentClass, "Sacrifice at Midnight");

    [Fact]
    public void One_map_with_fragments_opens_the_maps_area()
    {
        Assert.Equal(Beach, Device.Check([Fragment(), Map(), Fragment()], out string why));
        Assert.Equal("", why);
    }

}
