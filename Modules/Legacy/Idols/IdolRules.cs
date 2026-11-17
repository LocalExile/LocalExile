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
    public static Item? Drop(ItemCatalog catalog, ItemRolls rolls, int level, MonsterRarity rarity, double rate, Random random)
    {
        if (random.NextDouble() * 10_000 >= Chance(rarity) * rate) return null;
        var bases = Bases.Select(b => (Type: catalog.Find(b.Id), b.Weight))
            .Where(b => b.Type is { Supported: true } type && type.DropLevel <= level).ToList();
        int total = bases.Sum(b => b.Weight);
        if (total == 0) return null;
        int pick = random.Next(total);
        var chosen = bases.First(b => (pick -= b.Weight) < 0).Type!;
        var item = ItemCatalog.Create(chosen, random: random) with { ItemLevel = (byte)Math.Clamp(level, 1, 100) };
        return rolls.Roll(item, random.Next(4) == 0 ? ItemRarity.Rare : ItemRarity.Magic, random) with { Identified = false };
    }
}
