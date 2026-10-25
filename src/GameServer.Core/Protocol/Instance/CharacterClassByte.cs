namespace GameServer.Core.Protocol.Instance;

/// <summary>
/// The 3.29.3b Player/0x33 class byte. The base class is Characters.IntegerId; ClassNo is Ascendancy.ClassNo,
/// zero when unascended. This is not a contiguous three-bit shift: native 0x142156f24 decodes bit 5 as bit 2.
/// </summary>
public static class CharacterClassByte
{
    public static byte Encode(byte baseClass, byte ascendancyClassNo = 0)
    {
        if (baseClass > 6) throw new ArgumentOutOfRangeException(nameof(baseClass));
        if (ascendancyClassNo > 7) throw new ArgumentOutOfRangeException(nameof(ascendancyClassNo));
        return (byte)(baseClass | ((ascendancyClassNo & 3) << 6) | ((ascendancyClassNo & 4) << 3));
    }

    public static byte BaseClass(byte encoded) => (byte)(encoded & 0x1f);
    public static byte AscendancyClassNo(byte encoded) => (byte)((encoded >> 6) | ((encoded >> 3) & 4));
}
