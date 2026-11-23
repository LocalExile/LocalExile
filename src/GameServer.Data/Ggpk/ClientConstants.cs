using System.Text.Json;

namespace GameServer.Data.Ggpk;

/// <summary>
/// The two values the servers need from the client: the HNC mask from its executable, the same for every copy of the
/// client and kept in client.db (the client_constants table, sql/updates/client/), and the root hash of the local <c>Content.ggpk</c>,
/// in data/client-constants.json (never committed). Written by scripts/re/game-pack (extract-data.ps1). The root hash
/// changes when the gateway list in the GGPK does, so <c>GameServer.Patch</c>'s tools update it, and live.ps1 refreshes
/// it before each client start. Only the Patch server, which serves the client, reads it.
/// </summary>
public sealed record ClientConstants(string HncMask, string GgpkRootHash)
{
    public const string DefaultFile = "data/client-constants.json";

    public byte[] HncMaskBytes => Convert.FromHexString(HncMask);
    public byte[] GgpkRootHashBytes => Convert.FromHexString(GgpkRootHash);

    /// <summary>Whether client.db or the default file has the HNC mask.</summary>
    public static bool HasMask => GameServer.Core.ClientTables.Has("client_constants") || File.Exists(DefaultFile);

    /// <summary>The local file's values, with client.db's mask for the default file (without the file: no root hash).</summary>
    public static ClientConstants Load(string file = DefaultFile)
    {
        var local = File.Exists(file) ? JsonSerializer.Deserialize<ClientConstants>(File.ReadAllText(file)) : null;
        string? mask = null;
        if (file == DefaultFile && GameServer.Core.ClientTables.TableOrNull("client_constants") is { Count: > 0 } shared)
            mask = shared[0].Text("HncMask");
        mask ??= local?.HncMask;
        if (mask is null) throw new FileNotFoundException($"no HNC mask: not in {GameServer.Core.ClientTables.DefaultPath} or {file}", file);
        return new(mask, local?.GgpkRootHash ?? "");
    }

    /// <summary>Replaces the file in one step: the Patch server reads it for every client that connects.</summary>
    public void Save(string file = DefaultFile)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
        string temp = file + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this));
        File.Move(temp, file, overwrite: true);
    }
}
