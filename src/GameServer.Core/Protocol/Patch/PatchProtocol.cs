using GameServer.Core.Protocol.Login;

namespace GameServer.Core.Protocol.Patch;

/// <summary>
/// The patching server's TCP protocol (client 0x1428f2390 / dispatch 0x1428f3230, factory 0x1428ff920), as far as a
/// server needs it to let an up-to-date client through. Messages are a 1-byte id and fields, with no length prefix.
/// <list type="bullet">
/// <item>C→S 0x01: u8 protocol version (7 in 3.29.3b and 3.29.3c), sent on connect.</item>
/// <item>S→C 0x02 (vtable 0x1436497e0, read 0x1429ae290): bytes[32] root hash, wstr web root, wstr backup web root.
/// If the hash equals the hash on the client's local root directory, it logs "No patching required" and goes on to
/// the login screen. Otherwise it requests the root contents (C→S 0x03 …) and patches against the web root.</item>
/// </list>
/// Wide strings on this socket are in raw mode: u16 BE count, then that many UTF-16LE units (the reader 0x140e0b480 with
/// socket flag +0x254 set), unlike the login protocol's compact encoding. Verified against patch.pathofexile.com:12995,
/// which answers the 2-byte hello with id 2, 32 zero bytes (so a normal client always checks its files) and the web root
/// "https://patch.poecdn.com/3.29.3.3/" twice.
/// </summary>
public static class PatchProtocol
{
    public const byte Hello = 0x01, Welcome = 0x02;
    public const byte ExpectedVersion = 7;

    /// <summary>Returns the client's protocol version once its 2-byte hello has arrived; null if more bytes are needed.</summary>
    public static byte? TryReadHello(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < 2) return null;
        if (buf[0] != Hello) throw new InvalidDataException($"expected patch hello 0x01, got 0x{buf[0]:x2}");
        return buf[1];
    }

    public static byte[] EncodeWelcome(ReadOnlySpan<byte> rootHash, string webRoot, string backupWebRoot)
    {
        if (rootHash.Length != 32) throw new ArgumentException("root hash must be 32 bytes", nameof(rootHash));
        var w = new LoginWriter().U8(Welcome).Bytes(rootHash);
        foreach (string s in new[] { webRoot, backupWebRoot })
            w.U16(checked((ushort)s.Length)).Bytes(System.Text.Encoding.Unicode.GetBytes(s));
        return w.ToArray();
    }
}

/// <summary>
/// What GameServer.Patch tells the client and whether its web root may reach the real CDN. By default both web roots
/// in the welcome are our own (http://127.0.0.1:8080/) and the web root serves only our overrides (gateway_list.txt),
/// so a client that decides to patch can never fetch real files and overwrite the patched client. Passing requests
/// through to the CDN needs an explicit opt-in (Game_CDN_PASSTHROUGH=1 or --cdn-passthrough).
/// </summary>
public sealed record PatchServerConfig(string WebRoot, string BackupWebRoot, bool CdnPassthrough, string CdnBase)
{
    public const string DefaultListenUrl = "http://127.0.0.1:8080";
    public const string DefaultCdnBase = "https://patch.poecdn.com";
    public const string PassthroughFlag = "--cdn-passthrough";

    /// <param name="env">Environment lookup (Game_PATCH_WEB_ROOT, Game_CDN_PASSTHROUGH, Game_CDN_MIRROR).</param>
    /// <param name="listenUrl">The web root's bind URL; the advertised root defaults to it with wildcard hosts as 127.0.0.1.</param>
    public static PatchServerConfig From(Func<string, string?> env, string listenUrl = DefaultListenUrl, bool passthroughFlag = false)
    {
        string root = env("Game_PATCH_WEB_ROOT") ?? LocalRoot(listenUrl);
        root = root.TrimEnd('/') + "/";
        bool passthrough = passthroughFlag || env("Game_CDN_PASSTHROUGH") is "1" or "true" or "TRUE" or "True";
        return new(root, root, passthrough, (env("Game_CDN_MIRROR") ?? DefaultCdnBase).TrimEnd('/'));
    }

    private static string LocalRoot(string listenUrl) =>
        listenUrl.Split(';')[0].Replace("0.0.0.0", "127.0.0.1").Replace("://+", "://127.0.0.1").Replace("://*", "://127.0.0.1");
}
