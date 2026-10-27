using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using GameServer.Core.Imports;
using GameServer.Core.Protocol.Instance;

/// <summary>
/// Builds real, serializable items from the selected PoB set. Every modifier is resolved to this client's Mods.dat
/// and exact values; an unsupported item is an error, never a request to generate a superficially similar item.
/// </summary>
static class PathOfBuildingItems
{
    public sealed record Result(Dictionary<byte, List<CharacterSaves.SavedPlacement>> Inventories,
        IReadOnlyList<string> Warnings, Dictionary<int, string> SkillBar);

    sealed record Equipped(byte Row, byte X, ItemCatalog.BaseType Type, Item Item, string Slot);
    sealed record ParsedMod(string Text, ModSection Section, bool Fractured, bool Crafted, bool Derived);
    sealed record StatValue(string Id, int Value);
    static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
    static readonly Regex Tags = new(@"\{([^}:]+)(?::([^}]+))?\}", RegexOptions.CultureInvariant);
    static readonly Regex Ranges = new(@"\((-?\d+(?:\.\d+)?)-(-?\d+(?:\.\d+)?)\)", RegexOptions.CultureInvariant);
    static readonly Dictionary<string, byte> Equipment = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Body Armour"] = 1, ["Weapon 1"] = 2, ["Weapon 2"] = 3, ["Helmet"] = 4,
        ["Amulet"] = 5, ["Ring 1"] = 6, ["Ring 2"] = 7, ["Gloves"] = 8, ["Boots"] = 9,
        ["Belt"] = 10, ["Weapon 1 Swap"] = 14, ["Weapon 2 Swap"] = 15,
    };

    public static Result Materialize(PathOfBuildingBuild build, ItemCatalog catalog, string folder = "data")
    {
        if (catalog.Error is { } unavailable) throw new ArgumentException(unavailable);
        var errors = new List<string>();
        var warnings = new List<string>();
        var equipped = new Dictionary<string, Equipped>(StringComparer.OrdinalIgnoreCase);
        var definitions = build.Items.ToDictionary(i => i.Id);
        foreach (var slot in build.Slots.Where(s => s.ItemId != 0))
        {
            try
            {
                if (!definitions.TryGetValue(slot.ItemId, out var definition))
                    throw new ArgumentException($"item id {slot.ItemId} is missing");
                byte row, x = 0;
                if (Equipment.TryGetValue(slot.Name, out byte equipment)) row = equipment;
                else if (Regex.Match(slot.Name, @"^Flask ([1-5])$", RegexOptions.IgnoreCase) is { Success: true } flask)
                { row = PlayerInventories.Flask; x = (byte)(flask.Groups[1].Value[0] - '1'); }
                else throw new ArgumentException("this equipment slot is not implemented by the server");
                var (type, item) = ParseItem(definition, catalog, folder, warnings);
                if (!PlayerInventories.Accepts(row, type.Class))
                    throw new ArgumentException($"{type.Name} ({type.Class}) cannot be equipped in this slot");
                if (!equipped.TryAdd(slot.Name, new(row, x, type, item, slot.Name)))
                    throw new ArgumentException("the selected item set repeats this slot");
            }
            catch (ArgumentException e) { errors.Add($"{slot.Name}: {e.Message}"); }
        }
        ValidateHands(equipped, errors);

        var skillBar = new Dictionary<int, string> { [0] = "Action/Move" };
        var effectIds = LoadEffectIds(folder);
        var offsets = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var occupiedLinks = new HashSet<(string Slot, int Start)>();
        int key = 1;
        foreach (var group in build.Skills)
        {
            int offset = 0;
            if (group.Slot is { } slot)
            {
                offset = offsets.GetValueOrDefault(slot);
                offsets[slot] = offset + group.Gems.Count;
            }
            if (!group.Enabled)
            { warnings.Add($"Disabled skill group in {group.Slot ?? "an unspecified slot"} was excluded."); continue; }
            var selected = group.Gems.Where(g => g.Enabled).ToArray();
            if (selected.Length != group.Gems.Count)
                warnings.Add($"Disabled gems in {group.Slot ?? "an unspecified slot"} were excluded.");
            if (selected.Length == 0) continue;
            try
            {
                if (!string.IsNullOrWhiteSpace(group.Source))
                    throw new ArgumentException($"item-granted skill source '{group.Source}' cannot be imported as socketed gems");
                if (!string.IsNullOrWhiteSpace(group.ImbuedSupport))
                    throw new ArgumentException("imbued support gems are not implemented");
                if (group.Slot is not { Length: > 0 } slotName || !equipped.TryGetValue(slotName, out var host))
                    throw new ArgumentException("an enabled skill group must name an equipped item slot");
                var gems = group.Gems.Select(g => g.Enabled ? CreateGem(g, catalog, folder) : null).ToArray();
                var sockets = host.Item.Sockets.ToArray();
                var links = host.Item.Links ?? [.. Enumerable.Repeat((byte)1, sockets.Length)];
                int at = 0, linkStart = -1;
                foreach (int size in links)
                {
                    if (offset >= at && offset + gems.Length <= at + size) { linkStart = at; break; }
                    at += size;
                }
                if (linkStart < 0 || offset + gems.Length > sockets.Length)
                    throw new ArgumentException($"the exported gem order does not fit one linked group in {host.Type.Name}; export its actual sockets in PoB");
                if (!occupiedLinks.Add((slotName.ToLowerInvariant(), linkStart)))
                    throw new ArgumentException("separate enabled PoB groups would share one native linked group and change their support effects");
                for (int i = 0; i < gems.Length; i++)
                {
                    if (gems[i] is not null && sockets[offset + i].Colour == SocketColour.Abyss)
                        throw new ArgumentException("a skill gem cannot be placed in an Abyss socket");
                    sockets[offset + i] = sockets[offset + i] with { Gem = gems[i] };
                }
                equipped[slotName] = host with { Item = host.Item with { Sockets = sockets } };
                bool inactiveWeapon = host.Row is 14 or 15;
                if (build.UseSecondWeaponSet) inactiveWeapon = host.Row is 2 or 3;
                if (!inactiveWeapon)
                    foreach (var gem in gems.OfType<Item>())
                        foreach (int effect in catalog.GemEffects(gem))
                            if (key <= 12 && effect >= 0 && effect < effectIds.Length && effectIds[effect] is { Length: > 0 } id
                                && !skillBar.ContainsValue(id)) skillBar[key++] = id;
            }
            catch (ArgumentException e) { errors.Add($"Skills ({group.Slot ?? "unspecified slot"}): {e.Message}"); }
        }
        if (errors.Count > 0)
            throw new ArgumentException("PoB import cannot represent the selected build exactly:\n" + string.Join("\n", errors.Select(e => "- " + e)));

        var inventories = new Dictionary<byte, List<CharacterSaves.SavedPlacement>>();
        uint next = 1;
        foreach (var entry in equipped.Values)
        {
            byte row = entry.Row;
            if (build.UseSecondWeaponSet) row = row switch { 2 => 14, 3 => 15, 14 => 2, 15 => 3, _ => row };
            if (!inventories.TryGetValue(row, out var placements)) inventories[row] = placements = [];
            placements.Add(new(next++, entry.X, 0, CharacterSaves.ToSaved(entry.Item, catalog.Mods)));
        }
        return new(inventories, warnings.Distinct().ToArray(), skillBar);
    }

    static string[] LoadEffectIds(string folder)
    {
        string path = Path.Combine(folder, "tables", "GrantedEffects.json");
        if (!File.Exists(path)) return [];
        using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
        return [.. doc.RootElement.EnumerateArray().Select(r => r.GetProperty("Id").GetString() ?? "")];
    }

    static void ValidateHands(Dictionary<string, Equipped> items, List<string> errors)
    {
        foreach (string suffix in new[] { "", " Swap" })
        {
            items.TryGetValue("Weapon 1" + suffix, out var main);
            items.TryGetValue("Weapon 2" + suffix, out var off);
            if (off is null) continue;
            bool twoHand = main?.Type.Class is "Staff" or "Warstaff" or "Two Hand Sword" or "Two Hand Axe" or "Two Hand Mace";
            if (twoHand || main?.Type.Class == "Bow" && off.Type.Class != "Quiver"
                || off.Type.Class == "Quiver" && main is not null && main.Type.Class != "Bow")
                errors.Add($"Weapon set{suffix}: {main?.Type.Name ?? "empty main hand"} and {off.Type.Name} cannot be equipped together");
        }
    }

    internal static Item CreateGem(PathOfBuildingGem gem, ItemCatalog catalog, string folder = "data")
    {
        var matches = catalog.All.Where(t => t.Supported && !t.Id.Contains("Royale", StringComparison.OrdinalIgnoreCase) && catalog.Gem(t) is not null)
            .SelectMany(t => catalog.Gem(t)!.Variants.Where(v => v.Creatable).Select(v => (Type: t, Variant: v)))
            .Where(x => string.IsNullOrWhiteSpace(gem.Name) || string.Equals(x.Variant.Name, gem.Name, StringComparison.OrdinalIgnoreCase)
                || string.Equals(x.Variant.Name, gem.Name + " Support", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (gem.GemId is { Length: > 0 } id)
        {
            var byId = matches.Where(x => string.Equals(x.Type.Id, id, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (byId.Length == 0) throw new ArgumentException($"gem '{gem.Name}' does not match '{id}' in this client's obtainable gem catalogue");
            matches = byId;
        }
        if (gem.VariantId is { Length: > 0 } variantId) matches = matches.Where(x => x.Variant.Id == variantId).ToArray();
        var effects = LoadEffectIds(folder);
        if (gem.SkillId is { Length: > 0 } requiredSkill)
            matches = matches.Where(x => new[] { x.Variant.Effect, x.Variant.Effect2 }
                .Any(e => e >= 0 && e < effects.Length && effects[e] == requiredSkill)).ToArray();
        if (matches.Length != 1)
            throw new ArgumentException($"gem '{gem.Name}' is {(matches.Length == 0 ? "unavailable" : "ambiguous")} in this client's obtainable gem catalogue");
        var match = matches[0];
        var type = catalog.Gem(match.Type)!;
        if (gem.QualityId is { Length: > 0 } qualityId && qualityId != "Default")
            throw new ArgumentException($"{gem.Name} uses unsupported historical quality '{qualityId}'");
        if (gem.Level < 1 || gem.Level > type.MaxLevel + 1)
            throw new ArgumentException($"{gem.Name} level {gem.Level} is outside the representable range 1..{type.MaxLevel + 1}");
        if (gem.Quality is < 0 or > 23)
            throw new ArgumentException($"{gem.Name} quality {gem.Quality} is outside the representable range 0..23");
        return ItemCatalog.Create(match.Type, defenceBaseRoll: 0) with
        {
            GemVariant = match.Variant.Index, GemExperience = gem.Level > type.MaxLevel ? -1 : type.ExperienceFor(gem.Level),
            Quality = (byte)gem.Quality, Corrupted = type.IsVaal || gem.Level > type.MaxLevel || gem.Quality > 20,
        };
    }

    internal static (ItemCatalog.BaseType Type, Item Item) ParseItem(PathOfBuildingItem definition,
        ItemCatalog catalog, string folder, List<string> warnings)
    {
        if (definition.HasAlternateVariants) throw new ArgumentException("alternate item variants are not implemented");
        if (definition.HasLegacyModRanges) throw new ArgumentException("legacy per-mod roll overrides must be re-saved in current PoB before importing");
        var lines = definition.Text.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(l => l != "--------").ToArray();
        int pos = lines.FirstOrDefault()?.StartsWith("Item Class:", StringComparison.Ordinal) == true ? 1 : 0;
        if (pos >= lines.Length || !lines[pos].StartsWith("Rarity: ", StringComparison.Ordinal)
            || !Enum.TryParse<ItemRarity>(lines[pos++][8..], true, out var rarity) || !Enum.IsDefined(rarity))
            throw new ArgumentException("item text must start with a supported Rarity: NORMAL, MAGIC, RARE or UNIQUE header");
        if (pos >= lines.Length) throw new ArgumentException("the item has no base name");
        string? title = rarity is ItemRarity.Rare or ItemRarity.Unique ? lines[pos++] : null;
        if (pos >= lines.Length) throw new ArgumentException("the item has no base name");
        string baseName = lines[pos++];
        var ordinary = catalog.All.Where(t => t.Supported && !t.Id.Contains("Royale", StringComparison.OrdinalIgnoreCase)).ToArray();
        var bases = ordinary.Where(t => string.Equals(t.Name, baseName, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (bases.Length == 0 && rarity == ItemRarity.Magic)
        {
            bases = ordinary.Where(t => baseName.Equals(t.Name, StringComparison.OrdinalIgnoreCase)
                || baseName.Contains(" " + t.Name + " ", StringComparison.OrdinalIgnoreCase)
                || baseName.StartsWith(t.Name + " ", StringComparison.OrdinalIgnoreCase)
                || baseName.EndsWith(" " + t.Name, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (bases.Length > 0) bases = bases.Where(t => t.Name.Length == bases.Max(b => b.Name.Length)).ToArray();
        }
        if (bases.Length != 1)
            throw new ArgumentException($"base '{baseName}' is {(bases.Length == 0 ? "unknown or unsupported" : "ambiguous")} in this client's data");
        var type = bases[0];
        var item = ItemCatalog.Create(type, defenceBaseRoll: Item.MaximumDefenceBaseRoll) with { Rarity = rarity, Mods = [] };
        var rawMods = new List<ParsedMod>();
        var directMods = new List<(ItemMod Mod, bool Fractured)>();
        int implicitLines = 0, selectedVariant = definition.Variant ?? 1;
        bool hasImplicitMarker = false;
        int? exportedLevelRequirement = null;
        var percentile = new List<double>();
        var defenceDisplay = new Dictionary<string, int>();
        foreach (string raw in lines.Skip(pos))
        {
            if (definition.Variant is null && raw.StartsWith("Selected Variant: ", StringComparison.Ordinal))
            { selectedVariant = Number(raw[18..], "selected variant", 1, 10000); continue; }
        }
        foreach (string raw in lines.Skip(pos))
        {
            if (raw == "Corrupted") { item = item with { Corrupted = true }; continue; }
            if (raw == "Mirrored") { item = item with { Mirrored = true }; continue; }
            if (raw is "Split" or "Fractured Item") continue;
            if (raw.StartsWith("Rarity:", StringComparison.Ordinal)) throw new ArgumentException("multiple item records in one item definition");
            int colon = raw.IndexOf(':');
            string field = colon < 0 ? "" : raw[..colon], value = colon < 0 ? "" : raw[(colon + 1)..].Trim();
            if (field == "Item Level") { item = item with { ItemLevel = (byte)Number(value, field, 1, 100) }; continue; }
            if (field == "Quality") { item = item with { Quality = (byte)Number(value.TrimEnd('%'), field, 0, 30) }; continue; }
            if (field is "LevelReq" or "Requires Level")
            { exportedLevelRequirement = Number(value, field, 0, 100); continue; }
            if (field == "Sockets")
            {
                if (!Regex.IsMatch(value, @"^[RGBWA](?:-[RGBWA])*(?: +[RGBWA](?:-[RGBWA])*)*$", RegexOptions.CultureInvariant))
                    throw new ArgumentException("socket groups must be colour letters separated by single links, for example R-G-B W-W");
                var colours = new List<SocketColour>(); var links = new List<byte>();
                foreach (string word in value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    int n = 0;
                    foreach (char c in word)
                        if (c != '-')
                        {
                            colours.Add(c switch { 'R' => SocketColour.Red, 'G' => SocketColour.Green, 'B' => SocketColour.Blue,
                                'W' => SocketColour.White, 'A' => SocketColour.Abyss, _ => throw new ArgumentException($"unknown socket colour '{c}'") });
                            n++;
                        }
                    if (n > 0) links.Add((byte)n);
                }
                if (colours.Count > CurrencyEffects.SocketCapacity(item, type))
                    throw new ArgumentException($"{type.Name} cannot hold the exported {colours.Count} sockets");
                item = item with { Sockets = [.. colours.Select(c => new ItemSocket(c))], Links = links };
                continue;
            }
            if (field is "ArmourBasePercentile" or "EvasionBasePercentile" or "EnergyShieldBasePercentile")
            {
                if (!double.TryParse(value, NumberStyles.Float, Invariant, out double p) || !double.IsFinite(p) || p < 0 || p > 1)
                    throw new ArgumentException($"invalid {field}");
                percentile.Add(p); continue;
            }
            if (field == "Implicits") { implicitLines = Number(value, field, 0, 255); hasImplicitMarker = true; continue; }
            if (field is "Prefix" or "Suffix")
            {
                var (text, flags, ranges) = ReadTags(value, selectedVariant);
                if (flags.Contains("excluded")) continue;
                if (flags.Any(f => f != "fractured"))
                    throw new ArgumentException($"unsupported affix flags in '{raw}'");
                if (text == "None") continue;
                var mod = catalog.Mods.ById(text) ?? throw new ArgumentException($"modifier '{text}' is absent from this client");
                if (mod.GenerationType != (field == "Prefix" ? 1 : 2)) throw new ArgumentException($"{text} is not a {field.ToLowerInvariant()}");
                if (mod.Domain != type.ModDomain && mod.Domain != 9)
                    throw new ArgumentException($"{text} is not an item modifier for {type.Name}");
                if (ranges.Length > 1 && ranges.Any(r => r != ranges[0]))
                    throw new ArgumentException($"{text} has independent rolls which cannot be mapped safely to native stat order");
                double range = ranges.Length == 0 ? 0.5 : ranges[0];
                directMods.Add((new(ModSection.Explicit, mod.Hash,
                    [.. mod.Stats.Select(s => checked((int)Math.Floor(s.Min + (s.Max - (double)s.Min) * range + 0.5)))]), flags.Contains("fractured")));
                continue;
            }
            if (field is "Unique ID" or "League" or "Crafted" or "Variant" or "Selected Variant" or "Source" or "Upgrade"
                or "Requirements" or "Item Class") continue;
            // Numeric armour lines are final display values. Native base percentiles, when exported, carry their state.
            if (field is "Armour" or "Evasion" or "Energy Shield" or "EnergyShield")
            { defenceDisplay[field.Replace(" ", "")] = Number(value, field, 0, int.MaxValue); continue; }
            if (field is "Ward" or "WardBasePercentile" or "Catalyst" or "CatalystQuality" or "Version" or "Selected Version"
                or "Cluster Jewel Skill" or "Cluster Jewel Node Count" or "Radius" or "Limited to" or "Unreleased"
                || raw.Contains(" Alt Variant", StringComparison.Ordinal) || raw.StartsWith("Selected Variant Group", StringComparison.Ordinal))
                throw new ArgumentException($"unsupported item property '{raw}'");

            var (line, tags, rangeValues) = ReadTags(raw, selectedVariant);
            // Implicits counts physical text lines, including lines from inactive variants.
            bool implicitSection = implicitLines-- > 0;
            if (tags.Contains("excluded")) continue;
            if (tags.Any(t => t is not ("crafted" or "fractured" or "implicit" or "enchant" or "scourge" or "prefix" or "suffix")))
                throw new ArgumentException($"unsupported modifier flags in '{raw}'");
            if (rangeValues.Length > 1) throw new ArgumentException($"multiple independent ranges in '{raw}'");
            double lineRange = rangeValues.Length == 0 ? 1 : rangeValues[0];
            line = Ranges.Replace(line, m => Math.Floor(double.Parse(m.Groups[1].Value, Invariant)
                + (double.Parse(m.Groups[2].Value, Invariant) - double.Parse(m.Groups[1].Value, Invariant)) * lineRange + 0.5).ToString(Invariant));
            ModSection section = tags.Contains("enchant") ? ModSection.Enchant : tags.Contains("scourge") ? ModSection.Scourge
                : implicitSection || tags.Contains("implicit") ? ModSection.Implicit : ModSection.Explicit;
            rawMods.Add(new(line, section, tags.Contains("fractured"), tags.Contains("crafted"), tags.Contains("prefix") || tags.Contains("suffix")));
        }
        if (implicitLines > 0) throw new ArgumentException("the Implicits count exceeds the modifier lines");
        var mods = new List<(ItemMod Mod, bool Fractured)>(directMods);
        foreach (var line in rawMods)
        {
            if (line.Derived && directMods.Count > 0) continue; // These lines are generated from the exact Prefix/Suffix ids.
            mods.Add((ResolveMod(line, type, item, catalog), line.Fractured));
        }
        if (!hasImplicitMarker && type.Implicits is { Count: > 0 })
            throw new ArgumentException("the export omits its implicit modifier values; re-save the item in PoB");
        item = item with
        {
            Mods = [.. mods.Where(m => m.Fractured).Concat(mods.Where(m => !m.Fractured)).Select(m => m.Mod)],
            FracturedCount = checked((byte)mods.Count(m => m.Fractured && m.Mod.Section == ModSection.Explicit)),
        };
        item = WithHiddenDefenceMod(item, type, title, catalog.Mods);
        if (exportedLevelRequirement is int required && required != ItemModStats.LevelRequirement(item, type.DropLevel, catalog.Mods))
            throw new ArgumentException($"exported level requirement {required} does not match this client's base and modifier requirements");
        if (percentile.Count > 0)
        {
            var rolls = percentile.Select(p => (byte)Math.Floor(p * Item.MaximumDefenceBaseRoll + 0.5)).Distinct().ToArray();
            if (rolls.Length != 1) throw new ArgumentException("the exported armour percentiles require different native base rolls");
            item = item with { DefenceBaseRoll = rolls[0] };
        }
        if (defenceDisplay.Count > 0)
        {
            if (catalog.Defences(type) is not { } defences)
                throw new ArgumentException($"{type.Name} exports defences but its native armour data is unavailable");
            bool Matches(byte roll)
            {
                var values = ItemDefences.Calculate(item, defences, catalog.Mods, roll);
                return defenceDisplay.All(kv => kv.Value == (kv.Key == "Armour" ? values.Armour
                    : kv.Key == "Evasion" ? values.Evasion : values.EnergyShield));
            }
            var possible = Enumerable.Range(0, Item.MaximumDefenceBaseRoll + 1).Select(n => (byte)n).Where(Matches)
                .OrderBy(n => Math.Abs(n - item.DefenceBaseRoll)).ToArray();
            if (possible.Length == 0)
                throw new ArgumentException("the exported defence values cannot be represented by this client's native base roll and modifiers");
            item = item with { DefenceBaseRoll = possible[0] };
        }
        if (title is not null) item = WithName(item, title, folder, warnings);
        return (type, item);
    }

    static (string Text, HashSet<string> Flags, double[] Ranges) ReadTags(string raw, int selectedVariant)
    {
        var flags = new HashSet<string>(StringComparer.Ordinal);
        double[] ranges = [];
        foreach (Match tag in Tags.Matches(raw))
        {
            string name = tag.Groups[1].Value, value = tag.Groups[2].Value;
            if (name == "range")
            {
                ranges = value.Split(',').Select(v => double.TryParse(v, NumberStyles.Float, Invariant, out double r) && double.IsFinite(r) && r >= 0 && r <= 1
                    ? r : throw new ArgumentException($"invalid roll range '{value}'")).ToArray();
            }
            else if (name == "variant")
            {
                if (!value.Split(',').Any(v => int.TryParse(v, out int variant) && variant == selectedVariant)) flags.Add("excluded");
            }
            else if (name == "disabled") flags.Add("excluded");
            else if (name != "tags") flags.Add(name);
        }
        return (Tags.Replace(raw, "").Trim(), flags, ranges);
    }

    static int Number(string value, string field, int min, int max) =>
        int.TryParse(value, NumberStyles.Integer, Invariant, out int n) && n >= min && n <= max
            ? n : throw new ArgumentException($"{field} must be {min}..{max}, got '{value}'");

    // PoB omits hidden zero-ES modifiers and zero-valued defence lines from these unique exports.
    // Restore only verified native identities, before matching displayed defences or level requirements.
    static Item WithHiddenDefenceMod(Item item, ItemCatalog.BaseType type, string? title, ModCatalog mods)
    {
        if (item.Rarity != ItemRarity.Unique) return item;
        var identity = title switch
        {
            "Tabula Rasa" => (Base: "BodyInt1", Mod: "RemoveStatsFromTabulaRasa"),
            "Skin of the Loyal" or "Skin of the Lords" => (Base: "BodyInt1", Mod: "RemoveStatsUnique__1"),
            "The Apostate" => (Base: "BodyInt7", Mod: "RemoveStatsUnique__2"),
            _ => default,
        };
        if (identity.Mod is null) return item;
        if (type.Id != "Metadata/Items/Armours/BodyArmours/" + identity.Base)
            throw new ArgumentException($"unique '{title}' does not match this native base");
        var mod = mods.ById(identity.Mod);
        if (mod is null || mod.Stats.Count != 1
            || mod.Stats[0].Id != "local_unique_tabula_rasa_no_requirement_or_energy_shield"
            || mod.Stats[0].Min != 1 || mod.Stats[0].Max != 1)
            throw new ArgumentException($"the hidden defence modifier for '{title}' is unavailable in this client's data");
        return item.Mods.Any(m => m.Hash == mod.Hash) ? item
            : item with { Mods = [.. item.Mods, new(ModSection.Hidden, mod.Hash, [1])] };
    }

    static Item WithName(Item item, string title, string folder, List<string> warnings)
    {
        string path = Path.Combine(folder, "tables", "Words.json");
        if (!File.Exists(path)) throw new ArgumentException("tables/Words.json is required to preserve the item's name");
        using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
        var words = doc.RootElement.EnumerateArray().Select(r => (Text: r.GetProperty("Text").GetString() ?? "", Hash: r.GetProperty("HASH32").GetUInt32(),
            List: r.GetProperty("Wordlist").GetInt32(), Row: r)).ToArray();
        if (item.Rarity == ItemRarity.Unique)
        {
            var names = words.Where(w => w.List == 6 && w.Text == title).ToArray();
            if (names.Length != 1) throw new ArgumentException($"unique name '{title}' cannot be identified in this client");
            string uniquePath = Path.Combine(folder, "tables", "UniqueStashLayout.json"), visualPath = Path.Combine(folder, "tables", "ItemVisualIdentity.json");
            if (!File.Exists(uniquePath)) throw new ArgumentException("tables/UniqueStashLayout.json is required for unique item artwork");
            int index = Array.IndexOf(words, names[0]);
            using var uniques = JsonDocument.Parse(File.ReadAllBytes(uniquePath));
            var visuals = uniques.RootElement.EnumerateArray().Where(r => r.GetProperty("WordsKey").GetInt32() == index
                && (!r.TryGetProperty("IsAlternateArt", out var alternate) || !alternate.GetBoolean()))
                .Select(r => r.GetProperty("ItemVisualIdentityKey").GetInt32()).Distinct().ToArray();
            if (visuals.Length != 1) throw new ArgumentException($"unique artwork for '{title}' is unavailable or ambiguous");
            using var identities = JsonDocument.Parse(File.ReadAllBytes(visualPath));
            return item with { NameWords = [names[0].Hash], VisualIdentity = (ushort)identities.RootElement[visuals[0]].GetProperty("HASH16").GetInt32() };
        }
        foreach (var prefix in words.Where(w => w.List == 1 && title.StartsWith(w.Text, StringComparison.Ordinal)))
            foreach (var suffix in words.Where(w => w.List == 2 && prefix.Text + w.Text == title))
                return item with { NameWords = [prefix.Hash, suffix.Hash] };
        warnings.Add($"Rare item name '{title}' has no native Words.dat name; its base name is used, with its stats preserved.");
        return item;
    }

    static ItemMod ResolveMod(ParsedMod line, ItemCatalog.BaseType type, Item item, ItemCatalog catalog)
    {
        var values = Translate(line.Text, type);
        if (values is null) throw new ArgumentException($"unsupported {line.Section.ToString().ToLowerInvariant()} modifier '{line.Text}'");
        IEnumerable<ModCatalog.Mod> pool;
        if (line.Section == ModSection.Implicit && type.Implicits is { Count: > 0 }) pool = type.Implicits;
        else pool = catalog.Mods.All.Where(m =>
            line.Section == ModSection.Enchant ? m.GenerationType == 10
            : line.Crafted ? m.Domain == 9 && m.GenerationType is 1 or 2
            : item.Rarity == ItemRarity.Unique ? m.Domain == type.ModDomain && m.GenerationType == 3
            : line.Section == ModSection.Implicit ? m.Domain == type.ModDomain && m.GenerationType == 3
            : m.Domain == type.ModDomain && m.GenerationType is 1 or 2 && m.Level <= item.ItemLevel && m.WeightFor(type.Tags) > 0);
        var candidates = pool.Where(m => m.Stats.Count == values.Count
            && m.Stats.All(s => values.Any(v => v.Id == s.Id && v.Value >= Math.Min(s.Min, s.Max) && v.Value <= Math.Max(s.Min, s.Max))))
            .OrderBy(m => m.Stats.Sum(s => (long)Math.Abs(s.Max - s.Min))).ThenByDescending(m => m.Level).ThenBy(m => m.Id, StringComparer.Ordinal).ToArray();
        if (candidates.Length == 0)
            throw new ArgumentException($"no native modifier with matching stats and rolls for '{line.Text}' on {type.Name} at item level {item.ItemLevel}");
        var chosen = candidates[0];
        return new(line.Section, chosen.Hash, [.. chosen.Stats.Select(s => values.Single(v => v.Id == s.Id).Value)]);
    }

    // Deliberately bounded: these unambiguous English descriptions map to native Stats.dat keys. Complex/conditional
    // descriptions fail closed. Mod IDs, tiers and allowed rolled values still come from the installed client's tables.
    static IReadOnlyList<StatValue>? Translate(string text, ItemCatalog.BaseType type)
    {
        bool weapon = type.Components.Contains("Weapon");
        bool armour = type.Components.Contains("Armour");
        bool flask = type.Components.Contains("Flask");
        Match Match(string pattern) => Regex.Match(text, "^" + pattern + "$", RegexOptions.CultureInvariant);
        IReadOnlyList<StatValue> One(string key, Match m) => [new(key, int.Parse(m.Groups[1].Value, Invariant))];
        if (armour && text.Equals("Has no Energy Shield", StringComparison.OrdinalIgnoreCase))
            return [new("local_no_energy_shield", 1)];
        if (Match(@"(?i:(\d+)% increased maximum Energy Shield if Corrupted)") is { Success: true } conditionalES)
            return One("local_maximum_energy_shield_+%_if_item_corrupted", conditionalES);
        if (text.Equals("Gain Maximum Life instead of Maximum Energy Shield from Equipped Armour Items", StringComparison.OrdinalIgnoreCase))
            return [new("gain_maximum_life_instead_of_maximum_es_from_armour", 1)];
        if (Match(@"(?i:Gain Maximum Life instead of Maximum Energy Shield from Equipped Armour Items at (\d+)% of (?:the|its) value)") is { Success: true } conversion)
            return One("gain_maximum_life_instead_of_maximum_es_from_armour_at_x%_of_the_value", conversion);
        var flat = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["maximum Life"] = "base_maximum_life", ["maximum Mana"] = "base_maximum_mana",
            ["Strength"] = "additional_strength", ["Dexterity"] = "additional_dexterity", ["Intelligence"] = "additional_intelligence",
            ["all Attributes"] = "additional_all_attributes", ["Accuracy Rating"] = weapon ? "local_accuracy_rating" : "accuracy_rating",
            ["Armour"] = armour ? "local_base_physical_damage_reduction_rating" : "base_physical_damage_reduction_rating",
            ["Evasion Rating"] = armour ? "local_base_evasion_rating" : "base_evasion_rating",
            ["maximum Energy Shield"] = armour ? "local_energy_shield" : "base_maximum_energy_shield",
        };
        foreach (var (label, stat) in flat)
            if (Match(@"\+?(-?\d+) to " + Regex.Escape(label)) is { Success: true } m) return One(stat, m);
        var increased = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["maximum Life"] = "maximum_life_+%", ["maximum Mana"] = "maximum_mana_+%",
            ["Spell Damage"] = "spell_damage_+%", ["Physical Damage"] = weapon ? "local_physical_damage_+%" : "physical_damage_+%",
            ["Fire Damage"] = "fire_damage_+%", ["Cold Damage"] = "cold_damage_+%", ["Lightning Damage"] = "lightning_damage_+%",
            ["Chaos Damage"] = "chaos_damage_+%", ["Elemental Damage"] = "elemental_damage_+%",
            ["Attack Speed"] = weapon ? "local_attack_speed_+%" : "attack_speed_+%", ["Cast Speed"] = "base_cast_speed_+%",
            ["Movement Speed"] = "base_movement_velocity_+%", ["Critical Strike Chance"] = weapon ? "local_critical_strike_chance_+%" : "critical_strike_chance_+%",
            ["Global Critical Strike Chance"] = "critical_strike_chance_+%", ["Critical Strike Chance for Spells"] = "spell_critical_strike_chance_+%",
            ["Armour"] = armour ? "local_physical_damage_reduction_rating_+%" : "physical_damage_reduction_rating_+%",
            ["Evasion Rating"] = armour ? "local_evasion_rating_+%" : "evasion_rating_+%",
            ["Energy Shield"] = armour ? "local_energy_shield_+%" : "maximum_energy_shield_+%",
            ["Armour and Evasion"] = armour ? "local_armour_and_evasion_+%" : "armour_and_evasion_+%",
            ["Armour and Energy Shield"] = armour ? "local_armour_and_energy_shield_+%" : "armour_and_energy_shield_+%",
            ["Evasion and Energy Shield"] = armour ? "local_evasion_and_energy_shield_+%" : "evasion_and_energy_shield_+%",
            ["Flask Charges gained"] = "flask_charges_gained_+%", ["Flask Effect Duration"] = "flask_duration_+%",
        };
        foreach (var (label, stat) in increased)
            if (Match(@"(-?\d+)% increased " + Regex.Escape(label)) is { Success: true } m) return One(stat, m);
        if (flask)
        {
            var flaskStats = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Charge Recovery"] = "local_charges_added_+%", ["Charges per use"] = "local_charges_used_+%",
                ["Duration"] = "local_flask_duration_+%", ["Amount Recovered"] = "local_flask_amount_to_recover_+%",
                ["Recovery rate"] = "local_flask_recovery_speed_+%", ["effect"] = "local_flask_effect_+%",
            };
            foreach (var (label, stat) in flaskStats)
            {
                if (Match(@"(\d+)% increased " + Regex.Escape(label)) is { Success: true } inc) return One(stat, inc);
                if (Match(@"(\d+)% reduced " + Regex.Escape(label)) is { Success: true } dec)
                    return [new(stat, -int.Parse(dec.Groups[1].Value, Invariant))];
            }
            if (Match(@"\+(\d+) to Maximum Charges") is { Success: true } charges) return One("local_extra_max_charges", charges);
            if (text == "Instant Recovery") return [new("local_flask_recovers_instantly", 1)];
            if (Match(@"(\d+)% of Recovery applied Instantly") is { Success: true } instant)
                return One("local_flask_recovery_amount_%_to_recover_instantly", instant);
        }
        foreach (string element in new[] { "Fire", "Cold", "Lightning", "Chaos" })
            if (Match(@"\+?(-?\d+)% to " + element + " Resistance") is { Success: true } m)
                return One("base_" + element.ToLowerInvariant() + "_damage_resistance_%", m);
        if (Match(@"\+?(-?\d+)% to all Elemental Resistances") is { Success: true } all) return One("base_resist_all_elements_%", all);
        if (Match(@"\+?(-?\d+)% to Global Critical Strike Multiplier") is { Success: true } crit) return One("base_critical_strike_multiplier_+", crit);
        foreach (string element in new[] { "Physical", "Fire", "Cold", "Lightning", "Chaos" })
        {
            bool spells = text.EndsWith(" to Spells", StringComparison.Ordinal);
            if (Match(@"Adds (\d+) to (\d+) " + element + " Damage" + (spells ? " to Spells" : "")) is not { Success: true } m) continue;
            string prefix = spells ? "spell_" : weapon ? "local_" : "";
            string damage = element.ToLowerInvariant();
            return [new(prefix + "minimum_added_" + damage + "_damage", int.Parse(m.Groups[1].Value, Invariant)),
                new(prefix + "maximum_added_" + damage + "_damage", int.Parse(m.Groups[2].Value, Invariant))];
        }
        return null;
    }
}
