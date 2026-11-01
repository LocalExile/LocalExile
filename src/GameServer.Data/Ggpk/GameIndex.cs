using System.Buffers.Binary;
using System.Text;

namespace GameServer.Data.Ggpk;

/// <summary>A located file inside a bundle.</summary>
public readonly record struct FileLocation(int BundleIndex, string BundleFile, int Offset, int Size);

/// <summary>
/// The decoded <c>Bundles2/_.index.bin</c> file table.
/// Format per Game-tool-dev Bundle-scheme wiki (see also Game-dat-viewer index-bundle.ts).
/// </summary>
public sealed class GameIndex
{
    private const int FileRecordSize = 20; // u64 hash + i32 bundleIdx + i32 offset + i32 size

    // The file records sorted by path hash (records of one hash in their index order), searched by halving: a
    // Dictionary of 1.2 million hashes took another 39 MB beside the records.
    private readonly byte[] _filesInfo;
    private readonly string[] _bundleFiles;

    public int FileCount { get; }
    public IReadOnlyList<string> BundleFiles => _bundleFiles;

    /// <summary>Directory records (u64 hash, i32 offset, i32 size, i32 recursive size) into the path table.</summary>
    public IReadOnlyList<(int Offset, int Size)> Directories { get; private init; } = [];

    /// <summary>The path representation table: itself a compressed bundle (decompress with BundleFile).</summary>
    public byte[] PathTableBundle { get; private init; } = [];

    private GameIndex(byte[] filesInfo, string[] bundleFiles, int fileCount)
    {
        _bundleFiles = bundleFiles;
        FileCount = fileCount;

        var hashes = new ulong[fileCount];
        var order = new int[fileCount];
        for (int i = 0; i < fileCount; i++)
        {
            hashes[i] = BinaryPrimitives.ReadUInt64LittleEndian(filesInfo.AsSpan(i * FileRecordSize, 8));
            order[i] = i;
        }
        Array.Sort(hashes, order);
        // Equal hashes keep their index order, so a lookup finds the first record of a hash (as TryAdd kept it).
        for (int start = 0, end; start < fileCount; start = end)
        {
            for (end = start + 1; end < fileCount && hashes[end] == hashes[start]; end++) { }
            if (end - start > 1) Array.Sort(order, start, end - start);
        }
        _filesInfo = new byte[fileCount * FileRecordSize];
        for (int i = 0; i < fileCount; i++)
            filesInfo.AsSpan(order[i] * FileRecordSize, FileRecordSize).CopyTo(_filesInfo.AsSpan(i * FileRecordSize));
    }

    private ulong HashAt(int record) => BinaryPrimitives.ReadUInt64LittleEndian(_filesInfo.AsSpan(record * FileRecordSize, 8));

    // The first record with this hash, or -1.
    private int Find(ulong hash)
    {
        int lo = 0, hi = FileCount;
        while (lo < hi)
        {
            int mid = lo + (hi - lo) / 2;
            if (HashAt(mid) < hash) lo = mid + 1;
            else hi = mid;
        }
        return lo < FileCount && HashAt(lo) == hash ? lo : -1;
    }

    public static GameIndex Parse(byte[] indexContent)
    {
        ArgumentNullException.ThrowIfNull(indexContent);
        int offset = 0;

        // Bundles.
        int bundlesCount = BinaryPrimitives.ReadInt32LittleEndian(indexContent.AsSpan(offset));
        offset += 4;

        var bundleFiles = new string[bundlesCount];
        for (int i = 0; i < bundlesCount; i++)
        {
            int nameLen = BinaryPrimitives.ReadInt32LittleEndian(indexContent.AsSpan(offset));
            offset += 4;
            string name = Encoding.UTF8.GetString(indexContent, offset, nameLen);
            offset += nameLen;
            _ = BinaryPrimitives.ReadInt32LittleEndian(indexContent.AsSpan(offset)); // decompressed size
            offset += 4;

            bundleFiles[i] = ToBundlePath(name);
        }

        // Files.
        int filesCount = BinaryPrimitives.ReadInt32LittleEndian(indexContent.AsSpan(offset));
        offset += 4;
        var filesInfo = indexContent.AsSpan(offset, filesCount * FileRecordSize).ToArray();
        offset += filesCount * FileRecordSize;

        // Directories (only needed to list paths).
        int dirsCount = BinaryPrimitives.ReadInt32LittleEndian(indexContent.AsSpan(offset));
        offset += 4;
        var dirs = new (int, int)[dirsCount];
        for (int i = 0; i < dirsCount; i++)
        {
            var d = indexContent.AsSpan(offset + i * 20);
            dirs[i] = (BinaryPrimitives.ReadInt32LittleEndian(d[8..]), BinaryPrimitives.ReadInt32LittleEndian(d[12..]));
        }
        offset += dirsCount * 20;

        // Remaining bytes: the path representation table, a bundle of its own.
        return new GameIndex(filesInfo, bundleFiles, filesCount) { Directories = dirs, PathTableBundle = indexContent[offset..] };
    }

    private static string ToBundlePath(string name)
    {
        string file = name.EndsWith(".bundle.bin", StringComparison.OrdinalIgnoreCase)
            ? name
            : name + ".bundle.bin";
        return file.StartsWith("Bundles2/", StringComparison.OrdinalIgnoreCase)
            ? file
            : "Bundles2/" + file;
    }

    /// <summary>
    /// Every file path, from the decompressed path table. Per directory record: a stream of (u32 n, NUL-terminated
    /// UTF-8 string); n = 0 toggles "base" mode (entering it clears the base list); otherwise the string is appended to
    /// base entry n − 1 when one exists; in base mode the result is added to the base list, else it is a path.
    /// </summary>
    public IEnumerable<string> Paths(byte[] pathTable)
    {
        foreach (var (start, size) in Directories)
        {
            var bases = new List<string>();
            bool baseMode = false;
            int pos = start, end = start + size;
            while (pos + 4 <= end)
            {
                int n = BinaryPrimitives.ReadInt32LittleEndian(pathTable.AsSpan(pos));
                pos += 4;
                if (n == 0)
                {
                    baseMode = !baseMode;
                    if (baseMode) bases.Clear();
                    continue;
                }
                int z = Array.IndexOf(pathTable, (byte)0, pos);
                string str = Encoding.UTF8.GetString(pathTable, pos, z - pos);
                pos = z + 1;
                if (n - 1 < bases.Count) str = bases[n - 1] + str;
                if (baseMode) bases.Add(str);
                else yield return str;
            }
        }
    }

    /// <summary>Look up a file by its forward-slash path (case-insensitive).</summary>
    public bool TryGetFile(string path, out FileLocation location)
    {
        ulong h = MurmurHash64A.Hash(Encoding.UTF8.GetBytes(path.ToLowerInvariant()));
        int record = Find(h);
        if (record < 0)
        {
            location = default;
            return false;
        }

        int o = record * FileRecordSize;
        int bundleIdx = BinaryPrimitives.ReadInt32LittleEndian(_filesInfo.AsSpan(o + 8));
        int fileOffset = BinaryPrimitives.ReadInt32LittleEndian(_filesInfo.AsSpan(o + 12));
        int size = BinaryPrimitives.ReadInt32LittleEndian(_filesInfo.AsSpan(o + 16));

        if ((uint)bundleIdx >= (uint)_bundleFiles.Length)
        {
            location = default;
            return false;
        }

        location = new FileLocation(bundleIdx, _bundleFiles[bundleIdx], fileOffset, size);
        return true;
    }
}
