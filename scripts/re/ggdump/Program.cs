// usage: dotnet run --project scripts/re/ggdump -- <client folder or Content.ggpk> <outDir> <path>...   (e.g. Data/Inventories.datc64)
using GameServer.Data.Ggpk;
using GgDump;
// usage: GgDump <ggpk> <outDir> <path>...   (or "list <regex>" as first path to search every path in the index)
// The repository root: the first parent of the build output that has Tools/ooz.
string root = AppContext.BaseDirectory;
while (!File.Exists(Path.Combine(root, "Tools", "ooz", "ooz_worker.mjs")))
    root = Path.GetDirectoryName(root.TrimEnd(Path.DirectorySeparatorChar))
        ?? throw new InvalidOperationException("Tools/ooz/ooz_worker.mjs not found above the build output");
using var oodle = new NodeOodleDecoder("node", Path.Combine(root, "Tools", "ooz", "ooz_worker.mjs"));
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
// "extract <include regex> <exclude regex> <min free GB> [limit]": every matching file under <outDir> with its own
// directories, in bundle order (so the bundle cache works), skipping a file already there with the right size.
// Stops with exit code 3 below the free-space floor. Appends failures to <outDir>/extract-errors.tsv; rerun to resume.
if (args.Length > 5 && args[2] == "extract")
{
    var options = System.Text.RegularExpressions.RegexOptions.IgnoreCase;
    var include = new System.Text.RegularExpressions.Regex(args[3], options);
    var exclude = new System.Text.RegularExpressions.Regex(args[4], options);
    long floor = long.Parse(args[5]) << 30;
    int limit = args.Length > 6 ? int.Parse(args[6]) : 0;
    Directory.CreateDirectory(args[1]);
    var files = new List<(string Path, int Bundle, int Offset, int Size)>();
    foreach (var p in fs.EnumeratePaths())
        if (include.IsMatch(p) && !exclude.IsMatch(p) && fs.Index.TryGetFile(p, out var loc))
            files.Add((p, loc.BundleIndex, loc.Offset, loc.Size));
    files.Sort((a, b) => a.Bundle != b.Bundle ? a.Bundle.CompareTo(b.Bundle) : a.Offset.CompareTo(b.Offset));
    if (limit > 0 && files.Count > limit) files.RemoveRange(limit, files.Count - limit);
    long total = files.Sum(f => (long)f.Size), bytes = 0;
    Console.WriteLine($"extract: {files.Count} files, {total >> 20} MB");
    var drive = new DriveInfo(Path.GetFullPath(args[1]));
    using var errors = new StreamWriter(Path.Combine(args[1], "extract-errors.tsv"), append: true);
    var clock = System.Diagnostics.Stopwatch.StartNew();
    int written = 0, skipped = 0, failed = 0;
    for (int i = 0; i < files.Count; i++)
    {
        var f = files[i];
        bytes += f.Size;
        string o = Path.Combine(args[1], f.Path.Replace('/', Path.DirectorySeparatorChar));
        try
        {
            if (new FileInfo(o) is { Exists: true } have && have.Length == f.Size) skipped++;
            else
            {
                if (fs.TryReadFile(f.Path, out var data))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(o)!);
                    File.WriteAllBytes(o, data);
                    written++;
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or GgpkFormatException)
        {
            failed++;
            errors.WriteLine($"{f.Path}\t{e.GetType().Name}: {e.Message}");
            errors.Flush();
        }
        if (i % 2000 == 0)
        {
            Console.WriteLine($"{i}/{files.Count} files, {bytes >> 20}/{total >> 20} MB, {written} written, {skipped} skipped, {failed} failed, {clock.Elapsed:hh\\:mm\\:ss}");
            if (drive.AvailableFreeSpace < floor) { Console.WriteLine($"stopped: free space below {args[5]} GB"); Environment.Exit(3); }
        }
    }
    Console.WriteLine($"extract finished: {written} written, {skipped} skipped, {failed} failed, {clock.Elapsed:hh\\:mm\\:ss}");
    return;
}
// "plan": every path's location to <outDir>/paths.tsv and the index's size by extension to <outDir>/extensions.tsv.
if (args.Length > 2 && args[2] == "plan")
{
    Directory.CreateDirectory(args[1]);
    var byExt = new Dictionary<string, (long Files, long Bytes)>();
    using (var w = new StreamWriter(Path.Combine(args[1], "paths.tsv")))
    {
        w.WriteLine("path\tbundle\toffset\tsize");
        foreach (var p in fs.EnumeratePaths())
        {
            if (!fs.Index.TryGetFile(p, out var loc)) continue;
            w.WriteLine($"{p}\t{loc.BundleIndex}\t{loc.Offset}\t{loc.Size}");
            string ext = Path.GetExtension(p).ToLowerInvariant();
            byExt.TryGetValue(ext, out var e);
            byExt[ext] = (e.Files + 1, e.Bytes + loc.Size);
        }
    }
    using var s = new StreamWriter(Path.Combine(args[1], "extensions.tsv"));
    s.WriteLine("ext\tfiles\tbytes");
    foreach (var kv in byExt.OrderByDescending(k => k.Value.Bytes)) s.WriteLine($"{kv.Key}\t{kv.Value.Files}\t{kv.Value.Bytes}");
    return;
}
foreach (var path in args.Skip(2))
{
    if (!fs.TryReadFile(path, out var d)) { Console.WriteLine($"{path}: not found"); continue; }
    string o = Path.Combine(args[1], path.Replace('/', '_'));
    File.WriteAllBytes(o, d);
    Console.WriteLine($"{path} -> {o} ({d.Length} bytes)");
}
