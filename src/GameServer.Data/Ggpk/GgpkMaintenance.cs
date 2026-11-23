using LibGGPK3;
using LibGGPK3.Records;
using System.Runtime.Intrinsics;

namespace GameServer.Data.Ggpk;

/// <summary>
/// Small direct operations on a <c>Content.ggpk</c> for running a private server. Never use these while the game client
/// is running: the client keeps the GGPK open.
/// </summary>
public static class GgpkMaintenance
{
    /// <summary>
    /// SHA-256 hash stored on the root directory record. The patch client compares it with the hash in the patch server's
    /// first reply; if they are equal it logs "No patching required" and skips patching (client 0x1428f31c0).
    /// </summary>
    public static byte[] RootHash(string ggpkPath)
    {
        // Read-only and share-friendly, so it also works while another process has the file open (if it allows readers).
        using var stream = new FileStream(ggpkPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var ggpk = new GGPK(stream, false);
        return HashBytes(ggpk.Root);
    }

    /// <summary>
    /// Gives a root directory whose hash is zero a hash again and returns it; null when the hash is already set. The client
    /// skips patching only when its local root hash is non-zero (0x1421d3460) and equals the welcome's, so with a zero hash
    /// it always asks the patch server for the root contents, which ours does not serve. The value only has to be non-zero
    /// and match data/client-constants.json, so it is the SHA-256 of the children's hashes (GGG's own value is not
    /// reproducible from the file). Other tools leave a zero hash on purpose (LibGGPK3's <c>EraseRootHash</c>) so that the
    /// official client checks its files. LibGGPK3 renews only directories it changed, never a clean root, so the hash is
    /// written directly: PDIR is u32 length, tag, u32 name length, u32 entry count, then the 32-byte hash.
    /// </summary>
    public static byte[]? RepairRootHash(string ggpkPath)
    {
        long hashOffset;
        byte[] hash;
        using (var stream = new FileStream(ggpkPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var ggpk = new GGPK(stream, false))
        {
            if (HashBytes(ggpk.Root).AsSpan().ContainsAnyExcept((byte)0)) return null;
            byte[] children = [.. ggpk.Root.SelectMany(HashBytes)];
            hash = System.Security.Cryptography.SHA256.HashData(children);
            hashOffset = ggpk.Root.Offset + 16;
        }
        using (var stream = new FileStream(ggpkPath, FileMode.Open, FileAccess.Write, FileShare.Read))
        {
            stream.Position = hashOffset;
            stream.Write(hash);
        }
        return RootHash(ggpkPath);
    }

    private static byte[] HashBytes(TreeNode node)
    {
        byte[] b = new byte[32];
        node.Hash.CopyTo(b);
        return b;
    }
}
