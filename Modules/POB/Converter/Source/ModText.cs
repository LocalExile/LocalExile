using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using GameServer.Core.Protocol.Instance;

/// <summary>
/// Names modifiers from the text PoB shows for them. The native description renderer supplies lines by
/// Mods.dat Id, so a line is matched by its wording with the numbers ignored, and several lines can be one hybrid
/// modifier. The numbers then become stat values: exactly when a displayed roll is the stat's own range, otherwise in
/// proportion to it, because the text can show a stat in other units (40 per mille of leech reads 0.4%).
/// </summary>
sealed class ModText(IReadOnlyDictionary<string, string[]> entries)
{
    internal sealed record Line(ModSection Section, string Text);

    /// <summary>A modifier for these indices of the given lines. OutsideRoll: a value beyond the native roll, which
    /// catalysts and magnitude modifiers can cause; the value is kept.</summary>
    internal sealed record Found(ItemMod Mod, IReadOnlyList<int> Lines, bool OutsideRoll);

    sealed record Entry(string Id, string[] Lines);
    sealed record Token(double Low, double High)
    {
        public bool Holds(double value) => value >= Math.Min(Low, High) - 1e-6 && value <= Math.Max(Low, High) + 1e-6;
        public double Distance(double value) => Math.Max(0, Math.Max(Math.Min(Low, High) - value, value - Math.Max(Low, High)));
    }
    sealed record Candidate(Entry Entry, ModCatalog.Mod Mod, ModSection Section, int[] Lines, int[] Values, bool Outside, double Distance);

    static readonly Regex Number = new(@"([+-]?)\((-?\d+(?:\.\d+)?)-(-?\d+(?:\.\d+)?)\)|[+-]?\d+(?:\.\d+)?", RegexOptions.CultureInvariant);
    static readonly Lazy<ModText> Bundled = new(() =>
    {
        return new(NativeModText.Build());
    });

    public static ModText Embedded => Bundled.Value;

    readonly Lazy<ILookup<string, Entry>> byWording = new(() => entries
        .Select(e => new Entry(e.Key, e.Value))
        .SelectMany(e => e.Lines.Select(l => (Wording: Wording(l), Entry: e))).ToLookup(p => p.Wording, p => p.Entry));

    // Capitals differ between PoB's trade-derived text and its mod data ("All Lightning" and "all Lightning").
    static string Canonical(string line)
    {
        line = Regex.Replace(line, @"^Lose (\d+) Mana per Enemy Killed$", "Gain -$1 Mana per Enemy Killed", RegexOptions.IgnoreCase);
        return Regex.Replace(line, @"^(\d+)% reduced Curse Duration$", "-$1% increased Curse Duration", RegexOptions.IgnoreCase);
    }

    internal static string Wording(string line) => Number.Replace(Canonical(line), "#").ToLowerInvariant();

    static double Parse(string value) => double.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture);

    static IEnumerable<Token> Tokens(string line) => Number.Matches(Canonical(line)).Select(m =>
    {
        if (!m.Groups[2].Success) { double value = Parse(m.Value); return new Token(value, value); }
        int sign = m.Groups[1].Value == "-" ? -1 : 1;
        return new Token(sign * Parse(m.Groups[2].Value), sign * Parse(m.Groups[3].Value));
    });

    static IEnumerable<double> Values(string line) => Number.Matches(Canonical(line)).Select(m => Parse(m.Value));

    /// <summary>The best modifiers for these lines. Each line is used at most once; a line no modifier fits is left out.</summary>
    public IReadOnlyList<Found> Resolve(IReadOnlyList<Line> lines, ItemCatalog.BaseType type, Item item, ModCatalog mods)
    {
        var (expanded, source) = IndexedMods.ExpandSockets(lines, entries);
        return ResolveCore(expanded, type, item, mods).Select(f => f with { Lines = f.Lines.Select(i => source[i]).Distinct().ToArray() }).ToArray();
    }

    IReadOnlyList<Found> ResolveCore(IReadOnlyList<Line> lines, ItemCatalog.BaseType type, Item item, ModCatalog mods)
    {
        var candidates = new List<Candidate>();
        var seen = new HashSet<(string, ModSection)>();
        foreach (var line in lines)
            foreach (var entry in byWording.Value[Wording(line.Text)])
                if (seen.Add((entry.Id, line.Section)) && mods.ById(entry.Id) is { } mod)
                {
                    var excluded = new HashSet<int>();
                    while (Match(entry, mod, line.Section, lines, excluded) is { } candidate)
                    {
                        candidates.Add(candidate);
                        excluded.UnionWith(candidate.Lines);
                    }
                }
        var result = type.Name == "Great Crown" ? IndexedMods.Resolve(lines, item, mods).ToList() : new List<Found>();
        var used = result.SelectMany(f => f.Lines).ToHashSet();
        // What a modifier does outranks how well its roll fits: "+397 to Armour" on a body armour is the item's own armour
        // even when a catalyst lifts it past every local roll, and an in-range global armour stat would be a different item.
        foreach (var candidate in candidates.OrderByDescending(c => DomainFit(c.Mod, type) > 0 ? c.Lines.Length : 0).ThenByDescending(c => DomainFit(c.Mod, type)).ThenByDescending(c => c.Lines.Length).ThenByDescending(c => LocalFit(c.Mod, type))
            .ThenByDescending(c => SectionFits(c.Mod, c.Section, item))
            .ThenBy(c => c.Outside).ThenByDescending(c => c.Lines.Length).ThenBy(c => c.Distance)
            .ThenBy(c => c.Mod.Stats.Sum(s => (long)Math.Abs(s.Max - s.Min)))
            .ThenByDescending(c => c.Mod.WeightFor(type.Tags) > 0).ThenByDescending(c => c.Mod.Level).ThenBy(c => c.Mod.Id, StringComparer.Ordinal))
        {
            var available = candidate;
            if (candidate.Lines.Any(used.Contains))
            {
                if (candidate.Mod.Domain != 42 || Match(candidate.Entry, candidate.Mod, candidate.Section, lines, used) is not { } retry) continue;
                available = retry;
            }
            used.UnionWith(available.Lines);
            result.Add(new(new(available.Section, available.Mod.Hash, available.Values), available.Lines, available.Outside));
        }
        result.AddRange(IndexedMods.Resolve(lines, item, mods).Where(f => !f.Lines.Any(used.Contains)));
        return result;
    }

    // A modifier's Domain says which items it belongs to; 9 is the crafting bench's. Wording alone can repeat across them.
    static int DomainFit(ModCatalog.Mod mod, ItemCatalog.BaseType type) => mod.Domain == 42 && mod.Stats.Any(s => s.Id == "local_maximum_sockets_+") ? 3 : mod.Domain == type.ModDomain ? 2 : mod.Domain is 9 or 41 ? 1 : 0;

    // Weapons, armour and flasks carry their own local stats. Elsewhere the same wording is the global stat.
    static int LocalFit(ModCatalog.Mod mod, ItemCatalog.BaseType type)
    {
        int local = mod.Stats.Count(s => s.Local);
        return type.Components.Contains("Armour") || type.Components.Contains("Weapon") || type.Components.Contains("Flask") ? local : -local;
    }

    // Rare and magic explicit lines are prefixes and suffixes. A unique's own modifiers can share their wording.
    static bool SectionFits(ModCatalog.Mod mod, ModSection section, Item item) => section switch
    {
        ModSection.Enchant => mod.GenerationType == 10,
        ModSection.Implicit => mod.GenerationType is not (1 or 2),
        ModSection.Explicit => item.Rarity == ItemRarity.Unique || mod.GenerationType is 1 or 2,
        _ => true,
    };

    static Candidate? Match(Entry entry, ModCatalog.Mod mod, ModSection section, IReadOnlyList<Line> lines, IReadOnlySet<int> excluded)
    {
        var chosen = new List<int>();
        foreach (string text in entry.Lines)
        {
            string wording = Wording(text);
            int index = Enumerable.Range(0, lines.Count).FirstOrDefault(i => !chosen.Contains(i) && !excluded.Contains(i) && lines[i].Section == section && Wording(lines[i].Text) == wording, -1);
            if (index < 0) return null;
            if (text == "-1 to maximum Sockets" && lines[index].Text != text) return null;
            chosen.Add(index);
        }
        var tokens = entry.Lines.SelectMany(Tokens).ToArray();
        var numbers = chosen.SelectMany(i => Values(lines[i].Text)).ToArray();
        if (tokens.Length != numbers.Length) return null;
        return StatValues(mod, tokens, numbers) is { } values ? new(entry, mod, section, [.. chosen], values,
            Enumerable.Range(0, tokens.Length).Any(i => !tokens[i].Holds(numbers[i])),
            Enumerable.Range(0, tokens.Length).Sum(i => tokens[i].Distance(numbers[i]))) : null;
    }

    static int[]? StatValues(ModCatalog.Mod mod, Token[] tokens, double[] numbers)
    {
        var free = Enumerable.Range(0, tokens.Length).ToList();
        var values = new int?[mod.Stats.Count];
        static int Whole(double value) => checked((int)Math.Round(value, MidpointRounding.AwayFromZero));
        // A displayed range equal to the stat's, or its negative ("reduced" shows a negative stat as a positive number).
        for (int s = 0; s < values.Length; s++)
        {
            var stat = mod.Stats[s];
            if (stat.Min == stat.Max) continue;
            foreach (int sign in new[] { 1, -1 })
            {
                int token = free.FirstOrDefault(i => tokens[i].Low != tokens[i].High && Same(tokens[i], stat, sign), -1);
                if (token < 0) continue;
                values[s] = Whole(sign * numbers[token]); free.Remove(token);
                break;
            }
        }
        // Anything else that varies is the next varying number, in proportion to its displayed range.
        for (int s = 0; s < values.Length; s++)
        {
            var stat = mod.Stats[s];
            if (values[s] is not null || stat.Min == stat.Max) continue;
            int token = free.FirstOrDefault(i => tokens[i].Low != tokens[i].High, -1);
            if (token < 0) return null;
            double share = (numbers[token] - tokens[token].Low) / (tokens[token].High - tokens[token].Low);
            values[s] = Whole(stat.Min + (stat.Max - stat.Min) * share); free.Remove(token);
        }
        for (int s = 0; s < values.Length; s++) values[s] ??= mod.Stats[s].Min;
        return [.. values.Select(v => v!.Value)];
    }

    static bool Same(Token token, ModCatalog.ModStat stat, int sign)
    {
        double low = Math.Min(sign * token.Low, sign * token.High), high = Math.Max(sign * token.Low, sign * token.High);
        return Math.Abs(low - Math.Min(stat.Min, stat.Max)) < 1e-6 && Math.Abs(high - Math.Max(stat.Min, stat.Max)) < 1e-6;
    }
}
