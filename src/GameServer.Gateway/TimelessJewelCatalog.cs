using System.Text.Json;
using GameServer.Core.Protocol.Instance;

/// <summary>
/// The six Legion timeless jewels: their unique name, base type, faction, mod row, real seed range and current
/// conquerors. The item carries one <c>UniqueJewelAlternateTreeInRadius*</c> mod whose six values tell the client which
/// alternate tree to build (see Agents/docs/game/timeless-jewels.md). Militant Faith also carries two random
/// per-10-Devotion mods, as the real item does.
/// </summary>
static class TimelessJewelCatalog
{
    public const string BaseId = "Metadata/Items/Jewels/JewelTimeless";

    public sealed record Conqueror(string Name, int Index, int Version);
    public sealed record Entry(string Name, int TreeVersion, string Faction, string ModId, int MinSeed, int MaxSeed,
        int DisplayMultiplier, Conqueror[] Conquerors)
    {
        public int DisplayedMinSeed => MinSeed * DisplayMultiplier;
        public int DisplayedMaxSeed => MaxSeed * DisplayMultiplier;
        public bool IsElegantHubris => DisplayMultiplier > 1;
    }

    internal static readonly Entry[] Seed =
    [
        new("Glorious Vanity", 1, "Vaal", "UniqueJewelAlternateTreeInRadiusVaal", 100, 8000, 1,
            [new("Xibaqua", 1, 0), new("Ahuana", 2, 1), new("Doryani", 3, 0)]),
        new("Lethal Pride", 2, "Karui", "UniqueJewelAlternateTreeInRadiusKarui", 10000, 18000, 1,
            [new("Kaom", 1, 0), new("Rakiata", 2, 0), new("Akoya", 3, 1)]),
        new("Brutal Restraint", 3, "Maraketh", "UniqueJewelAlternateTreeInRadiusMaraketh", 500, 8000, 1,
            [new("Balbala", 1, 1), new("Asenath", 2, 0), new("Nasima", 3, 0)]),
        new("Militant Faith", 4, "Templar", "UniqueJewelAlternateTreeInRadiusTemplar", 2000, 10000, 1,
            [new("Maxarius", 1, 1), new("Dominus", 2, 0), new("Avarius", 3, 0)]),
        new("Elegant Hubris", 5, "Eternal", "UniqueJewelAlternateTreeInRadiusEternal", 100, 8000, 20,
            [new("Cadiro", 1, 0), new("Victario", 2, 0), new("Caspiro", 3, 1)]),
        new("Heroic Tragedy", 6, "Kalguur", "UniqueJewelAlternateTreeInRadiusKalguur", 100, 8000, 1,
            [new("Vorana", 1, 0), new("Uhtred", 2, 0), new("Medved", 3, 0)]),
    ];

    public static Entry[] All => GameDataCatalog.TimelessJewels;

    public static string[] Names => [.. All.Select(entry => entry.Name)];

    public static Entry? Find(string query) =>
        All.FirstOrDefault(entry => entry.Name.Equals(query, StringComparison.OrdinalIgnoreCase))
        ?? All.FirstOrDefault(entry => entry.Name.Contains(query, StringComparison.OrdinalIgnoreCase));

    public static Entry? ForTreeVersion(int version) => All.FirstOrDefault(entry => entry.TreeVersion == version);

    public static Conqueror? FindConqueror(Entry entry, string text) =>
        int.TryParse(text, out int index)
            ? entry.Conquerors.FirstOrDefault(c => c.Index == index)
            : entry.Conquerors.FirstOrDefault(c => c.Name.Equals(text, StringComparison.OrdinalIgnoreCase))
              ?? entry.Conquerors.FirstOrDefault(c => c.Name.Contains(text, StringComparison.OrdinalIgnoreCase));

    /// <summary>The item a timeless jewel is: the unique base type, regardless of whether its mod is readable.</summary>
    public static bool IsTimeless(Item item, ItemCatalog items) =>
        items.ByHash(item.TypeHash)?.Id == BaseId;

    /// <summary>Create a timeless jewel. <paramref name="displayedSeed"/> is what the tooltip or a calculator shows:
    /// Elegant Hubris multiplies its stored 100..8000 seed by 20, the other jewels do not. <paramref name="random"/>
    /// lets a caller (a test) make the seed and the Militant Faith devotion mods deterministic.</summary>
    public static Item? Create(ItemCatalog items, string name, int? displayedSeed = null, int? conquerorIndex = null,
        string folder = "data", Random? random = null)
    {
        if (Find(name) is not { } entry) return null;
        if (items.Find("Timeless Jewel") is not { } baseType) return null;
        if (items.Mods.ById(entry.ModId) is not { Stats.Count: 6 } mod) return null;
        if (UniqueJewelVisuals.Of(entry.Name) is not ushort visual) return null;
        if (WordHash(entry.Name, folder) is not uint word) return null;

        var rng = random ?? Random.Shared;
        int shown;
        int real;
        if (displayedSeed is int asked)
        {
            if (asked < entry.DisplayedMinSeed || asked > entry.DisplayedMaxSeed) return null;
            if (entry.DisplayMultiplier > 1 && asked % entry.DisplayMultiplier != 0) return null;
            shown = asked;
            real = asked / entry.DisplayMultiplier;
        }
        else
        {
            real = rng.Next(entry.MinSeed, entry.MaxSeed + 1);
            shown = real * entry.DisplayMultiplier;
        }

        var conqueror = conquerorIndex is int index
            ? entry.Conquerors.FirstOrDefault(c => c.Index == index)
            : entry.Conquerors[0];
        if (conqueror is null) return null;

        var mods = new List<ItemMod>
        {
            // The internal revision is the item-data generation, not the conqueror version: current items are 1.
            // The client picks the highest alternate keystone version <= this value (so v0-only conquerors still work).
            new ItemMod(ModSection.Explicit, mod.Hash,
                [entry.TreeVersion, real, conqueror.Index, 1500, 1, 1]),
        };
        if (entry.Name == "Militant Faith")
        {
            // Militant Faith also carries two distinct per-10-Devotion mods. The client keeps the pre-3.29.0
            // "1% reduced Mana Cost of Skills" row for legacy items; current items roll the Mana Cost Efficiency row.
            var pool = items.Mods.All
                .Where(m => m.GenerationType == 3 && m.Id.Contains("PerDevotion", StringComparison.Ordinal)
                    && m.Id != "ReducedManaCostPerDevotion")
                .ToArray();
            if (pool.Length < 2) return null;
            int first = rng.Next(pool.Length);
            int second = rng.Next(pool.Length - 1);
            if (second >= first) second++;
            mods.Add(pool[first].Roll(ModSection.Explicit, rng));
            mods.Add(pool[second].Roll(ModSection.Explicit, rng));
        }

        return ItemCatalog.Create(baseType) with
        {
            Rarity = ItemRarity.Unique,
            Identified = true,
            ItemLevel = 20,
            VisualIdentity = visual,
            NameWords = [word],
            Mods = mods,
        };
    }

    /// <summary>Read the alternate-tree values from an item's mod, or null when it is not a readable timeless jewel.
    /// The stat keys are 0-based Stats.dat row indices in Mods.dat, which ModCatalog has already resolved by Id.</summary>
    public static TimelessSpec? Read(Item item, ModCatalog mods)
    {
        foreach (var mod in item.Mods)
        {
            if (mods.ByHash(mod.Hash) is not { } row) continue;
            int? Value(string id)
            {
                for (int i = 0; i < row.Stats.Count && i < mod.Values.Count; i++)
                    if (row.Stats[i].Id == id) return mod.Values[i];
                return null;
            }

            if (Value("local_unique_jewel_alternate_tree_version") is not int version
                || Value("local_unique_jewel_alternate_tree_seed") is not int seed
                || Value("local_unique_jewel_alternate_tree_keystone") is not int keystone
                || Value("local_jewel_effect_base_radius") is not int radius) continue;
            if (ForTreeVersion(version) is not { } entry || seed < entry.MinSeed || seed > entry.MaxSeed
                || entry.Conquerors.FirstOrDefault(c => c.Index == keystone) is not { } conqueror) continue;
            int revision = Value("local_unique_jewel_alternate_tree_internal_revision") ?? 1;
            string name = revision == 0 ? LegacyName(entry, keystone) ?? conqueror.Name : conqueror.Name;
            return new TimelessSpec(entry.Name, version, entry.Faction, seed, seed * entry.DisplayMultiplier,
                keystone, name, revision == 0 ? 0 : conqueror.Version, radius);
        }
        return null;
    }

    /// <summary>The pre-3.11 conqueror that uses the v0 row at a given index. A legacy item's keystone stat is only the
    /// index; the current name at that index belongs to the v1 item.</summary>
    static string? LegacyName(Entry entry, int keystone) => (entry.TreeVersion, keystone) switch
    {
        (1, 2) => "Zerphi",
        (2, 3) => "Kiloava",
        (3, 1) => "Deshret",
        (4, 1) => "Venarius",
        (5, 3) => "Chitus",
        _ => null,
    };

    static readonly Lazy<Dictionary<string, uint>> NameWords = new(() => LoadNameWords("data"));

    static uint? WordHash(string name, string folder) =>
        folder == "data" ? NameWords.Value.GetValueOrDefault(name) : LoadNameWords(folder).GetValueOrDefault(name);

    static Dictionary<string, uint> LoadNameWords(string folder)
    {
        var result = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
        string path = Path.Combine(folder, "tables", "Words.json");
        if (!File.Exists(path)) return result;
        using var d = JsonDocument.Parse(File.ReadAllBytes(path));
        foreach (var row in d.RootElement.EnumerateArray())
            if (row.GetProperty("Wordlist").GetInt32() == 6)
                result[row.GetProperty("Text").GetString() ?? ""] = row.GetProperty("HASH32").GetUInt32();
        return result;
    }
}

/// <summary>The values a socketed timeless jewel contributes to the passive tree. <see cref="Seed"/> is the real
/// generation seed; <see cref="DisplayedSeed"/> is what the item tooltip shows (Elegant Hubris ×20).</summary>
sealed record TimelessSpec(string Name, int TreeVersion, string Faction, int Seed, int DisplayedSeed,
    int ConquerorIndex, string ConquerorName, int ConquerorVersion, int Radius);
