using System.Buffers.Binary;

namespace GameServer.Data.Ggpk;

/// <summary>
/// Reader for the Game bundle container (<c>*.bundle.bin</c>), including the
/// special <c>_.index.bin</c>. Format per Game-tool-dev/ggpk.discussion wiki.
///
/// Header:
///   i32 @ 0   decompressed data size
///   i32 @ 36  chunk count
///   i32 @ 40  compression granularity (uncompressed bytes per chunk)
///   i32 @ 60  chunk compressed sizes[chunkCount]
///   data      starts at 60 + chunkCount*4
/// Each chunk is Oodle compressed (Kraken/Mermaid/Selkie/...); the last chunk holds the remainder.
/// </summary>
public static class BundleFile
{
    private const int DecompressedSizeOffset = 0;
    private const int ChunkCountOffset = 36;
    private const int GranularityOffset = 40;
    private const int ChunkSizesOffset = 60;

    public static int DecompressedSize(ReadOnlySpan<byte> bundle)
        => Header(bundle).DecompressedSize;

    /// <summary>A bundle's decompressed size and its chunk size (every chunk but the last decompresses to it).</summary>
    public static (int DecompressedSize, int Granularity) Layout(ReadOnlySpan<byte> bundle)
    {
        var header = Header(bundle);
        return (header.DecompressedSize, header.Granularity);
    }

    public static byte[] DecompressWhole(ReadOnlySpan<byte> bundle, IOodleDecoder decoder, int maxOutputBytes = int.MaxValue)
    {
        int size = DecompressedSize(bundle);
        if (size > maxOutputBytes)
            throw new GgpkFormatException("validation-limit", $"bundle declares {size} decoded bytes; quick-check limit is {maxOutputBytes}");
        return DecompressSlice(bundle, 0, size, decoder);
    }

    private static (int DecompressedSize, int ChunkCount, int Granularity) Header(ReadOnlySpan<byte> bundle)
    {
        if (bundle.Length < ChunkSizesOffset)
            throw new GgpkFormatException($"bundle header is truncated: {bundle.Length} bytes, need {ChunkSizesOffset}");
        int size = BinaryPrimitives.ReadInt32LittleEndian(bundle[DecompressedSizeOffset..]);
        int chunks = BinaryPrimitives.ReadInt32LittleEndian(bundle[ChunkCountOffset..]);
        int granularity = BinaryPrimitives.ReadInt32LittleEndian(bundle[GranularityOffset..]);
        if (size < 0 || chunks < 0 || chunks > (bundle.Length - ChunkSizesOffset) / 4)
            throw new GgpkFormatException($"invalid bundle sizes: decoded={size}, chunks={chunks}, available={bundle.Length}");
        if (size == 0 ? chunks != 0 : chunks == 0 || granularity <= 0
            || (long)(chunks - 1) * granularity >= size || (long)chunks * granularity < size)
            throw new GgpkFormatException($"inconsistent bundle layout: decoded={size}, chunks={chunks}, granularity={granularity}");
        return (size, chunks, granularity);
    }

    /// <summary>Decompresses chunk <paramref name="chunk"/> of a bundle (its compressed bytes) to
    /// <paramref name="decompressedSize"/> bytes; the result may be a cached array the caller must not change.</summary>
    public delegate byte[] ChunkDecoder(int chunk, ReadOnlySpan<byte> compressed, int decompressedSize);

    public static byte[] DecompressSlice(ReadOnlySpan<byte> bundle, int sliceOffset, int sliceSize, IOodleDecoder decoder, int maxChunkBytes = int.MaxValue)
    {
        ArgumentNullException.ThrowIfNull(decoder);
        return DecompressSlice(bundle, sliceOffset, sliceSize, (_, compressed, size) => decoder.Decompress(compressed.ToArray(), size), maxChunkBytes);
    }

    public static byte[] DecompressSlice(ReadOnlySpan<byte> bundle, int sliceOffset, int sliceSize, ChunkDecoder decoder, int maxChunkBytes = int.MaxValue)
    {
        ArgumentNullException.ThrowIfNull(decoder);
        var (decompressedSize, chunkCount, granularity) = Header(bundle);
        if (sliceOffset < 0 || sliceSize < 0 || sliceOffset > decompressedSize || sliceSize > decompressedSize - sliceOffset)
            throw new GgpkFormatException($"slice ({sliceOffset}, {sliceSize}) is outside a {decompressedSize}-byte bundle");

        var result = new byte[sliceSize];
        int chunkBegin = ChunkSizesOffset + (chunkCount * 4);
        int decompOffset = 0;
        int destOffset = 0;

        // Only the chunks the slice overlaps are copied and decompressed: a terrain file is a few KB of a bundle of
        // hundreds of chunks, and copying every chunk for each read was most of a level generation's time.
        for (int idx = 0; idx < chunkCount && decompOffset < sliceOffset + sliceSize; idx++)
        {
            int chunkSize = BinaryPrimitives.ReadInt32LittleEndian(bundle.Slice(ChunkSizesOffset + (idx * 4), 4));
            if (chunkSize <= 0 || chunkSize > bundle.Length - chunkBegin)
                throw new GgpkFormatException($"chunk {idx} is truncated or invalid: size={chunkSize}, available={bundle.Length - chunkBegin}");

            int decompChunkSize = (idx == chunkCount - 1)
                ? decompressedSize - decompOffset
                : granularity;

            if (decompChunkSize > 0
                && Math.Max(decompOffset, sliceOffset) < Math.Min(decompOffset + decompChunkSize, sliceOffset + sliceSize))
            {
                if (decompChunkSize > maxChunkBytes)
                    throw new GgpkFormatException("validation-limit", $"chunk declares {decompChunkSize} decoded bytes; quick-check limit is {maxChunkBytes}");
                int begin = Math.Max(sliceOffset - decompOffset, 0);
                int end = Math.Min(sliceOffset + sliceSize - decompOffset, decompChunkSize);

                byte[] raw = decoder(idx, bundle.Slice(chunkBegin, chunkSize), decompChunkSize);
                if (raw.Length != decompChunkSize)
                    throw new GgpkFormatException("decoder-error", $"chunk produced {raw.Length} bytes, expected {decompChunkSize}");

                raw.AsSpan(begin, end - begin).CopyTo(result.AsSpan(destOffset));
                destOffset += end - begin;
            }

            decompOffset += decompChunkSize;
            chunkBegin += chunkSize;
        }

        if (destOffset != sliceSize)
            throw new GgpkFormatException($"bundle slice incomplete: wrote {destOffset} of {sliceSize} bytes");

        return result;
    }
}
