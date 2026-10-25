using LibGGPK3;
using LibGGPK3.Records;

namespace GameServer.Data.Ggpk;

/// <summary>
/// Read-only view over a standalone <c>Content.ggpk</c>: resolves files through
/// the bundle index and decompresses bundle chunks with managed Oodle (OozSharp).
/// No native Oodle library is required.
/// </summary>
public sealed class GameFileSystem : IDisposable
{
    private const long BundleCacheBudgetBytes = 1L << 30; // 1 GiB

    private readonly GGPK _ggpk;
    private readonly IOodleDecoder _oodle;
    private readonly Dictionary<int, byte[]> _bundleCache = new();
    private readonly LinkedList<int> _lru = new();
    private readonly Dictionary<int, LinkedListNode<int>> _lruNodes = new();
    private long _cacheBytes;

    public GameIndex Index { get; }

    private GameFileSystem(GGPK ggpk, GameIndex index, IOodleDecoder oodle)
    {
        _ggpk = ggpk;
        Index = index;
        _oodle = oodle;
    }

    public static GameFileSystem Open(string ggpkPath, IOodleDecoder? oodle = null)
    {
        oodle ??= new ManagedOodleDecoder();
        // Read-only and shared, so the data can be read while the game client has the GGPK open.
        var ggpk = new GGPK(new FileStream(ggpkPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete), false);
        try
        {
            byte[] indexRaw = ReadGgpkFile(ggpk, "Bundles2/_.index.bin");
            byte[] indexContent = BundleFile.DecompressWhole(indexRaw, oodle);
            return new GameFileSystem(ggpk, GameIndex.Parse(indexContent), oodle);
        }
        catch
        {
            var stream = ggpk.UnsafeGetStream();
            ggpk.Dispose();
            stream.Dispose(); // LibGGPK3's Dispose returns early for a read-only stream.
            throw;
        }
    }

    public bool TryReadFile(string path, out byte[] data)
    {
        if (!Index.TryGetFile(path, out var loc))
        {
            data = Array.Empty<byte>();
            return false;
        }
        data = BundleFile.DecompressSlice(GetBundle(loc.BundleIndex), loc.Offset, loc.Size, _oodle);
        return true;
    }

    /// <summary>Every file path in the bundle index (decompresses the path table once per call).</summary>
    public IEnumerable<string> EnumeratePaths() => Index.Paths(BundleFile.DecompressWhole(Index.PathTableBundle, _oodle));

    public byte[] ReadFile(string path)
    {
        if (!TryReadFile(path, out var data))
            throw new FileNotFoundException($"file not present in bundle index: {path}", path);
        return data;
    }

    public void ClearBundleCache()
    {
        _bundleCache.Clear();
        _lru.Clear();
        _lruNodes.Clear();
        _cacheBytes = 0;
    }

    private byte[] GetBundle(int index)
    {
        if (_bundleCache.TryGetValue(index, out var cached))
        {
            Touch(index);
            return cached;
        }

        byte[] raw = ReadGgpkFile(_ggpk, Index.BundleFiles[index]);
        _bundleCache[index] = raw;
        _lruNodes[index] = _lru.AddFirst(index);
        _cacheBytes += raw.Length;

        while (_cacheBytes > BundleCacheBudgetBytes && _lru.Last is { } last)
        {
            int evict = last.Value;
            _lru.RemoveLast();
            _lruNodes.Remove(evict);
            if (_bundleCache.Remove(evict, out var evicted))
                _cacheBytes -= evicted.Length;
        }

        return raw;
    }

    private void Touch(int index)
    {
        if (_lruNodes.TryGetValue(index, out var node))
        {
            _lru.Remove(node);
            _lru.AddFirst(node);
        }
    }

    private static byte[] ReadGgpkFile(GGPK ggpk, string path)
    {
        if (ggpk.Root.TryFindNode(path, out var node) && node is FileRecord fr)
            return fr.Read();
        throw new FileNotFoundException($"file not found in GGPK: {path}", path);
    }

    /// <summary>Read a raw (undecoded) file straight from the GGPK container, bypassing bundles.</summary>
    public static byte[] ReadRawGgpkFile(string ggpkPath, string path)
    {
        using var ggpk = new GGPK(ggpkPath);
        return ReadGgpkFile(ggpk, path);
    }

    public void Dispose()
    {
        var stream = _ggpk.UnsafeGetStream();
        try { _ggpk.Dispose(); }
        finally { stream.Dispose(); }
    }
}
