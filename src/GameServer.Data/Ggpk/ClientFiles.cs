using System.Security.Cryptography;
using LibGGPK3;
using LibGGPK3.Records;

namespace GameServer.Data.Ggpk;

/// <summary>How a client install stores its game files.</summary>
public enum ClientLayout
{
    /// <summary>The standalone install: one <c>Content.ggpk</c> holding <c>Bundles2/</c> and the loose files.</summary>
    Standalone,
    /// <summary>The Steam install: the same <c>Bundles2/</c> tree and loose files as plain files beside the executable
    /// (<c>PathOfExileSteam.exe</c>, byte-identical to the standalone <c>PathOfExile.exe</c>).</summary>
    Steam,
}

/// <summary>
/// The client's game files in either layout. A location is a client folder, or (standalone) its <c>Content.ggpk</c>
/// itself. Every tool that reads or writes the client's files goes through here or <see cref="GameFileSystem"/>.
/// </summary>
public static class ClientFiles
{
    public const string Ggpk = "Content.ggpk";
    public const string Index = "Bundles2/_.index.bin";

    /// <summary>SHA-256 of <c>Bundles2/_.index.bin</c> in Steam's 3.29.3c: 3.29.3.5, the hotfix of 2026-11-09 (app 238960,
    /// depot 238961 manifest 2467965126630699088). The only Steam build LocalExile accepts.</summary>
    public const string SteamIndexSha256 = "d23c78997206716586041a3e47a89dde0eb516f1488df4a1ccf39b0d51be54d0";

    /// <summary>The layout and the path to open: the GGPK file for a standalone install, the folder for a Steam one. Null
    /// when the location holds neither.</summary>
    public static (ClientLayout Layout, string Path)? Resolve(string location)
    {
        if (File.Exists(location)) return (ClientLayout.Standalone, location);
        if (!Directory.Exists(location)) return null;
        string ggpk = System.IO.Path.Combine(location, Ggpk);
        if (File.Exists(ggpk)) return (ClientLayout.Standalone, ggpk);
        if (File.Exists(Loose(location, Index))) return (ClientLayout.Steam, location);
        return null;
    }

    static (ClientLayout Layout, string Path) Require(string location) => Resolve(location)
        ?? throw new FileNotFoundException($"{location} holds neither {Ggpk} (standalone) nor {Index} (Steam)", location);

    /// <summary>Why the location's game files are not ones LocalExile runs, or null. A Steam install must be the 3.29.3c
    /// build <see cref="SteamIndexSha256"/> names; a standalone one is checked by its executable.</summary>
    public static string? Problem(string location)
    {
        if (Resolve(location) is not { } found)
            return $"{location} has neither {Ggpk} (the standalone client) nor {Index} (the Steam client)";
        if (found.Layout == ClientLayout.Standalone) return null;
        using var index = OpenShared(Loose(found.Path, Index));
        string hash = Convert.ToHexString(SHA256.HashData(index)).ToLowerInvariant();
        return hash == SteamIndexSha256 ? null
            : $"{location} is a Steam install of another version; LocalExile needs Steam's 3.29.3c (the 2026-11-09 hotfix, 3.29.3.5)";
    }

    /// <summary>A file's bytes (a path inside the GGPK, or relative to the Steam folder), or null. Read-only and
    /// share-friendly: a running client holds its files open for reading and lets only readers in.</summary>
    public static byte[]? TryReadFile(string location, string path)
    {
        var (layout, at) = Require(location);
        if (layout == ClientLayout.Steam)
        {
            string file = Loose(at, path);
            if (!File.Exists(file)) return null;
            using var stream = OpenShared(file);
            byte[] data = new byte[stream.Length];
            stream.ReadExactly(data);
            return data;
        }
        using var ggpkStream = OpenShared(at);
        using var ggpk = new GGPK(ggpkStream, false);
        return ggpk.Root.TryFindNode(path, out var node) && node is FileRecord fr ? fr.Read() : null;
    }

    /// <summary>Replaces an existing file in place. In a GGPK, LibGGPK3 also updates the file and directory hashes up to the
    /// root. Never while a client runs: it holds the files open.</summary>
    public static void ReplaceFile(string location, string path, ReadOnlySpan<byte> data)
    {
        var (layout, at) = Require(location);
        if (layout == ClientLayout.Steam)
        {
            string file = Loose(at, path);
            if (!File.Exists(file)) throw new FileNotFoundException($"file not found in the Steam client: {path}", file);
            using var stream = new FileStream(file, FileMode.Truncate, FileAccess.Write, FileShare.Read);
            stream.Write(data);
            return;
        }
        using var ggpk = new GGPK(at);
        if (!ggpk.Root.TryFindNode(path, out var node) || node is not FileRecord fr)
            throw new FileNotFoundException($"file not found in GGPK: {path}", path);
        fr.Write(data);
    }

    internal static string Loose(string folder, string path) => System.IO.Path.Combine(folder, path.Replace('/', System.IO.Path.DirectorySeparatorChar));

    internal static FileStream OpenShared(string file) => new(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
}
