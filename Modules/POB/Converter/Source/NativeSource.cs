using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.IO.Compression;
using System.Xml.Linq;
using GameServer.Core.Imports;
using GameServer.Core.Protocol.Instance;

// The downloaded ninja response retains native modifier IDs/values and physical socket
// indexes which its PoB export omits (notably Energy Blade's computed lightning implicit).
// Accept a response only when its complete PoB code matches the requested build.
static class NativeSource
{
    static JsonDocument? document;
    static Dictionary<string, JsonElement> items = new(StringComparer.Ordinal);
    static Dictionary<string, PathOfBuildingItem> definitions = new(StringComparer.Ordinal);
    static Dictionary<string, string>? index;
    public static string? SourcePath { get; private set; }
    public static int ResolvedItems { get; private set; }
    public static bool Available => SourcePath is not null;

    static string? Find(string code, string? sourceFile)
    {
        if (!string.IsNullOrWhiteSpace(sourceFile)) return sourceFile;
        string path = Environment.GetEnvironmentVariable("POB_SOURCE_INDEX") ?? "data/pob-import/source-index.json";
        index ??= File.Exists(path) ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllBytes(path))! : [];
        return index.GetValueOrDefault(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(code.Trim()))));
    }

    public static PathOfBuildingBuild Parse(string code, string? sourceFile)
    {
        try { return PathOfBuilding.Parse(code); }
        catch (FormatException e) when (e.Message.Contains("A gem has no name or identity.") && Find(code, sourceFile) is { } path)
        {
            using var source = JsonDocument.Parse(File.ReadAllBytes(path));
            if (source.RootElement.GetProperty("pob").GetString()?.Trim() != code.Trim()) throw;
            // The strict parser has already validated the XML/size; ninja sometimes exports empty
            // calculation placeholders. Physical gems are restored by socket index from this exact snapshot.
            string xml = code.Trim();
            if (!xml.StartsWith('<'))
            {
                string b64 = xml.Replace('-', '+').Replace('_', '/');
                using var input = new MemoryStream(Convert.FromBase64String(b64.PadRight((b64.Length + 3) / 4 * 4, '=')));
                using var zipped = new ZLibStream(input, CompressionMode.Decompress);
                using var reader = new StreamReader(zipped, Encoding.UTF8);
                xml = reader.ReadToEnd();
            }
            var document = XDocument.Parse(xml);
            document.Descendants("Gem").Where(g => new[] { "nameSpec", "gemId", "skillId" }.All(a => string.IsNullOrWhiteSpace((string?)g.Attribute(a)))).Remove();
            return PathOfBuilding.Parse(document.ToString());
        }
    }

    public static void Begin(string code, PathOfBuildingBuild build, string? sourceFile)
    {
        document?.Dispose(); document = null;
        items.Clear(); definitions.Clear(); SourcePath = null; ResolvedItems = 0;
        sourceFile = Find(code, sourceFile);
        if (sourceFile is null) return;
        document = JsonDocument.Parse(File.ReadAllBytes(sourceFile));
        if (document.RootElement.GetProperty("pob").GetString()?.Trim() != code.Trim())
            throw new ArgumentException("The native snapshot belongs to a different PoB code.");
        SourcePath = sourceFile;
        foreach (var definition in build.Items)
            if (UniqueId(definition.Text) is { } id) definitions.TryAdd(id, definition);
        Visit(document.RootElement.GetProperty("build"));
    }

    static void Visit(JsonElement node)
    {
        if (node.ValueKind == JsonValueKind.Object)
        {
            if (node.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String
                && node.TryGetProperty("baseType", out _)) items.TryAdd(id.GetString()!, node);
            foreach (var property in node.EnumerateObject()) Visit(property.Value);
        }
        else if (node.ValueKind == JsonValueKind.Array)
            foreach (var child in node.EnumerateArray()) Visit(child);
    }

    static string? UniqueId(string text) => Regex.Match(text, @"(?m)^Unique ID: ([^\r\n]+)") is { Success: true } m ? m.Groups[1].Value.Trim() : null;
    static bool Flag(JsonElement row, string field) => row.TryGetProperty(field, out var p) && p.ValueKind == JsonValueKind.True;
    public static bool HasExactMods(PathOfBuildingItem definition) => UniqueId(definition.Text) is { } id && items.TryGetValue(id, out var row)
        && row.TryGetProperty("mods", out var mods) && mods.ValueKind == JsonValueKind.Object;
    public static string? BaseName(PathOfBuildingItem definition) => UniqueId(definition.Text) is { } id && items.TryGetValue(id, out var row)
        ? row.GetProperty("baseType").GetString() : null;

    public static Item Apply(PathOfBuildingItem definition, ItemCatalog.BaseType type, Item item, ItemCatalog catalog, string folder, List<string> warnings)
    {
        try { return ApplyCore(definition, type, item, catalog, folder, warnings); }
        catch (Exception e) when (e is ArgumentException or PathOfBuildingUnsupportedException)
        { throw new InvalidDataException($"Native snapshot of {type.Name}: {e.Message}", e); }
    }

    static Item ApplyCore(PathOfBuildingItem definition, ItemCatalog.BaseType type, Item item, ItemCatalog catalog, string folder, List<string> warnings)
    {
        if (UniqueId(definition.Text) is not { } id || !items.TryGetValue(id, out var row))
        {
            if (type.Components.Contains("Transformed")) throw new ArgumentException("PoB omits Energy Blade's calculated damage; a matching downloaded native snapshot is required.");
            return item;
        }
        if (row.TryGetProperty("mods", out var sections) && sections.ValueKind == JsonValueKind.Object)
        {
            var native = new List<ItemMod>();
            var fractured = new HashSet<ushort>();
            foreach (var section in sections.EnumerateObject())
            {
                ModSection target = section.Name switch
                {
                    "implicit" => ModSection.Implicit, "explicit" or "crafted" or "fractured" or "veiled" => ModSection.Explicit,
                    "enchant" => ModSection.Enchant, "scourge" => ModSection.Scourge,
                    "crucible" => ModSection.Enchant,
                    _ => throw new ArgumentException($"Unknown native modifier section '{section.Name}' on {type.Name}.")
                };
                foreach (var entry in section.Value.EnumerateArray())
                {
                    string modId = entry.GetProperty("id").GetString()!;
                    var mod = catalog.Mods.ById(modId) ?? throw new ArgumentException($"Native modifier '{modId}' is absent from this client.");
                    var placement = section.Name == "crafted" ? mod.GenerationType == 10 ? ModSection.Enchant
                        : mod.Domain == 42 ? ModSection.Implicit : ModSection.Explicit : target;
                    var stats = entry.GetProperty("stats");
                    var values = mod.Stats.Select(s => stats.TryGetProperty(s.Id, out var value) && value.TryGetInt32(out int v)
                        ? v : throw new ArgumentException($"Native modifier '{modId}' omits integer stat '{s.Id}'.")).ToArray();
                    if (stats.EnumerateObject().Any(s => !mod.Stats.Any(m => m.Id == s.Name)))
                        throw new ArgumentException($"Native modifier '{modId}' has a different stat layout.");
                    native.Add(new(placement, mod.Hash, values));
                    if (section.Name == "fractured") fractured.Add(mod.Hash);
                }
            }
            item = item with { Mods = [.. native.Where(m => fractured.Contains(m.Hash)), .. native.Where(m => !fractured.Contains(m.Hash))],
                NativeMods = true, FracturedCount = checked((byte)native.Count(m => m.Section == ModSection.Explicit && fractured.Contains(m.Hash))) };
            warnings.RemoveAll(w => w.StartsWith("Skipped modifier", StringComparison.Ordinal) || w.Contains("kept a value beyond the native roll"));
        }
        ItemInfluence influences = ItemInfluence.None;
        if (row.TryGetProperty("influences", out var influenceRows))
            foreach (var name in Enum.GetNames<ItemInfluence>().Where(n => n != "None"))
                if (Flag(influenceRows, name.ToLowerInvariant())) influences |= Enum.Parse<ItemInfluence>(name);
        item = item with { Corrupted = Flag(row, "corrupted"), Mirrored = Flag(row, "duplicated"),
            Split = Flag(row, "split"), Synthesised = Flag(row, "synthesised"), Influences = influences };
        warnings.RemoveAll(w => w.Contains("skipped influence identity"));
        if (type.Components.Contains("Sockets") && row.TryGetProperty("sockets", out var socketRows))
        {
            var sockets = new List<ItemSocket>(); var links = new List<byte>(); int previousGroup = -1;
            foreach (var socket in socketRows.EnumerateArray())
            {
                string colour = socket.GetProperty("sColour").GetString()!;
                var nativeColour = colour switch { "R" => SocketColour.Red, "G" => SocketColour.Green, "B" => SocketColour.Blue,
                    "W" => SocketColour.White, "A" => SocketColour.Abyss, _ => throw new ArgumentException($"Unknown socket colour '{colour}'.") };
                int group = socket.GetProperty("group").GetInt32();
                if (group == previousGroup) links[^1]++;
                else { links.Add(1); previousGroup = group; }
                sockets.Add(new(nativeColour));
            }
            foreach (var child in row.GetProperty("socketedItems").EnumerateArray())
            {
                int slot = child.GetProperty("socket").GetInt32();
                if (slot < 0 || slot >= sockets.Count || sockets[slot].Gem is not null) throw new ArgumentException("Invalid native socket index.");
                Item content;
                if (Flag(child, "abyssJewel"))
                {
                    if (sockets[slot].Colour != SocketColour.Abyss) throw new ArgumentException("A native abyss jewel occupies a gem socket.");
                    string childId = child.GetProperty("id").GetString()!;
                    if (!definitions.TryGetValue(childId, out var jewel)) throw new ArgumentException($"PoB omits native socketed jewel '{childId}'.");
                    content = PathOfBuildingItems.ParseItem(jewel, catalog, folder, warnings).Item;
                }
                else
                {
                    if (sockets[slot].Colour == SocketColour.Abyss) throw new ArgumentException("A native skill gem occupies an abyss socket.");
                    content = PathOfBuildingItems.CreateGem(new(child.GetProperty("baseType").GetString()!, null, null, null,
                        Property(child, "Level", 1), Property(child, "Quality", 0), true), catalog, folder);
                }
                sockets[slot] = sockets[slot] with { Gem = content };
            }
            item = item with { Sockets = sockets, Links = links, NativeSockets = true };
        }
        ResolvedItems++;
        return item;
    }

    static int Property(JsonElement row, string name, int fallback)
    {
        foreach (var p in row.GetProperty("properties").EnumerateArray())
            if (p.GetProperty("name").GetString() == name)
            {
                var text = p.GetProperty("values")[0][0].GetString()!;
                if (Regex.Match(text, @"\d+") is { Success: true } match) return int.Parse(match.Value);
            }
        return fallback;
    }
}
