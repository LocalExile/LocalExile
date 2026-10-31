using System.Text.Json;

namespace GameServer.Data.Ggpk;

/// <summary>
/// The two values the servers need from the local client, in data/client-constants.json (never committed):
/// the HNC mask from its executable and the root hash of its <c>Content.ggpk</c>. Written by scripts/re/game-pack
/// (extract-data.ps1). The root hash changes when the gateway list in the GGPK does, so <c>GameServer.Patch</c>'s tools
/// update it, and live.ps1 refreshes it before each client start.
/// </summary>
public sealed record ClientConstants(string HncMask, string GgpkRootHash)
{
    public const string DefaultFile = "data/client-constants.json";

    public byte[] HncMaskBytes => Convert.FromHexString(HncMask);
    public byte[] GgpkRootHashBytes => Convert.FromHexString(GgpkRootHash);

    public static ClientConstants Load(string file = DefaultFile) =>
        JsonSerializer.Deserialize<ClientConstants>(File.ReadAllText(file)) ?? throw new InvalidDataException($"{file} is empty");

    /// <summary>Replaces the file in one step: the Patch server reads it for every client that connects.</summary>
    public void Save(string file = DefaultFile)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
        string temp = file + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this));
        File.Move(temp, file, overwrite: true);
    }
}
