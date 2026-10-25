using System.Buffers.Binary;

namespace GameServer.Data.Ggpk;

/// <summary>
/// A bundle writer using the Oodle stream's uncompressed Kraken blocks. No proprietary compressor is needed.
/// Each independent <=256 KiB block has a restart/uncompressed header CC 06, followed by its literal bytes.
/// </summary>
public static class StoredBundle
{
    public const int ChunkSize = 256 * 1024;

    public static byte[] Write(ReadOnlySpan<byte> content)
    {
        int count = checked((int)(((long)content.Length + ChunkSize - 1) / ChunkSize));
        int packedSize = checked(content.Length + count * 2);
        var result = new byte[checked(60 + count * 4 + packedSize)];
        void I32(int offset, int value) => BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(offset), value);
        I32(0, content.Length); I32(4, packedSize); I32(8, 48 + count * 4);
        I32(12, 8); I32(16, 1); // Kraken; normal bundle header's constant
        BinaryPrimitives.WriteInt64LittleEndian(result.AsSpan(20), content.Length);
        BinaryPrimitives.WriteInt64LittleEndian(result.AsSpan(28), packedSize);
        I32(36, count); I32(40, ChunkSize);
        int target = 60 + count * 4;
        for (int i = 0; i < count; i++)
        {
            int size = Math.Min(ChunkSize, content.Length - i * ChunkSize);
            I32(60 + i * 4, size + 2);
            result[target++] = 0xcc;
            result[target++] = 6;
            content.Slice(i * ChunkSize, size).CopyTo(result.AsSpan(target));
            target += size;
        }
        return result;
    }
}
