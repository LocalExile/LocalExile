using System.Text;

namespace GameServer.Data.Ggpk;

/// <summary>
/// The GGPK's cached <c>gateway_list.txt</c>: the login servers a client may choose. Stored as UTF-16LE with a BOM and
/// CRLF lines, one per gateway, <c>&lt;id&gt;;&lt;display&gt;;&lt;login host:port&gt;;&lt;speedtest host&gt;;&lt;bool&gt;</c>
/// (Agents/docs/re/004-redirect-and-servers.md). Every player's copy must carry the same bytes: the patch welcome
/// compares the GGPK's root hash, so one player's edited list would otherwise make that player start patching
/// (Agents/docs/dev/lan-play.md).
/// </summary>
public static class GatewayList
{
    /// <summary>The client reads <c>Bundles2/gateway_list.txt</c> first, then <c>gateway_list.txt</c>; the last wins
    /// (0x140cfc2d0). Returns the entry that exists in this GGPK.</summary>
    public static readonly string[] Paths = ["Bundles2/gateway_list.txt", "gateway_list.txt"];

    /// <summary>The GGPK entry holding the list, or null when the GGPK has none.</summary>
    public static string? Find(string ggpkPath) =>
        Paths.FirstOrDefault(p => GgpkMaintenance.TryReadFile(ggpkPath, p) is not null);

    public static string Decode(byte[] b) =>
        b.Length >= 2 && b[0] == 0xFF && b[1] == 0xFE ? Encoding.Unicode.GetString(b, 2, b.Length - 2) : Encoding.Unicode.GetString(b);

    public static byte[] Encode(string text) =>
        [.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes(text)];

    /// <summary>The same list with every gateway's login <c>host:port</c> replaced; the other fields are kept as they are.</summary>
    public static string WithLogin(string text, string hostPort) =>
        string.Join("\r\n", text.Split("\r\n").Select(line =>
        {
            var fields = line.Split(';');
            if (fields.Length >= 3) fields[2] = hostPort;
            return string.Join(';', fields);
        }));

    /// <summary>
    /// Points every gateway at <paramref name="hostPort"/>, keeping a copy of the original in
    /// <paramref name="backupPath"/> the first time (null to keep none). Returns the entry written, or null when the
    /// GGPK has no list.
    /// </summary>
    public static string? SetLogin(string ggpkPath, string hostPort, string? backupPath)
    {
        string? entry = Find(ggpkPath);
        if (entry is null) return null;
        byte[] current = GgpkMaintenance.TryReadFile(ggpkPath, entry)!;
        if (backupPath is not null && !File.Exists(backupPath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(backupPath))!);
            File.WriteAllBytes(backupPath, current);
        }
        GgpkMaintenance.ReplaceFile(ggpkPath, entry, Encode(WithLogin(Decode(current), hostPort)));
        return entry;
    }
}