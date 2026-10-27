using System.Text.RegularExpressions;
using GameServer.Core.Protocol.Instance;

/// <summary>
/// Reads Path of Exile's copied item text (Ctrl+C on an item) into a base type, rarity, level, quality and explicit
/// affixes. The game exposes mod ids and stat ids, not the localized mod sentences, so each line is matched against the
/// base domain's explicit mods by stat/mod-type words and the rolled values; unmatched lines are reported for manual
/// selection in the item builder.
/// </summary>
sealed partial class ItemTextImport(ItemCatalog catalog)
{
    public sealed record StatValue(string Id, int Min, int Max);
    public sealed record Match(ushort Hash, string Id, string Name, string ModType, int Level, string Generation,
        IReadOnlyList<StatValue> Stats, int[] Values, bool Fractured, bool Crafted, string Line);
    public sealed record Result(ItemCatalog.BaseType Base, ItemRarity Rarity, int ItemLevel, int Quality,
        string? QualityType, IReadOnlyList<Match> Mods, IReadOnlyList<string> Unmatched, IReadOnlyList<string> Notes);

    static readonly HashSet<string> Ignored = new(StringComparer.Ordinal)
    {
        "base", "local", "with", "to", "of", "the", "and", "a", "an", "in", "on", "by", "from", "per", "adds",
        "increased", "reduced", "additional", "have", "has", "this", "item",
    };

    public Result? Parse(string text, out string error)
    {
        error = "";
        var lines = text.Replace("\r", "").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        int first = lines.FindIndex(IsSeparator);
        if (first < 0) { error = "that is not a Path of Exile item text"; return null; }
        var header = lines.Take(first).ToList();
        string? rarityText = header.FirstOrDefault(l => l.StartsWith("Rarity:", StringComparison.OrdinalIgnoreCase));
        if (rarityText is null || !Enum.TryParse<ItemRarity>(rarityText["Rarity:".Length..].Trim(), true, out var rarity) ||
            rarity is ItemRarity.Unique)
        { error = "only Normal, Magic and Rare items can be imported"; return null; }

        string baseName = header.LastOrDefault(l => !l.StartsWith("Rarity:", StringComparison.OrdinalIgnoreCase) &&
            !l.StartsWith("Quality", StringComparison.OrdinalIgnoreCase)) ?? "";
        var type = catalog.All.FirstOrDefault(t => t.Supported && t.Components.Contains("Mods") &&
            Normalize(t.Name) == Normalize(baseName));
        if (type is null) { error = $"no buildable base matches \"{baseName}\""; return null; }

        int quality = 0;
        string? qualityType = null;
        var qualityMatch = QualityLine().Match(text);
        if (qualityMatch.Success)
        {
            quality = Math.Clamp(int.Parse(qualityMatch.Groups[2].Value), 0, 100);
            qualityType = qualityMatch.Groups[1].Success ? qualityMatch.Groups[1].Value : null;
        }
        int itemLevel = ItemLevelLine().Match(text) is { Success: true } level ? Math.Clamp(int.Parse(level.Groups[1].Value), 1, 100) : 1;

        var mods = new List<Match>();
        var unmatched = new List<string>();
        var notes = new List<string>();
        var blocks = new List<List<string>>();
        var block = new List<string>();
        foreach (string line in lines.Skip(first))
        {
            if (IsSeparator(line)) { if (block.Count > 0) { blocks.Add(block); block = []; } continue; }
            block.Add(line);
        }
        if (block.Count > 0) blocks.Add(block);

        foreach (var linesInSection in blocks)
            foreach (string raw in linesInSection)
            {
                if (IsFooter(raw)) continue;
                var (line, flags) = Flags(raw);
                if (line.Length == 0 || IsMetadata(line)) continue;
                if (flags.Contains("implicit") || flags.Contains("enchant"))
                {
                    notes.Add($"{line} ({(flags.Contains("implicit") ? "implicit" : "enchant")}; supplied by the base or not modelled)");
                    continue;
                }
                bool crafted = flags.Contains("crafted");
                bool fractured = flags.Contains("fractured");
                if (MatchLine(line, type, itemLevel, quality, qualityType, crafted, fractured) is { } match)
                    mods.Add(match);
                else
                    unmatched.Add(line);
            }
        return new(type, rarity, itemLevel, quality, qualityType, mods, unmatched, notes);
    }

    Match? MatchLine(string line, ItemCatalog.BaseType type, int itemLevel, int quality, string? qualityType, bool crafted, bool fractured)
    {
        var numbers = Number().Matches(line).Select(m => double.Parse(m.Value, System.Globalization.CultureInfo.InvariantCulture))
            .Select(v => (int)Math.Round(v)).ToArray();
        if (numbers.Length == 0) return null;
        int domain = crafted ? 9 : type.ModDomain;
        var tokens = Tokens(line);
        bool percent = line.Contains('%');
        double qualityFactor = qualityType?.Contains("Attribute", StringComparison.OrdinalIgnoreCase) == true ? 1 + quality / 100.0 : 1;

        Match? best = null;
        int bestScore = 0;
        foreach (var mod in catalog.Mods.All)
        {
            if (mod.Domain != domain || mod.GenerationType is not (ItemRolls.Prefix or ItemRolls.Suffix) ||
                mod.EssenceOnly || mod.Level > itemLevel || mod.Stats.Count != numbers.Length ||
                (!percent && mod.Stats.Any(s => s.Id.Contains('%'))) ||
                (percent && mod.Stats.All(s => !s.Id.Contains('%')))) continue;
            var values = new int[numbers.Length];
            bool fits = true;
            for (int i = 0; i < values.Length; i++)
            {
                var stat = mod.Stats[i];
                int value = numbers[i];
                if (IsAttribute(stat.Id) && qualityFactor != 1)
                    value = (int)Math.Round(value / qualityFactor);
                int lo = Math.Min(stat.Min, stat.Max), hi = Math.Max(stat.Min, stat.Max);
                if (value < lo || value > hi) { fits = false; break; }
                values[i] = value;
            }
            if (!fits) continue;
            int score = Score(tokens, percent, mod);
            if (score > bestScore || score == bestScore && best is not null && mod.Level > best.Level)
            {
                bestScore = score;
                best = new(mod.Hash, mod.Id, mod.Name, mod.ModType, mod.Level,
                    mod.GenerationType == ItemRolls.Prefix ? "prefix" : "suffix",
                    [.. mod.Stats.Select(s => new StatValue(s.Id, s.Min, s.Max))], values, fractured, crafted, line);
            }
        }
        return bestScore >= 6 ? best : null;
    }

    static bool IsAttribute(string stat) =>
        stat.Contains("strength", StringComparison.OrdinalIgnoreCase) ||
        stat.Contains("dexterity", StringComparison.OrdinalIgnoreCase) ||
        stat.Contains("intelligence", StringComparison.OrdinalIgnoreCase);

    static int Score(string[] tokens, bool percentLine, ModCatalog.Mod mod)
    {
        string hay = string.Concat(tokens);
        string[] modTokens = [.. Tokens(mod.Id), .. Tokens(mod.ModType), .. mod.Stats.SelectMany(s => Tokens(s.Id))];
        int score = 0;
        foreach (string token in modTokens.Distinct(StringComparer.Ordinal))
            if (token.Length >= 3 && !Ignored.Contains(token) && hay.Contains(token, StringComparison.Ordinal))
                score += Math.Min(12, token.Length);
        if (percentLine && mod.Stats.Any(s => s.Id.Contains('%'))) score += 5;
        return score;
    }

    static string[] Tokens(string text) =>
        Regex.Split(text, "[^A-Za-z0-9]+|(?<=[a-z])(?=[A-Z])")
            .Select(t => t.ToLowerInvariant()).Where(t => t.Length > 0).ToArray();

    static (string Line, HashSet<string> Flags) Flags(string line)
    {
        var flags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var match = Flag().Match(line);
        if (match.Success) flags.Add(match.Groups[2].Value);
        return (match.Success ? match.Groups[1].Value.Trim() : line.Trim(), flags);
    }

    static bool IsFooter(string line) => line is "Fractured Item" or "Corrupted" or "Mirrored" or "Split" or
        "Synthesised Item" or "Unidentified" or "Map Tier:";

    static bool IsMetadata(string line) =>
        line.StartsWith("Requirements:", StringComparison.OrdinalIgnoreCase) ||
        line.StartsWith("Level:", StringComparison.OrdinalIgnoreCase) ||
        line.StartsWith("Item Level:", StringComparison.OrdinalIgnoreCase) ||
        line.StartsWith("Quality", StringComparison.OrdinalIgnoreCase);

    static bool IsSeparator(string line) => Dash().IsMatch(line);

    static string Normalize(string text) => new(text.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    [GeneratedRegex(@"^-{3,}$")]
    private static partial Regex Dash();
    [GeneratedRegex(@"^(.*?)\((implicit|enchant|crafted|fractured|corrupted)\)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex Flag();
    [GeneratedRegex(@"[-+]?\d+(?:\.\d+)?", RegexOptions.CultureInvariant)]
    private static partial Regex Number();
    [GeneratedRegex(@"^Quality(?: \(([^)]+)\))?: \+?(\d+)%", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex QualityLine();
    [GeneratedRegex(@"^Item Level: (\d+)", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex ItemLevelLine();
}
