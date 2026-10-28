// usage: dotnet run --project scripts/re/ggdump -- <Content.ggpk> <outDir> <path>...   (e.g. Data/Inventories.datc64)
using GameServer.Data.Ggpk;
using GgDump;
// usage: GgDump <ggpk> <outDir> <path>...   (or "list <regex>" as first path to search every path in the index)
// Use the source working directory, independently of the configured binary location.
string root = Directory.GetCurrentDirectory();
string source = Environment.GetEnvironmentVariable("Game_SOURCE_ROOT") ?? root;
if (!File.Exists(Path.Combine(source, "Tools", "ooz", "ooz_worker.mjs")))
    throw new InvalidOperationException("Run through extract-data.ps1 or set Game_SOURCE_ROOT to the source checkout.");
using var oodle = new NodeOodleDecoder("node", Path.Combine(source, "Tools", "ooz", "ooz_worker.mjs"));
using var fs = GameFileSystem.Open(args[0], oodle);
// "list <regex>" (as the first path): print every path in the index that matches, case-insensitive.
if (args.Length > 3 && args[2] == "list")
{
    var re = new System.Text.RegularExpressions.Regex(args[3], System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    int n = 0;
    foreach (var p in fs.EnumeratePaths()) { n++; if (re.IsMatch(p)) Console.WriteLine(p); }
    Console.Error.WriteLine($"{n} paths in the index");
    return;
}
foreach (var path in args.Skip(2))
{
    if (!fs.TryReadFile(path, out var d)) { Console.WriteLine($"{path}: not found"); continue; }
    string o = Path.Combine(args[1], path.Replace('/', '_'));
    File.WriteAllBytes(o, d);
    Console.WriteLine($"{path} -> {o} ({d.Length} bytes)");
}
