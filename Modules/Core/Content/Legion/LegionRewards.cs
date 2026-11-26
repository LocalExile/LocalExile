using GameServer.Core.Protocol.Instance;
using GameServer.Modding;

namespace GameServer.Modules.Legion;

/// <summary>
/// What a fighter of the army drops (GameEvents.Drops) and what a broken-out War Chest drops on open: its reward
/// types' items, and Timeless Splinters. Only the monsters that woke at the release (the per-area fighters table in
/// LegionStasis) are looked at: a frozen member cannot die, and a general that flees drops nothing.
/// </summary>
static class LegionRewards
{
    /// <summary>GUESS: the Domain's end rewards. Timeless Jewel rolls per slain General (one per DomainKillsPerJewelRoll kills, at most DomainJewelMaxRolls)
    /// and their percent chance; the Enshrouding Crystal chance; the reward rows
    /// and items per row; the generic loot from the kills; the cap on the drops.</summary>
    const int DomainJewelMaxRolls = 4, DomainKillsPerJewelRoll = 2, DomainJewelBasePct = 25,
        DomainJewelPerKillPct = 5, DomainJewelKillCapPct = 25, DomainJewelMaxPct = 85;
    const int DomainCrystalBasePct = 20;
    const int DomainRewardRowsBase = 36;
    const double DomainGenericBase = 1.2;
    const int DomainGenericMax = 40, DomainRarityBase = 150, DomainMaxDrops = 140;
    /// <summary>GUESS: the items each reward type gives on a sergeant.</summary>
    const int ItemsPerReward = 2;
    /// <summary>GUESS: a sergeant drops splinters of its faction with this percent chance, a stack of this range; normal soldiers (tiers 0, 1) drop none (3.29).</summary>
    const int SergeantSplinterChance = 50, SergeantSplinterMin = 3, SergeantSplinterMax = 8;
    /// <summary>GUESS: the general always drops this many splinters.</summary>
    const int GeneralSplinterMin = 15, GeneralSplinterMax = 30;
    /// <summary>GUESS: the general's percent chance of its faction's Enshrouding Crystal (maps of at least <see cref="CrystalMinLevel"/> only) and its
    /// per mille chance of a Timeless Jewel (when the faction's unique exists).
    /// The jewel is off (0): jewels come from the Domain of Timeless Conflict (phase 3); poewiki: "extremely rarely" from map generals, the user's experience: none.</summary>
    const int GeneralCrystalChance = 15, GeneralJewelPermille = 0, CrystalMinLevel = 68;
    /// <summary>GUESS: stack range for shards/splinters/common currency; Maps reward roll chance factor (the generic 5% of a rare becomes certain).</summary>
    const int StackMin = 1, StackMax = 3, MapChanceFactor = 20;
    /// <summary>GUESS: increased rarity bonus for armour/weapons/jewellery reward chests & sergeants.</summary>
    const int EquipmentRewardRarity = 400;
    /// <summary>GUESS: a chest's items per reward type, a War Hoard's; and the splinters of its faction (percent chance, then min..max).</summary>
    const int ChestItemsPerReward = 3, HoardItemsPerReward = 4;
    const int ChestSplinterChance = 60, ChestSplinterMin = 4, ChestSplinterMax = 10, HoardSplinterChance = 100, HoardSplinterMin = 10, HoardSplinterMax = 20;

    /// <summary>The faction's splinter, Timeless Emblem, Enshrouding Crystal (BaseItemTypes ids) and Timeless Jewel (unique name), by LegionFactions Id. Afarud uses Maraketh's.</summary>
    static readonly Dictionary<string, (string Splinter, string Emblem, string Crystal, string Jewel)> Factions = new()
    {
        ["Karui"] = ("Metadata/Items/Currency/CurrencyLegionKaruiShard", "Metadata/Items/MapFragments/CurrencyLegionFragmentKarui", "Metadata/Items/Currency/LegionCocoonGloves", "Lethal Pride"),
        ["Maraketh"] = ("Metadata/Items/Currency/CurrencyLegionMarakethShard", "Metadata/Items/MapFragments/CurrencyLegionFragmentMaraketh", "Metadata/Items/Currency/LegionCocoonBodyArmour", "Brutal Restraint"),
        ["EternalEmpire"] = ("Metadata/Items/Currency/CurrencyLegionEternalEmpireShard", "Metadata/Items/MapFragments/CurrencyLegionFragmentEternal", "Metadata/Items/Currency/LegionCocoonBoots", "Elegant Hubris"),
        ["Templar"] = ("Metadata/Items/Currency/CurrencyLegionTemplarShard", "Metadata/Items/MapFragments/CurrencyLegionFragmentTemplar", "Metadata/Items/Currency/LegionCocoonShield", "Militant Faith"),
        ["Vaal"] = ("Metadata/Items/Currency/CurrencyLegionVaalShard", "Metadata/Items/MapFragments/CurrencyLegionFragmentVaal", "Metadata/Items/Currency/LegionCocoonHelmet", "Glorious Vanity"),
    };

    const string StackedDeck = "Metadata/Items/DivinationCards/DivinationCardDeck";
    const string GemcuttersPrism = "Metadata/Items/Currency/CurrencyGemQuality";
    const string MirrorShard = "/CurrencyDuplicateShard";

    /// <summary>THE MAPPING TABLE: LegionRewardTypeVisuals row -> what it drops. Rows 11 and 17 are never rolled (LegionArmy).</summary>
    static readonly Dictionary<int, string> Kinds = new()
    {
        [0] = "currency", [1] = "unique", [2] = "gems", [3] = "maps", [4] = "jewellery", [5] = "armour", [6] = "weapons", [7] = "abyss",
        [8] = "harbinger", [9] = "breach", [10] = "essences", [12] = "fragments", [13] = "scarabs", [14] = "fossils", [15] = "divination", [16] = "generic",
    };

    /// <summary>Real Harbinger reward items (shards common, full orbs rarer, Mirror Shard rarest). Beachhead boss-only
    /// unique upgrade scrolls (<c>CurrencyHarbingerBlessing*</c>) are excluded.</summary>
    static readonly (string Id, int Weight, bool Stackable)[] HarbingerItems =
    [
        ("/CurrencyRerollRareShard", 40, true),
        ("/CurrencyStrongboxQualityShard", 40, true),
        ("/CurrencyUpgradeMagicToRareShard", 35, true),
        ("/CurrencyUpgradeToRareAndSetSocketsShard", 35, true),
        ("/CurrencyRerollMapTypeShard", 30, true),
        ("/CurrencyUpgradeMapTierShard", 15, true),
        ("/CurrencyRemoveModShard", 12, true),
        ("/CurrencyRerollUniqueShard", 10, true),
        ("/CurrencyAddModToRareShard", 8, true),
        ("/CurrencyFractureRareShard", 3, true),
        ("/CurrencyDuplicateShard", 1, false),
        ("/CurrencyStrongboxQuality", 10, false),
        ("/CurrencyUpgradeToRareAndSetSockets", 10, false),
        ("/CurrencyRerollMapType", 8, false),
        ("/CurrencyUpgradeMapTier", 6, false),
        ("/CurrencyRemoveMod", 3, false),
        ("/CurrencyRerollUnique", 3, false),
    ];

    /// <summary>Explicit allowlist of Legion Fragment reward drops (suffix of BaseItemTypes Id, weight, min area level).
    /// Excludes [DNT - UNUSED] items, Quest items, *Complete sets, removed Prophecy/Crucible items, Unrelenting emblems,
    /// Uber boss keys, and non-fragment league entries that share ItemClass MapFragment.</summary>
    static readonly (string Id, int Weight, int MinLevel, bool Stackable)[] FragmentItems =
    [
        ("/CurrencyVaalFragment1_2", 40, 36, false),          // Sacrifice at Dawn
        ("/CurrencyVaalFragment1_3", 40, 36, false),          // Sacrifice at Noon
        ("/CurrencyVaalFragment1_4", 40, 36, false),          // Sacrifice at Dusk
        ("/CurrencyOfferingToTheGoddess", 25, 36, false),     // Offering to the Goddess
        ("/CurrencyVaalFragment1_1", 18, 60, false),          // Sacrifice at Midnight
        ("/CurrencyFragmentPantheonFlask", 15, 68, false),    // Divine Vessel
        ("/CurrencyVaalFragment2_1", 10, 68, false),          // Mortal Rage
        ("/CurrencyVaalFragment2_2", 10, 68, false),          // Mortal Hope
        ("/CurrencyVaalFragment2_3", 10, 68, false),          // Mortal Ignorance
        ("/CurrencyVaalFragment2_4", 10, 68, false),          // Mortal Grief
        ("/CurrencyLegionFragmentKarui", 6, 75, false),       // Timeless Karui Emblem
        ("/CurrencyLegionFragmentEternal", 6, 75, false),     // Timeless Eternal Emblem
        ("/CurrencyLegionFragmentVaal", 5, 75, false),        // Timeless Vaal Emblem
        ("/CurrencyLegionFragmentTemplar", 4, 75, false),     // Timeless Templar Emblem
        ("/CurrencyLegionFragmentMaraketh", 3, 75, false),    // Timeless Maraketh Emblem
        ("/CurrencyElderFragment1", 5, 80, false),            // Fragment of Enslavement
        ("/CurrencyElderFragment2", 5, 80, false),            // Fragment of Eradication
        ("/CurrencyElderFragment3", 5, 80, false),            // Fragment of Constriction
        ("/CurrencyElderFragment4", 5, 80, false),            // Fragment of Purification
        ("/CurrencyMavenKeyFragment", 6, 83, true),           // Crescent Splinter
        ("/CurrencyShaperFragment1", 4, 84, false),           // Fragment of the Phoenix
        ("/CurrencyShaperFragment2", 4, 84, false),           // Fragment of the Minotaur
        ("/CurrencyShaperFragment3", 4, 84, false),           // Fragment of the Chimera
        ("/CurrencyShaperFragment4", 4, 84, false),           // Fragment of the Hydra
    ];

    /// <summary>Real Abyss bases: the four Eye Jewels (excluding special Assembled Eye Jewel) and Stygian Vise.</summary>
    static readonly (string Id, int Weight)[] AbyssItems =
    [
        ("/JewelAbyssMelee", 4),
        ("/JewelAbyssRanged", 4),
        ("/JewelAbyssCaster", 4),
        ("/JewelAbyssSummoner", 4),
        ("/BeltAbyss", 4),
    ];

    /// <summary>GUESS weights of the Breach pool: the splinters common, the Blessings and the Flesh of Xesht rare.</summary>
    const int BreachSplinterWeight = 40, BreachBlessingWeight = 5, BreachFleshWeight = 2;

    /// <summary>GUESS weights of the fossils: the plain ones common, the specialised ones less, the sought-after ones rare (by the end of the id).</summary>
    static readonly string[] RareFossils = ["LuckyModRolls", "Mirror", "GemLevel", "CorruptEssence", "Random"];
    static readonly string[] UncommonFossils = ["Vaal", "Abyss", "SellPrice", "Sockets", "Enchant", "MinionsAuras", "Quality", "Speed", "Mana", "CasterMods", "AttackMods"];
    const int CommonFossilWeight = 30, UncommonFossilWeight = 10, RareFossilWeight = 3;

    /// <summary>The four corruption-only essences (Hysteria, Insanity, Horror, Delirium).</summary>
    static readonly string[] CorruptionEssences = ["Hysteria1", "Insanity1", "Horror1", "Delirium1"];

    /// <summary>Non-scroll currency entries from <see cref="ItemDrops.Currency"/> (Scroll of Wisdom &amp; Portal Scroll excluded from Currency rewards).</summary>
    static readonly (string Id, double Weight)[] RewardCurrency =
        [.. ItemDrops.Currency.Where(c => !c.Id.EndsWith("/CurrencyIdentification", StringComparison.Ordinal) && !c.Id.EndsWith("/CurrencyPortal", StringComparison.Ordinal))];

    static bool Weighted(string kind) => kind is "harbinger" or "breach" or "fossils" or "essences" or "fragments" or "abyss";

    static int Weight(string kind, ItemCatalog.BaseType t)
    {
        switch (kind)
        {
            case "harbinger":
                foreach (var (id, weight, _) in HarbingerItems) if (t.Id.EndsWith(id, StringComparison.Ordinal)) return weight;
                return 1;
            case "fragments":
                foreach (var (id, weight, _, _) in FragmentItems) if (t.Id.EndsWith(id, StringComparison.Ordinal)) return weight;
                return 1;
            case "abyss":
                foreach (var (id, weight) in AbyssItems) if (t.Id.EndsWith(id, StringComparison.Ordinal)) return weight;
                return 1;
            case "breach":
                return t.Id.EndsWith("UpgradeUniqueGeneral", StringComparison.Ordinal) ? BreachFleshWeight
                    : t.Id.Contains("UpgradeUnique", StringComparison.Ordinal) ? BreachBlessingWeight : BreachSplinterWeight;
            case "fossils":
                return RareFossils.Any(f => t.Id.EndsWith("CurrencyDelveCrafting" + f, StringComparison.Ordinal)) ? RareFossilWeight
                    : UncommonFossils.Any(f => t.Id.EndsWith("CurrencyDelveCrafting" + f, StringComparison.Ordinal)) ? UncommonFossilWeight : CommonFossilWeight;
            case "essences":
                if (t.Name.StartsWith("Shrieking ", StringComparison.Ordinal)) return 4;
                if (t.Name.StartsWith("Screaming ", StringComparison.Ordinal)) return 12;
                if (t.Name.StartsWith("Wailing ", StringComparison.Ordinal)) return 22;
                if (t.Name.StartsWith("Weeping ", StringComparison.Ordinal)) return 30;
                if (t.Name.StartsWith("Muttering ", StringComparison.Ordinal)) return 35;
                return 40;
            default: return 1;
        }
    }

    static int MinLevelFor(string kind, ItemCatalog.BaseType t)
    {
        int level = Math.Max(0, t.DropLevel);
        if (kind == "fragments")
        {
            foreach (var (id, _, minLevel, _) in FragmentItems)
                if (t.Id.EndsWith(id, StringComparison.Ordinal)) return Math.Max(level, minLevel);
        }
        else if (kind == "essences")
        {
            if (t.Name.StartsWith("Shrieking ", StringComparison.Ordinal)) return Math.Max(level, 68);
            if (t.Name.StartsWith("Screaming ", StringComparison.Ordinal)) return Math.Max(level, 48);
            if (t.Name.StartsWith("Wailing ", StringComparison.Ordinal)) return Math.Max(level, 30);
        }
        return level;
    }

    static bool AllowsStack(string kind, ItemCatalog.BaseType t) => kind switch
    {
        "harbinger" => HarbingerItems.Any(h => h.Stackable && t.Id.EndsWith(h.Id, StringComparison.Ordinal)),
        "fragments" => FragmentItems.Any(f => f.Stackable && t.Id.EndsWith(f.Id, StringComparison.Ordinal)),
        "breach" => t.Id.EndsWith("Shard", StringComparison.Ordinal),
        "fossils" => Weight("fossils", t) >= CommonFossilWeight,
        "essences" => !t.Name.StartsWith("Shrieking ", StringComparison.Ordinal),
        _ => false,
    };

    /// <summary>The catalog's items of each kind picked from the catalog (not a container roll), built once per catalog, sorted by effective min drop level.</summary>
    sealed class Pools
    {
        readonly Dictionary<string, (ItemCatalog.BaseType Type, int MinLevel)[]> byKind = [];
        IReadOnlySet<int>? gems;

        public (ItemCatalog.BaseType Type, int MinLevel)[] Of(ItemCatalog catalog, string kind)
        {
            lock (byKind)
            {
                if (byKind.TryGetValue(kind, out var pool)) return pool;
                var gemRows = kind == "gems" ? gems ??= GemSources.DropPool(catalog) : null;
                return byKind[kind] = [.. catalog.All
                    .Where(t => t.Supported && t.DropLevel >= 0 && !t.Components.Contains("Quest") && Eligible(kind, t, gemRows))
                    .Select(t => (Type: t, MinLevel: MinLevelFor(kind, t)))
                    .OrderBy(x => x.MinLevel)];
            }
        }
    }

    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ItemCatalog, Pools> PoolsOf = new();

    static bool Eligible(string kind, ItemCatalog.BaseType t, IReadOnlySet<int>? gems) => kind switch
    {
        "gems" => gems!.Contains(t.Row) || t.Id.StartsWith("Metadata/Items/Gems/SkillGemVaal", StringComparison.Ordinal),
        "abyss" => AbyssItems.Any(a => t.Id.EndsWith(a.Id, StringComparison.Ordinal)),
        "harbinger" => HarbingerItems.Any(h => t.Id.EndsWith(h.Id, StringComparison.Ordinal)),
        "breach" => t.Id.Contains("/Currency/CurrencyBreach", StringComparison.Ordinal) && (t.Id.EndsWith("Shard", StringComparison.Ordinal) || t.Id.Contains("UpgradeUnique", StringComparison.Ordinal)),
        "essences" => t.Id.Contains("/Currency/CurrencyEssence", StringComparison.Ordinal)
            && !t.Name.StartsWith("Deafening ", StringComparison.Ordinal)
            && !CorruptionEssences.Any(e => t.Id.EndsWith(e, StringComparison.Ordinal)),
        "fragments" => FragmentItems.Any(f => t.Id.EndsWith(f.Id, StringComparison.Ordinal)),
        "fossils" => t.Id.Contains("/CurrencyDelveCrafting", StringComparison.Ordinal),
        _ => false,
    };

    /// <summary>Scales a drop or splinter count by a positive multiplier with fractional rounding (supports both Ruthless &lt; 1 and Eternal Conflict &gt; 1).</summary>
    static int ScaleCount(int baseCount, double multiplier, Random rng, int minimum = 0)
    {
        if (baseCount <= 0) return 0;
        if (!double.IsFinite(multiplier) || Math.Abs(multiplier - 1.0) < 1e-9) return baseCount;
        if (multiplier <= 0) return minimum;
        double scaled = baseCount * multiplier;
        int whole = (int)Math.Floor(scaled);
        if (rng.NextDouble() < scaled - whole) whole++;
        return Math.Max(minimum, whole);
    }

    /// <summary>The items of an opened chest by its reward types, plus its faction's splinters (factionId null: none);
    /// honors league drop scaling, splinter duplication, additional splinter chance, and Emblematic emblem upgrades.</summary>
    public static List<Item> Chest(Player player, int[] rewards, string? factionId, bool hoard, int level, Random rng)
    {
        var rules = Program.LeagueFor(player).Rules;
        var stats = LegionMonolith.AreaStats(player);
        var made = new List<Item>();
        var stacks = new List<(string Item, int Count)>();
        foreach (int row in rewards)
        {
            int count = ScaleCount(hoard ? HoardItemsPerReward : ChestItemsPerReward, rules.ItemDropMultiplier, rng, minimum: hoard ? 1 : 0);
            if (count > 0) RollReward(player, made, stacks, row, level, count, rng);
        }
        if (factionId is not null && rng.Next(100) < (hoard ? HoardSplinterChance : ChestSplinterChance))
        {
            int splinters = ScaleCount(rng.Next(hoard ? HoardSplinterMin : ChestSplinterMin, (hoard ? HoardSplinterMax : ChestSplinterMax) + 1),
                rules.ItemDropMultiplier, rng, minimum: hoard ? 1 : 0);
            if (splinters > 0) AddSplinters(stacks, factionId, splinters, stats, isChest: true, isGeneral: false, rng);
        }
        var catalog = global::Game.ItemsFor(player);
        ExpandStacks(catalog, stacks, level, made);
        return made;
    }

    static void ExpandStacks(ItemCatalog catalog, List<(string Item, int Count)> stacks, int level, List<Item> made)
    {
        foreach (var (id, count) in stacks)
            if (catalog.Find(id) is { Supported: true } type)
                for (int left = count; left > 0; left -= Math.Max(1, (int)type.MaxStack))
                    made.Add(ItemCatalog.Create(type) with { ItemLevel = (byte)level, StackSize = (ushort)Math.Min(left, Math.Max(1, (int)type.MaxStack)) });
    }

    /// <summary>The kinds picked from the catalog's own pool, not rolled by a container (the tests read them).</summary>
    static readonly HashSet<string> PoolKinds = ["gems", "abyss", "harbinger", "breach", "essences", "fragments", "fossils"];

    /// <summary>The items a reward row can give at a level when it is picked from a catalog pool; null for the kinds that are rolled otherwise.</summary>
    internal static IReadOnlyList<ItemCatalog.BaseType>? Pool(ItemCatalog catalog, int row, int level)
    {
        string kind = Kinds.GetValueOrDefault(row, "generic");
        return PoolKinds.Contains(kind) ? [.. PoolsOf.GetValue(catalog, _ => new Pools()).Of(catalog, kind).Where(x => x.MinLevel <= level).Select(x => x.Type)] : null;
    }

    /// <summary>The Harbinger pool's weight of an item (the Mirror Shard's is the smallest).</summary>
    internal static int HarbingerWeight(ItemCatalog.BaseType t) => Weight("harbinger", t);

    internal static int WeightOf(string kind, ItemCatalog.BaseType t) => Weight(kind, t);

    internal static IEnumerable<int> RewardRows => Kinds.Keys;

    static bool warned;

    /// <summary>GameEvents.MonsterKilled: a fighter's reward symbols end at its death (the area's Buffs end them only at removal). Only reads the entry:
    /// Drops may have run first and taken it, and then cleared them itself.</summary>
    public static async Task Killed(MonsterKill kill)
    {
        if (!LegionStasis.AnyFighter || LegionStasis.FighterOf(kill.Player, kill.Monster.ObjectId, remove: false) is not { } fighter) return;
        if (fighter.Rewards.Length > 0) kill.Player.Area.Buffs.ClearRow(kill.Monster.ObjectId, LegionStasis.RewardBuff);
        if (fighter.Tier == 3) await LegionSpeech.Say(kill.Player, kill.Monster.VarietyId, "Defeat");
    }

    /// <summary>GameEvents.Drops: the common path is a volatile flag, then one lookup in the area's table.</summary>
    public static void Drops(DropRoll drop)
    {
        if (!LegionStasis.AnyFighter || LegionStasis.FighterOf(drop.Player, drop.Monster.ObjectId, remove: true) is not { } fighter) return;
        var player = drop.Player;
        if (fighter.Rewards.Length > 0) player.Area.Buffs.ClearRow(drop.Monster.ObjectId, LegionStasis.RewardBuff);
        if (!GameEvents.AllowsLoot(player) || Environment.GetEnvironmentVariable("Game_DROPS") == "0") return;
        var rules = Program.LeagueFor(player).Rules;
        var stats = LegionMonolith.AreaStats(player);
        var rng = global::Game.AreaRng(player);
        int level = Math.Clamp(drop.Monster.Level, 1, 100);
        int bonusRarity = fighter.Tier == 2 ? Math.Max(0, fighter.SergeantRarityPct) : 0;
        if (bonusRarity > 0) drop.Rarity *= 1.0 + bonusRarity / 100.0;
        // Eternal Conflict (ScarabLegionNew5): each extra break-out increases reward drops by 30% (patch 3.27.0c+).
        double breakoutMult = 1.0 + Math.Max(0, fighter.BreakOutCount - 1) * 0.30;
        foreach (int row in fighter.Rewards)
        {
            int count = ScaleCount(ItemsPerReward, rules.ItemDropMultiplier * breakoutMult, rng);
            if (count > 0) RollReward(player, drop.Made, drop.Stacks, row, level, count, rng, bonusRarity);
        }
        // Splinters: sergeants and the general only (the chests: LegionRewards.Chest).
        if (fighter.Tier == 2 && rng.Next(100) < SergeantSplinterChance)
        {
            int splinters = ScaleCount(rng.Next(SergeantSplinterMin, SergeantSplinterMax + 1), rules.ItemDropMultiplier * breakoutMult, rng);
            if (splinters > 0) AddSplinters(drop.Stacks, fighter.FactionId, splinters, stats, isChest: false, isGeneral: false, rng);
        }
        else if (fighter.Tier == 3)
        {
            int splinters = ScaleCount(rng.Next(GeneralSplinterMin, GeneralSplinterMax + 1), rules.ItemDropMultiplier * breakoutMult, rng, minimum: 1);
            AddSplinters(drop.Stacks, fighter.FactionId, splinters, stats, isChest: false, isGeneral: true, rng);
            if (Factions.TryGetValue(fighter.FactionId == "Faridun" ? "Maraketh" : fighter.FactionId, out var f))
            {
                if (level >= CrystalMinLevel && MapDevices.IsMapArea(player.Layout.Hash) && rng.Next(100) < GeneralCrystalChance) drop.Stacks.Add((f.Crystal, 1));
                if (rng.Next(1000) < GeneralJewelPermille && CreateTimelessJewel(global::Game.ItemsFor(player), fighter.FactionId, level, rng) is { } jewel)
                    drop.Made.Add(jewel);
            }
        }
    }

    /// <summary>Adds faction splinters with all Phase 4 Atlas/Scarab/Idol modifiers: General splinter scaling,
    /// additional splinter chance, monster/chest duplication, and Emblematic splinter-to-emblem conversion.</summary>
    internal static void AddSplinters(List<(string Item, int Count)> stacks, string factionId, int count,
        IReadOnlyDictionary<uint, int> stats, bool isChest, bool isGeneral, Random rng)
    {
        if (count <= 0 || !Factions.TryGetValue(factionId == "Faridun" ? "Maraketh" : factionId, out var f)) return;
        if (isGeneral)
        {
            int genPct = stats.GetValueOrDefault(Stat.MapLegionGeneralsSplinterDropAmountPlusPct);
            if (genPct != 0) count = Math.Max(1, (int)Math.Round(count * Math.Max(0, 100 + genPct) / 100.0));
        }
        int extraChance = stats.GetValueOrDefault(Stat.MapLegionMonstersChestsAdditionalSplinterChancePct);
        if (extraChance > 0)
        {
            count += extraChance / 100;
            if (rng.Next(100) < extraChance % 100) count++;
        }
        bool alwaysDupe = !isChest && stats.GetValueOrDefault(Stat.MapLegionMonsterSplinterEmblemDropsDuplicated) > 0;
        int dupePct = stats.GetValueOrDefault(Stat.MapLegionMonsterChestSplinterDropsDuplicatedChancePct)
            + stats.GetValueOrDefault(isChest ? Stat.MapLegionChestOnlySplinterDropsDuplicatedChancePct : Stat.MapLegionMonsterOnlySplinterDropsDuplicatedChancePct);
        if (alwaysDupe || (dupePct > 0 && rng.Next(100) < Math.Clamp(dupePct, 0, 100)))
            count *= 2;
        int emblemPermyriad = stats.GetValueOrDefault(Stat.MapReplaceLegionSplinterWithLegionFragmentChancePermyriad);
        if (emblemPermyriad > 0)
        {
            int emblems = 0;
            for (int i = 0; i < count; i++)
                if (rng.Next(10000) < emblemPermyriad) emblems++;
            int remaining = count - emblems;
            if (emblems > 0) stacks.Add((f.Emblem, emblems));
            if (remaining > 0) stacks.Add((f.Splinter, remaining));
            return;
        }
        stacks.Add((f.Splinter, count));
    }

    /// <summary>Creates a faction-matched unidentified Timeless Jewel with random conqueror and seed via <see cref="TimelessJewelCatalog"/>.</summary>
    public static Item? CreateTimelessJewel(ItemCatalog items, string factionId, int level, Random rng)
    {
        if (!Factions.TryGetValue(factionId == "Faridun" ? "Maraketh" : factionId, out var f)) return null;
        if (TimelessJewelCatalog.Find(f.Jewel) is not { Conquerors.Length: > 0 } entry) return null;
        var conqueror = entry.Conquerors[rng.Next(entry.Conquerors.Length)];
        if (TimelessJewelCatalog.Create(items, entry.Name, conquerorIndex: conqueror.Index, random: rng) is not { } item) return null;
        return item with { ItemLevel = (byte)Math.Clamp(level, 1, 100), Identified = false };
    }

    /// <summary>Generates the complete end-of-encounter loot burst for the Domain of Timeless Conflict: faction-matched
    /// Timeless Jewels, Enshrouding Crystals, accumulated Sergeant/General reward
    /// symbol pools, and diminishing-returns kill loot (unaffected by player IIR/IIQ, scaled by league drop rules).</summary>
    public static List<Item> RollDomainRewards(Player player, IReadOnlyList<int> rewardRows,
        IReadOnlyDictionary<string, int> generalKills, int totalKills, int factionCount, int level, Random rng)
    {
        var rules = Program.LeagueFor(player).Rules;
        var catalog = global::Game.ItemsFor(player);
        var drops = global::Game.DropsFor(player);
        var made = new List<Item>();
        var stacks = new List<(string Item, int Count)>();
        int clampedLevel = Math.Clamp(level, 1, 100);

        // 1. Timeless Jewels & Enshrouding Crystals from defeated faction Generals.
        int jewelsDropped = 0;
        var killedFactions = generalKills.Where(kv => kv.Value > 0).ToList();
        foreach (var (factionId, kills) in killedFactions)
        {
            int rolls = Math.Min(DomainJewelMaxRolls, Math.Max(1, (kills + 1) / DomainKillsPerJewelRoll));
            int jewelPct = Math.Clamp(DomainJewelBasePct + Math.Min(DomainJewelKillCapPct, kills * DomainJewelPerKillPct), 0, DomainJewelMaxPct);
            for (int r = 0; r < rolls; r++)
            {
                if (rng.Next(100) < jewelPct && CreateTimelessJewel(catalog, factionId, clampedLevel, rng) is { } jewel)
                {
                    made.Add(jewel);
                    jewelsDropped++;
                }
            }
            if (Factions.TryGetValue(factionId == "Faridun" ? "Maraketh" : factionId, out var f))
            {
                if (rng.Next(100) < DomainCrystalBasePct) stacks.Add((f.Crystal, 1));
            }
        }
        // Guarantee at least one Timeless Jewel on 4-way/5-way runs when at least one General was slain.
        if (jewelsDropped == 0 && killedFactions.Count > 0 && factionCount >= 4)
        {
            var top = killedFactions.OrderByDescending(kv => kv.Value).First();
            if (CreateTimelessJewel(catalog, top.Key, clampedLevel, rng) is { } guaranteed) made.Add(guaranteed);
        }

        // 2. Accumulated Sergeant & General reward rows (capped to keep final burst crisp and responsive).
        int maxRewardRows = Math.Min(rewardRows.Count, DomainRewardRowsBase);
        for (int i = 0; i < maxRewardRows; i++)
        {
            int count = ScaleCount(1, rules.ItemDropMultiplier, rng, minimum: rules.ItemDropMultiplier >= 1.0 ? 1 : 0);
            if (count > 0) RollReward(player, made, stacks, rewardRows[i], clampedLevel, count, rng);
        }

        // 3. Diminishing-returns generic loot from total army kills.
        int genericCount = ScaleCount((int)Math.Clamp(Math.Round(Math.Sqrt(Math.Max(0, totalKills)) * DomainGenericBase), 0, DomainGenericMax),
            rules.ItemDropMultiplier, rng);
        if (genericCount > 0)
            made.AddRange(drops.Container(clampedLevel, genericCount, DomainRarityBase, rng).Select(d => d.Item));

        ExpandStacks(catalog, stacks, clampedLevel, made);
        if (made.Count > DomainMaxDrops) made.RemoveRange(DomainMaxDrops, made.Count - DomainMaxDrops);
        return made;
    }

    static void AddSplinter(List<(string Item, int Count)> stacks, string factionId, int count)
    {
        if (count > 0 && Factions.TryGetValue(factionId == "Faridun" ? "Maraketh" : factionId, out var f)) stacks.Add((f.Splinter, count));
    }

    /// <summary>A stack of the faction's Timeless Splinters, min..max. Also for the chests.</summary>
    public static void Splinter(List<(string Item, int Count)> stacks, string factionId, int min, int max, Random rng) =>
        AddSplinter(stacks, factionId, rng.Next(min, max + 1));

    static void RollReward(Player player, List<Item> made, List<(string Item, int Count)> stacks, int row, int level, int count, Random rng, int bonusRarity = 0)
    {
        var catalog = global::Game.ItemsFor(player);
        var drops = global::Game.DropsFor(player);
        var rolls = global::Game.RollsFor(player);
        var league = Program.LeagueFor(player);
        var rules = league.Rules;
        string kind = Kinds.GetValueOrDefault(row, "generic");
        IEnumerable<Item> Items(IEnumerable<ItemDrops.Drop> d) => d.Select(x => x.Item);
        switch (kind)
        {
            case "currency":
            {
                // Real currency orbs weighted by ItemDrops.Currency (Scroll of Wisdom & Portal Scroll excluded).
                var candidates = new List<(ItemCatalog.BaseType Type, double Weight)>();
                double total = 0;
                foreach (var (id, weight) in RewardCurrency)
                    if (catalog.Find(id) is { Supported: true } t && t.DropLevel <= level)
                    {
                        candidates.Add((t, weight));
                        total += weight;
                    }
                for (int i = 0; i < count && candidates.Count > 0; i++)
                {
                    double pick = rng.NextDouble() * total;
                    var chosen = candidates[0];
                    foreach (var c in candidates) { chosen = c; if ((pick -= c.Weight) < 0) break; }
                    int stack = chosen.Weight >= 50 ? rng.Next(1, 3) : 1;
                    made.Add(ItemCatalog.Create(chosen.Type) with { ItemLevel = (byte)level, StackSize = (ushort)Math.Min(chosen.Type.MaxStack, stack) });
                }
                return;
            }
            case "unique":
            {
                var uniques = global::Game.UniquesFor(player);
                var eligibleUniques = uniques.All
                    .Where(u => drops.IsDropEligibleUnique(u, level) && catalog.Find(u.BaseTypeId) is { } bt && ItemDrops.Droppable(bt) && bt.DropLevel <= level)
                    .ToList();
                for (int i = 0; i < count && eligibleUniques.Count > 0; i++)
                    if (drops.PickUnique(eligibleUniques, rng) is { } u && uniques.Create(u, rng) is { } item)
                        made.Add(item with { ItemLevel = (byte)level, Identified = false });
                return;
            }
            case "maps":
            {
                // Uses RollMapDropForKill so Voidstones, tier upgrades and MapMonsterNoMapDrops apply.
                int before = made.Count;
                int mapTier = level >= 68 ? Math.Clamp(level - 67, 1, 16) : 0;
                if (mapTier > 0)
                {
                    for (int i = 0; i < count; i++)
                        if (global::Game.RollMapDropForKill(player, level, MonsterRarity.Rare, mapTier, rng, MapChanceFactor) is { } map)
                            made.Add(map.Item);
                }
                if (made.Count == before) made.AddRange(Items(drops.Container(level, count, 0, rng)));
                return;
            }
            case "scarabs":
            {
                int before = made.Count;
                var mapMods = global::Game.MapModsOf(player);
                for (int i = 0; i < count; i++)
                    if (drops.Scarab(rng, level, mapMods) is { } scarab) made.Add(scarab.Item with { ItemLevel = (byte)level });
                if (made.Count == before) made.AddRange(Items(drops.Container(level, count, 0, rng)));
                return;
            }
            case "divination":
            {
                // Mix of weighted Divination Cards and Stacked Decks, honoring count (Sergeant 2 / Chest 3 / Hoard 4).
                bool hasDeck = catalog.Find(StackedDeck) is { Supported: true };
                for (int i = 0; i < count; i++)
                {
                    if (hasDeck && rng.Next(100) < 35) stacks.Add((StackedDeck, 1));
                    else if (drops.Card(rng, level) is { } card) made.Add(card.Item);
                    else if (hasDeck) stacks.Add((StackedDeck, 1));
                }
                return;
            }
            case "jewellery":
            case "armour":
            case "weapons":
            {
                Func<ItemCatalog.BaseType, bool> accepts = kind switch
                {
                    "jewellery" => t => t.Class is "Ring" or "Amulet" or "Belt",
                    "armour" => t => t.Class is "Body Armour" or "Helmet" or "Gloves" or "Boots" or "Shield",
                    _ => t => ItemDrops.EquipmentClasses.Contains(t.Class) && (t.Components.Contains("Weapon") || t.Class == "Quiver"),
                };
                int rarity = (int)Math.Round((EquipmentRewardRarity + bonusRarity) * Math.Max(0, rules.EquipmentRarityMultiplier));
                foreach (var d in drops.Container(level, count, rarity, rng, accepts))
                {
                    var item = d.Item;
                    // Outside Ruthless, Legion equipment rewards roll at least Magic/Rare rather than white trash.
                    if (!league.HardMode && item.Rarity == ItemRarity.Normal && d.Type.Components.Contains("Mods"))
                        item = rolls.Roll(item, ItemRarity.Rare, rng) with { Identified = false };
                    made.Add(item);
                }
                return;
            }
            case "generic":
                made.AddRange(Items(drops.Container(level, count, 100 + bonusRarity, rng)));
                return;
        }
        // The classes picked from the catalog's own list (cached per catalog, sorted by effective drop level).
        var pool = PoolsOf.GetValue(catalog, _ => new Pools()).Of(catalog, kind);
        int eligible = 0;
        while (eligible < pool.Length && pool[eligible].MinLevel <= level) eligible++;
        if (eligible == 0)
        {
            if (!warned) { warned = true; Console.Error.WriteLine($"legion reward '{kind}' has no items in the catalog: generic drops instead"); }
            made.AddRange(Items(drops.Container(level, count, 0, rng)));
            return;
        }
        for (int i = 0; i < count; i++)
        {
            if (kind == "gems")
            {
                RollGem(catalog, made, stacks, pool, eligible, level, rng);
                continue;
            }
            var type = PickFromPool(kind, pool, eligible, rng);
            if (kind == "abyss")
            {
                made.Add(RollAbyssItem(catalog, rolls, type, level, league.HardMode, rng));
                continue;
            }
            int stack = AllowsStack(kind, type) && !type.Id.EndsWith(MirrorShard, StringComparison.Ordinal)
                ? rng.Next(StackMin, StackMax + 1) : 1;
            made.Add(ItemCatalog.Create(type) with { ItemLevel = (byte)level, StackSize = (ushort)Math.Min(type.MaxStack, stack) });
        }
    }

    static ItemCatalog.BaseType PickFromPool(string kind, (ItemCatalog.BaseType Type, int MinLevel)[] pool, int eligible, Random rng)
    {
        if (!Weighted(kind)) return pool[rng.Next(eligible)].Type;
        int total = 0;
        for (int k = 0; k < eligible; k++) total += Weight(kind, pool[k].Type);
        int pick = rng.Next(Math.Max(1, total));
        var chosen = pool[0].Type;
        for (int k = 0; k < eligible; k++)
        {
            chosen = pool[k].Type;
            if ((pick -= Weight(kind, chosen)) < 0) break;
        }
        return chosen;
    }

    /// <summary>Legion Gem reward: Superior quality gems (5-20%), occasional Vaal skill gems (corrupted), and occasional Gemcutter's Prism.</summary>
    static void RollGem(ItemCatalog catalog, List<Item> made, List<(string Item, int Count)> stacks,
        (ItemCatalog.BaseType Type, int MinLevel)[] pool, int eligible, int level, Random rng)
    {
        if (level >= 35 && rng.Next(100) < 15 && catalog.Find(GemcuttersPrism) is { Supported: true })
        {
            stacks.Add((GemcuttersPrism, 1));
            return;
        }
        bool wantVaal = rng.Next(100) < 20;
        ItemCatalog.BaseType? picked = null;
        if (wantVaal)
        {
            var vaals = new List<ItemCatalog.BaseType>();
            for (int k = 0; k < eligible; k++)
                if (pool[k].Type.Id.StartsWith("Metadata/Items/Gems/SkillGemVaal", StringComparison.Ordinal)) vaals.Add(pool[k].Type);
            if (vaals.Count > 0) picked = vaals[rng.Next(vaals.Count)];
        }
        picked ??= pool[rng.Next(eligible)].Type;
        bool isVaal = picked.Id.StartsWith("Metadata/Items/Gems/SkillGemVaal", StringComparison.Ordinal);
        byte quality = (byte)rng.Next(5, 21);
        made.Add(ItemCatalog.Create(picked) with { ItemLevel = (byte)level, Quality = quality, Corrupted = isVaal });
    }

    /// <summary>Abyss reward: Magic/Rare Abyss Eye Jewels and Rare Stygian Vise with its Abyssal Socket (never white 0-mod jewels).</summary>
    static Item RollAbyssItem(ItemCatalog catalog, ItemRolls rolls, ItemCatalog.BaseType type, int level, bool hardMode, Random rng)
    {
        var item = ItemCatalog.Create(type, random: rng) with { ItemLevel = (byte)level };
        if (type.Id == AbyssJewels.StygianVise)
            item = AbyssJewels.StygianSockets(item, ItemModStats.Local(item, catalog.Mods));
        var rarity = type.Id == AbyssJewels.StygianVise
            ? (hardMode && rng.Next(100) < 50 ? ItemRarity.Magic : ItemRarity.Rare)
            : (rng.Next(100) < (hardMode ? 75 : 35) ? ItemRarity.Magic : ItemRarity.Rare);
        return rolls.Roll(item, rarity, rng) with { Identified = false };
    }
}
