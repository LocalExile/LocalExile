using System.Text.RegularExpressions;
using GameServer.Core;
using GameServer.Core.Protocol.Instance;

/// <summary>
/// Data-driven mapping from PoE 3.29 <c>MutatedUnique*</c> (Foulborn) modifiers in <c>Mods</c> (365 rows) to the base
/// unique items they mutate, resolved from <c>ItemVisualIdentity.Id</c> + <c>UniqueStashLayout</c> + <c>Words</c>.
/// Framework catalog only: game rules deciding when/how many mutations apply live in modules.
/// </summary>
public static partial class MutatedUniqueCatalog
{
    static readonly Lock Sync = new();
    static long _loadedRevision = -1;
    static Dictionary<string, string[]> _modIdsByUnique = new(StringComparer.OrdinalIgnoreCase);
    static string[] _eligibleUniques = [];

    // GGG mod-ID typos / irregular ItemVisualIdentity names across the 365 MutatedUnique* rows.
    static readonly Dictionary<string, string> TokenAliases = new(StringComparer.Ordinal)
    {
        ["UniqueBottsStr7"] = "UniqueBootsStr7",
        ["UniqueAmluet24"] = "UniqueAmulet24",
        ["UniqueHelmetDex2"] = "UniqueHelmetDex2x",
    };

    // UniqueHelmetInt2 has no ItemVisualIdentity.Id of that exact token (Asenath's Mark uses HelmetInt2).
    static readonly Dictionary<string, string> DirectUniqueNames = new(StringComparer.Ordinal)
    {
        ["UniqueHelmetInt2"] = "Asenath's Mark",
    };

    [GeneratedRegex(@"^(Unique[A-Za-z]+[0-9]+)", RegexOptions.CultureInvariant)]
    private static partial Regex UniqueTokenRegex();

    static void EnsureLoaded(ModCatalog mods)
    {
        long rev = mods.Revision;
        if (Volatile.Read(ref _loadedRevision) == rev) return;
        lock (Sync)
        {
            if (_loadedRevision == rev) return;
            var byVisual = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            if (ClientTables.Has("Words") && ClientTables.Has("UniqueStashLayout") && ClientTables.Has("ItemVisualIdentity"))
            {
                var words = ClientTables.Table("Words");
                var visuals = ClientTables.Table("ItemVisualIdentity");
                var wordByRow = new Dictionary<int, string>();
                for (int i = 0; i < words.Count; i++)
                    if (words[i].Int("Wordlist") == 6 && words[i].Text("Text") is { Length: > 0 } text)
                        wordByRow[i] = UniqueCatalog.NameKey(text);
                foreach (var row in ClientTables.Table("UniqueStashLayout"))
                {
                    int wk = row.Int("WordsKey"), vk = row.Int("ItemVisualIdentityKey");
                    if (!wordByRow.TryGetValue(wk, out string? uniqueName) || vk < 0 || vk >= visuals.Count) continue;
                    string? iviId = visuals[vk].TextOrNull("Id")?.TrimEnd('_');
                    if (string.IsNullOrEmpty(iviId)) continue;
                    if (!byVisual.TryGetValue(iviId, out var names)) byVisual[iviId] = names = [];
                    if (!names.Contains(uniqueName, StringComparer.OrdinalIgnoreCase)) names.Add(uniqueName);
                }
            }

            var grouped = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var mod in mods.All)
            {
                if (!mod.Id.StartsWith("MutatedUnique", StringComparison.Ordinal)) continue;
                string suffix = mod.Id["Mutated".Length..];
                if (suffix.StartsWith("UniqueUnique", StringComparison.Ordinal))
                    suffix = suffix["Unique".Length..];
                var match = UniqueTokenRegex().Match(suffix);
                if (!match.Success) continue;
                string token = match.Groups[1].Value;
                if (TokenAliases.TryGetValue(token, out string? alias)) token = alias;

                if (DirectUniqueNames.TryGetValue(token, out string? direct))
                {
                    Add(grouped, direct, mod.Id);
                    continue;
                }
                if (byVisual.TryGetValue(token, out var names))
                    foreach (string uniqueName in names)
                        Add(grouped, uniqueName, mod.Id);
            }

            _modIdsByUnique = grouped.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray(), StringComparer.OrdinalIgnoreCase);
            _eligibleUniques = [.. _modIdsByUnique.Keys.Order(StringComparer.OrdinalIgnoreCase)];
            Volatile.Write(ref _loadedRevision, rev);
        }
    }

    static void Add(Dictionary<string, List<string>> map, string key, string modId)
    {
        if (!map.TryGetValue(key, out var list)) map[key] = list = [];
        if (!list.Contains(modId, StringComparer.Ordinal)) list.Add(modId);
    }

    /// <summary>All base unique names that have at least one <c>MutatedUnique*</c> modifier.</summary>
    public static IReadOnlyList<string> EligibleUniques(ModCatalog mods)
    {
        EnsureLoaded(mods);
        return _eligibleUniques;
    }

    /// <summary>The <c>MutatedUnique*</c> modifier rows for <paramref name="uniqueName"/>, or empty when none exist.</summary>
    public static IReadOnlyList<ModCatalog.Mod> MutatedModsFor(string uniqueName, ModCatalog mods)
    {
        EnsureLoaded(mods);
        if (!_modIdsByUnique.TryGetValue(UniqueCatalog.NameKey(uniqueName), out var ids)) return [];
        var result = new List<ModCatalog.Mod>(ids.Length);
        foreach (string id in ids)
            if (mods.ById(id) is { } mod) result.Add(mod);
        return result;
    }

    /// <summary>Replaces up to <paramref name="count"/> random explicit modifiers on <paramref name="uniqueItem"/> with
    /// distinct <c>MutatedUnique*</c> modifiers belonging to <paramref name="uniqueName"/>. Returns null if the unique
    /// has no mutated modifiers or no explicit modifiers to replace.</summary>
    public static Item? Mutate(Item uniqueItem, string uniqueName, int count, ModCatalog mods, Random random)
    {
        if (count <= 0 || uniqueItem.Rarity != ItemRarity.Unique) return null;
        var pool = MutatedModsFor(uniqueName, mods);
        if (pool.Count == 0) return null;
        var explicitIndexes = new List<int>();
        for (int i = 0; i < uniqueItem.Mods.Count; i++)
            if (uniqueItem.Mods[i].Section == ModSection.Explicit) explicitIndexes.Add(i);
        if (explicitIndexes.Count == 0) return null;

        int mutations = Math.Min(count, Math.Min(pool.Count, explicitIndexes.Count));
        var chosenPool = pool.OrderBy(_ => random.Next()).Take(mutations).ToArray();
        var chosenSlots = explicitIndexes.OrderBy(_ => random.Next()).Take(mutations).ToArray();
        var nextMods = uniqueItem.Mods.ToList();
        for (int i = 0; i < mutations; i++)
            nextMods[chosenSlots[i]] = chosenPool[i].Roll(ModSection.Explicit, random);
        return uniqueItem with { Mods = nextMods };
    }
}
