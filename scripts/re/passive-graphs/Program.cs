// Read-only analysis of the exact target client's ordinary/Phrecia topology, never a different patch's web export.
// dotnet run --project scripts/re/passive-graphs -- Client/Content.ggpk data/passive-skill-graphs.json
using System.Security.Cryptography;
using System.Text.Json;
using GameServer.Data;
using GameServer.Data.Ggpk;
using GgDump;

const string Target = "metadata/alternateskillgraphs/customascendancypassiveskillgraph.psg";
const string OriginalNormalHash = "82D82CD3CD2DAD81606AF0DA9D18FCD53B2E92CF006550947D65A4F7E102DC32";
const string OriginalPhreciaHash = "9A6D43A997BE44B53FC13FB6CED2E9F935AA11F8E4C4940CE8ECF9300A2FC1BC";
const string CombinedHash = "F2766B5A4618CDBE7E2993973B4D653F155F71BDFE0E5D063A425196CC93D638";
const string Artifact = "phrecia-passive-skillgraph.psg";

if (args.Length != 2 || args[0].StartsWith("--", StringComparison.Ordinal))
    throw new ArgumentException("Usage: passive-graphs <Content.ggpk> data/passive-skill-graphs.json (read-only graph analysis; no client installation)");
string ggpkPath = Path.GetFullPath(args[0]);
string target = Path.GetFullPath(args[1]);
if (!string.Equals(target, Path.GetFullPath("data/passive-skill-graphs.json"), StringComparison.OrdinalIgnoreCase))
    throw new ArgumentException("Graph analysis output must be data/passive-skill-graphs.json in this repository.");
for (var directory = new DirectoryInfo(Path.GetDirectoryName(target)!); directory is not null; directory = directory.Parent)
    if (directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint) != 0)
        throw new ArgumentException("Graph analysis output must use ordinary directories, not links or junctions.");
string artifactPath = Path.Combine(Path.GetDirectoryName(target)!, Artifact);
foreach (string outputPath in new[] { target, target + ".tmp", artifactPath, artifactPath + ".tmp" })
{
    if (string.Equals(outputPath, ggpkPath, StringComparison.OrdinalIgnoreCase))
        throw new ArgumentException("Graph analysis output cannot overwrite its source archive.");
    if (File.Exists(outputPath) && (File.GetAttributes(outputPath) & FileAttributes.ReparsePoint) != 0)
        throw new ArgumentException("Graph analysis output must use ordinary files, not links.");
}

using var decoder = new NodeOodleDecoder("node", Path.GetFullPath("Tools/ooz/ooz_worker.mjs"));
using var fs = GameFileSystem.Open(ggpkPath, decoder);
byte[] normalBytes = fs.ReadFile("metadata/passiveskillgraph.psg");
if (Convert.ToHexString(SHA256.HashData(normalBytes)) != OriginalNormalHash)
    throw new InvalidDataException("The ordinary PSG is not the pinned original 3.29.3b graph. The client archive was not changed.");
byte[] phreciaBytes = fs.ReadFile(Target);
string alternateHash = Convert.ToHexString(SHA256.HashData(phreciaBytes));
if (alternateHash != OriginalPhreciaHash && alternateHash != CombinedHash)
    throw new InvalidDataException("The alternate PSG is neither the pinned original nor the pinned compatible 3.29.3b graph. The client archive was not changed.");
using var passives = JsonDocument.Parse(File.ReadAllBytes("data/tables/PassiveSkills.json"));
HashSet<uint> ascendancyIds = passives.RootElement.EnumerateArray()
    .Where(r => r.GetProperty("AscendancyKey").ValueKind == JsonValueKind.Number)
    .Select(r => r.GetProperty("PassiveSkillGraphId").GetUInt32()).ToHashSet();
var merged = alternateHash == CombinedHash ? PassiveSkillGraph.Read(phreciaBytes)
    : AdditionalAscendancyGraph.Merge(PassiveSkillGraph.Read(normalBytes), PassiveSkillGraph.Read(phreciaBytes), ascendancyIds);
byte[] mergedBytes = merged.Write();
if (Convert.ToHexString(SHA256.HashData(mergedBytes)) != CombinedHash)
    throw new InvalidDataException("The derived graph differs from the pinned compatible 3.29.3b graph.");
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
    analysisOnly = true,
    mergedGraphFile = Artifact,
    originalPhreciaSha256 = OriginalPhreciaHash,
    normal = Extract("metadata/passiveskillgraph.psg", normalBytes),
    phrecia = Extract("derived: ordinary/Bloodline tree with original Phrecia main ascendancies", mergedBytes)
};
Directory.CreateDirectory(Path.GetDirectoryName(target)!);
File.WriteAllBytes(artifactPath + ".tmp", mergedBytes);
File.Move(artifactPath + ".tmp", artifactPath, overwrite: true);
string pending = target + ".tmp";
File.WriteAllText(pending, JsonSerializer.Serialize(output));
File.Move(pending, target, overwrite: true);
Console.WriteLine($"Wrote analysis artifacts under data/: {target}. The client archive was not changed.");
