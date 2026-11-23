namespace GameServer.Core.Protocol;

/// <summary>Minimal PE (x64) reader: maps a virtual address of the client executable to bytes in the file.</summary>
public static class PeImage
{
    public static byte[] ReadAtVa(byte[] image, long va, int length) =>
        image.AsSpan(FileOffset(image, va, length), length).ToArray();

    /// <summary>Where <paramref name="length"/> bytes at <paramref name="va"/> sit in the file.</summary>
    public static int FileOffset(byte[] image, long va, int length)
    {
        int pe = BitConverter.ToInt32(image, 0x3c);
        if (BitConverter.ToUInt32(image, pe) != 0x00004550) throw new InvalidDataException("not a PE file");
        int sections = BitConverter.ToUInt16(image, pe + 6);
        int optionalSize = BitConverter.ToUInt16(image, pe + 20);
        int optional = pe + 24;
        long imageBase = BitConverter.ToInt64(image, optional + 24);
        long rva = va - imageBase;
        int table = optional + optionalSize;
        for (int i = 0; i < sections; i++)
        {
            int h = table + i * 40;
            uint virtualSize = BitConverter.ToUInt32(image, h + 8), virtualAddress = BitConverter.ToUInt32(image, h + 12);
            uint rawSize = BitConverter.ToUInt32(image, h + 16), rawPointer = BitConverter.ToUInt32(image, h + 20);
            if (rva >= virtualAddress && rva + length <= virtualAddress + Math.Max(virtualSize, rawSize))
            {
                long offset = rawPointer + (rva - virtualAddress);
                if (offset + length > image.Length) break;
                return (int)offset;
            }
        }
        throw new InvalidDataException($"VA 0x{va:x} is not in the file");
    }
}
