// Extract the exact target client's ordinary/Phrecia topology, never a different patch's web export.
// dotnet run --project scripts/re/passive-graphs -- Client/Content.ggpk data/passive-skill-graphs.json
using System.Security.Cryptography;
using System.Text.Json;
using GameServer.Data;
using GameServer.Data.Ggpk;
using GgDump;

const string Target = "metadata/alternateskillgraphs/customascendancypassiveskillgraph.psg";
const string OriginalNormalHash = "82D82CD3CD2DAD81606AF0DA9D18FCD53B2E92CF006550947D65A4F7E102DC32";
const string OriginalPhreciaHash = "9A6D43A997BE44B53FC13FB6CED2E9F935AA11F8E4C4940CE8ECF9300A2FC1BC";
const string Artifact = "phrecia-passive-skillgraph.psg";
string Backup(string pack) => VirtualFilePatch.BackupFolder(pack);

if (args.Length == 2 && args[0] == "--recover")
{
    Console.WriteLine(VirtualFilePatch.Recover(args[1], Backup(args[1])) ? "Interrupted GGPK writes recovered before index parsing." : "No interrupted GGPK writes remain.");
    return;
}

if (args.Length == 2 && args[0] == "--restore")
{
    Console.WriteLine(VirtualFilePatch.Restore(args[1], Backup(args[1])) ? "Original Phrecia graph restored." : "Original Phrecia graph already restored.");
    return;
}
if (args.Length != 2 && !(args.Length == 3 && args[0] is "--apply" or "--check"))
    throw new ArgumentException("Usage: passive-graphs <Content.ggpk> <output.json>; --apply|--check <Content.ggpk> <graph.json>; --restore|--recover <Content.ggpk>");
using var decoder = new NodeOodleDecoder("node", Path.GetFullPath("Tools/ooz/ooz_worker.mjs"));
if (args[0] is "--apply" or "--check")
{
    if (args[0] == "--apply") VirtualFilePatch.Recover(args[1], Backup(args[1]));
    else VirtualFilePatch.EnsureNoPending(Backup(args[1]));
    string graphJson = Path.GetFullPath(args[2]);
    using var expected = JsonDocument.Parse(File.ReadAllBytes(graphJson));
    if (expected.RootElement.GetProperty("revision").GetString() != "3.29.3b" ||
        expected.RootElement.GetProperty("clientPatchVersion").GetInt32() != 1 ||
        expected.RootElement.GetProperty("normal").GetProperty("sha256").GetString() != OriginalNormalHash ||
        expected.RootElement.GetProperty("originalPhreciaSha256").GetString() != OriginalPhreciaHash)
        throw new InvalidDataException("The graph manifest is not the pinned client/revision/patch version.");
    using (var actual = GameFileSystem.Open(args[1], decoder))
        if (VirtualFilePatch.Hash(actual.ReadFile("metadata/passiveskillgraph.psg")) != OriginalNormalHash)
            throw new InvalidDataException("The installed ordinary PSG is not the pinned 3.29.3b graph. No patch was applied.");
    byte[] desired = File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(graphJson)!, Artifact));
    _ = PassiveSkillGraph.Read(desired);
    if (VirtualFilePatch.Hash(desired) != expected.RootElement.GetProperty("phrecia").GetProperty("sha256").GetString())
        throw new InvalidDataException("Generated server graph and client graph hashes differ. Run extract-data.ps1 -Only passive-graphs.");
    if (args[0] == "--apply")
        Console.WriteLine(VirtualFilePatch.Apply(args[1], Target, desired, OriginalPhreciaHash, Backup(args[1]), decoder)
            ? "Installed merged Phrecia graph; original index/file backed up under Client/LocalExileBackups."
            : "Merged Phrecia graph already installed.");
    if (!VirtualFilePatch.Matches(args[1], Target, desired, decoder, Backup(args[1])))
        throw new InvalidDataException("The client graph differs from the server graph. Close the client and run live.ps1 to install it.");
    Console.WriteLine("Client/server Phrecia graph SHA-256 verified: " + VirtualFilePatch.Hash(desired));
    return;
}
VirtualFilePatch.EnsureNoPending(Backup(args[0]));
using var fs = GameFileSystem.Open(args[0], decoder);
byte[] normalBytes = fs.ReadFile("metadata/passiveskillgraph.psg");
if (VirtualFilePatch.Hash(normalBytes) != OriginalNormalHash) throw new InvalidDataException("The ordinary PSG is not the pinned 3.29.3b graph.");
byte[] installed = fs.ReadFile(Target);
byte[] phreciaBytes = VirtualFilePatch.Hash(installed) == OriginalPhreciaHash
    ? installed : VirtualFilePatch.OriginalFile(Backup(args[0]), OriginalPhreciaHash);
using var passives = JsonDocument.Parse(File.ReadAllBytes("data/tables/PassiveSkills.json"));
HashSet<uint> ascendancyIds = passives.RootElement.EnumerateArray()
    .Where(r => r.GetProperty("AscendancyKey").ValueKind == JsonValueKind.Number)
    .Select(r => r.GetProperty("PassiveSkillGraphId").GetUInt32()).ToHashSet();
var merged = AdditionalAscendancyGraph.Merge(PassiveSkillGraph.Read(normalBytes), PassiveSkillGraph.Read(phreciaBytes), ascendancyIds);
byte[] mergedBytes = merged.Write();
if (VirtualFilePatch.Hash(installed) != OriginalPhreciaHash && !installed.AsSpan().SequenceEqual(mergedBytes))
    throw new InvalidDataException("The installed Phrecia graph has other changes; restore them before extraction.");
object Extract(string path, byte[] bytes)
{
    var graph = PassiveSkillGraph.Read(bytes);
    if (!bytes.AsSpan().SequenceEqual(graph.Write())) throw new InvalidDataException($"Lossless passive graph check failed: {path}");
    var adjacent = graph.Adjacency();
    Console.WriteLine($"{path}: {graph.Nodes.Count} nodes, {graph.Groups.Count} groups, " +
        $"{graph.Nodes.Values.Sum(n => n.Connections.Count)} directed connections; exact round trip");
    return new
    {
        source = path,
        sha256 = Convert.ToHexString(SHA256.HashData(bytes)),
        roots = graph.Roots,
        nodes = graph.Nodes.Keys.Order().Select(id => new { id, adjacent = adjacent[id] }).ToArray()
    };
}
var output = new
{
    revision = "3.29.3b",
    clientPatchVersion = 1,
    clientGraphFile = Artifact,
    originalPhreciaSha256 = OriginalPhreciaHash,
    normal = Extract("metadata/passiveskillgraph.psg", normalBytes),
    phrecia = Extract(Target, mergedBytes)
};
string target = Path.GetFullPath(args[1]);
Directory.CreateDirectory(Path.GetDirectoryName(target)!);
string artifactPath = Path.Combine(Path.GetDirectoryName(target)!, Artifact);
File.WriteAllBytes(artifactPath + ".tmp", mergedBytes);
File.Move(artifactPath + ".tmp", artifactPath, overwrite: true);
string pending = target + ".tmp";
File.WriteAllText(pending, JsonSerializer.Serialize(output));
File.Move(pending, target, overwrite: true);
Console.WriteLine($"Wrote {target}");

// Lets the data tests locate this CLI for fresh-process recovery checks without hard-coded build-output paths.
public static class PassiveGraphToolMarker { }
