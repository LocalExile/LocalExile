// usage: dotnet run --project scripts/re/atlas -- [Client/Content.ggpk] [out.json | survey | room-points [out.json] | hideouts [out.json]]
// Derives every WorldAreas row's area transitions and their destinations from the game files and writes
// data/area-transitions.json plus a coverage report. Agents/docs/protocol/08-area-transitions.md §6.
using Atlas;
using GameServer.Data.Ggpk;
using GgDump;

Console.OutputEncoding = System.Text.Encoding.UTF8;

string root = AppContext.BaseDirectory;
while (!File.Exists(Path.Combine(root, "Tools", "ooz", "ooz_worker.mjs")))
    root = Path.GetDirectoryName(root.TrimEnd(Path.DirectorySeparatorChar))
        ?? throw new InvalidOperationException("Tools/ooz/ooz_worker.mjs not found above the build output");
string ggpk = args.Length > 0 ? args[0] : Path.Combine(root, "Client", "Content.ggpk");
string output = args.Length > 1 ? args[1] : Path.Combine(root, "data", "area-transitions.json");

using var oodle = new NodeOodleDecoder("node", Path.Combine(root, @"Tools\ooz\ooz_worker.mjs"));
using var fs = GameFileSystem.Open(ggpk, oodle);
var files = new GameFiles(fs);
if (output == "delve-paths")
{
    DelvePaths.Run(fs, args.Length > 2 ? args[2] : Path.Combine(root, "data", "delve-paths.json"));
    return;
}
var (areas, topologies, problems) = WorldAreas.Load(files, root);
foreach (var p in problems) Console.WriteLine("worldareas: " + p);

if (output == "survey")
{
    Survey.Run(files, areas, topologies);
    return;
}
if (output == "survey2")
{
    Survey2.Run(files, topologies, args.Length > 2 ? args[2] : "Act1/");
    return;
}
if (output == "room-points")
{
    RoomPoints.Run(files, areas, topologies, args.Length > 2 ? args[2] : Path.Combine(root, "data", "room-points.json"));
    return;
}
if (output == "hideouts")
{
    HideoutFiles.Run(files, areas, root, args.Length > 2 ? args[2] : Path.Combine(root, "data", "hideouts.json"));
    return;
}
if (output == "debug")
{
    Debug.Run(files, areas, topologies, args[2]);
    return;
}
AtlasBuilder.Run(files, areas, topologies, output);
