using System.Security.Cryptography;
using System.Text.Json;
using GameServer.Data.Dat;
using GameServer.Data.Ggpk;

namespace PoEDataExtractor;

internal static class Program
{
    private const string Usage =
        """
        PoEDataExtractor - Path of Exile 3.29.3b data pipeline

        usage:
          PoEDataExtractor list   <ggpk>
          PoEDataExtractor cat    <ggpk> <index-path> [--out <file>]
          PoEDataExtractor decode <ggpk> [--schema <schema.min.json>] [--out <dir>]
        """;

    private static int Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine(Usage);
            return 2;
        }

        try
        {
            return args[0] switch
            {
                "list" => List(args[1]),
                "raw" => Raw(args[1], args),
                "cat" => Cat(args[1], args),
                "decode" => Decode(args[1], args),
                _ => Fail(),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: {ex}");
            return 1;
        }
    }

    private static int Fail()
    {
        Console.Error.WriteLine(Usage);
        return 2;
    }

    private static int List(string ggpkPath)
    {
        using var oodle = CreateOodle();
        using var fs = GameFileSystem.Open(ggpkPath, oodle);
        Console.WriteLine($"GGPK: {ggpkPath}");
        Console.WriteLine($"indexed files : {fs.Index.FileCount}");
        Console.WriteLine($"bundles       : {fs.Index.BundleFiles.Count}");
        foreach (var b in fs.Index.BundleFiles.Take(5))
            Console.WriteLine($"  {b}");
        return 0;
    }

    private static int Cat(string ggpkPath, string[] args)
    {
        if (args.Length < 3)
            return Fail();

        using var oodle = CreateOodle();
        using var fs = GameFileSystem.Open(ggpkPath, oodle);
        byte[] data = fs.ReadFile(args[2]);
        string hash = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
        Console.WriteLine($"path   : {args[2]}");
        Console.WriteLine($"size   : {data.Length} bytes");
        Console.WriteLine($"sha256 : {hash}");
        if (GetOption(args, "--out") is { } outPath)
            File.WriteAllBytes(outPath, data);
        return 0;
    }

    private static int Raw(string ggpkPath, string[] args)
    {
        if (args.Length < 3)
            return Fail();

        byte[] data = GameFileSystem.ReadRawGgpkFile(ggpkPath, args[2]);
        string? outPath = GetOption(args, "--out");
        if (outPath is null)
        {
            Console.WriteLine($"path: {args[2]}  size: {data.Length}");
            return 0;
        }

        File.WriteAllBytes(outPath, data);
        Console.WriteLine($"wrote {data.Length} bytes -> {outPath}");
        return 0;
    }

    private static int Decode(string ggpkPath, string[] args)
    {
        string schemaPath = GetOption(args, "--schema") ?? "data/schema/schema.min.json";
        string outDir = GetOption(args, "--out") ?? "data/tables";

        Console.WriteLine($"schema: {schemaPath}");
        var schema = DatSchema.LoadFile(schemaPath);
        Console.WriteLine($"schema tables: {schema.Count}");

        Directory.CreateDirectory(outDir);
        var jsonOptions = new JsonSerializerOptions { WriteIndented = false };

        using var oodle = CreateOodle();
        using var fs = GameFileSystem.Open(ggpkPath, oodle);

        int hits = 0, empty = 0, missing = 0, mismatch = 0;
        var mismatches = new List<string>();
        var missingNames = new List<string>();

        foreach (var table in schema.Values.OrderBy(t => t.Name, StringComparer.Ordinal))
        {
            if ((table.ValidFor & 1) == 0)
                continue; // PoE2-only table

            string path = $"data/{table.Name.ToLowerInvariant()}.datc64";
            if (!fs.TryReadFile(path, out var bytes))
            {
                missing++;
                missingNames.Add(table.Name);
                continue;
            }

            try
            {
                var dat = DatFile.Parse(bytes);
                if (dat.RowCount == 0)
                {
                    empty++;
                    continue;
                }

                var rows = dat.ReadRows(table);
                var projected = new List<Dictionary<string, object?>>(rows.Count);
                foreach (var row in rows)
                {
                    var d = new Dictionary<string, object?>(row.Count, StringComparer.Ordinal);
                    foreach (var kv in row)
                        d[kv.Key] = kv.Value.ToClr();
                    projected.Add(d);
                }

                File.WriteAllText(
                    Path.Combine(outDir, table.Name + ".json"),
                    JsonSerializer.Serialize(projected, jsonOptions));
                hits++;
            }
            catch (Exception ex)
            {
                mismatch++;
                mismatches.Add($"{table.Name}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        var report = new
        {
            ggpk = ggpkPath,
            schema = schemaPath,
            decoded = hits,
            empty,
            missing,
            schemaMismatch = mismatch,
            mismatches,
            missingTables = missingNames,
        };
        File.WriteAllText("data/extract-report.json", JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));

        Console.WriteLine($"decoded        : {hits}");
        Console.WriteLine($"empty tables   : {empty}");
        Console.WriteLine($"missing in ggpk: {missing}");
        Console.WriteLine($"schema mismatch: {mismatch}");
        Console.WriteLine($"report         : data/extract-report.json");
        return 0;
    }

    private static string? GetOption(string[] args, string name)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (string.Equals(args[i], name, StringComparison.Ordinal))
                return args[i + 1];
        return null;
    }

    private static NodeOodleDecoder CreateOodle()
    {
        string worker = Path.GetFullPath(
            Environment.GetEnvironmentVariable("POE_OOZ_WORKER") ?? Path.Combine(
                Environment.GetEnvironmentVariable("Game_SOURCE_ROOT") ?? Directory.GetCurrentDirectory(),
                "Tools", "ooz", "ooz_worker.mjs"));
        string? node = Environment.GetEnvironmentVariable("POE_NODE");
        if (node is not null)
            return new NodeOodleDecoder(node, worker);

        try
        {
            return new NodeOodleDecoder("node", worker);
        }
        catch (Exception)
        {
            return new NodeOodleDecoder(@"C:\Program Files\nodejs\node.exe", worker);
        }
    }
}
