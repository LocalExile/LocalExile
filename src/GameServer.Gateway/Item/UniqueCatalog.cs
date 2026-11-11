using System.Text.Json;
using GameServer.Core.Protocol.Instance;
using GameServer.Core;

/// <summary>
/// Unique items the server can create. The client data has a unique's name (<c>Words</c>) and artwork
/// (<c>ItemVisualIdentity</c>, linked through <c>UniqueStashLayout</c>), but no table that says which Mods rows belong to
/// it, so each definition below lists them from the game's data (poedb: Mageblood, 3.29). The base type's implicits are
/// rolled by <see cref="ItemCatalog.Create"/>; the unique's own mods are explicit and rolled once per item.
/// </summary>
public sealed class UniqueCatalog(ItemCatalog items, string folder = "data")
{
    // Several native names use a typographic apostrophe while the definition export uses ASCII.
    public static string NameKey(string name) => name.Replace('\u2019', '\'').Replace('\u2018', '\'').Replace('\u02bc', '\'') switch
    {
        "Rigwald's Charge" => "Rigvald's Charge",
        "The Broken Elegy" => "Broken Elegy",
        "The Goddess Unleashed" => "The GoddessUnleashed",
        var key => key,
    };

    /// <summary>A unique's roll of one stat where it differs from the Mods.dat row: PoB can tie a line to another row with
    /// the same stats. <paramref name="Implicit"/> tells apart a mod in both of the unique's lists.</summary>
    public sealed record RollRange(string Mod, int Stat, int Min, int Max, bool Implicit = false);

    /// <summary>How a stat of a mod rolls on one unique, or null for the mod's own range.</summary>
    public delegate (int Min, int Max)? StatRoll(ModCatalog.Mod mod, ModSection section, int stat);

    /// <summary>One unique. <paramref name="Implicits"/> replaces the base type's own when the unique differs from it
    /// (null keeps the base's). The first <paramref name="Fractured"/> of <paramref name="Mods"/> are fractured.</summary>
    public sealed record Definition(string Name, string BaseTypeId, string[] Mods, byte ItemLevel = 86,
        bool Corrupted = false, string[]? Implicits = null, RollRange[]? Ranges = null, bool Mirrored = false,
        ItemInfluence Influences = ItemInfluence.None, byte Fractured = 0);

    /// <summary>Hidden mods that the Path of Building export does not carry, keyed by <c>name|base type</c>. They are
    /// sent in the item's hidden list. The level overrides are the game's generic LevelRequirementOverrideUnique__N
    /// rows: poedb's item cards give each unique's own requirement, which can be lower than the base type's level
    /// (Seven-League Step on Rawhide Boots, drop level 3, is "Requires Level 1, 11 Dex"). The table is the place for
    /// other hidden mods PoB omits as they are identified.</summary>
    static readonly Dictionary<string, string[]> HiddenMods = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Araku Tiki|Metadata/Items/Amulets/Amulet2"] = ["LevelRequirementOverrideUnique__1"],
        ["Blackheart|Metadata/Items/Rings/Ring1"] = ["LevelRequirementOverrideUnique__1"],
        ["Cragfall|Metadata/Items/Quivers/Quiver6"] = ["LevelRequirementOverrideUnique__4"],
        ["Craghead|Metadata/Items/Quivers/Quiver6"] = ["LevelRequirementOverrideUnique__4"],
        // 3.29.0: these three uniques have a hidden property forbidding white sockets (PoEDB version history).
        // Existing items keep their sockets; only newly generated ones are affected.
        ["Dialla's Malefaction|Metadata/Items/Armours/BodyArmours/BodyInt8"] = ["LocalNoWhiteSocketsUnique__1"],
        ["Skin of the Loyal|Metadata/Items/Armours/BodyArmours/BodyInt1"] = ["LocalNoWhiteSocketsUnique__1"],
        ["Doedre's Damning|Metadata/Items/Rings/Ring3"] = ["LevelRequirementOverrideUnique__1"],
        ["Kalandra's Touch|Metadata/Items/Rings/MirrorRing"] = ["LevelRequirementOverrideUnique__1"],
        ["Kaom's Sign|Metadata/Items/Rings/Ring2"] = ["LevelRequirementOverrideUnique__1"],
        ["Karui Ward|Metadata/Items/Amulets/Amulet4"] = ["LevelRequirementOverrideUnique__5"],
        ["Malachai's Artifice|Metadata/Items/Rings/Ring15"] = ["LevelRequirementOverrideUnique__5"],
        ["Perandus Signet|Metadata/Items/Rings/Ring3"] = ["LevelRequirementOverrideUnique__1"],
        ["Replica Doedre's Damning|Metadata/Items/Rings/Ring3"] = ["LevelRequirementOverrideUnique__1"],
        ["Replica Karui Ward|Metadata/Items/Amulets/Amulet4"] = ["LevelRequirementOverrideUnique__5"],
        ["Replica Malachai's Artifice|Metadata/Items/Rings/Ring15"] = ["LevelRequirementOverrideUnique__5"],
        ["Seven-League Step|Metadata/Items/Armours/Boots/BootsDex1"] = ["LevelRequirementOverrideUnique__1"],
        ["Sidhebreath|Metadata/Items/Amulets/Amulet1"] = ["LevelRequirementOverrideUnique__1"],
        ["Stone of Lazhwar|Metadata/Items/Amulets/Amulet5"] = ["LevelRequirementOverrideUnique__5"],
        ["Sudden Dawn|Metadata/Items/Armours/Helmets/HelmetInt7"] = ["LevelRequirementOverrideUnique__3"],
        ["Tabula Rasa|Metadata/Items/Armours/BodyArmours/BodyInt1"] = ["RemoveStatsFromTabulaRasa", "LevelRequirementOverrideUnique__1"],
        ["Tear of Purity|Metadata/Items/Amulets/Amulet5"] = ["LevelRequirementOverrideUnique__5"],
    };

    readonly Lazy<Dictionary<string, UniqueName>> _names = new(() => LoadNames(folder));

    public IReadOnlyList<Definition> All => GameDataCatalog.Uniques;

    /// <summary>Creatable collection entries include the named-map catalogue as well as equipment and jewels.</summary>
    public IReadOnlyList<(string Name, Item? Item)> CollectionItems(Random random)
    {
        var result = All.Select(d => (d.Name, Item: Create(d, random))).ToList();
        // A pre-existing catalogue can gain this captured field through hot reload without rerunning its constructor.
        result.AddRange(NessaMapStock.Build(items, folder ?? "data").Where(m => m.Item.Rarity == ItemRarity.Unique)
            .Select(m => (m.Name, (Item?)m.Item)));
        return result;
    }

    /// <summary>The definition a name matches, or null: the exact name, then the name followed by a base type's name for
    /// a unique with several bases ("Grand Spectrum Cobalt Jewel"), then the first containing it.</summary>
    /// <summary>A legendary map (UniqueMaps) by name: the named-map catalogue builds it on 3.29's tier-1 map base. Null
    /// when no unique map has that name. Its visual identity keys its map tab page (#477).</summary>
    public Item? NamedMap(string name) =>
        NessaMapStock.Build(items, folder).FirstOrDefault(e => e.Item.Rarity == ItemRarity.Unique
            && NameKey(e.Name).Equals(NameKey(name), StringComparison.OrdinalIgnoreCase)) is { } entry ? entry.Item : null;

    public Definition? Find(string name) =>
        All.FirstOrDefault(d => NameKey(d.Name).Equals(NameKey(name), StringComparison.OrdinalIgnoreCase))
        ?? All.FirstOrDefault(d => name.StartsWith(d.Name + " ", StringComparison.OrdinalIgnoreCase)
            && items.Find(d.BaseTypeId)?.Name.Equals(name[(d.Name.Length + 1)..], StringComparison.OrdinalIgnoreCase) == true)
        ?? All.FirstOrDefault(d => d.Name.Contains(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>The word-list word a unique's name resolves to, or null when the client data has no such name. The
    /// survivors pool uses it to skip a unique the character already wears without rolling a whole item.</summary>
    public uint? NameWord(Definition definition) =>
        _names.Value.TryGetValue(NameKey(definition.Name), out var unique) ? unique.Word : null;

    /// <summary>The unique item for a name, or null when the name is not one or its base type is not writable.</summary>
    public Item? Create(string name, Random random) => Find(name) is { } definition ? Create(definition, random) : null;

    /// <summary>The item of one definition, which tells apart the bases of a unique with several.</summary>
    public Item? Create(Definition definition, Random random)
    {
        if (items.Find(definition.BaseTypeId) is not { Supported: true } type) return null;
        if (!_names.Value.TryGetValue(NameKey(definition.Name), out var unique)) return null;
        var item = ItemCatalog.Create(type) with
        {
            ItemLevel = definition.ItemLevel, Rarity = ItemRarity.Unique, Identified = true,
            Corrupted = definition.Corrupted, Mirrored = definition.Mirrored, Influences = definition.Influences,
            FracturedCount = definition.Fractured,
            NameWords = [unique.Word], VisualIdentity = unique.VisualIdentity,
        };
        var mods = item.Mods.ToList();
        if (definition.Name.Equals("That Which Was Taken", StringComparison.Ordinal))
        {
            // Four random charm modifiers, ignoring attribute alignment (poewiki, "That Which Was Taken"). Charm mods
            // are Mods.dat Domain 35; one modifier per family, as on a charm. The item stays modifiable/divinable.
            var candidates = items.Mods.All
                .Where(m => m.Domain == 35 && m.Level <= definition.ItemLevel && m.Stats.Count > 0)
                .ToList();
            var families = new HashSet<int>();
            for (int i = 0; i < 4 && candidates.Count > 0; i++)
            {
                int pick = random.Next(candidates.Count);
                var mod = candidates[pick];
                candidates.RemoveAt(pick);
                int family = mod.Families.Count > 0 ? mod.Families[0] : mod.Row;
                if (!families.Add(family)) { i--; continue; }
                var values = mod.Stats.Select((_, stat) =>
                {
                    var (min, max) = mod.RangeAt(stat);
                    return min == max ? min : random.Next(min, max + 1);
                }).ToArray();
                mods.Add(new ItemMod(ModSection.Explicit, mod.Hash, values));
            }
            return item with { Mods = mods };
        }
        if (definition.Implicits is { } implicits)
        {
            mods.RemoveAll(m => m.Section == ModSection.Implicit);
            mods.AddRange(Rolled(definition, implicits, ModSection.Implicit, random));
        }
        if (definition.Name == "Grand Spectrum" && GrandSpectrum.Roll(type.Name, random) is { } variant)
            definition = definition with { Mods = [variant] };
        mods.AddRange(Rolled(definition, definition.Mods, ModSection.Explicit, random));
        if (HiddenMods.TryGetValue($"{NameKey(definition.Name)}|{definition.BaseTypeId}", out var hidden))
            mods.AddRange(Rolled(definition, hidden, ModSection.Hidden, random));
        item = item with { Mods = mods };
        if (definition.Name.Equals("Tabula Rasa", StringComparison.OrdinalIgnoreCase)
            && definition.BaseTypeId == "Metadata/Items/Armours/BodyArmours/BodyInt1")
            return item with
            {
                Sockets = Enumerable.Range(0, 6).Select(_ => new ItemSocket(SocketColour.White)).ToArray(),
                Links = [6],
            };
        // The seed omits layout metadata, so the layout comes from the modifiers the client itself reads.
        var local = ItemModStats.Local(item, items.Mods);
        bool noWhite = local.ContainsKey("local_sockets_cannot_be_white");
        bool abyss = local.Keys.Any(id => id is "local_has_X_abyss_sockets" or "local_cannot_have_non_abyssal_sockets");
        if (local.ContainsKey("local_has_no_sockets")) return item;
        if (!abyss && local.ContainsKey("local_six_linked_random_sockets"))
        {
            int count = Math.Min(6, CurrencyEffects.SocketCapacity(item, type));
            if (count == 0) return item;
            var requirements = items.Requirements(type);
            return item with
            {
                Sockets = [.. Enumerable.Range(0, count).Select(_ =>
                    new ItemSocket(SocketColours.Roll(requirements, item.ItemLevel, item.Quality, random, noWhite: noWhite)))],
                Links = [(byte)count],
            };
        }
        // "Has X Sockets" rows add up: Voidfletcher's implicit gives one and its own mod two more (3.29, three sockets).
        // The same row can be both the base's implicit and one of the unique's mods; count each row once.
        int FixedSockets(ItemMod mod)
        {
            if (items.Mods.ByHash(mod.Hash) is not { } row) return 0;
            int total = 0;
            for (int s = 0; s < row.Stats.Count && s < mod.Values.Count; s++)
                if (row.Stats[s].Id == "local_has_X_sockets") total += mod.Values[s];
            return total;
        }
        int fixedSockets = item.Mods.GroupBy(mod => mod.Hash).Sum(group => FixedSockets(group.First()));
        bool allWhite = local.ContainsKey("local_all_sockets_are_white");
        if (!abyss && fixedSockets > 0 && type.Components.Contains("Sockets"))
        {
            // A fixed count overrides the class cap (rings and quivers are not in the socket settings but can carry
            // sockets); 6 is the client's own maximum. Link groups use the same approximation as Jeweller's Orb: the
            // exact unique link distribution is not known.
            int count = Math.Clamp(fixedSockets, 0, SocketLayout.MaximumSockets);
            var requirements = items.Requirements(type);
            var fixedList = new List<ItemSocket>(count);
            var fixedLinks = new List<byte> { 1 };
            for (int i = 0; i < count; i++)
            {
                fixedList.Add(new(allWhite ? SocketColour.White
                    : SocketColours.Roll(requirements, item.ItemLevel, item.Quality, random, noWhite: noWhite)));
                if (i == 0) continue;
                if (random.Next(2) == 0) fixedLinks[^1]++;
                else fixedLinks.Add(1);
            }
            return item with { Sockets = fixedList, Links = count > 0 ? fixedLinks : null };
        }
        if (local.Keys.Any(id => id is "local_has_X_sockets" or "local_all_sockets_are_white"
            or "local_has_X_abyss_sockets" or "local_cannot_have_non_abyssal_sockets")) return item;
        var (sockets, links) = items.DefaultSockets(type, item.ItemLevel, item.Quality, random, noWhite);
        return item with { Sockets = sockets, Links = links };
    }

    IEnumerable<ItemMod> Rolled(Definition definition, string[] ids, ModSection section, Random random)
    {
        foreach (string id in ids)
            if (items.Mods.ById(id) is { } mod)
                yield return mod.Roll(section, random, stat => RollOf(definition, mod, section, stat));
    }

    /// <summary>The definition's roll of one stat. A range for the mod in world.db's mod_stat_overrides comes first.</summary>
    static (int Min, int Max)? RollOf(Definition definition, ModCatalog.Mod mod, ModSection section, int stat) =>
        !mod.RangeOverridden(stat) && definition.Ranges?.FirstOrDefault(range => range.Mod == mod.Id && range.Stat == stat
            && range.Implicit == (section == ModSection.Implicit)) is { } own ? (own.Min, own.Max) : null;

    /// <summary>The rolls of the unique an item is, for currency that rerolls its values; null for any other item.</summary>
    public StatRoll? RollsOf(Item item) =>
        item.Rarity == ItemRarity.Unique && All.FirstOrDefault(d => items.Find(d.BaseTypeId)?.Hash == item.TypeHash
            && _names.Value.TryGetValue(NameKey(d.Name), out var name) && item.NameWords.Contains(name.Word)) is { } definition
            ? (mod, section, stat) => RollOf(definition, mod, section, stat) : null;

    /// <summary>Ancient Orb ("Reforges a unique equipment as another of the same item class"): another unique of the class,
    /// by <paramref name="weight"/> (ItemDrops.UniqueWeight: a Mageblood stays rare), keeping the item's level, quality,
    /// sockets, links and identified state. Corrupted and mirrored items are refused, and corrupted and Replica uniques are
    /// not results. Our own limit: only uniques of the same inventory size, which can hold its sockets, so the item fits
    /// where it lies and no socketed gem is lost.</summary>
    public Item? Reforge(Item item, Func<string, double> weight, Random random)
    {
        if (item.Rarity != ItemRarity.Unique || item.Corrupted || item.Mirrored || items.ByHash(item.TypeHash) is not { } current
            || !ItemDrops.EquipmentClasses.Contains(current.Class)) return null;
        var candidates = new List<(Definition Definition, double Weight)>();
        foreach (var definition in All)
        {
            if (definition.Corrupted || definition.Name.StartsWith("Replica ", StringComparison.Ordinal)
                || weight(definition.Name) is not (> 0 and var chance) || items.Find(definition.BaseTypeId) is not { Supported: true } type
                || type.Class != current.Class || type.Width != current.Width || type.Height != current.Height
                || !_names.Value.TryGetValue(NameKey(definition.Name), out var name) || item.NameWords.Contains(name.Word)
                || item.Sockets.Count > CurrencyEffects.SocketCapacity(item, type)) continue;
            candidates.Add((definition, chance));
        }
        if (candidates.Count == 0) return null;
        double pick = random.NextDouble() * candidates.Sum(c => c.Weight);
        var chosen = candidates[^1].Definition;
        foreach (var (definition, chance) in candidates)
        {
            if (pick < chance) { chosen = definition; break; }
            pick -= chance;
        }
        return Create(chosen, random) is { } created ? created with
        {
            ItemLevel = item.ItemLevel, Quality = item.Quality, Sockets = item.Sockets, Links = item.Links, Identified = item.Identified,
        } : null;
    }

    sealed record UniqueName(uint Word, ushort VisualIdentity);

    static Dictionary<string, UniqueName> LoadNames(string folder)
    {
        var result = new Dictionary<string, UniqueName>(StringComparer.OrdinalIgnoreCase);
        string Table(string name) => Path.Combine(folder, "tables", name + ".json");
        if (!ClientTables.Exists(Table("Words")) || !ClientTables.Exists(Table("UniqueStashLayout")) || !ClientTables.Exists(Table("ItemVisualIdentity")))
            return result;
        using var words = GameTables.Open(Table("Words"));
        using var layout = GameTables.Open(Table("UniqueStashLayout"));
        using var visuals = GameTables.Open(Table("ItemVisualIdentity"));
        // Unique names are wordlist 6 (rare names use 1 and 2); a layout row links the word to the unique artwork.
        // Keep every wordlist row, not only the definitions known at startup: a definition added to the database later
        // can then resolve its existing client word and art without a code change.
        var byWord = new Dictionary<int, string>();
        for (int i = 0; i < words.RootElement.GetArrayLength(); i++)
            if (words.RootElement[i].GetProperty("Wordlist").GetInt32() == 6
                && words.RootElement[i].GetProperty("Text").GetString() is { Length: > 0 } text)
                byWord[i] = text;
        foreach (var r in layout.RootElement.EnumerateArray())
        {
            int wordsKey = r.GetProperty("WordsKey").GetInt32(), visualKey = r.GetProperty("ItemVisualIdentityKey").GetInt32();
            if (!byWord.TryGetValue(wordsKey, out string? name) || visualKey < 0
                || visualKey >= visuals.RootElement.GetArrayLength()) continue;
            name = NameKey(name);
            // A unique can have a second row with its alternate art (Headhunter2.dds); the ordinary art is the item's.
            if (r.TryGetProperty("IsAlternateArt", out var alternate) && alternate.GetBoolean() && result.ContainsKey(name)) continue;
            result[name] = new(words.RootElement[wordsKey].GetProperty("HASH32").GetUInt32(),
                (ushort)visuals.RootElement[visualKey].GetProperty("HASH16").GetInt32());
        }
        return result;
    }
}
