using System.Collections.ObjectModel;
using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using GameServer.Core.Accounts;

namespace GameServer.Core.Imports;

public sealed record PathOfBuildingBuild(CharacterClass Class, int Level, PathOfBuildingTree Tree,
    IReadOnlyList<PathOfBuildingItem> Items, IReadOnlyList<PathOfBuildingSlot> Slots,
    IReadOnlyList<PathOfBuildingSkillGroup> Skills, string? Ascendancy, bool UseSecondWeaponSet = false,
    string? Bandit = null, string? PantheonMajor = null, string? PantheonMinor = null);
public sealed record PathOfBuildingTree(int ClassId, int AscendancyId, string? Version, IReadOnlyList<uint> Nodes,
    IReadOnlyDictionary<ushort, ushort> Masteries, IReadOnlyList<PathOfBuildingJewelSocket> Jewels,
    bool HasOverrides, int SecondaryAscendancyId = 0);
public sealed record PathOfBuildingJewelSocket(uint NodeId, int ItemId);
public sealed record PathOfBuildingItem(int Id, string Text, int? Variant, bool HasAlternateVariants = false,
    bool HasLegacyModRanges = false);
public sealed record PathOfBuildingSlot(string Name, int ItemId);
public sealed record PathOfBuildingSkillGroup(string? Slot, bool Enabled, IReadOnlyList<PathOfBuildingGem> Gems,
    string? Source, string? ImbuedSupport = null);
public sealed record PathOfBuildingGem(string Name, string? GemId, string? SkillId, string? VariantId,
    int Level, int Quality, bool Enabled, string? QualityId = null);

/// <summary>Reads offline PoE 1 exports from PathOfBuildingCommunity/PathOfBuilding.
/// Format: src/Classes/ImportTab.lua, PassiveSpec.lua, TreeTab.lua, ItemsTab.lua and SkillsTab.lua.
/// Parsing never fetches links or evaluates XML entities. Game-data compatibility is checked by the importer.</summary>
public static class PathOfBuilding
{
    public const int MaximumXmlBytes = 8 * 1024 * 1024;
    public const int MaximumCodeCharacters = 2 * 1024 * 1024;
    static readonly Regex MasteryPair = new(@"\{(\d+),(\d+)\}", RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    public static PathOfBuildingBuild Parse(string input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.Length > MaximumXmlBytes) throw Error("The build exceeds the 8 MiB import limit.");
        string text = input.Trim().TrimStart('\uFEFF').TrimStart();
        if (text.Length == 0) throw Error("The build is empty. Supply a PoB export code or XML file.");
        if (!text.StartsWith('<')) text = Decode(text);
        if (Encoding.UTF8.GetByteCount(text) > MaximumXmlBytes) throw Error("The build exceeds the 8 MiB import limit.");
        XDocument document;
        try
        {
            using var reader = XmlReader.Create(new StringReader(text), new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = MaximumXmlBytes,
                IgnoreComments = true,
                IgnoreProcessingInstructions = true
            });
            document = XDocument.Load(reader, LoadOptions.None);
        }
        catch (XmlException e) { throw Error("Invalid PoB XML: " + e.Message); }
        var root = document.Root;
        if (root?.Name != "PathOfBuilding")
            throw Error("Expected a PathOfBuilding PoE 1 export. PoE 2 builds are not supported.");
        var build = Single(root, "Build", required: true)!;
        string className = Required(build, "className");
        if (!Enum.GetNames<CharacterClass>().Any(n => n.Equals(className, StringComparison.OrdinalIgnoreCase)) ||
            !Enum.TryParse<CharacterClass>(className, ignoreCase: true, out var characterClass))
            throw Error($"Unsupported PoE 1 class '{className}'.");
        int level = Number(build, "level", 1, 1, 100);
        var tree = ReadTree(root, characterClass);
        var itemsElement = Single(root, "Items");
        var items = ReadItems(itemsElement);
        var selectedItems = SelectSet(itemsElement, "ItemSet", "activeItemSet");
        var slots = ReadSlots(selectedItems);
        var references = slots.Select(s => s.ItemId).Concat(tree.Jewels.Select(j => j.ItemId));
        var ids = items.Select(i => i.Id).ToHashSet();
        foreach (int id in references)
            if (!ids.Contains(id)) throw Error($"The selected build references missing item {id}.");
        var skillsElement = Single(root, "Skills");
        var selectedSkills = SelectSet(skillsElement, "SkillSet", "activeSkillSet");
        return new(characterClass, level, tree, ReadOnly(items), ReadOnly(slots),
            ReadOnly(ReadSkills(selectedSkills, skillsElement)), Optional(build, "ascendClassName"),
            Boolean(selectedItems, "useSecondWeaponSet", false), Optional(build, "bandit"),
            Optional(build, "pantheonMajorGod"), Optional(build, "pantheonMinorGod"));
    }

    static string Decode(string code)
    {
        if (code.Length > MaximumCodeCharacters) throw Error("The PoB export code exceeds the 2 MiB import limit.");
        if (code.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            throw Error("Supply the PoB export code or a saved XML file, rather than a sharing link.");
        string base64 = string.Concat(code.Where(c => !char.IsWhiteSpace(c))).Replace('-', '+').Replace('_', '/');
        if (base64.Length % 4 == 1) throw Error("Invalid PoB export code (Base64 length).");
        base64 = base64.PadRight((base64.Length + 3) / 4 * 4, '=');
        try
        {
            byte[] compressed = Convert.FromBase64String(base64);
            if (compressed.Length < 6) throw Error("Invalid PoB export code (truncated zlib stream).");
            using var input = new MemoryStream(compressed);
            using var zlib = new ZLibStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            byte[] buffer = new byte[8192];
            uint adlerA = 1, adlerB = 0;
            int count;
            while ((count = zlib.Read(buffer)) > 0)
            {
                if (output.Length + count > MaximumXmlBytes) throw Error("The expanded build exceeds the 8 MiB import limit.");
                foreach (byte value in buffer.AsSpan(0, count))
                {
                    adlerA = (adlerA + value) % 65521;
                    adlerB = (adlerB + adlerA) % 65521;
                }
                output.Write(buffer, 0, count);
            }
            // ZLibStream can finish without throwing when the checksum trailer was truncated.
            // Require the export's Adler-32 trailer as well as a successful decompression.
            if (((adlerB << 16) | adlerA) != BinaryPrimitives.ReadUInt32BigEndian(compressed.AsSpan(compressed.Length - 4)))
                throw Error("Invalid PoB export code (zlib checksum mismatch or truncated stream).");
            return new UTF8Encoding(false, true).GetString(output.ToArray());
        }
        catch (Exception e) when (e is InvalidDataException or DecoderFallbackException)
        { throw Error("Invalid PoB export code (expected Base64-encoded zlib XML)."); }
        catch (FormatException e) when (!e.Message.StartsWith("PoB: ", StringComparison.Ordinal))
        { throw Error("Invalid PoB export code (Base64)."); }
    }

    static PathOfBuildingTree ReadTree(XElement root, CharacterClass characterClass)
    {
        var tree = Single(root, "Tree");
        var legacySpec = Single(root, "Spec");
        if (tree is not null && legacySpec is not null) throw Error("The build contains both Tree and legacy Spec sections.");
        XElement? spec = legacySpec;
        if (tree is not null)
        {
            var specs = tree.Elements("Spec").ToArray();
            int active = Number(tree, "activeSpec", 1, 1, int.MaxValue);
            if (specs.Length == 0)
            {
                if (tree.Attribute("activeSpec") is not null) throw Error("The active passive specification is missing.");
            }
            else if (active > specs.Length) throw Error($"Active passive specification {active} does not exist.");
            else spec = specs[active - 1];
        }
        var nodes = new List<uint>();
        var masteries = new Dictionary<ushort, ushort>();
        int classId = (int)characterClass, ascendancy = 0, secondary = 0;
        if (spec is not null)
        {
            if (spec.Attribute("nodes") is { } nodeAttribute)
            {
                classId = Number(spec, "classId", null, 0, 6);
                ascendancy = Number(spec, "ascendClassId", null, 0, 3);
                secondary = Optional(spec, "secondaryAscendClassId") == "nil"
                    ? 0 : Number(spec, "secondaryAscendClassId", 0, 0, 255);
                if (!string.IsNullOrWhiteSpace(nodeAttribute.Value))
                    foreach (string value in nodeAttribute.Value.Split(','))
                        nodes.Add(Unsigned(value.Trim(), "passive node"));
                if (Optional(spec, "masteryEffects") is { } effects)
                {
                    int end = 0;
                    foreach (Match match in MasteryPair.Matches(effects))
                    {
                        if (effects[end..match.Index].Trim() != (end == 0 ? "" : ","))
                            throw Error("Invalid masteryEffects format; expected {node,effect} pairs.");
                        ushort node = UShort(match.Groups[1].Value, "mastery node");
                        ushort effect = UShort(match.Groups[2].Value, "mastery effect");
                        if (!masteries.TryAdd(node, effect)) throw Error($"Duplicate mastery selection for node {node}.");
                        end = match.Index + match.Length;
                    }
                    if (end == 0 || effects[end..].Trim().Length != 0)
                        throw Error("Invalid masteryEffects format; expected {node,effect} pairs.");
                }
            }
            else if (Single(spec, "URL") is { } url)
                ReadTreeUrl(url.Value.Trim(), nodes, masteries, out classId, out ascendancy, out secondary);
            else throw Error("The selected passive specification has neither nodes nor a tree URL.");
        }
        if (classId != (int)characterClass)
            throw Error("The selected passive specification class does not match Build.className.");
        if (nodes.Count != nodes.Distinct().Count()) throw Error("The selected passive specification contains duplicate nodes.");
        var sockets = Single(spec, "Sockets")?.Elements("Socket").ToArray() ?? [];
        var jewels = new List<PathOfBuildingJewelSocket>();
        foreach (var socket in sockets)
        {
            int item = Number(socket, "itemId", null, 0, int.MaxValue);
            if (item == 0) continue; // Upstream ignores old empty socket records.
            jewels.Add(new(Unsigned(Required(socket, "nodeId"), "jewel socket"), item));
        }
        if (jewels.Select(j => j.NodeId).Distinct().Count() != jewels.Count)
            throw Error("The selected tree contains duplicate jewel sockets.");
        return new(classId, ascendancy, Optional(spec, "treeVersion"), ReadOnly(nodes),
            new ReadOnlyDictionary<ushort, ushort>(masteries), ReadOnly(jewels),
            Single(spec, "Overrides")?.HasElements == true, secondary);
    }

    // Legacy official tree URLs, as emitted by PassiveSpec:EncodeURL. Integers are big-endian.
    static void ReadTreeUrl(string url, List<uint> nodes, Dictionary<ushort, ushort> masteries,
        out int classId, out int ascendancy, out int secondary)
    {
        string code = url[(url.LastIndexOf('/') + 1)..].Replace('-', '+').Replace('_', '/');
        byte[] data;
        try { data = Convert.FromBase64String(code.PadRight((code.Length + 3) / 4 * 4, '=')); }
        catch (FormatException) { throw Error("Invalid legacy passive-tree URL encoding."); }
        int position = 0;
        byte Next()
        {
            if (position == data.Length) throw Error("The legacy passive-tree URL is truncated.");
            return data[position++];
        }
        uint version = ((uint)Next() << 24) | ((uint)Next() << 16) | ((uint)Next() << 8) | Next();
        if (version is < 1 or > 6) throw Error($"Unsupported legacy passive-tree URL version {version}.");
        classId = Next();
        int asc = Next();
        ascendancy = version >= 4 ? asc & 3 : 0;
        secondary = version >= 4 ? (asc >> 2) & 3 : 0;
        if (version < 4)
        {
            while (position < data.Length) nodes.Add((uint)(Next() << 8) | Next());
            return;
        }
        int count = Next();
        if (version < 5)
        {
            while (position < data.Length) nodes.Add((uint)(Next() << 8) | Next());
            return;
        }
        for (int i = 0; i < count; i++) nodes.Add((uint)(Next() << 8) | Next());
        count = Next();
        for (int i = 0; i < count; i++) nodes.Add(65536 + ((uint)(Next() << 8) | Next()));
        if (version >= 6)
        {
            count = Next();
            for (int i = 0; i < count; i++)
            {
                ushort effect = (ushort)((Next() << 8) | Next());
                ushort node = (ushort)((Next() << 8) | Next());
                if (!masteries.TryAdd(node, effect)) throw Error($"Duplicate mastery selection for node {node}.");
            }
        }
        if (position != data.Length) throw Error("The legacy passive-tree URL contains unexpected trailing bytes.");
    }

    static List<PathOfBuildingItem> ReadItems(XElement? parent)
    {
        var result = new List<PathOfBuildingItem>();
        foreach (var item in parent?.Elements("Item") ?? [])
        {
            int id = Number(item, "id", null, 1, int.MaxValue);
            string text = string.Concat(item.Nodes().OfType<XText>().Select(t => t.Value)).Trim();
            if (text.Length == 0) throw Error($"Item {id} has no item text.");
            int? variant = item.Attribute("variant") is not null ? Number(item, "variant", null, 1, int.MaxValue) : null;
            result.Add(new(id, text, variant,
                item.Attributes().Any(a => a.Name.LocalName.StartsWith("variantAlt", StringComparison.Ordinal)),
                item.Elements("ModRange").Any()));
        }
        if (result.Count != result.Select(i => i.Id).Distinct().Count()) throw Error("Duplicate item IDs in the build.");
        return result;
    }

    static XElement? SelectSet(XElement? parent, string element, string selector)
    {
        if (parent is null) return null;
        var sets = parent.Elements(element).ToArray();
        if (sets.Length == 0)
        {
            if (parent.Attribute(selector) is not null && Number(parent, selector, 1, 1, int.MaxValue) != 1)
                throw Error($"The selected {element} does not exist.");
            return parent; // Legacy direct slots and skill groups.
        }
        var byId = new Dictionary<int, XElement>();
        foreach (var set in sets)
        {
            int id = Number(set, "id", null, 1, int.MaxValue);
            if (!byId.TryAdd(id, set)) throw Error($"Duplicate {element} ID {id}.");
        }
        if (parent.Attribute(selector) is not null)
        {
            int selected = Number(parent, selector, null, 1, int.MaxValue);
            return byId.TryGetValue(selected, out var set) ? set : throw Error($"Selected {element} {selected} does not exist.");
        }
        return byId.GetValueOrDefault(1) ?? sets[0];
    }

    static List<PathOfBuildingSlot> ReadSlots(XElement? selected)
    {
        var result = new List<PathOfBuildingSlot>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var slot in selected?.Elements("Slot") ?? [])
        {
            string name = Required(slot, "name");
            if (!names.Add(name)) throw Error($"Duplicate equipment slot '{name}'.");
            int id = Number(slot, "itemId", null, 0, int.MaxValue);
            if (id > 0) result.Add(new(name, id));
        }
        return result;
    }

    static List<PathOfBuildingSkillGroup> ReadSkills(XElement? selected, XElement? skills)
    {
        var result = new List<PathOfBuildingSkillGroup>();
        // Older official PoB exports wrote tostring(nil) for this setting; upstream reads it as zero.
        int defaultQuality = Optional(skills, "defaultGemQuality") == "nil"
            ? 0 : Number(skills, "defaultGemQuality", 0, 0, 100);
        string? defaultLevelText = Optional(skills, "defaultGemLevel");
        int? defaultLevel = int.TryParse(defaultLevelText, NumberStyles.None, CultureInfo.InvariantCulture, out int value)
            ? value : null;
        foreach (var group in selected?.Elements("Skill") ?? [])
        {
            var gems = new List<PathOfBuildingGem>();
            foreach (var gem in group.Elements("Gem"))
            {
                string name = Optional(gem, "nameSpec") ?? "";
                string? gemId = Optional(gem, "gemId"), skillId = Optional(gem, "skillId");
                if (name.Length == 0 && gemId is null && skillId is null) throw Error("A gem has no name or identity.");
                gems.Add(new(name, gemId, skillId, Optional(gem, "variantId"),
                    Number(gem, "level", defaultLevel, 1, 40), Number(gem, "quality", defaultQuality, 0, 100),
                    Boolean(gem, "enabled", true), Optional(gem, "qualityId")));
            }
            result.Add(new(Optional(group, "slot"), Boolean(group, "enabled", Boolean(group, "active", false)),
                ReadOnly(gems), Optional(group, "source"), Optional(group, "imbuedSupport")));
        }
        return result;
    }

    static XElement? Single(XElement? parent, string name, bool required = false)
    {
        var found = parent?.Elements(name).Take(2).ToArray() ?? [];
        if (found.Length > 1) throw Error($"Duplicate {name} sections.");
        if (found.Length == 0 && required) throw Error($"Missing {name} section.");
        return found.FirstOrDefault();
    }
    static string Required(XElement element, string name) => Optional(element, name)
        ?? throw Error($"Missing {element.Name}.{name} attribute.");
    static string? Optional(XElement? element, string name) =>
        element?.Attribute(name)?.Value.Trim() is { Length: > 0 } value ? value : null;
    static int Number(XElement? element, string name, int? fallback, int minimum, int maximum)
    {
        string? text = Optional(element, name);
        if (text is null && fallback.HasValue) return fallback.Value;
        if (text is not null && int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int value) &&
            value >= minimum && value <= maximum) return value;
        throw Error($"Invalid or missing {element?.Name}.{name}; expected {minimum} through {maximum}.");
    }
    static bool Boolean(XElement? element, string name, bool fallback) => Optional(element, name) switch
    {
        null => fallback,
        "true" => true,
        "false" => false,
        _ => throw Error($"Invalid {element?.Name}.{name}; expected true or false.")
    };
    static uint Unsigned(string text, string description) =>
        uint.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out uint value) ? value
        : throw Error($"Invalid {description} ID '{text}'.");
    static ushort UShort(string text, string description)
    {
        uint value = Unsigned(text, description);
        return value <= ushort.MaxValue ? (ushort)value : throw Error($"The {description} ID {value} is out of range.");
    }
    static ReadOnlyCollection<T> ReadOnly<T>(List<T> values) => values.AsReadOnly();
    static FormatException Error(string text) => new("PoB: " + text);
}
