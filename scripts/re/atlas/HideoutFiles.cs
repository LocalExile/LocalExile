using System.Text;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Text.Json;
using GameServer.Core;

namespace Atlas;

/// <summary>
/// data/hideouts.json: every Hideouts.dat row with its area and the doodads of its default decoration (the row's
/// HideoutFile, e.g. Metadata/Terrain/Missions/Hideouts/Forest/decoration.hideout). A .hideout file is JSON whose
/// "doodads" object repeats keys (one per placed doodad, named after the doodad), so it is read as a token stream.
/// Each doodad: hash (MtxTypeGameSpecific HASH32), grid x / y, r (rotation, 65536 = a full turn), fv (variation: index
/// into HideoutDoodads Variation_AOFiles). The Gateway resolves the hashes (HideoutCatalog).
/// Agents/docs/protocol/21-hideouts.md §2.
/// </summary>
static class HideoutFiles
{
    public static void Run(GameFiles files, List<AreaRow> areas, string root, string output)
    {
        var table = ClientTable.Load(Path.Combine(root, "data", "client.db"), "Hideouts");
        var list = new List<object>();
        int doodads = 0;
        foreach (var h in table)
        {
            int row = h.Index;
            string file = h.Text("HideoutFile") ?? "";
            int areaRow = h.IntOrNull("HideoutArea") ?? -1;
            if (areaRow < 0 || areaRow >= areas.Count) continue;
            var area = areas[areaRow];
            var placed = files.Bytes(file) is { } bytes ? Parse(bytes) : null;
            if (placed is null) Console.WriteLine($"hideout {row} {area.Id}: {file} missing or unreadable");
            doodads += placed?.Count ?? 0;
            int hash = h.Int("HASH16");
            list.Add(new { row, hash16 = hash, area = area.Id, areaHash16 = area.Hash16, name = area.Name, file, doodads = placed ?? [] });
        }
        File.WriteAllText(output, JsonSerializer.Serialize(new
        {
            note = "Hideouts.dat rows with their area and default decoration (HideoutFile): doodads with MtxTypeGameSpecific hash, grid x/y, rotation r (65536 = a turn), variation fv. scripts/re/atlas hideouts.",
            hideouts = list,
            collisionRadii = StationaryCollisionRadii(files, root),
        }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        Console.WriteLine($"{output}: {list.Count} hideouts, {doodads} doodads");
        if (files.Missing.Count > 0) Console.WriteLine($"missing: {string.Join(", ", files.Missing.Keys)}");
    }

    static Dictionary<string, double> StationaryCollisionRadii(GameFiles files, string root)
    {
        var result = new Dictionary<string, double>(StringComparer.Ordinal);
        var models = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in ClientTable.Load(Path.Combine(root, "data", "client.db"), "HideoutDoodads"))
        {
            string type = row.Text("InheritsFrom") ?? "";
            if (!type.EndsWith("MappingDevice", StringComparison.Ordinal)
                && !type.EndsWith("HideoutCraftingBench", StringComparison.Ordinal)) continue;
            foreach (var model in row.Texts("Variation_AOFiles")) models.Add(model);
        }
        // object-types.json: the generator's fresh output when extract-data just wrote it, else client.db's copy.
        string objectTypes = Path.Combine(root, "data", "object-types.json");
        using var types = JsonDocument.Parse(File.Exists(objectTypes) ? File.ReadAllBytes(objectTypes)
            : ClientTables.FileBytes("object-types.json") ?? throw new FileNotFoundException("object-types.json is in neither data/ nor client.db"));
        foreach (var type in types.RootElement.GetProperty("types").EnumerateArray())
            if (type.GetProperty("components").EnumerateArray().Any(c => c.GetString() == "NPC")
                && type.TryGetProperty("positioned", out var p) && p.TryGetProperty("static", out var isStatic)
                && isStatic.GetString() == "true" && type.TryGetProperty("animated_object", out var model)
                && model.GetString() is { Length: > 0 } path) models.Add(path);
        double Radius(string path, HashSet<string> chain)
        {
            if (!chain.Add(path) || files.Bytes(path) is not { } bytes) return 0;
            string text = bytes.AsSpan().StartsWith(Encoding.Unicode.Preamble)
                ? Encoding.Unicode.GetString(bytes) : Encoding.UTF8.GetString(bytes);
            double radius = 0;
            foreach (Match parent in Regex.Matches(text, "extends\\s*\"([^\"]+)\""))
            {
                string name = parent.Groups[1].Value;
                if (name != "nothing") radius = Math.Max(radius, Radius(name.EndsWith(".ao") ? name : name + ".ao", chain));
            }
            foreach (Match match in Regex.Matches(text, "walk_(tri|circle)\\s*=\\s*\"([^\"]+)\""))
            {
                var values = match.Groups[2].Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                    .Select(v => double.Parse(v, CultureInfo.InvariantCulture)).ToArray();
                int step = match.Groups[1].Value == "circle" ? 3 : 2;
                for (int i = 0; i + step - 1 < values.Length; i += step)
                    radius = Math.Max(radius, Math.Sqrt(values[i] * values[i] + values[i + 1] * values[i + 1])
                        + (step == 3 ? Math.Max(0, values[i + 2]) : 0));
            }
            return radius;
        }
        foreach (string path in models)
        {
            double radius = Radius(path, new(StringComparer.Ordinal));
            if (radius > 0 && double.IsFinite(radius)) result[path] = radius;
        }
        return result;
    }

    public sealed record Doodad(string Name, uint Hash, int X, int Y, int R, int Fv);

    /// <summary>The "doodads" of a .hideout file, in file order (repeated keys kept).</summary>
    public static List<Doodad>? Parse(byte[] bytes)
    {
        try
        {
            var span = bytes.AsSpan();
            if (span.StartsWith(Encoding.UTF8.Preamble)) span = span[3..];
            var reader = new Utf8JsonReader(span, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            var result = new List<Doodad>();
            // Find "doodads" at depth 1, then each property (name) → object of numbers.
            while (reader.Read())
            {
                if (reader.TokenType != JsonTokenType.PropertyName || reader.CurrentDepth != 1 || reader.GetString() != "doodads") continue;
                reader.Read();   // StartObject
                while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
                {
                    string name = reader.GetString() ?? "";
                    reader.Read();   // StartObject
                    var values = new Dictionary<string, long>();
                    while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
                    {
                        string key = reader.GetString() ?? "";
                        reader.Read();
                        if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt64(out long v)) values[key] = v;
                        else reader.Skip();
                    }
                    result.Add(new Doodad(name, (uint)values.GetValueOrDefault("hash"), (int)values.GetValueOrDefault("x"),
                        (int)values.GetValueOrDefault("y"), (int)values.GetValueOrDefault("r"), (int)values.GetValueOrDefault("fv")));
                }
                break;
            }
            return result;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
