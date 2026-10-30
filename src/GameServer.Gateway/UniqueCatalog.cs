using System.Text.Json;
using GameServer.Core.Protocol.Instance;

/// <summary>
/// Unique items the server can create. The client data has a unique's name (<c>Words</c>) and artwork
/// (<c>ItemVisualIdentity</c>, linked through <c>UniqueStashLayout</c>), but no table that says which Mods rows belong to
/// it, so each definition below lists them from the game's data (poedb: Mageblood, 3.29). The base type's implicits are
/// rolled by <see cref="ItemCatalog.Create"/>; the unique's own mods are explicit and rolled once per item.
/// </summary>
sealed class UniqueCatalog(ItemCatalog items, string folder = "data")
{
    public sealed record Definition(string Name, string BaseTypeId, IReadOnlyList<string> Mods, byte ItemLevel = 86);

    static readonly Definition[] Definitions =
    [
        // Mageblood (Heavy Belt): the +25-35 Strength is the Heavy Belt implicit; the rest are the unique's mods.
        new("Mageblood", "Metadata/Items/Belts/Belt4",
            ["DexterityUnique__3", "FireResistUnique__24", "ColdResistUnique__34",
             "MagicUtilityFlasksCannotUseUnique__1____", "MagicUtilityFlasksCannotRemoveUnique__1",
             "MagicUtilityFlasksAlwaysApplyUnique__1"]),
        // Crown of Eyes (3.29): use the current attack-leech mods, not the old physical-only variants.
        new("Crown of Eyes", "Metadata/Items/Armours/Helmets/HelmetInt7",
            ["IncreasedAccuracyUniqueHelmetInt7", "LocalIncreasedEnergyShieldUniqueHelmetInt7",
             "FireResistUniqueHelmetInt7", "LifeLeechPermyriadUniqueHelmetInt7",
             "ManaLeechPermyriadUniqueHelmetInt7", "SpellDamageModifiersApplyToAttackDamageUniqueHelmetInt7"]),
    ];

    readonly Lazy<Dictionary<string, UniqueName>> _names = new(() => LoadNames(folder));

    public IReadOnlyList<Definition> All => Definitions;

    /// <summary>The definition a name matches (exact first, then the first containing it), or null.</summary>
    public Definition? Find(string name) =>
        Definitions.FirstOrDefault(d => d.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
        ?? Definitions.FirstOrDefault(d => d.Name.Contains(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>The unique item for a name, or null when the name is not one or its base type is not writable.</summary>
    public Item? Create(string name, Random random)
    {
        if (Find(name) is not { } definition || items.Find(definition.BaseTypeId) is not { Supported: true } type) return null;
        if (!_names.Value.TryGetValue(definition.Name, out var unique)) return null;
        var item = ItemCatalog.Create(type) with
        {
            ItemLevel = definition.ItemLevel, Rarity = ItemRarity.Unique, Identified = true,
            NameWords = [unique.Word], VisualIdentity = unique.VisualIdentity,
        };
        var mods = item.Mods.ToList();
        foreach (string id in definition.Mods)
        {
            if (items.Mods.ById(id) is not { } mod) continue;
            var values = mod.Stats.Select(s => s.Min == s.Max ? s.Min : random.Next(s.Min, s.Max + 1)).ToArray();
            mods.Add(new ItemMod(ModSection.Explicit, mod.Hash, values));
        }
        return item with { Mods = mods };
    }

    sealed record UniqueName(uint Word, ushort VisualIdentity);

    static Dictionary<string, UniqueName> LoadNames(string folder)
    {
        var result = new Dictionary<string, UniqueName>(StringComparer.OrdinalIgnoreCase);
        string Table(string name) => Path.Combine(folder, "tables", name + ".json");
        if (!File.Exists(Table("Words")) || !File.Exists(Table("UniqueStashLayout")) || !File.Exists(Table("ItemVisualIdentity")))
            return result;
        using var words = JsonDocument.Parse(File.ReadAllBytes(Table("Words")));
        using var layout = JsonDocument.Parse(File.ReadAllBytes(Table("UniqueStashLayout")));
        using var visuals = JsonDocument.Parse(File.ReadAllBytes(Table("ItemVisualIdentity")));
        // Unique names are wordlist 6 (rare names use 1 and 2); a layout row links the word to the unique artwork.
        var byWord = new Dictionary<int, string>();
        foreach (var definition in Definitions)
        {
            int row = -1;
            for (int i = 0; i < words.RootElement.GetArrayLength(); i++)
                if (words.RootElement[i].GetProperty("Wordlist").GetInt32() == 6
                    && words.RootElement[i].GetProperty("Text").GetString() == definition.Name) { row = i; break; }
            if (row < 0) continue;
            byWord[row] = definition.Name;
        }
        foreach (var r in layout.RootElement.EnumerateArray())
        {
            int wordsKey = r.GetProperty("WordsKey").GetInt32(), visualKey = r.GetProperty("ItemVisualIdentityKey").GetInt32();
            if (!byWord.TryGetValue(wordsKey, out string? name) || visualKey < 0
                || visualKey >= visuals.RootElement.GetArrayLength()) continue;
            result[name] = new(words.RootElement[wordsKey].GetProperty("HASH32").GetUInt32(),
                (ushort)visuals.RootElement[visualKey].GetProperty("HASH16").GetInt32());
        }
        return result;
    }
}
