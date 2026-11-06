using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using GameServer.Core.Protocol.Instance;

// 3.29.3b FUN_1424c6020 cases 0x27/0x28/0x3a resolve the numeric stat through the
// Index column of these tables, not a zero-based row number or a displayed numeric roll.
static class IndexedMods
{
    // PoB combines identical stat lines: two tattoos expose one "-2" line, while each
    // native modifier still carries its own -1 and companion stat. Expand only a complete
    // set of native tattoo companions; never reinterpret a lone penalty or a Heist enchant.
    public static (IReadOnlyList<ModText.Line> Lines, int[] Source) ExpandSockets(
        IReadOnlyList<ModText.Line> lines, IReadOnlyDictionary<string, string[]> entries)
    {
        var expanded = new List<ModText.Line>();
        var source = new List<int>();
        for (int i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            var match = Match(line.Text, @"^-(?<count>[2-3]) to maximum Sockets$");
            int copies = 1;
            if (match.Success)
            {
                int count = Number(match, "count");
                int companions = lines.Count(other => other.Section == line.Section && entries.Any(e =>
                    e.Key.StartsWith("DeepwaterTattooEnchant", StringComparison.Ordinal)
                    && e.Value.Length == 2 && e.Value.Contains("-1 to maximum Sockets")
                    && e.Value.Any(text => text != "-1 to maximum Sockets" && string.Equals(text, other.Text, StringComparison.OrdinalIgnoreCase))));
                if (companions == count) copies = count;
            }
            for (int n = 0; n < copies; n++)
            {
                expanded.Add(copies > 1 ? line with { Text = "-1 to maximum Sockets" } : line);
                source.Add(i);
            }
        }
        return (expanded, source.ToArray());
    }

    static readonly Lazy<Dictionary<string, int>> Skills = new(() => Names("IndexableSkillGems", "Name1"));
    static readonly Lazy<Dictionary<string, int>> Supports = new(() => Names("IndexableSupportGems", "Name"));
    static readonly Lazy<Dictionary<string, int>> NonActive = new(() => Names("IndexableNonActiveSupportGems", "Name"));

    static Dictionary<string, int> Names(string table, string field)
    {
        using var doc = JsonDocument.Parse(File.ReadAllBytes($"data/tables/{table}.json"));
        return doc.RootElement.EnumerateArray().GroupBy(r => r.GetProperty(field).GetString()!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().GetProperty("Index").GetInt32(), StringComparer.OrdinalIgnoreCase);
    }

    static Match Match(string line, string pattern) => Regex.Match(line, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    static int Number(Match m, string group) => int.Parse(m.Groups[group].Value, CultureInfo.InvariantCulture);

    public static IReadOnlyList<ModText.Found> Resolve(IReadOnlyList<ModText.Line> lines, Item item, ModCatalog mods)
    {
        var result = new List<ModText.Found>();
        var used = new HashSet<string>();
        for (int i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (item.Rarity != ItemRarity.Unique || line.Section != ModSection.Explicit) continue;
            string? id = null;
            Dictionary<string, int>? values = null;
            var skill = Match(line.Text, @"^(?<level>[+-]\d+) to Level of all (?<name>.+) Gems$");
            var pearl = Match(line.Text, @"^Skills Socketed in your (?<slot>Helmet|Gloves|Boots) are Supported by level (?<level>\d+) (?<name>.+)$");
            var tree = Match(line.Text, @"^Skills granted by your Passive Tree are Supported by level (?<level>\d+) (?<name>.+)$");
            var support = Match(line.Text, @"^Socketed Gems are Supported by Level (?<level>\d+) (?<name>.+)$");
            if (skill.Success && Skills.Value.TryGetValue(skill.Groups["name"].Value, out int skillIndex))
            {
                id = "RandomSkillUnique__1";
                values = new() { ["random_skill_gem_level_+_level"] = Number(skill, "level"), ["random_skill_gem_level_+_index"] = skillIndex };
            }
            else if ((pearl.Success || tree.Success) && NonActive.Value.TryGetValue((pearl.Success ? pearl : tree).Groups["name"].Value, out int pearlIndex))
            {
                var match = pearl.Success ? pearl : tree;
                int slot = !pearl.Success ? 4 : pearl.Groups["slot"].Value.ToLowerInvariant() switch { "helmet" => 1, "gloves" => 2, _ => 3 };
                int n = used.Contains("UniquePearlSupportOne") ? 2 : 1;
                id = n == 1 ? "UniquePearlSupportOne" : "UniquePearlSupportTwo";
                values = new() { [$"local_pearl_random_support_gem_{n}_slot_index"] = slot,
                    [$"local_pearl_random_support_gem_{n}_index"] = pearlIndex, [$"local_pearl_random_support_gem_{n}_level"] = Number(match, "level") };
            }
            else if (support.Success && Supports.Value.TryGetValue(support.Groups["name"].Value, out int supportIndex))
            {
                int level = Number(support, "level");
                bool high = level >= 25;
                id = high ? "RandomSupportUnique__2" : "RandomSupportUnique__1";
                string suffix = high ? "_1" : "";
                values = new() { [$"local_random_support_gem_index{suffix}"] = supportIndex, [$"local_random_support_gem_level{suffix}"] = level };
            }
            if (id is null || values is null || used.Contains(id) || mods.ById(id) is not { } mod
                || mod.Stats.Any(s => !values.ContainsKey(s.Id))) continue;
            // Out-of-roll levels can be retained, but an index must always name an actual native entry.
            int[] native = [.. mod.Stats.Select(s => values[s.Id])];
            bool outside = mod.Stats.Where((s, n) => native[n] < Math.Min(s.Min, s.Max) || native[n] > Math.Max(s.Min, s.Max)).Any();
            used.Add(id);
            result.Add(new(new(line.Section, mod.Hash, native), [i], outside));
        }
        return result;
    }
}
