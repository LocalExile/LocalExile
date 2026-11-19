using System.Text.Json;
using GameServer.Core.Protocol.Instance;
using GameServer.Core;

/// <summary>
/// Unique items the server can create. The client data has a unique's name (<c>Words</c>) and artwork
/// (<c>ItemVisualIdentity</c>, linked through <c>UniqueStashLayout</c>), but no table that says which Mods rows belong to
/// it, so each definition below lists them from the game's data (poedb: Mageblood, 3.29). The base type's implicits are
/// rolled by <see cref="ItemCatalog.Create"/>; the unique's own mods are explicit and rolled once per item.
/// </summary>
public sealed partial class UniqueCatalog
{
    readonly ItemCatalog items;
    readonly string folder;

    public UniqueCatalog(ItemCatalog items, string folder = "data")
    {
        this.items = items;
        this.folder = folder;
        _names = LoadNames(folder);
    }

    // Several native names use a typographic apostrophe while the definition export uses ASCII; one ends in a space
    // ("Demigod's Immortality ").
    public static string NameKey(string name) => name.Trim().Replace('\u2019', '\'').Replace('\u2018', '\'').Replace('\u02bc', '\'') switch
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
    public sealed partial record Definition(string Name, string BaseTypeId, string[] Mods, byte ItemLevel = 86,
        bool Corrupted = false, string[]? Implicits = null, RollRange[]? Ranges = null, bool Mirrored = false,
        ItemInfluence Influences = ItemInfluence.None, byte Fractured = 0, bool EncounterOnly = false,
        RollGroup[]? RollGroups = null, int JewelLimit = 0)
    {
        [System.Text.Json.Serialization.JsonIgnore]
        public string? CreationVariant { get; init; }
        public Variant[]? Variants { get; init; }
    }

    public sealed record Variant(string Name, string? BaseTypeId, string[] Mods, int Weight = 0);

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

    readonly Dictionary<string, UniqueName> _names;

    public IReadOnlyList<Definition> All => GameDataCatalog.Uniques;

    /// <summary>Creatable collection entries include the named-map catalogue as well as equipment and jewels.</summary>
    public IReadOnlyList<(string Name, Item? Item)> CollectionItems(Random random)
    {
        var result = All.Select(d => (d.Name, Item: Create(d, random))).ToList();
        result.AddRange(items.MapStock.Where(m => m.Item.Rarity == ItemRarity.Unique)
            .Select(m => (m.Name, (Item?)m.Item)));
        return result;
    }

    /// <summary>The definition a name matches, or null: the exact name, then the name followed by a base type's name for
    /// a unique with several bases ("Grand Spectrum Cobalt Jewel"), then the first containing it.</summary>
    /// <summary>A legendary map (UniqueMaps) by name: the named-map catalogue builds it on 3.29's tier-1 map base. Null
    /// when no unique map has that name. Its visual identity keys its map tab page (#477).</summary>
    public Item? NamedMap(string name) =>
        items.MapStock.FirstOrDefault(e => e.Item.Rarity == ItemRarity.Unique
            && NameKey(e.Name).Equals(NameKey(name), StringComparison.OrdinalIgnoreCase)) is { } entry ? entry.Item : null;

    public Definition? Find(string name)
    {
        int variantStart = name.LastIndexOf(" (", StringComparison.Ordinal);
        if (variantStart > 0 && name.EndsWith(')'))
        {
            string parent = NameKey(name[..variantStart]), wanted = name[(variantStart + 2)..^1];
            if (All.FirstOrDefault(d => NameKey(d.Name).Equals(parent, StringComparison.OrdinalIgnoreCase)) is { Variants: not null } definition)
                return definition.Variants.FirstOrDefault(v => v.Name.Equals(wanted, StringComparison.OrdinalIgnoreCase)) is { } selected
                    ? definition with { CreationVariant = selected.Name } : null;
        }
        return All.FirstOrDefault(d => NameKey(d.Name).Equals(NameKey(name), StringComparison.OrdinalIgnoreCase))
            ?? All.FirstOrDefault(d => name.StartsWith(d.Name + " ", StringComparison.OrdinalIgnoreCase)
                && items.Find(d.BaseTypeId)?.Name.Equals(name[(d.Name.Length + 1)..], StringComparison.OrdinalIgnoreCase) == true)
            ?? All.FirstOrDefault(d => d.Name.Contains(name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The word-list word a unique's name resolves to, or null when the client data has no such name. The
    /// survivors pool uses it to skip a unique the character already wears without rolling a whole item.</summary>
    public uint? NameWord(Definition definition) =>
        Identity(definition)?.Word;

    /// <summary>The unique item for a name, or null when the name is not one or its base type is not writable.</summary>
    public Item? Create(string name, Random random) => Find(name) is { } definition ? Create(definition, random) : null;

    /// <summary>The item of one definition, which tells apart the bases of a unique with several.</summary>
    public Item? Create(Definition definition, Random random)
    {
        if (definition.Variants is { Length: > 0 } variants)
        {
            Variant? selected;
            if (definition.CreationVariant is { } wanted)
                selected = variants.FirstOrDefault(v => v.Name.Equals(wanted, StringComparison.OrdinalIgnoreCase));
            else
            {
                int total = variants.Sum(v => Math.Max(0, v.Weight));
                int roll = total > 0 ? random.Next(total) : -1;
                selected = total > 0 ? variants.First(v => (roll -= Math.Max(0, v.Weight)) < 0)
                    : variants.FirstOrDefault(v => v.BaseTypeId == definition.BaseTypeId) ?? variants[0];
            }
            if (selected is null) throw new InvalidOperationException("Unknown unique item variant");
            var variantMods = variants.SelectMany(v => v.Mods).ToHashSet(StringComparer.Ordinal);
            definition = definition with { BaseTypeId = selected.BaseTypeId ?? definition.BaseTypeId,
                Mods = [.. definition.Mods.Where(id => !variantMods.Contains(id)), .. selected.Mods] };
        }
        if (items.Find(definition.BaseTypeId) is not { Supported: true } type) return null;
        if (Identity(definition) is not { } unique) return null;
        var item = ItemCatalog.Create(type) with
        {
            ItemLevel = definition.ItemLevel, Rarity = ItemRarity.Unique, Identified = true,
            Corrupted = definition.Corrupted, Mirrored = definition.Mirrored, Influences = definition.Influences,
            FracturedCount = definition.Fractured,
            NameWords = [unique.Word], VisualIdentity = unique.VisualIdentity,
        };
        if (definition.BaseTypeId == TimelessJewelCatalog.BaseId)
        {
            var legion = TimelessJewelCatalog.All.FirstOrDefault(entry => entry.Name == definition.Name);
            if (legion is null || legion.Conquerors.Length == 0) return null;
            var conqueror = legion.Conquerors[random.Next(legion.Conquerors.Length)];
            var seeded = TimelessJewelCatalog.Create(items, legion.Name, conquerorIndex: conqueror.Index,
                folder: folder, random: random);
            return seeded is null ? null : item with { Mods = seeded.Mods };
        }
        var mods = item.Mods.ToList();
        // A unique has its own lists: not the enchantments a new item of its base rolls (a cluster jewel's theme and count).
        mods.RemoveAll(m => m.Section == ModSection.Enchant);
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
        if (definition.Name == "Grand Spectrum" && GrandSpectrum.Roll(type.Name, random) is { } spectrumVariant)
            definition = definition with { Mods = [spectrumVariant] };
        string[] explicitMods = definition.Mods;
        mods.AddRange(Rolled(definition, explicitMods, ModSection.Explicit, random));
        if (HiddenMods.TryGetValue($"{NameKey(definition.Name)}|{definition.BaseTypeId}", out var hidden))
            mods.AddRange(Rolled(definition, hidden, ModSection.Hidden, random));
        if (!RollEncounterModifiers(definition, random, mods)) return null;
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
        bool noWhite = local.ContainsKey(Stat.LocalSocketsCannotBeWhite);
        bool abyss = local.Keys.Any(id => id is Stat.LocalHasXAbyssSockets or Stat.LocalCannotHaveNonAbyssalSockets);
        if (local.ContainsKey(Stat.LocalHasNoSockets)) return item with { Sockets = [], Links = null };
        if (type.Id == AbyssJewels.StygianVise) return AbyssJewels.StygianSockets(item, local);
        if (local.GetValueOrDefault(Stat.LocalHasXAbyssSockets) > 0)
            return AbyssJewels.EquipmentSockets(item, type, items, local, random);
        if (!abyss && local.ContainsKey(Stat.LocalSixLinkedRandomSockets))
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
                if (row.Stats[s].Id == Stat.LocalHasXSockets) total += mod.Values[s];
            return total;
        }
        int fixedSockets = item.Mods.GroupBy(mod => mod.Hash).Sum(group => FixedSockets(group.First()));
        bool allWhite = local.ContainsKey(Stat.LocalAllSocketsAreWhite);
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
        if (local.Keys.Any(id => id is Stat.LocalHasXSockets or Stat.LocalAllSocketsAreWhite
            or Stat.LocalHasXAbyssSockets or Stat.LocalCannotHaveNonAbyssalSockets)) return item;
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
            && _names.TryGetValue(NameKey(d.Name), out var name) && item.NameWords.Contains(name.Word)) is { } definition
            ? (mod, section, stat) => RollOf(definition, mod, section, stat) : null;

    /// <summary>Ancient Orb ("Reforges a unique equipment as another of the same item class"): another unique of the class,
    /// by <paramref name="weight"/> (ItemDrops.UniqueWeight: a Mageblood stays rare), keeping the item's level, quality,
    /// sockets, links and identified state. Corrupted and mirrored items are refused, and corrupted and Replica uniques are
    /// not results. Our own limit: only uniques of the same inventory size, which can hold its sockets, so the item fits
    /// where it lies and no socketed gem is lost.</summary>
    public Item? Reforge(Item item, Func<string, double> weight, Random random)
    {
        if (item.Rarity != ItemRarity.Unique || item.Corrupted || item.Mirrored || item.Vestigial || items.ByHash(item.TypeHash) is not { } current
            || !ItemDrops.EquipmentClasses.Contains(current.Class)) return null;
        var candidates = new List<(Definition Definition, double Weight)>();
        foreach (var definition in All)
        {
            if (definition.EncounterOnly || definition.Corrupted || definition.Name.StartsWith("Replica ", StringComparison.Ordinal)
                || weight(definition.Name) is not (> 0 and var chance) || items.Find(definition.BaseTypeId) is not { Supported: true } type
                || type.Class != current.Class || type.Width != current.Width || type.Height != current.Height
                || !_names.TryGetValue(NameKey(definition.Name), out var name) || item.NameWords.Contains(name.Word)
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
        if (!ClientTables.Has("Words") || !ClientTables.Has("UniqueStashLayout") || !ClientTables.Has("ItemVisualIdentity"))
            return result;
        var words = ClientTables.Table("Words");
        var layout = ClientTables.Table("UniqueStashLayout");
        var visuals = ClientTables.Table("ItemVisualIdentity");
        // Unique names are wordlist 6 (rare names use 1 and 2); a layout row links the word to the unique artwork.
        // Keep every wordlist row, not only the definitions known at startup: a definition added to the database later
        // can then resolve its existing client word and art without a code change.
        var byWord = new Dictionary<int, string>();
        for (int i = 0; i < words.Count; i++)
            if (words[i].Int("Wordlist") == 6
                && words[i].Text("Text") is { Length: > 0 } text)
                byWord[i] = text;
        foreach (var r in layout)
        {
            int wordsKey = r.Int("WordsKey"), visualKey = r.Int("ItemVisualIdentityKey");
            if (!byWord.TryGetValue(wordsKey, out string? name) || visualKey < 0
                || visualKey >= visuals.Count) continue;
            name = NameKey(name);
            // A unique can have a second row with its alternate art (Headhunter2.dds); the ordinary art is the item's.
            if (r.BoolOrNull("IsAlternateArt") == true && result.ContainsKey(name)) continue;
            result[name] = new(words[wordsKey].UInt("HASH32"),
                (ushort)visuals[visualKey].Int("HASH16"));
        }
        return result;
    }
}
