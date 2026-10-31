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
        foreach (string name in new[] { "Folders/a", "b" }) { I32(name.Length); bytes.AddRange(Encoding.UTF8.GetBytes(name)); I32(0); }
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
}
