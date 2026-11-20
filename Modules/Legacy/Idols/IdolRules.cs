using GameServer.Core;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Modules.Idols;

/// <summary>What an Idol is, where the placed ones are, what they add to a map and how they drop.</summary>
public static class IdolRules
{
    /// <summary>The ItemClasses Id of the Idols (row 94, "Idols").</summary>
    public const string ItemClass = "AtlasRelic";

    /// <summary>league_enable_atlas_relics: the area stat of a league with Idols (Mods row LeagueAtlasRelics).</summary>
    public const string LeagueStat = "league_enable_atlas_relics";

    /// <summary>The Idol inventory: Inventories row 62, MapDeviceStorage1, the 4×5 grid of the Atlas screen's side
    /// panel. The client's own Idol inventories (rows 89 to 91, MapRelics1 to 3) have no panel in the 3.29.3 clients.</summary>
    public const byte Row = 62;

    /// <summary>The inventory kind the row is registered with. Under its own kind (0x3d) the client refuses an Idol,
    /// and under the Idols' (0x3b) it allows only the cells of MapRelicInventoryLayout, 11 of the panel's 20. Kind 0x0f
    /// (the generic one of the crafting slots) takes any item in any cell, so the server refuses what is not an Idol.</summary>
    public const byte PanelKind = 0x0f;

    public static bool IsIdol(ItemCatalog.BaseType? type) => type?.Class == ItemClass;

    /// <summary>The identified Idols in the Idol inventory: an unidentified one has no effect, as its modifiers are not known.</summary>
    public static IReadOnlyList<Item> Placed(PlayerInventories items, ItemCatalog catalog) =>
        [.. items.Items(Row).Select(i => i.Item).Where(i => i.Identified && IsIdol(catalog.ByHash(i.TypeHash)))];

    // UniqueMapRelicLimits: how many of a unique Idol may lie in the Idol inventory, by the name's Words HASH32, which
    // is what a unique item carries as its name.
    static readonly Lazy<Dictionary<uint, int>> UniqueLimits = new(() =>
    {
        var limits = new Dictionary<uint, int>();
        if (!ClientTables.Has("UniqueMapRelicLimits") || !ClientTables.Has("Words")) return limits;
        var words = ClientTables.Table("Words");
        foreach (var row in ClientTables.Table("UniqueMapRelicLimits"))
            if (row.Int("Idol") is int word && word >= 0 && word < words.Count)
                limits[words[word].UInt("HASH32")] = row.Int("Limit");
        return limits;
    });

    /// <summary>Why this Idol may not join the ones placed, or null: a unique Idol is limited to its
    /// UniqueMapRelicLimits count (one of each). When <paramref name="replaced"/> is the item being swapped out of the
    /// Idol inventory onto the cursor, it is not counted toward the limit.</summary>
    public static string? LimitRefusal(PlayerInventories items, Item idol, InventoryItem? replaced = null)
    {
        if (idol.Rarity != ItemRarity.Unique || idol.NameWords.Count == 0
            || !UniqueLimits.Value.TryGetValue(idol.NameWords[0], out int limit)) return null;
        int placed = items.Items(Row).Count(i =>
            (replaced is null || i.Id != replaced.Id)
            && i.Item.Rarity == ItemRarity.Unique && i.Item.NameWords.Contains(idol.NameWords[0]));
        return placed >= limit ? $"this unique Idol is limited to {limit}" : null;
    }

    /// <summary>The map's modifiers with the Idols' stats added: unscaled, as the device's fragments are, so the map's
    /// modifier effect does not multiply them.</summary>
    public static MapMods With(MapMods mods, IReadOnlyDictionary<string, int> idols)
    {
        static Dictionary<string, int> Added(IReadOnlyDictionary<string, int> to, IReadOnlyDictionary<string, int> more)
        {
            var sum = new Dictionary<string, int>(to);
            foreach (var (stat, value) in more)
                sum[stat] = (int)Math.Clamp((long)sum.GetValueOrDefault(stat) + value, int.MinValue, int.MaxValue);
            return sum;
        }
        // A map without a breakdown is composed from its flat stats alone: it stays without one.
        bool flat = mods.ExplicitMods.Count == 0 && mods.Implicit.Count == 0 && mods.Fragments.Count == 0;
        return new MapMods(Added(mods.Stats, idols), mods.Quality)
        {
            ExplicitMods = mods.ExplicitMods, Implicit = mods.Implicit,
            Fragments = flat ? mods.Fragments : Added(mods.Fragments, idols),
            FragmentCount = mods.FragmentCount, ScarabCount = mods.ScarabCount, ScarabKinds = mods.ScarabKinds,
            MapRarity = mods.MapRarity, Corrupted = mods.Corrupted,
        };
    }

    // GGG does not publish the Idols' drop rates: these are the server's own. A kill in a map drops an Idol with this
    // chance in 10,000 by the monster's rarity, three in four magic and the rest rare, the small bases more often.
    // One Idol in UniqueShare is a unique one instead, each as likely as the next.
    const int UniqueShare = 50;
    static int Chance(MonsterRarity rarity) => rarity switch
    {
        MonsterRarity.Unique => 3000, MonsterRarity.Rare => 500, MonsterRarity.Magic => 120, _ => 40,
    };

    static readonly (string Id, int Weight)[] Bases =
    [
        ("Metadata/Items/Relics/AtlasRelic1x1", 40), ("Metadata/Items/Relics/AtlasRelic1x2", 20),
        ("Metadata/Items/Relics/AtlasRelic2x1", 20), ("Metadata/Items/Relics/AtlasRelic1x3", 8),
        ("Metadata/Items/Relics/AtlasRelic3x1", 8), ("Metadata/Items/Relics/AtlasRelic2x2", 4),
    ];

    /// <summary>The Idol a kill drops, unidentified, or null: a base whose drop level the monster has reached, at the
    /// monster's level.</summary>
    public static Item? Drop(ItemCatalog catalog, ItemRolls rolls, UniqueCatalog uniques, int level, MonsterRarity rarity, double rate, Random random)
    {
        if (random.NextDouble() * 10_000 >= Chance(rarity) * rate) return null;
        if (random.Next(UniqueShare) == 0 && Unique(catalog, uniques, level, random) is { } unique) return unique;
        var bases = Bases.Select(b => (Type: catalog.Find(b.Id), b.Weight))
            .Where(b => b.Type is { Supported: true } type && type.DropLevel <= level).ToList();
        int total = bases.Sum(b => b.Weight);
        if (total == 0) return null;
        int pick = random.Next(total);
        var chosen = bases.First(b => (pick -= b.Weight) < 0).Type!;
        var item = ItemCatalog.Create(chosen, random: random) with { ItemLevel = (byte)Math.Clamp(level, 1, 100) };
        return rolls.Roll(item, random.Next(4) == 0 ? ItemRarity.Rare : ItemRarity.Magic, random) with { Identified = false };
    }

    /// <summary>A unique Idol, unidentified: one of the uniques on an Idol base whose drop level the monster has reached.</summary>
    public static Item? Unique(ItemCatalog catalog, UniqueCatalog uniques, int level, Random random)
    {
        var pool = uniques.All.Where(d => catalog.Find(d.BaseTypeId) is { Supported: true } type && IsIdol(type) && type.DropLevel <= level).ToList();
        if (pool.Count == 0 || uniques.Create(pool[random.Next(pool.Count)], random) is not { } made) return null;
        return made with { ItemLevel = (byte)Math.Clamp(level, 1, 100), Identified = false };
    }

    // ---- Gambling ------------------------------------------------------------------------------------------------------

    /// <summary>Faustus, in Kingsmarch and in the hideout: the Idols are gambled at the Kingsmarch black market only.</summary>
    public static bool GamblesIdols(string npc) => npc.StartsWith("Metadata/NPC/League/Kalguur/VillageFaustus", StringComparison.Ordinal);

    // The client's "Random … Idol" rows (HiddenItem, BaseItemTypes 5236 to 5241) and the Idol each gives.
    static readonly (string Shown, string Idol)[] GambleBases =
    [
        ("Metadata/Items/Relics/AtlasRelic1x1Gamble", "Metadata/Items/Relics/AtlasRelic1x1"),
        ("Metadata/Items/Relics/AtlasRelic1x2Gamble", "Metadata/Items/Relics/AtlasRelic1x2"),
        ("Metadata/Items/Relics/AtlasRelic2x1Gamble", "Metadata/Items/Relics/AtlasRelic2x1"),
        ("Metadata/Items/Relics/AtlasRelic1x3Gamble", "Metadata/Items/Relics/AtlasRelic1x3"),
        ("Metadata/Items/Relics/AtlasRelic3x1Gamble", "Metadata/Items/Relics/AtlasRelic3x1"),
        ("Metadata/Items/Relics/AtlasRelic2x2Gamble", "Metadata/Items/Relics/AtlasRelic2x2"),
    ];

    // GoldPriceMultiFromTag: the client's gold value multiplier of each Idol size (atlas_relic_small 1.2 to
    // atlas_relic_very_large 2.5), by Tags Id.
    static readonly Lazy<Dictionary<string, double>> GoldMultipliers = new(() =>
    {
        var result = new Dictionary<string, double>(StringComparer.Ordinal);
        if (!ClientTables.Has("GoldPriceMultiFromTag") || !ClientTables.Has("Tags")) return result;
        var tags = ClientTables.Table("Tags");
        foreach (var row in ClientTables.Table("GoldPriceMultiFromTag"))
            if (row.IntOrNull("Tag") is int tag && tags.Row(tag)?.Text("Id") is { } id) result[id] = row.Real("Multiplier");
        return result;
    });

    /// <summary>
    /// The gold price of gambling for an Idol. GGG's is not published: ours is the 3.29 ring price at the window's
    /// pricing level (poewiki's formula, 90.74 × 1.047^L, L from 4 to 83) times the Idol size's GoldPriceMultiFromTag
    /// multiplier.
    /// </summary>
    public static uint GamblePrice(ItemCatalog.BaseType idol, int priceLevel)
    {
        double multiplier = idol.Tags.Select(t => GoldMultipliers.Value.GetValueOrDefault(t)).DefaultIfEmpty(0).Max();
        double ring = 90.74 * Math.Pow(1.047, Math.Clamp(priceLevel, 4, 83));
        return (uint)Math.Max(1, Math.Truncate(ring * (multiplier > 0 ? multiplier : 1)));
    }

    /// <summary>The Idol offers of a gamble window: the sizes whose drop level the character has reached, priced at
    /// <paramref name="priceLevel"/> (defaulting to <paramref name="characterLevel"/>).</summary>
    public static IEnumerable<(ItemCatalog.BaseType Shown, ItemCatalog.BaseType Idol, uint Gold)> GambleOffers(
        ItemCatalog catalog, int characterLevel, int? priceLevel = null)
    {
        int pricing = Math.Max(characterLevel, priceLevel ?? characterLevel);
        foreach (var (shown, idol) in GambleBases)
            if (catalog.Find(shown) is { Supported: true } s && s.Id == shown && catalog.Find(idol) is { Supported: true } i && i.Id == idol
                && i.DropLevel <= characterLevel)
                yield return (s, i, GamblePrice(i, pricing));
    }

    /// <summary>
    /// A gambled Idol of this base, identified: one in <see cref="UniqueShare"/> a unique of the base when it has one, else
    /// two in three rare (gambling gives rares more often than drops do) and the rest magic, at the character's level
    /// (68 at least, 83 at most) plus 0 to 2.
    /// </summary>
    public static Item? Gamble(ItemCatalog catalog, ItemRolls rolls, UniqueCatalog uniques, ItemCatalog.BaseType idol, int characterLevel, Random random)
    {
        int itemLevel = Math.Clamp(characterLevel, idol.DropLevel, 83) + random.Next(3);
        if (random.Next(UniqueShare) == 0)
        {
            var pool = uniques.All.Where(d => d.BaseTypeId == idol.Id).ToList();
            if (pool.Count > 0 && uniques.Create(pool[random.Next(pool.Count)], random) is { } made)
                return made with { ItemLevel = (byte)itemLevel, Identified = true };
        }
        var item = ItemCatalog.Create(idol, random: random) with { ItemLevel = (byte)itemLevel };
        return rolls.Roll(item, random.Next(3) == 0 ? ItemRarity.Magic : ItemRarity.Rare, random) with { Identified = true };
    }
}
