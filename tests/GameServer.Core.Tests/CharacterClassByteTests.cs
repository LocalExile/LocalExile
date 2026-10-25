using GameServer.Core.Protocol.Instance;

namespace GameServer.Core.Tests;

public class CharacterClassByteTests
{
    [Fact]
    public void Native_decode_round_trips_every_representable_class_number()
    {
        for (byte baseClass = 0; baseClass <= 6; baseClass++)
            for (byte ascendancy = 0; ascendancy <= 7; ascendancy++)
            {
                byte encoded = CharacterClassByte.Encode(baseClass, ascendancy);
                Assert.Equal(baseClass, CharacterClassByte.BaseClass(encoded));
                Assert.Equal(ascendancy, CharacterClassByte.AscendancyClassNo(encoded));
            }
        Assert.Equal((byte)0x44, CharacterClassByte.Encode(4, 1)); // Duelist / Slayer or Aristocrat
        Assert.Equal((byte)0x84, CharacterClassByte.Encode(4, 2));
        Assert.Equal((byte)0xc4, CharacterClassByte.Encode(4, 3));
        Assert.Equal((byte)0x24, CharacterClassByte.Encode(4, 4));
        Assert.Throws<ArgumentOutOfRangeException>(() => CharacterClassByte.Encode(7));
        Assert.Throws<ArgumentOutOfRangeException>(() => CharacterClassByte.Encode(0, 8));
    }
}
