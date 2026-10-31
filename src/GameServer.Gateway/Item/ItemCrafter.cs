using System.Text.Json;
using System.Text.Json.Nodes;
using GameServer.Core.Protocol.Instance;

/// <summary>
/// The item crafter of tools such as the desktop launcher (Agents/docs/dev/item-crafter.md): <see cref="Catalog"/> is what
/// the Gateway's `crafter` mode prints (the bases, the mods each can have, their stat descriptions, the rare name words),
/// and <see cref="Build"/> turns the item a tool composed into an <see cref="Item"/> for /item_give.
/// </summary>
static class ItemCrafter
{
    /// <summary>The influences a tool may switch, by their /item_state names.</summary>
    static readonly ItemInfluence[] Influences =
        [ItemInfluence.Shaper, ItemInfluence.Elder, ItemInfluence.Crusader, ItemInfluence.Redeemer, ItemInfluence.Hunter, ItemInfluence.Warlord];

    /// <summary>Mods.dat GenerationType of corruption implicits (Vaal Orb).</summary>
    const int CorruptedImplicit = 5;

    /// <summary>Bases a tool can craft: modifiable items with mods (equipment, flasks, jewels, maps …), no gems, stacks or
    /// placeholder rows.</summary>
    public static bool Craftable(ItemCatalog items, ItemCatalog.BaseType t) =>
        t.Supported && t.Components.Contains("Mods") && !t.Unmodifiable && t.CanBeFractured && !t.Stackable
        && !t.Name.StartsWith('[') && t.Name.Length > 0 && items.Gem(t) is null;

    /// <summary>
    /// One JSON object per line: "class" (ItemClasses Id and name), "pool" (mod Ids), "mod" (every mod a pool or base
    /// names), "base" (with its pools: affixes, per influence, crafted, corruption and Eldritch implicits), "desc" (the
    /// stat descriptions of those mods' stats) and "word" (rare name words, lists 1 and 2). Affix pools are
    /// <see cref="ItemRolls.Pool(ItemCatalog.BaseType, int, ItemInfluence)"/> at item level 100: a tool hides what
    /// needs a higher item level by each mod's "level".
    /// </summary>
    public static IEnumerable<JsonObject> Catalog(ItemCatalog items, ItemRolls rolls, BenchCrafting bench, StatDescriptions descriptions,
        string folder = "data")
    {
        var classNames = LoadClassNames(folder);
        var bases = items.All.Where(t => Craftable(items, t)).ToList();
        var pools = new Dictionary<string, int>(StringComparer.Ordinal);
        var poolLines = new List<JsonObject>();
        var mods = new Dictionary<string, ModCatalog.Mod>(StringComparer.Ordinal);
        int Pool(IEnumerable<ModCatalog.Mod> list)
        {
            var ids = list.Select(m => { mods.TryAdd(m.Id, m); return m.Id; }).Distinct().ToList();
            string key = string.Join('\n', ids);
            if (pools.TryGetValue(key, out int id)) return id;
            pools[key] = id = pools.Count;
            poolLines.Add(new JsonObject { ["type"] = "pool", ["id"] = id, ["mods"] = new JsonArray([.. ids.Select(i => JsonValue.Create(i))]) });
            return id;
        }

        var crafted = bench.All.Where(r => r.Mod is { Domain: 9 }).ToList();
        var implicitMods = items.Mods.All.Where(m => m.GenerationType is CorruptedImplicit or ItemRolls.ExarchImplicit or ItemRolls.EaterImplicit).ToList();
        // Bases with the same domain, class and tags have the same pools.
        var byShape = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        var baseLines = new List<JsonObject>();
        foreach (var t in bases)
        {
            string shape = $"{t.ModDomain}/{t.ClassRow}/{string.Join(',', t.Tags.Order(StringComparer.Ordinal))}";
            if (!byShape.TryGetValue(shape, out var shared))
            {
                var affixes = rolls.Pool(t, 100).Select(p => p.Mod).ToList();
                var influence = new JsonObject();
                foreach (var i in Influences)
                {
                    var more = rolls.Pool(t, 100, i).Select(p => p.Mod).Except(affixes).ToList();
                    if (more.Count > 0) influence[i.ToString().ToLowerInvariant()] = Pool(more);
                }
                shared = new JsonObject
                {
                    ["pool"] = Pool(affixes),
                    ["influence"] = influence,
                    ["crafted"] = Pool(crafted.Where(r => r.Classes.Contains(t.ClassRow)).Select(r => r.Mod!)),
                    ["corrupted"] = Pool(implicitMods.Where(m => m.GenerationType == CorruptedImplicit && m.Domain == t.ModDomain && m.WeightFor(t.Tags) > 0)),
                    ["eldritch"] = Pool(implicitMods.Where(m => m.GenerationType != CorruptedImplicit && m.Domain == t.ModDomain && m.WeightFor(t.Tags) > 0)),
                };
                byShape[shape] = shared;
            }
            var created = ItemCatalog.Create(t);
            var (str, dex, @int) = items.Requirements(t);
            var line = new JsonObject
            {
                ["type"] = "base",
                ["id"] = t.Id,
                ["name"] = t.Name,
                ["class"] = t.Class,
                ["width"] = t.Width,
                ["height"] = t.Height,
                ["dropLevel"] = t.DropLevel,
                ["requirements"] = new JsonArray(str, dex, @int),
                ["sockets"] = CurrencyEffects.SocketCapacity(created, t),
                ["quality"] = t.Components.Contains("Quality"),
                ["implicits"] = new JsonArray([.. (t.Implicits ?? []).Select(m => { mods.TryAdd(m.Id, m); return JsonValue.Create(m.Id); })]),
            };
            foreach (var (key, value) in shared) line[key] = value?.DeepClone();
            baseLines.Add(line);
        }

        foreach (var c in bases.Select(t => t.Class).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
            yield return new JsonObject { ["type"] = "class", ["id"] = c, ["name"] = classNames.GetValueOrDefault(c, c) };
        foreach (var p in poolLines) yield return p;
        foreach (var m in mods.Values.OrderBy(m => m.Row))
            yield return new JsonObject
            {
                ["type"] = "mod",
                ["id"] = m.Id,
                ["name"] = m.Name,
                ["generation"] = m.GenerationType,
                ["domain"] = m.Domain,
                ["level"] = m.Level,
                ["families"] = new JsonArray([.. m.Families.Select(f => JsonValue.Create(f))]),
                ["tags"] = new JsonArray([.. m.Tags.Order(StringComparer.Ordinal).Select(g => JsonValue.Create(g))]),
                ["stats"] = new JsonArray([.. m.Stats.Select((s, i) => (JsonNode)new JsonObject
                {
                    ["id"] = s.Id, ["min"] = m.RangeAt(i).Min, ["max"] = m.RangeAt(i).Max,
                })]),
            };
        foreach (var b in baseLines) yield return b;
        var described = new HashSet<StatDescriptions.Description>(ReferenceEqualityComparer.Instance);
        foreach (var stat in mods.Values.SelectMany(m => m.Stats).Select(s => s.Id).Distinct(StringComparer.Ordinal))
            if (descriptions.For(stat) is { } d && described.Add(d))
                yield return new JsonObject
                {
                    ["type"] = "desc",
                    ["stats"] = new JsonArray([.. d.Stats.Select(s => JsonValue.Create(s))]),
                    ["variants"] = new JsonArray([.. d.Variants.Select(v => (JsonNode)new JsonObject
                    {
                        ["when"] = new JsonArray([.. v.Conditions.Select(c => JsonValue.Create(c))]),
                        ["text"] = v.Text,
                        ["handlers"] = new JsonArray([.. v.Handlers.Select(h => (JsonNode)new JsonArray(h.Name, h.Stat))]),
                    })]),
                };
        foreach (var w in rolls.Words.Where(w => w.List is 1 or 2))
            yield return new JsonObject { ["type"] = "word", ["list"] = w.List, ["key"] = w.Key, ["text"] = w.Text };
    }

    static Dictionary<string, string> LoadClassNames(string folder)
    {
        string path = Path.Combine(folder, "tables", "ItemClasses.json");
        if (!File.Exists(path)) return [];
        using var d = JsonDocument.Parse(File.ReadAllBytes(path));
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var r in d.RootElement.EnumerateArray())
            if (r.GetProperty("Id").GetString() is { Length: > 0 } id) names.TryAdd(id, r.GetProperty("Name").GetString() ?? id);
        return names;
    }

    /// <summary>What a tool sends with /item_give (one JSON object, Agents/docs/dev/item-crafter.md).</summary>
    public sealed record Spec(string Base, string? Rarity = null, int? ItemLevel = null, int? Quality = null, bool Identified = true,
        IReadOnlyList<string>? States = null, int Fractured = 0, IReadOnlyList<uint>? Name = null, string? Sockets = null,
        IReadOnlyList<SpecMod>? Mods = null);

    /// <param name="Section">implicit, explicit, enchant, scourge or hidden; the mod's own list when missing.</param>
    public sealed record SpecMod(string Id, string? Section = null, IReadOnlyList<int>? Values = null);

    static readonly JsonSerializerOptions SpecJson = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// The item a spec describes, or null with the reason. Mods are taken as given (any mod on any base, values outside
    /// the roll range too): this is a dev tool, as /item_mod is. Missing values are rolled, a rare without a name gets
    /// one, sockets default as for /item ("" for none), and the item level defaults to <paramref name="level"/>.
    /// </summary>
    public static Item? Build(ItemCatalog items, ItemRolls rolls, string json, int level, Random random, out string? error)
    {
        Spec? spec;
        try { spec = JsonSerializer.Deserialize<Spec>(json, SpecJson); }
        catch (JsonException e) { error = $"not an item: {e.Message}"; return null; }
        if (spec is null || string.IsNullOrWhiteSpace(spec.Base)) { error = "which base?"; return null; }
        if (items.Find(spec.Base) is not { } type) { error = $"No item type \"{spec.Base}\""; return null; }
        if (!Craftable(items, type)) { error = $"{type.Name} is not a craftable item"; return null; }
        var rarity = ItemRarity.Normal;
        if (spec.Rarity is { } r && (!Enum.TryParse(r, true, out rarity) || !Enum.IsDefined(rarity))) { error = $"no rarity {r}"; return null; }
        int itemLevel = Math.Clamp(spec.ItemLevel ?? level, 1, 100);
        var (defaultSockets, defaultLinks) = items.DefaultSockets(type, itemLevel, random: random);
        var item = ItemCatalog.Create(type, defaultSockets, defaultLinks) with
        {
            ItemLevel = (byte)itemLevel,
            Quality = (byte)Math.Clamp(spec.Quality ?? 0, 0, 100),
            Rarity = rarity,
            Identified = spec.Identified,
        };
        var mods = new List<ItemMod>();
        foreach (var m in spec.Mods ?? [])
        {
            if (items.Mods.ById(m.Id) is not { } mod) { error = $"No mod \"{m.Id}\" in Mods.dat"; return null; }
            var section = mod.DefaultSection;
            if (m.Section is { } s && !Enum.TryParse(s, true, out section)) { error = $"no mod list {s}"; return null; }
            mods.Add(mod.With(section, m.Values ?? [], random));
        }
        item = item with
        {
            Mods = mods,
            FracturedCount = (byte)Math.Clamp(spec.Fractured, 0, mods.Count(m => m.Section == ModSection.Explicit)),
            NameWords = rarity != ItemRarity.Rare ? [] : spec.Name is [_, _] name ? name : rolls.RareName(type, random),
        };
        foreach (string state in spec.States ?? [])
            if (On(item, state) is { } changed) item = changed;
            else { error = $"no state {state}"; return null; }
        if (spec.Sockets is { } layout)
        {
            if (string.IsNullOrWhiteSpace(layout)) item = item with { Sockets = [], Links = null };
            else if (SocketLayout.Parse(layout.Split(' ', StringSplitOptions.RemoveEmptyEntries), out error) is not var (colours, links)
                || SocketLayout.Apply(item, type, colours, links, out error, SocketLayout.CapacityOverride) is not { } socketed) return null;
            else item = socketed;
        }
        error = null;
        return item;
    }

    /// <summary>The item with a state of /item_state's on (a state named twice stays on), or null for no such state.</summary>
    static Item? On(Item item, string state) => state.ToLowerInvariant() switch
    {
        "corrupted" => item with { Corrupted = true },
        "mirrored" => item with { Mirrored = true },
        "split" => item with { Split = true },
        "synthesised" or "synthesized" => item with { Synthesised = true },
        _ => Influences.FirstOrDefault(i => i.ToString().Equals(state, StringComparison.OrdinalIgnoreCase)) is var i and not ItemInfluence.None
            ? item with { Influences = item.Influences | i } : null,
    };
}
