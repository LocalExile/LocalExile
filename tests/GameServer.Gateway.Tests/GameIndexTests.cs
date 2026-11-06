using System.Buffers.Binary;
using System.Text;
using GameServer.Data.Ggpk;

namespace GameServer.Gateway.Tests;

public class GameIndexTests
{
    static ulong Hash(string path) => MurmurHash64A.Hash(Encoding.UTF8.GetBytes(path.ToLowerInvariant()));

    // A bundle index: two bundles, then file records (hash, bundle, offset, size), no directories, an empty path table.
    static byte[] Index(params (ulong Hash, int Bundle, int Offset, int Size)[] files)
    {
        var bytes = new List<byte>();
        void I32(int v) { var b = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(b, v); bytes.AddRange(b); }
        I32(2);
        foreach (string name in new[] { "Folders/a", "b" }) { I32(name.Length); bytes.AddRange(Encoding.UTF8.GetBytes(name)); I32(1024); }
        I32(files.Length);
        foreach (var f in files)
        {
            var h = new byte[8];
            BinaryPrimitives.WriteUInt64LittleEndian(h, f.Hash);
            bytes.AddRange(h);
            I32(f.Bundle); I32(f.Offset); I32(f.Size);
        }
        I32(0);
        return [.. bytes];
    }

    [Fact]
    public void Files_are_found_by_path_whatever_their_order_and_a_repeated_hash_keeps_its_first_record()
    {
        var index = GameIndex.Parse(Index(
            (Hash("Art/z.dds"), 1, 30, 3),
            (Hash("Metadata/a.ot"), 0, 10, 1),
            (Hash("Data/Mods.datc64"), 1, 20, 2),
            (Hash("metadata/A.ot"), 1, 99, 9)));   // the same hash again: ignored, as before

        Assert.True(index.TryGetFile("Metadata/a.ot", out var a));
        Assert.Equal(new FileLocation(0, "Bundles2/Folders/a.bundle.bin", 10, 1), a);
        Assert.True(index.TryGetFile("DATA/MODS.DATC64", out var mods));
        Assert.Equal(new FileLocation(1, "Bundles2/b.bundle.bin", 20, 2), mods);
        Assert.True(index.TryGetFile("Art/z.dds", out var z));
        Assert.Equal((1, 30, 3), (z.BundleIndex, z.Offset, z.Size));
        Assert.False(index.TryGetFile("Art/missing.dds", out _));
        Assert.Equal(4, index.FileCount);
    }

    [Fact]
    public void Malformed_indexes_and_bundle_headers_fail_before_counts_allocate_or_slices_overflow()
    {
        foreach (int length in new[] { 0, 1, 2, 3, 4, 7, 8 })
            Assert.Equal("invalid-index", Assert.Throws<GgpkFormatException>(() => GameIndex.Parse(new byte[length])).Reason);
        foreach (int count in new[] { -1, int.MaxValue, int.MinValue, 0x40000000 })
        {
            var content = new byte[16];
            BinaryPrimitives.WriteInt32LittleEndian(content, count);
            Assert.Equal("invalid-index", Assert.Throws<GgpkFormatException>(() => GameIndex.Parse(content)).Reason);
            BinaryPrimitives.WriteInt32LittleEndian(content, 0);
            BinaryPrimitives.WriteInt32LittleEndian(content.AsSpan(4), count);
            Assert.Throws<GgpkFormatException>(() => GameIndex.Parse(content));
        }
        foreach (var file in new[] { (1UL, -1, 0, 1), (1UL, 2, 0, 1),
            (1UL, 0, -1, 1), (1UL, 0, int.MaxValue, 1), (1UL, 0, 0, -1), (1UL, 0, 1024, 1) })
            Assert.Equal("invalid-index", Assert.Throws<GgpkFormatException>(() => GameIndex.Parse(Index(file))).Reason);
        Assert.Equal("validation-limit", Assert.Throws<GgpkFormatException>(() => GameIndex.Parse(Index(), maxRecords: 1)).Reason);
        foreach (int length in new[] { 0, 3, 43, 59 })
            Assert.Throws<GgpkFormatException>(() => BundleFile.Layout(new byte[length]));

        byte[] bundle = new byte[65];
        void I32(int offset, int value) => BinaryPrimitives.WriteInt32LittleEndian(bundle.AsSpan(offset), value);
        I32(0, 1); I32(36, 1); I32(40, 1); I32(60, 1);
        bundle[64] = 42;
        Assert.Equal(new byte[] { 42 }, BundleFile.DecompressSlice(bundle, 0, 1, (_, bytes, _) => bytes.ToArray()));
        foreach (var slice in new[] { (-1, 1), (0, -1), (int.MaxValue, 1), (1, int.MaxValue), (0, 2) })
            Assert.Throws<GgpkFormatException>(() => BundleFile.DecompressSlice(bundle, slice.Item1, slice.Item2,
                (_, _, _) => throw new Xunit.Sdk.XunitException("invalid slice reached the decoder")));
        I32(36, int.MaxValue);
        Assert.Throws<GgpkFormatException>(() => BundleFile.Layout(bundle));
        I32(36, 1); I32(0, int.MaxValue); I32(40, int.MaxValue);
        Assert.Equal("validation-limit", Assert.Throws<GgpkFormatException>(() =>
            BundleFile.DecompressWhole(bundle, new IdentityDecoder(), maxOutputBytes: 1024)).Reason);
        I32(0, 1); I32(40, 1); I32(60, int.MaxValue);
        Assert.Throws<GgpkFormatException>(() => BundleFile.DecompressSlice(bundle, 0, 1, new IdentityDecoder()));
    }

    sealed class IdentityDecoder : IOodleDecoder
    {
        public byte[] Decompress(ReadOnlyMemory<byte> compressed, int size) => compressed.ToArray();
    }
}
