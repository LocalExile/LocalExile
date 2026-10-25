using System.Buffers.Binary;
using System.Text;

namespace GameServer.Data.Ggpk;

/// <summary>Append one bundle descriptor and redirect one existing virtual file; preserve every other index byte.</summary>
public static class BundleIndexRedirect
{
    public static byte[] Create(byte[] content, string virtualPath, string bundleName, int newSize)
    {
        if (newSize < 0 || string.IsNullOrWhiteSpace(bundleName) || bundleName.Contains('/') || bundleName.Contains('\\'))
            throw new ArgumentException("Expected a simple bundle name and a nonnegative file size.");
        using var input = new MemoryStream(content, false);
        using var reader = new BinaryReader(input, Encoding.UTF8);
        int bundleCount = reader.ReadInt32();
        if (bundleCount < 0 || bundleCount > content.Length / 8) throw new InvalidDataException("Invalid bundle count.");
        for (int i = 0; i < bundleCount; i++)
        {
            int length = reader.ReadInt32();
            if (length < 0 || length > input.Length - input.Position - 4) throw new InvalidDataException("Invalid bundle name length.");
            string name = Encoding.UTF8.GetString(reader.ReadBytes(length));
            if (string.Equals(CanonicalName(name), CanonicalName(bundleName), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"An equivalent bundle name already exists in the index: {name}");
            reader.ReadInt32();
        }
        int filesCountOffset = checked((int)input.Position);
        int fileCount = reader.ReadInt32();
        if (fileCount < 0 || (long)fileCount * 20 > input.Length - input.Position)
            throw new InvalidDataException("Invalid index file count.");
        int filesOffset = checked((int)input.Position);
        ulong targetHash = MurmurHash64A.Hash(Encoding.UTF8.GetBytes(virtualPath.ToLowerInvariant()));
        int target = -1;
        for (int i = 0; i < fileCount; i++)
            if (BinaryPrimitives.ReadUInt64LittleEndian(content.AsSpan(filesOffset + i * 20)) == targetHash)
            {
                if (target >= 0) throw new InvalidDataException("Duplicate virtual file hash in index.");
                target = filesOffset + i * 20;
            }
        if (target < 0) throw new FileNotFoundException("Virtual file is not in the bundle index.", virtualPath);
        byte[] tail = content.AsSpan(filesCountOffset).ToArray();
        int relative = target - filesCountOffset;
        BinaryPrimitives.WriteInt32LittleEndian(tail.AsSpan(relative + 8), bundleCount);
        BinaryPrimitives.WriteInt32LittleEndian(tail.AsSpan(relative + 12), 0);
        BinaryPrimitives.WriteInt32LittleEndian(tail.AsSpan(relative + 16), newSize);
        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output, Encoding.UTF8);
        writer.Write(checked(bundleCount + 1));
        writer.Write(content, 4, filesCountOffset - 4);
        byte[] nameBytes = Encoding.UTF8.GetBytes(bundleName);
        writer.Write(nameBytes.Length); writer.Write(nameBytes); writer.Write(newSize);
        writer.Write(tail);
        return output.ToArray();
    }

    static string CanonicalName(string name)
    {
        name = name.Replace('\\', '/');
        if (name.StartsWith("Bundles2/", StringComparison.OrdinalIgnoreCase)) name = name[9..];
        if (name.EndsWith(".bundle.bin", StringComparison.OrdinalIgnoreCase)) name = name[..^11];
        return name;
    }
}
