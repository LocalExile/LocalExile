using System.Runtime.CompilerServices;
using GameServer.Core;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;
using GameServer.Storage;
using static Program;

/// <summary>
/// Gold gambling, the "Gamble for Items" option of Bestel, Silk, Maramoa, Tasuni, Vilenta and Faustus
/// (Agents/docs/protocol/17-npcs.md §5, "Gambling"). Their <c>NPCs</c> ShopSet is <c>Village_Gamble</c>, whose <c>NPCShop</c> row has window kind 10,
/// the black market. The window shows the client's "Random …" items (<c>HiddenItem</c> rows, <c>…Gamble</c>) at a gold price;
/// a purchase rolls a new item of that kind. Prices are poewiki's 3.29 formulas; the rolls follow its measurements.
/// </summary>
static class Gambling
{
    /// <summary>S→C 0x8c's window kind for the black market (0x140740840 plays <c>SettlersLeague_BlackMarketOpen</c> for it).</summary>
    public const ushort WindowKind = 10;

    /// <summary>The price groups of poewiki's 3.29 formulas.</summary>
    internal enum PriceGroup { Heavy, Light, Quiver, Belt, Ring, Amulet }

    /// <summary>A core offer: the shown item's id, then the bases it rolls (an item class and, for armour, its attribute tag).</summary>
    internal sealed record Kind(string Shown, string Class, string? Tag, PriceGroup Group, string Tab);

    const string Weapons = "Weapons", Armour = "Armour", Jewellery = "Jewellery";

    /// <summary>The client's <c>…Gamble</c> rows, in the table's order (BaseItemTypes 5187 to 5235).</summary>
    internal static readonly Kind[] Kinds =
    [
        new("Metadata/Items/Weapons/OneHandWeapons/OneHandSwords/OneHandSwordGamble", "One Hand Sword", null, PriceGroup.Light, Weapons),
        new("Metadata/Items/Weapons/OneHandWeapons/OneHandSwords/ThrustingOneHandSwordGamble", "Thrusting One Hand Sword", null, PriceGroup.Light, Weapons),
        new("Metadata/Items/Weapons/TwoHandWeapons/TwoHandSwords/TwoHandSwordGamble", "Two Hand Sword", null, PriceGroup.Heavy, Weapons),
        new("Metadata/Items/Weapons/OneHandWeapons/OneHandAxes/OneHandAxeGamble", "One Hand Axe", null, PriceGroup.Light, Weapons),
        new("Metadata/Items/Weapons/TwoHandWeapons/TwoHandAxes/TwoHandAxeGamble", "Two Hand Axe", null, PriceGroup.Heavy, Weapons),
        new("Metadata/Items/Weapons/OneHandWeapons/OneHandMaces/OneHandMaceGamble", "One Hand Mace", null, PriceGroup.Heavy, Weapons),
        new("Metadata/Items/Weapons/TwoHandWeapons/TwoHandMaces/TwoHandMaceGamble", "Two Hand Mace", null, PriceGroup.Heavy, Weapons),
        new("Metadata/Items/Weapons/OneHandWeapons/Claws/ClawGamble", "Claw", null, PriceGroup.Light, Weapons),
        new("Metadata/Items/Weapons/OneHandWeapons/Daggers/DaggerGamble", "Dagger", null, PriceGroup.Light, Weapons),
        new("Metadata/Items/Weapons/OneHandWeapons/Daggers/RuneDaggerGamble", "Rune Dagger", null, PriceGroup.Light, Weapons),
        new("Metadata/Items/Weapons/TwoHandWeapons/Staves/WarstaffGamble", "Warstaff", null, PriceGroup.Heavy, Weapons),
        new("Metadata/Items/Weapons/OneHandWeapons/Wands/WandGamble", "Wand", null, PriceGroup.Light, Weapons),
        new("Metadata/Items/Weapons/TwoHandWeapons/Staves/StaffGamble", "Staff", null, PriceGroup.Heavy, Weapons),
        new("Metadata/Items/Weapons/TwoHandWeapons/Bows/BowGamble", "Bow", null, PriceGroup.Heavy, Weapons),
        new("Metadata/Items/Weapons/OneHandWeapons/OneHandMaces/SceptreGamble", "Sceptre", null, PriceGroup.Light, Weapons),
        .. Armours("BodyArmours/Body", "Body Armour", PriceGroup.Heavy),
        .. Armours("Helmets/Helmet", "Helmet", PriceGroup.Light),
        .. Armours("Gloves/Gloves", "Gloves", PriceGroup.Light),
        .. Armours("Boots/Boots", "Boots", PriceGroup.Light),
        .. Armours("Shields/Shield", "Shield", PriceGroup.Light),
        new("Metadata/Items/Quivers/QuiverGamble", "Quiver", null, PriceGroup.Quiver, Jewellery),
        new("Metadata/Items/Amulets/AmuletGamble", "Amulet", null, PriceGroup.Amulet, Jewellery),
        new("Metadata/Items/Rings/RingGamble", "Ring", null, PriceGroup.Ring, Jewellery),
        new("Metadata/Items/Belts/BeltGamble", "Belt", null, PriceGroup.Belt, Jewellery),
    ];

    static IEnumerable<Kind> Armours(string path, string itemClass, PriceGroup group) =>
        new[] { ("Str", "str_armour"), ("Dex", "dex_armour"), ("Int", "int_armour"), ("StrDex", "str_dex_armour"),
            ("StrInt", "str_int_armour"), ("DexInt", "dex_int_armour") }
        .Select(a => new Kind($"Metadata/Items/Armours/{path}{a.Item1}Gamble", itemClass, a.Item2, group, Armour));

    /// <summary>NPCs whose shop set opens the black market (<c>NPCs</c> ShopSet → <c>NPCShopSets</c> Shop → <c>NPCShop</c>
    /// column 1, the window kind).</summary>
    static HashSet<string> Gamblers = new(StringComparer.Ordinal);

    public static void LoadGameData()
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        if (ClientTables.Has("NPCs") && ClientTables.Has("NPCShopSets") && ClientTables.Has("NPCShop"))
        {
            var sets = ClientTables.Table("NPCShopSets");
            var shops = ClientTables.Table("NPCShop");
            foreach (var npc in ClientTables.Table("NPCs"))
                if (npc.IntOrNull("ShopSet") is int set && sets.Row(set) is { } shopSet && shopSet.IntOrNull("Shop") is int shop
                    && shops.Row(shop) is { } row && row.IntOrNull("__col1") == WindowKind && npc.Text("Id") is { } id)
                    result.Add(id);
        }
        Gamblers = result;
    }

    /// <summary>Whether this NPC's buy option is the gamble window.</summary>
    public static bool Gambles(string npc) => Gamblers.Contains(npc);

    /// <summary>
    /// poewiki's 3.29 price per gamble ("Analysis of gold gambling", price formula), by the character's level clamped to 4..83:
    /// trunc(a × r^L) for quivers and jewellery, trunc(r^L × (a + b × L)) for the rest.
    /// </summary>
    public static uint Price(PriceGroup group, int characterLevel)
    {
        int level = Math.Clamp(characterLevel, 4, 83);
        double price = group switch
        {
            PriceGroup.Heavy => Math.Pow(1.035, level) * (17.36 + 1.9328 * level),
            PriceGroup.Light => Math.Pow(1.035, level) * (12.51 + 1.9328 * level),
            PriceGroup.Quiver => 42.97 * Math.Pow(1.047, level),
            PriceGroup.Belt => 71.635 * Math.Pow(1.047, level),
            PriceGroup.Ring => 90.74 * Math.Pow(1.047, level),
            _ => 133.717 * Math.Pow(1.047, level),
        };
        return (uint)Math.Max(1, Math.Truncate(price));
    }

    /// <summary>
    /// The item level of a gambled item: the character's level (4 at least) up to 69, falling behind above it to 83 at 94, plus
    /// 0 to 3 (poewiki's measured offsets, 3.29).
    /// </summary>
    public static int ItemLevel(int characterLevel, Random random)
    {
        int level = Math.Max(characterLevel, 4);
        double baseLevel = level <= 69 ? level : Interpolate(level, [(69, 69), (84, 81), (90, 82), (94, 83)]);
        // +0/+1/+2/+3 weights by character level.
        int[] weights = level < 9 ? [76, 20, 4, 0] : level < 23 ? [52, 40, 8, 0] : level < 35 ? [36, 48, 16, 0]
            : level < 49 ? [24, 52, 24, 0] : level < 60 ? [8, 64, 28, 0] : level < 69 ? [6, 54, 40, 0]
            : level < 100 ? [8, 45, 43, 4] : [7, 41, 44, 8];
        return (int)baseLevel + Pick(weights, random);
    }

    /// <summary>The chance of a rare item rather than a magic one: 24% at item level 4 to 72% at 84 (poewiki, 3.29).</summary>
    public static double RareChance(int itemLevel) => Interpolate(itemLevel, [(4, 0.24), (50, 0.50), (61, 0.65), (84, 0.72)]);

    /// <summary>The chance of a unique: about 2% at low levels to 2.7% from character level 37 (poewiki, 3.29).</summary>
    public static double UniqueChance(int characterLevel) => Interpolate(characterLevel, [(4, 0.02), (37, 0.027)]);

    /// <summary>
    /// How many of the lowest tiers of an affix with <paramref name="tiers"/> eligible tiers a gambled item skips: none up to item
    /// level 44, then the lowest, then about a fifth, three tenths and two fifths of them; magic items keep to the top half from
    /// item level 69 (poewiki's "well-rolled" measurements, 3.29). At least one tier is left.
    /// </summary>
    public static int ExcludedTiers(int tiers, int itemLevel, ItemRarity rarity)
    {
        if (tiers <= 1 || itemLevel < 45) return 0;
        int excluded = itemLevel < 58 ? 1 : (int)Math.Max(1, Math.Round(tiers * (itemLevel < 66 ? 0.2 : itemLevel < 80 ? 0.3 : 0.4),
            MidpointRounding.AwayFromZero));
        if (rarity == ItemRarity.Magic && itemLevel >= 69) excluded = Math.Max(excluded, tiers / 2);
        return Math.Min(excluded, tiers - 1);
    }

    /// <summary>
    /// The effective pricing level for a character of <paramref name="characterLevel"/> in a league whose highest-level
    /// character on the same account is <paramref name="highestLeagueLevel"/> (poewiki "Analysis of gold gambling", 3.29):
    /// at least <c>min(highestLeagueLevel − 20, 68)</c>, taking effect once the highest character reaches level 25 (level 5
    /// price) and stopping at the level 68 price at level 88.
    /// </summary>
    public static int PriceLevel(int characterLevel, int highestLeagueLevel) =>
        Math.Max(characterLevel, Math.Min(highestLeagueLevel - 20, 68));

    /// <summary>The highest level among this account's characters in the same league (at least this player's level).</summary>
    public static int HighestLeagueLevel(Player player)
    {
        int highest = player.Pools.Level;
        if (player.Character.Account is not { } accountName || player.Character.League is not { } league) return highest;
        var store = new SqliteAccountStore(Saves.Db);
        if (store.FindAccount(accountName) is not { } account) return highest;
        foreach (var character in store.CharactersOf(account.Id))
        {
            if (!string.Equals(character.League, league, StringComparison.OrdinalIgnoreCase)) continue;
            int level = CharacterSaves.Experience(character.Name) is uint xp
                ? Progression.LevelFor(xp, CharacterStats.LevelStarts)
                : Math.Clamp(character.Level, 1, Progression.MaxLevel);
            if (level > highest) highest = level;
        }
        return highest;
    }

    /// <summary>The core offers of a window at this character level, priced at <paramref name="priceLevel"/> (defaulting to
    /// <paramref name="characterLevel"/>), in their tabs' order.</summary>
    public static List<GambleOffer> CoreOffers(Player player, int characterLevel, int? priceLevel = null)
    {
        int pricing = Math.Max(characterLevel, priceLevel ?? characterLevel);
        var items = ItemsFor(player);
        var offers = new List<GambleOffer>();
        foreach (var kind in Kinds)
            if (items.Find(kind.Shown) is { Supported: true } shown && shown.Id == kind.Shown)
                offers.Add(new(shown, Price(kind.Group, pricing), random => Roll(player, kind, characterLevel, random), kind.Tab));
        return offers;
    }

    /// <summary>The bases a kind rolls: those that drop, of its class and attribute tag.</summary>
    internal static List<ItemCatalog.BaseType> Bases(ItemCatalog items, Kind kind) =>
        [.. items.All.Where(t => t.Class == kind.Class && ItemDrops.Droppable(t) && t.Components.Contains("Mods")
            && (kind.Tag is null || t.Tags.Contains(kind.Tag)))];

    /// <summary>
    /// A base's weight in a gamble: rising with its drop level up to the mid-50s, then falling, and the top tier bases less
    /// (poewiki: the highest eligible base is the most common at low levels, mid-range bases at high ones). Our own curve.
    /// </summary>
    internal static double BaseWeight(ItemCatalog.BaseType type)
    {
        double weight = type.DropLevel <= 55 ? 10 + type.DropLevel : Math.Max(10, 65 - (type.DropLevel - 55) * 1.5);
        return type.Tags.Contains("top_tier_base_item_type") ? weight / 3 : weight;
    }

    /// <summary>A gambled item of this kind: unique, rare or magic, identified, with the gamble's sockets and better affixes.</summary>
    static Item? Roll(Player player, Kind kind, int characterLevel, Random random)
    {
        var items = ItemsFor(player);
        int itemLevel = ItemLevel(characterLevel, random);
        var bases = Bases(items, kind).Where(t => t.DropLevel <= itemLevel).ToList();
        if (bases.Count == 0) return null;
        if (random.NextDouble() < UniqueChance(characterLevel) && Unique(player, bases, itemLevel, random) is { } unique) return unique;
        var type = PickWeighted(bases, BaseWeight, random)!;
        var rarity = random.NextDouble() < RareChance(itemLevel) ? ItemRarity.Rare : ItemRarity.Magic;
        var item = ItemCatalog.Create(type, random: random) with { ItemLevel = (byte)itemLevel };
        item = Sockets(items, type, item, itemLevel, random);
        return Affixes(RollsFor(player), type, item, rarity, random) with { Identified = true };
    }

    /// <summary>A unique of these bases at this item level, each weighted by its rarity (<see cref="ItemDrops.UniqueWeight"/>).</summary>
    static Item? Unique(Player player, List<ItemCatalog.BaseType> bases, int itemLevel, Random random)
    {
        var ids = bases.Select(b => b.Id).ToHashSet(StringComparer.Ordinal);
        var drops = DropsFor(player);
        var uniques = UniquesFor(player);
        var eligible = uniques.All.Where(u => !u.EncounterOnly && ids.Contains(u.BaseTypeId)).ToList();
        if (PickWeighted(eligible, u => drops.UniqueWeight(u.Name), random) is not { } chosen) return null;
        return uniques.Create(chosen with { ItemLevel = (byte)itemLevel }, random) is { } item ? item with { Identified = true } : null;
    }

    /// <summary>Rolls the affixes, without the lowest tiers <see cref="ExcludedTiers"/> leaves out of each.</summary>
    internal static Item Affixes(ItemRolls rolls, ItemCatalog.BaseType type, Item item, ItemRarity rarity, Random random)
    {
        var excluded = new HashSet<ModCatalog.Mod>();
        foreach (var group in rolls.Pool(type, item).Select(p => p.Mod).GroupBy(m => (m.GenerationType, m.ModType, m.Families.FirstOrDefault())))
        {
            var levels = group.Select(m => m.Level).Distinct().Order().ToList();
            int skip = ExcludedTiers(levels.Count, item.ItemLevel, rarity);
            if (skip > 0) excluded.UnionWith(group.Where(m => m.Level < levels[skip]));
        }
        return excluded.Count == 0 ? rolls.Roll(item, rarity, random) : rolls.Roll(item, rarity, random, m => !excluded.Contains(m));
    }

    // Sockets and largest link by item level (poewiki's gambled body armours, 3.29, 25 items each): 1 to 6 sockets, then 1 to 5 linked.
    static readonly (int ItemLevel, int[] Sockets, int[] Links)[] SocketTable =
    [
        (0, [3, 0, 22, 0, 0, 0], [6, 9, 10, 0, 0]),
        (9, [0, 1, 24, 0, 0, 0], [0, 8, 17, 0, 0]),
        (23, [0, 1, 24, 0, 0, 0], [0, 1, 24, 0, 0]),
        (25, [0, 1, 14, 10, 0, 0], [0, 1, 19, 5, 0]),
        (32, [0, 0, 8, 17, 0, 0], [0, 0, 11, 14, 0]),
        (35, [0, 0, 6, 18, 1, 0], [0, 0, 9, 16, 0]),
        (49, [0, 0, 5, 12, 7, 1], [0, 0, 5, 18, 2]),
        (69, [0, 0, 0, 20, 4, 1], [0, 0, 0, 23, 2]),
        (83, [0, 0, 2, 17, 5, 1], [0, 0, 3, 21, 1]),
    ];

    /// <summary>The gamble's sockets: more and better linked than a drop's, within the base's capacity.</summary>
    internal static Item Sockets(ItemCatalog items, ItemCatalog.BaseType type, Item item, int itemLevel, Random random)
    {
        int capacity = CurrencyEffects.SocketCapacity(item, type);
        if (capacity == 0 || type.HasOneSocket) return item;
        var row = SocketTable.Last(r => r.ItemLevel <= itemLevel);
        int sockets = Math.Min(capacity, Pick(row.Sockets, random) + 1);
        int largest = Math.Min(sockets, Pick(row.Links, random) + 1);
        var links = new List<byte> { (byte)largest };
        for (int left = sockets - largest; left > 0;)
        {
            int group = Math.Min(left, random.Next(1, Math.Min(largest, 2) + 1));
            links.Add((byte)group);
            left -= group;
        }
        // The largest group is not always first.
        links = [.. links.OrderBy(_ => random.Next())];
        var requirements = items.Requirements(type);
        return item with
        {
            Sockets = [.. Enumerable.Range(0, sockets).Select(_ => new ItemSocket(SocketColours.Roll(requirements, itemLevel, 0, random)))],
            Links = links,
        };
    }

    // ---- The open window --------------------------------------------------------------------------------------------------

    static readonly ConditionalWeakTable<OpenShop, Dictionary<uint, GambleOffer>> Open = new();

    /// <summary>The window of a gambler: the core offers, then the league's changes (<see cref="GameEvents.Gambles"/>), laid out in
    /// tabs, each offer priced in gold.</summary>
    public static (VendorStock.Window Window, Dictionary<uint, GambleOffer> Offers) Window(Player player, string npc, Func<uint> nextId,
        List<string> notes)
    {
        var items = ItemsFor(player);
        int level = player.Pools.Level;
        int priceLevel = PriceLevel(level, HighestLeagueLevel(player));
        var window = new GambleWindow(player, npc, level, priceLevel);
        window.Offers.AddRange(CoreOffers(player, level, priceLevel));
        GameEvents.RunGambles(window);
        var gold = items.Find(ItemDrops.GoldId);
        var tabs = new List<NpcShops.ShopTab>();
        var byId = new Dictionary<uint, GambleOffer>();
        if (gold is null) { notes.Add("no gold item type: the gamble window is empty"); return (new([new("Gamble", [])], "", []), byId); }
        foreach (var tab in window.Offers.GroupBy(o => o.Tab))
        {
            int page = 1;
            var placed = new List<InventoryItem>();
            bool[,] occupied = new bool[NpcShops.Tab.Width, NpcShops.Tab.Height];
            void Flush()
            {
                tabs.Add(new(page == 1 ? tab.Key : $"{tab.Key} {page}", placed));
                placed = []; occupied = new bool[NpcShops.Tab.Width, NpcShops.Tab.Height]; page++;
            }
            foreach (var offer in tab)
            {
                var cell = Free(occupied, offer.Shown.Width, offer.Shown.Height);
                if (cell is null && placed.Count > 0) { Flush(); cell = Free(occupied, offer.Shown.Width, offer.Shown.Height); }
                if (cell is not { } at) { notes.Add($"no room for {offer.Shown.Name}"); continue; }
                uint id = nextId();
                placed.Add(new(id, at.X, at.Y, ItemCatalog.Create(offer.Shown) with { Price = [(gold.Hash, Math.Max(1, offer.Gold))] }));
                for (int x = at.X; x < at.X + offer.Shown.Width; x++)
                    for (int y = at.Y; y < at.Y + offer.Shown.Height; y++) occupied[x, y] = true;
                byId[id] = offer;
            }
            if (placed.Count > 0) Flush();
        }
        if (tabs.Count == 0) tabs.Add(new("Gamble", []));
        return (new(tabs, "", []), byId);
    }

    /// <summary>Column by column from the top left, as the vendors' generated stock.</summary>
    static (byte X, byte Y)? Free(bool[,] occupied, byte width, byte height)
    {
        for (byte x = 0; x + width <= occupied.GetLength(0); x++)
            for (byte y = 0; y + height <= occupied.GetLength(1); y++)
                if (!Enumerable.Range(x, width).Any(cx => Enumerable.Range(y, height).Any(cy => occupied[cx, cy]))) return (x, y);
        return null;
    }

    public static void Remember(OpenShop shop, Dictionary<uint, GambleOffer> offers) => Open.AddOrUpdate(shop, offers);

    /// <summary>The gamble offer behind a shop item, if the open shop is a gamble window.</summary>
    public static GambleOffer? OfferOf(OpenShop shop, uint id) =>
        Open.TryGetValue(shop, out var offers) ? offers.GetValueOrDefault(id) : null;

    // ---- Helpers -----------------------------------------------------------------------------------------------------------

    static double Interpolate(double x, (double X, double Y)[] points)
    {
        if (x <= points[0].X) return points[0].Y;
        for (int i = 1; i < points.Length; i++)
            if (x <= points[i].X)
                return points[i - 1].Y + (points[i].Y - points[i - 1].Y) * (x - points[i - 1].X) / (points[i].X - points[i - 1].X);
        return points[^1].Y;
    }

    static int Pick(int[] weights, Random random)
    {
        int roll = random.Next(Math.Max(1, weights.Sum()));
        for (int i = 0; i < weights.Length; i++)
            if ((roll -= weights[i]) < 0) return i;
        return 0;
    }

    static T? PickWeighted<T>(IReadOnlyList<T> options, Func<T, double> weight, Random random) where T : class
    {
        double total = options.Sum(o => Math.Max(0, weight(o)));
        if (total <= 0) return null;
        double roll = random.NextDouble() * total;
        foreach (var option in options)
            if ((roll -= Math.Max(0, weight(option))) < 0) return option;
        return options[^1];
    }
}
