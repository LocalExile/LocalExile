using GameServer.Core.Protocol.Instance;
using GameServer.Modding;

namespace GameServer.Modules.Legion;

/// <summary>
/// What a fighter of the army drops (GameEvents.Drops): its reward types' items, and Timeless Splinters. Only the monsters that
/// woke at the release (LegionStasis.Fighters) are looked at: a frozen member cannot die, and a general that flees drops nothing.
/// ALL THE NUMBERS ARE GUESSES (nothing of the real game's is known); tune them here.
/// </summary>
static class LegionRewards
{
    /// <summary>GUESS: the items each reward type gives.</summary>
    const int ItemsPerReward = 2;
    /// <summary>GUESS: a sergeant drops splinters of its faction with this percent chance, a stack of this range; normal soldiers (tiers 0, 1) drop none (3.29).</summary>
    const int SergeantSplinterChance = 50, SergeantSplinterMin = 3, SergeantSplinterMax = 8;
    /// <summary>GUESS: the general always drops this many splinters.</summary>
    const int GeneralSplinterMin = 15, GeneralSplinterMax = 30;
    /// <summary>GUESS: the general's percent chance of its faction's Enshrouding Crystal (maps of at least <see cref="CrystalMinLevel"/> only) and its
    /// per mille chance of a Timeless Jewel (when the faction's unique exists).</summary>
    const int GeneralCrystalChance = 15, GeneralJewelPermille = 5, CrystalMinLevel = 68;

    /// <summary>The faction's splinter, Enshrouding Crystal (BaseItemTypes ids) and Timeless Jewel (unique name), by LegionFactions Id. Afarud uses Maraketh's.</summary>
    static readonly Dictionary<string, (string Splinter, string Crystal, string Jewel)> Factions = new()
    {
        ["Karui"] = ("Metadata/Items/Currency/CurrencyLegionKaruiShard", "Metadata/Items/Currency/LegionCocoonGloves", "Lethal Pride"),
        ["Maraketh"] = ("Metadata/Items/Currency/CurrencyLegionMarakethShard", "Metadata/Items/Currency/LegionCocoonBodyArmour", "Brutal Restraint"),
        ["EternalEmpire"] = ("Metadata/Items/Currency/CurrencyLegionEternalEmpireShard", "Metadata/Items/Currency/LegionCocoonBoots", "Elegant Hubris"),
        ["Templar"] = ("Metadata/Items/Currency/CurrencyLegionTemplarShard", "Metadata/Items/Currency/LegionCocoonShield", "Militant Faith"),
        ["Vaal"] = ("Metadata/Items/Currency/CurrencyLegionVaalShard", "Metadata/Items/Currency/LegionCocoonHelmet", "Glorious Vanity"),
    };

    const string StackedDeck = "Metadata/Items/DivinationCards/DivinationCardDeck";

    /// <summary>THE MAPPING TABLE: LegionRewardTypeVisuals row -> what it drops. Rows 11 and 17 are never rolled (LegionArmy).</summary>
    static readonly Dictionary<int, string> Kinds = new()
    {
        [0] = "currency", [1] = "unique", [2] = "gems", [3] = "maps", [4] = "jewellery", [5] = "armour", [6] = "weapons", [7] = "abyss",
        [8] = "harbinger", [9] = "breach", [10] = "essences", [12] = "fragments", [13] = "scarabs", [14] = "fossils", [15] = "divination", [16] = "generic",
    };

    /// <summary>The Harbinger currencies by the end of their ids, with GUESS weights (the cheap scrolls and shards common, the valuable ones rare, the Mirror
    /// Shard very rare); the Scrolls are the CurrencyHarbingerBlessing* items, weight <see cref="ScrollWeight"/>.</summary>
    static readonly (string Id, int Weight)[] HarbingerCurrency =
    [
        ("/CurrencyRerollRareShard", 40), ("/CurrencyUpgradeToRareAndSetSockets", 30), ("/CurrencyRerollMapType", 30), ("/CurrencyStrongboxQuality", 30),
        ("/CurrencyRemoveModShard", 8), ("/CurrencyAddModToRareShard", 8), ("/CurrencyRerollUnique", 8), ("/CurrencyUpgradeMapTier", 8),
        ("/CurrencyDuplicateShard", 1),
    ];
    const int ScrollWeight = 40;
    const string MirrorShard = "/CurrencyDuplicateShard";

    static int Weight(string kind, ItemCatalog.BaseType t)
    {
        if (kind != "harbinger") return 1;
        foreach (var (id, weight) in HarbingerCurrency) if (t.Id.EndsWith(id, StringComparison.Ordinal)) return weight;
        return ScrollWeight;
    }

    /// <summary>The catalog's items of each kind picked from the catalog (not a container roll), built once per catalog, sorted by drop level.</summary>
    sealed class Pools
    {
        readonly Dictionary<string, ItemCatalog.BaseType[]> byKind = [];
        IReadOnlySet<int>? gems;
        UniqueCatalog.Definition[]? uniques;
        int[]? uniqueLevels;

        public ItemCatalog.BaseType[] Of(ItemCatalog catalog, string kind)
        {
            lock (byKind)
            {
                if (byKind.TryGetValue(kind, out var pool)) return pool;
                var gemRows = kind == "gems" ? gems ??= GemSources.DropPool(catalog) : null;
                return byKind[kind] = [.. catalog.All.Where(t => t.Supported && t.DropLevel >= 0 && !t.Components.Contains("Quest") && Eligible(kind, t, gemRows))
                    .OrderBy(t => t.DropLevel)];
            }
        }

        /// <summary>The uniques whose base is droppable, sorted by the base's drop level, and those levels.</summary>
        public (UniqueCatalog.Definition[] Defs, int[] Levels) Uniques(ItemCatalog catalog, UniqueCatalog source)
        {
            lock (byKind)
            {
                if (uniques is null)
                {
                    var list = source.All.Select(u => (U: u, T: catalog.Find(u.BaseTypeId))).Where(x => x.T is not null && ItemDrops.Droppable(x.T)).OrderBy(x => x.T!.DropLevel).ToList();
                    uniques = [.. list.Select(x => x.U)];
                    uniqueLevels = [.. list.Select(x => x.T!.DropLevel)];
                }
                return (uniques, uniqueLevels!);
            }
        }
    }

    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ItemCatalog, Pools> PoolsOf = new();

    /// <summary>How many of the ascending drop levels are at most <paramref name="level"/>.</summary>
    static int Upto(IReadOnlyList<int> levels, int level)
    {
        int lo = 0, hi = levels.Count;
        while (lo < hi) { int mid = (lo + hi) / 2; if (levels[mid] <= level) lo = mid + 1; else hi = mid; }
        return lo;
    }

    static bool Eligible(string kind, ItemCatalog.BaseType t, IReadOnlySet<int>? gems) => kind switch
    {
        "gems" => gems!.Contains(t.Row),
        "abyss" => t.Class == "AbyssJewel" || t.Id.Contains("/JewelAbyss", StringComparison.Ordinal),
        "harbinger" => t.Id.Contains("/CurrencyHarbingerBlessing", StringComparison.Ordinal) || HarbingerCurrency.Any(h => t.Id.EndsWith(h.Id, StringComparison.Ordinal)),
        "breach" => t.Id.Contains("/CurrencyBreach", StringComparison.Ordinal),
        "essences" => t.Class == "StackableCurrency" && t.Id.Contains("Essence", StringComparison.Ordinal) && !t.Id.Contains("Corrupt", StringComparison.Ordinal),
        "fragments" => t.Class == "MapFragment" && !t.Id.Contains("CurrencyAffliction", StringComparison.Ordinal) && !t.Id.Contains("/Scarabs/", StringComparison.Ordinal),
        "fossils" => t.Id.Contains("/CurrencyDelveCrafting", StringComparison.Ordinal),
        _ => false,
    };

    /// <summary>GUESS: a chest's items per reward type, a War Hoard's; and the splinters of its faction (percent chance, then min..max).</summary>
    const int ChestItemsPerReward = 3, HoardItemsPerReward = 4;
    const int ChestSplinterChance = 60, ChestSplinterMin = 4, ChestSplinterMax = 10, HoardSplinterChance = 100, HoardSplinterMin = 10, HoardSplinterMax = 20;

    /// <summary>The items of an opened chest by its reward types, plus its faction's splinters (factionId null: none); the same mapping as a fighter's.</summary>
    public static List<Item> Chest(Player player, int[] rewards, string? factionId, bool hoard, int level, Random rng)
    {
        var made = new List<Item>();
        var stacks = new List<(string Item, int Count)>();
        foreach (int row in rewards) RollReward(player, made, stacks, row, level, hoard ? HoardItemsPerReward : ChestItemsPerReward, rng);
        if (factionId is not null && rng.Next(100) < (hoard ? HoardSplinterChance : ChestSplinterChance))
            Splinter(stacks, factionId, hoard ? HoardSplinterMin : ChestSplinterMin, hoard ? HoardSplinterMax : ChestSplinterMax, rng);
        var catalog = global::Game.ItemsFor(player);
        foreach (var (id, count) in stacks)
            if (catalog.Find(id) is { Supported: true } type)
                for (int left = count; left > 0; left -= Math.Max(1, (int)type.MaxStack))
                    made.Add(ItemCatalog.Create(type) with { ItemLevel = (byte)level, StackSize = (ushort)Math.Min(left, Math.Max(1, (int)type.MaxStack)) });
        return made;
    }

    /// <summary>The kinds picked from the catalog's own pool, not rolled by a container (the tests read them).</summary>
    static readonly HashSet<string> PoolKinds = ["gems", "abyss", "harbinger", "breach", "essences", "fragments", "fossils"];

    /// <summary>The items a reward row can give at a level when it is picked from a catalog pool; null for the kinds that are rolled otherwise.</summary>
    internal static IReadOnlyList<ItemCatalog.BaseType>? Pool(ItemCatalog catalog, int row, int level)
    {
        string kind = Kinds.GetValueOrDefault(row, "generic");
        return PoolKinds.Contains(kind) ? [.. PoolsOf.GetValue(catalog, _ => new Pools()).Of(catalog, kind).Where(t => t.DropLevel <= level)] : null;
    }

    /// <summary>The Harbinger pool's weight of an item (the Mirror Shard's is the smallest).</summary>
    internal static int HarbingerWeight(ItemCatalog.BaseType t) => Weight("harbinger", t);

    internal static IEnumerable<int> RewardRows => Kinds.Keys;

    static bool warned;

    /// <summary>GameEvents.MonsterKilled: a fighter's reward symbols end at its death (the area's Buffs end them only at removal). Only reads the entry:
    /// Drops may have run first and taken it, and then cleared them itself.</summary>
    public static Task Killed(MonsterKill kill)
    {
        if (LegionStasis.AnyFighter && LegionStasis.FighterOf(kill.Player, kill.Monster.ObjectId, remove: false) is { Rewards.Length: > 0 })
            kill.Player.Area.Buffs.ClearRow(kill.Monster.ObjectId, LegionStasis.RewardBuff);
        return Task.CompletedTask;
    }

    /// <summary>GameEvents.Drops: the common path is a volatile flag, then one lookup in the area's table.</summary>
    public static void Drops(DropRoll drop)
    {
        if (!LegionStasis.AnyFighter || LegionStasis.FighterOf(drop.Player, drop.Monster.ObjectId, remove: true) is not { } fighter) return;
        var player = drop.Player;
        if (fighter.Rewards.Length > 0) player.Area.Buffs.ClearRow(drop.Monster.ObjectId, LegionStasis.RewardBuff);
        if (!GameEvents.AllowsLoot(player) || Environment.GetEnvironmentVariable("Game_DROPS") == "0") return;
        var rng = global::Game.AreaRng(player);
        int level = Math.Clamp(drop.Monster.Level, 1, 100);
        foreach (int row in fighter.Rewards) RollReward(player, drop.Made, drop.Stacks, row, level, ItemsPerReward, rng);
        // Splinters: sergeants and the general only (the chests: LegionRewards.Chest).
        if (fighter.Tier == 2 && rng.Next(100) < SergeantSplinterChance) Splinter(drop.Stacks, fighter.FactionId, SergeantSplinterMin, SergeantSplinterMax, rng);
        else if (fighter.Tier == 3)
        {
            Splinter(drop.Stacks, fighter.FactionId, GeneralSplinterMin, GeneralSplinterMax, rng);
            if (Factions.TryGetValue(fighter.FactionId == "Faridun" ? "Maraketh" : fighter.FactionId, out var f))
            {
                if (level >= CrystalMinLevel && MapDevices.IsMapArea(player.Layout.Hash) && rng.Next(100) < GeneralCrystalChance) drop.Stacks.Add((f.Crystal, 1));
                if (rng.Next(1000) < GeneralJewelPermille && global::Game.UniquesFor(player) is { } uniques && uniques.Find(f.Jewel) is { } jewel
                    && uniques.Create(jewel, rng) is { } item) drop.Made.Add(item with { ItemLevel = (byte)level });
            }
        }
    }

    /// <summary>A stack of the faction's Timeless Splinters, min..max. Also for the chests (P2-E4).</summary>
    public static void Splinter(List<(string Item, int Count)> stacks, string factionId, int min, int max, Random rng)
    {
        if (Factions.TryGetValue(factionId == "Faridun" ? "Maraketh" : factionId, out var f)) stacks.Add((f.Splinter, rng.Next(min, max + 1)));
    }

    static void RollReward(Player player, List<Item> made, List<(string Item, int Count)> stacks, int row, int level, int count, Random rng)
    {
        var catalog = global::Game.ItemsFor(player);
        var drops = global::Game.DropsFor(player);
        string kind = Kinds.GetValueOrDefault(row, "generic");
        IEnumerable<Item> Items(IEnumerable<ItemDrops.Drop> d) => d.Select(x => x.Item);
        switch (kind)
        {
            case "currency":
                made.AddRange(Items(drops.Container(level, count, 0, rng, t => t.Class is "StackableCurrency" or "Currency")));
                return;
            case "unique":
            {
                var uniques = global::Game.UniquesFor(player);
                var (defs, levels) = PoolsOf.GetValue(catalog, _ => new Pools()).Uniques(catalog, uniques);
                int n = Upto(levels, level);
                for (int i = 0; i < count && n > 0; i++)
                    if (drops.PickUnique(new ArraySegment<UniqueCatalog.Definition>(defs, 0, n), rng) is { } u && uniques.Create(u, rng) is { } item) made.Add(item with { ItemLevel = (byte)level });
                return;
            }
            case "maps":
                // chanceFactor 20: the generic 5% map chance of a rare becomes certain, a reward is not a roll to miss.
                for (int i = 0; i < count; i++)
                    if (drops.Map(level, MonsterRarity.Rare, Math.Clamp(level - 67, 1, 16), rng, chanceFactor: 20) is { } map) made.Add(map.Item);
                return;
            case "scarabs":
                for (int i = 0; i < count; i++) if (drops.Scarab(rng, level) is { } scarab) made.Add(scarab.Item);
                return;
            case "divination":
                if (catalog.Find(StackedDeck) is { Supported: true }) stacks.Add((StackedDeck, rng.Next(1, 4)));
                return;
            case "jewellery":
                made.AddRange(Items(drops.Container(level, count, 0, rng, t => t.Class is "Ring" or "Amulet" or "Belt")));
                return;
            case "armour":
                made.AddRange(Items(drops.Container(level, count, 0, rng, t => t.Class is "Body Armour" or "Helmet" or "Gloves" or "Boots" or "Shield")));
                return;
            case "weapons":
                made.AddRange(Items(drops.Container(level, count, 0, rng, t => ItemDrops.EquipmentClasses.Contains(t.Class) && t.Components.Contains("Weapon"))));
                return;
            case "generic":
                made.AddRange(Items(drops.Container(level, count, 0, rng)));
                return;
        }
        // The classes picked from the catalog's own list (cached per catalog, sorted by drop level).
        var pool = PoolsOf.GetValue(catalog, _ => new Pools()).Of(catalog, kind);
        int eligible = 0;
        while (eligible < pool.Length && pool[eligible].DropLevel <= level) eligible++;
        if (eligible == 0)
        {
            if (!warned) { warned = true; Console.Error.WriteLine($"legion reward '{kind}' has no items in the catalog: generic drops instead"); }
            made.AddRange(Items(drops.Container(level, count, 0, rng)));
            return;
        }
        for (int i = 0; i < count; i++)
        {
            var type = pool[rng.Next(eligible)];
            if (kind == "harbinger")
            {
                int pick = rng.Next(Enumerable.Range(0, eligible).Sum(k => Weight(kind, pool[k])));
                for (int k = 0; k < eligible; k++) { type = pool[k]; if ((pick -= Weight(kind, type)) < 0) break; }
            }
            int stack = type.Id.EndsWith(MirrorShard, StringComparison.Ordinal) ? 1 : rng.Next(1, 4);   // a Mirror Shard is never more than one
            made.Add(ItemCatalog.Create(type) with { ItemLevel = (byte)level, StackSize = (ushort)Math.Min(type.MaxStack, stack) });
        }
    }
}
