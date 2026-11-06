using System.Text.Json;
using GameServer.Core.Protocol.Instance;

// One immutable catalogue per input data directory, shared by every item in a batch.
static class NativeNames
{
    sealed record Word(int Index, string Text, string Alternate, uint Hash, int List);
    sealed record Data(Word[] Prefixes, Dictionary<string, Word> Suffixes, Dictionary<string, Word[]> Uniques, Dictionary<int, ushort> Visuals);
    static readonly Dictionary<string, Data> Cache = new(StringComparer.OrdinalIgnoreCase);
    static string Plain(string text) => string.Concat(text.Select(c => "àáâãäåèéêëìíîïòóôõöùúûüñçýÀÁÂÃÄÅÈÉÊËÌÍÎÏÒÓÔÕÖÙÚÛÜÑÇÝ".IndexOf(c) is int i and >= 0
        ? "aaaaaaeeeeiiiiooooouuuuncyAAAAAAEEEEIIIIOOOOOUUUUNCY"[i] : c));
    static Data Load(string folder)
    {
        if (Cache.TryGetValue(folder, out var data)) return data;
        using var words = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(folder, "tables/Words.json")));
        using var uniques = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(folder, "tables/UniqueStashLayout.json")));
        using var visuals = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(folder, "tables/ItemVisualIdentity.json")));
        var names = words.RootElement.EnumerateArray().Select((r, i) => new Word(i, r.GetProperty("Text").GetString() ?? "",
            r.TryGetProperty("Text2", out var alt) ? alt.GetString() ?? "" : "", r.GetProperty("HASH32").GetUInt32(), r.GetProperty("Wordlist").GetInt32())).ToArray();
        var art = new Dictionary<int, ushort>();
        foreach (var row in uniques.RootElement.EnumerateArray())
            if (!row.TryGetProperty("IsAlternateArt", out var alt) || !alt.GetBoolean())
                art.TryAdd(row.GetProperty("WordsKey").GetInt32(), (ushort)visuals.RootElement[row.GetProperty("ItemVisualIdentityKey").GetInt32()].GetProperty("HASH16").GetInt32());
        var byName = names.Where(w => w.List == 6).SelectMany(w => new[] { (Key: Plain(w.Text), Word: w), (Key: Plain(w.Alternate), Word: w) })
            .Where(p => p.Key.Length > 0).GroupBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Select(p => p.Word).Distinct().ToArray(), StringComparer.OrdinalIgnoreCase);
        return Cache[folder] = new(names.Where(w => w.List == 1).ToArray(),
            names.Where(w => w.List == 2).GroupBy(w => w.Text).ToDictionary(g => g.Key, g => g.First()), byName, art);
    }
    public static Item WithName(Item item, string title, string folder, List<string> warnings)
    {
        var data = Load(folder);
        if (item.Rarity == ItemRarity.Unique)
        {
            if (title.StartsWith("Foulborn ", StringComparison.Ordinal)) title = title[9..];
            var name = (data.Uniques.GetValueOrDefault(Plain(title)) ?? []).OrderByDescending(w => w.Text == title).FirstOrDefault()
                ?? throw new PathOfBuildingUnsupportedException($"unique name '{title}' cannot be identified in this client");
            if (!data.Visuals.TryGetValue(name.Index, out ushort art)) throw new PathOfBuildingUnsupportedException($"unique artwork for '{title}' is unavailable");
            return item with { NameWords = [name.Hash], VisualIdentity = art };
        }
        foreach (var prefix in data.Prefixes.Where(w => title.StartsWith(w.Text, StringComparison.Ordinal)))
            if (data.Suffixes.TryGetValue(title[prefix.Text.Length..], out var suffix))
                return item with { NameWords = [prefix.Hash, suffix.Hash] };
        warnings.Add($"Rare item name '{title}' has no native Words.dat name; its base name is used, with its stats preserved.");
        return item;
    }
}
