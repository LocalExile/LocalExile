using GameServer.Core.Protocol.Instance;

/// <summary>
/// What a dying monster drops: a first, basic loot table of our own (the game's drop pools, item rarity and mods come
/// later; Agents/docs/protocol/15-progression.md §3). How many items: normal 1 at 35%, magic 1–2 (one sure), rare 2–4,
/// unique 4–6. Each item is currency (45%: the common orbs and scrolls, weighted), a gem (4%: a creatable gem whose
/// DropLevel is at most the monster's level, unlevelled like a vendor's), a life or mana flask (10%) or a piece
/// of equipment (41%: any weapon, armour or jewellery base whose DropLevel is at most the monster's level) at the
/// monster's level as its item level. Equipment is magic or rare by <see cref="EquipmentRarity"/> (our own chances),
/// raised by the player's increased item rarity (<see cref="WithIncreasedRarity"/>), with affixes from <see cref="ItemRolls"/>.
/// </summary>
sealed class ItemDrops(ItemCatalog catalog, ItemRolls? rolls = null, string folder = "data", bool ruthless = false, AtlasCatalog? maps = null)
{
    /// <summary>Chances (magic, rare) of a dropped piece of equipment by the monster's rarity: our own numbers until the
    /// game's are known.</summary>
    public static (double Magic, double Rare) EquipmentRarity(MonsterRarity monster) => monster switch
    {
        MonsterRarity.Magic => (0.35, 0.10),
        MonsterRarity.Rare => (0.40, 0.25),
        MonsterRarity.Unique => (0.30, 0.50),
        _ => (0.25, 0.05),
    };

    /// <summary>The (magic, rare) chances with the player's increased rarity of items found (Gold Flask's 30%, gear and
    /// passives: base_item_found_rarity_+% and item_found_rarity_+%): each chance × (1 + increased / 100), as "increased
    /// rarity" raises the chance of a better rarity in the game, scaled down together if they would pass 100%.</summary>
    public static (double Magic, double Rare) WithIncreasedRarity((double Magic, double Rare) chances, int increased)
    {
        double factor = Math.Max(0, 100 + increased) / 100.0;
        double magic = chances.Magic * factor, rare = chances.Rare * factor;
        return magic + rare <= 1 ? (magic, rare) : (magic / (magic + rare), rare / (magic + rare));
    }

    // (BaseItemTypes Id, weight): Scroll of Wisdom and Portal Scroll most, then shards' worth of basic orbs.
    static readonly (string Id, int Weight)[] Currency =
    [
        ("Metadata/Items/Currency/CurrencyIdentification", 400), ("Metadata/Items/Currency/CurrencyPortal", 200),
        ("Metadata/Items/Currency/CurrencyArmourQuality", 80), ("Metadata/Items/Currency/CurrencyWeaponQuality", 60),
        ("Metadata/Items/Currency/CurrencyUpgradeToMagic", 100), ("Metadata/Items/Currency/CurrencyAddModToMagic", 60),
        ("Metadata/Items/Currency/CurrencyRerollMagic", 40), ("Metadata/Items/Currency/CurrencyFlaskQuality", 30),
        ("Metadata/Items/Currency/CurrencyRerollSocketColours", 25), ("Metadata/Items/Currency/CurrencyRerollSocketNumbers", 10),
        ("Metadata/Items/Currency/CurrencyRerollSocketLinks", 5), ("Metadata/Items/Currency/CurrencyUpgradeToRare", 8),
        ("Metadata/Items/Currency/CurrencyUpgradeRandomly", 8), ("Metadata/Items/Currency/CurrencyPassiveRefund", 6),
        ("Metadata/Items/Currency/CurrencyRerollRare", 3),
    ];

    // Equipment classes that drop (ItemClasses Id).
    static readonly HashSet<string> EquipmentClasses =
    [
        "One Hand Sword", "Thrusting One Hand Sword", "One Hand Axe", "One Hand Mace", "Sceptre", "Dagger", "Rune Dagger", "Claw", "Wand",
        "Two Hand Sword", "Two Hand Axe", "Two Hand Mace", "Staff", "Warstaff", "Bow",
        "Body Armour", "Helmet", "Gloves", "Boots", "Shield", "Quiver", "Ring", "Amulet", "Belt",
    ];

    // Bases that exist in the tables but never drop (event, league and test items).
    static readonly string[] NotDropped = ["Royale", "Demigod", "Talisman", "Breach", "Synthesis", "Fishing", "Test", "Unarmed", "Atlas",
        "Expedition", "Heist", "Relic", "Basilisk", "Crusader", "Eyrie", "Elder", "Shaper", "Necropolis", "Sanctum", "Ultimatum", "Legion"];

    private readonly Lazy<(List<(ItemCatalog.BaseType Type, int Weight)> Currency, List<(ItemCatalog.BaseType Type, int DropLevel)> Equipment,
        List<(ItemCatalog.BaseType Type, int DropLevel)> Flasks, List<(ItemCatalog.BaseType Type, int DropLevel)> Gems)> _pools = new(() => Build(catalog, folder, ruthless));

    public sealed record Drop(Item Item, ItemCatalog.BaseType Type);

    /// <summary>The items a monster of this level and rarity drops (possibly none).</summary>
    public IReadOnlyList<Drop> Roll(int monsterLevel, MonsterRarity rarity, Random rng, int increasedRarity = 0,
        double quantityMultiplier = 1, double rarityMultiplier = 1)
    {
        int count = rarity switch
        {
            MonsterRarity.Magic => rng.Next(1, 3),
            MonsterRarity.Rare => rng.Next(2, 5),
            MonsterRarity.Unique => rng.Next(4, 7),
            _ => rng.NextDouble() < 0.35 ? 1 : 0,
        };
        var drops = new List<Drop>();
        for (int i = 0; i < count; i++)
            if ((quantityMultiplier >= 1 || rng.NextDouble() < Math.Clamp(quantityMultiplier, 0, 1))
                && RollOne(monsterLevel, rarity, rng, increasedRarity, rarityMultiplier) is { } d) drops.Add(d);
        return drops;
    }

    /// <summary>The gold item (ItemClasses "Gold"): on the ground a pile of <see cref="Item.StackSize"/> gold, which a pickup
    /// adds to the stash's gold instead of the inventory.</summary>
    public const string GoldId = "Metadata/Items/Currency/GoldCoin", GoldClass = "Gold";

    /// <summary>The gold a dying monster drops, 0 for none: normal monsters 40% of the time, magic 75%, rare and unique
    /// always; (3 + 1.2 × level) × 1 / 2 / 5 / 15 by rarity, ±25%. Our own numbers until the game's are known.</summary>
    public static int GoldAmount(int monsterLevel, MonsterRarity rarity, Random rng)
    {
        double chance = rarity switch { MonsterRarity.Normal => 0.40, MonsterRarity.Magic => 0.75, _ => 1 };
        if (rng.NextDouble() >= chance) return 0;
        double times = rarity switch { MonsterRarity.Magic => 2, MonsterRarity.Rare => 5, MonsterRarity.Unique => 15, _ => 1 };
        return Math.Max(1, (int)Math.Round((3 + 1.2 * Math.Max(1, monsterLevel)) * times * (0.75 + 0.5 * rng.NextDouble())));
    }

    /// <summary>A pile of gold for a dying monster (<see cref="GoldAmount"/>), or null for none.</summary>
    public Drop? Gold(int monsterLevel, MonsterRarity rarity, Random rng, double multiplier = 1) =>
        (int)Math.Floor(GoldAmount(monsterLevel, rarity, rng) * Math.Clamp(multiplier, 0, 1)) is int amount and > 0 && catalog.Find(GoldId) is { Supported: true } type
            ? new Drop(ItemCatalog.Create(type) with { StackSize = (ushort)Math.Min(amount, ushort.MaxValue) }, type) : null;

    /// <summary>An approximate generic-map drop: a map of the current map tier (or tier 1 in the campaign). Our own
    /// rates until the game pools are known.</summary>
    public Drop? Map(int monsterLevel, MonsterRarity monster, int mapTier, Random rng)
    {
        double chance = monster switch { MonsterRarity.Normal => 0.004, MonsterRarity.Magic => 0.012, MonsterRarity.Rare => 0.05, _ => 0.20 };
        if (maps is null || rng.NextDouble() >= chance) return null;
        int tier = Math.Clamp(mapTier > 0 ? mapTier : 1, 1, 16);
        if (maps.KeyOfTier(tier) is not uint hash || catalog.ByHash(hash) is not { Supported: true } type) return null;
        var item = ItemCatalog.Create(type) with { ItemLevel = (byte)Math.Clamp(monsterLevel, 1, 100) };
        if (rolls is not null && rng.NextDouble() < 0.10) item = rolls.Roll(item, ItemRarity.Magic, rng) with { Identified = true };
        return new(item, type);
    }
    Drop? RollOne(int level, MonsterRarity monster, Random rng, int increasedRarity = 0, double rarityMultiplier = 1)
    {
        var (currency, equipment, flasks, gems) = _pools.Value;
        double kind = rng.NextDouble();
        ItemCatalog.BaseType? type = null;
        if (kind < 0.45 && currency.Count > 0)
        {
            int total = currency.Sum(c => c.Weight), pick = rng.Next(total);
            foreach (var (t, w) in currency) { if (pick < w) { type = t; break; } pick -= w; }
        }
        // 4% of items are gems, a rate of our own until the game's drop pools are known; gems have no rarity roll.
        else if (kind < 0.49 && gems.Where(g => g.DropLevel <= level).ToList() is { Count: > 0 } g)
            type = g[rng.Next(g.Count)].Type;
        else if (kind < 0.59 && flasks.Where(f => f.DropLevel <= level).ToList() is { Count: > 0 } f)
            type = f[rng.Next(f.Count)].Type;
        else if (equipment.Where(e => e.DropLevel <= level).ToList() is { Count: > 0 } e)
            type = e[rng.Next(e.Count)].Type;
        if (type is null) return null;
        var (sockets, links) = catalog.DefaultSockets(type, level);
        var item = ItemCatalog.Create(type, sockets, links, random: rng) with
        {
            // A dropped gem is unlevelled, like a vendor's: its level comes from its experience (ItemCatalog.GemLevel).
            ItemLevel = catalog.Gem(type) is not null ? (byte)1 : (byte)Math.Clamp(level, 1, 100),
        };
        if (rolls is not null && EquipmentClasses.Contains(type.Class))
        {
            var chances = EquipmentRarity(monster);
            var (magic, rare) = WithIncreasedRarity((chances.Magic * rarityMultiplier, chances.Rare * rarityMultiplier), increasedRarity);
            double r = rng.NextDouble();
            // Magic and rare items drop unidentified (a Scroll of Wisdom identifies them, C→S 0x71).
            if (r < rare) item = rolls.Roll(item, ItemRarity.Rare, rng) with { Identified = false };
            else if (r < rare + magic) item = rolls.Roll(item, ItemRarity.Magic, rng) with { Identified = false };
        }
        return new Drop(item, type);
    }

    private static (List<(ItemCatalog.BaseType, int)>, List<(ItemCatalog.BaseType, int)>, List<(ItemCatalog.BaseType, int)>,
        List<(ItemCatalog.BaseType, int)>) Build(ItemCatalog catalog, string folder, bool ruthless)
    {
        var currency = new List<(ItemCatalog.BaseType, int)>();
        foreach (var (id, weight) in Currency)
            if (catalog.Find(id) is { Supported: true } t && t.Id == id) currency.Add((t, weight));
        var equipment = new List<(ItemCatalog.BaseType, int)>();
        var flasks = new List<(ItemCatalog.BaseType, int)>();
        for (int row = 0; catalog.Row(row) is { } t; row++)
        {
            if (!t.Supported || t.DropLevel < 1 || string.IsNullOrEmpty(t.Name) || NotDropped.Any(n => t.Id.Contains(n))) continue;
            if (EquipmentClasses.Contains(t.Class)) equipment.Add((t, t.DropLevel));
            else if (t.Class is "LifeFlask" or "ManaFlask") flasks.Add((t, t.DropLevel));
        }
        // Obtainable gems only (GemSources): the data's Graft, legacy and placeholder rows are not normal drops.
        var pool = GemSources.DropPool(catalog, folder);
        var gems = new List<(ItemCatalog.BaseType, int)>();
        foreach (int row in pool)
            if (catalog.Row(row) is { DropLevel: >= 1 } type && (!ruthless || RuthlessGemRules.AllowsBase(type.Id)))
                gems.Add((type, type.DropLevel));
        return (currency, equipment, flasks, gems);
    }
}
