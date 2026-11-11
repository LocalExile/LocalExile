using GameServer.Core.Protocol.Instance;
using GameServer.Core;

/// <summary>
/// Vendor-specific quest/class gem membership from the reviewed snapshot, joined to native quest offers.
/// Siosa and campaign Lilly use their own all-class unlocks; hard mode keeps its native gem index.
/// </summary>
public sealed class GemVendorStock(ItemCatalog items, QuestRewardCatalog rewards, NpcCatalog npcs, SkillCatalog skills, string folder = "data")
{
    // Cache immutable catalogues, never character stock: quest/class/rule filters are evaluated on every open.
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ItemCatalog,
        System.Collections.Concurrent.ConcurrentDictionary<(NpcCatalog Npcs, SkillCatalog Skills, bool Hard, bool Supports),
            Lazy<GemVendorStock>>> Cache = new();

    public static GemVendorStock For(ItemCatalog items, NpcCatalog npcs, SkillCatalog skills,
        bool hardMode = false, bool supportGems = true) => Cache.GetOrCreateValue(items)
        .GetOrAdd((npcs,skills,hardMode,supportGems), key => new(() => new(items,
            QuestRewardCatalog.Load(hardMode:key.Hard,supportGems:key.Supports),key.Npcs,key.Skills))).Value;

    static readonly string[] CurrencyIds =
    [
        "Metadata/Items/Currency/CurrencyIdentification", "Metadata/Items/Currency/CurrencyUpgradeToMagic",
        "Metadata/Items/Currency/CurrencyRerollMagic", "Metadata/Items/Currency/CurrencyUpgradeRandomly",
        "Metadata/Items/Currency/CurrencyUpgradeToRare",
    ];
    /// <summary>Campaign Lilly's gem shop opens with this flag (a6q4 "Fallen from Grace").</summary>
    public const string LillyUnlock = "UnlockedLillyGemShop";

    /// <summary>Siosa sells after a3q12 "A Fixture of Fate".</summary>
    public const string SiosaUnlock = "a3q12-DeliveredPages";

    /// <summary>The currency/flask/jewellery vendors that also sell gems (poedb "Vendor" roles), by NPCs.dat Id.</summary>
    static readonly HashSet<string> Vendors = new(StringComparer.OrdinalIgnoreCase)
    {
        "Metadata/NPC/Act1/Nessa", "Metadata/NPC/Act2/Yeena", "Metadata/NPC/Act3/ClarissaTown",
        "Metadata/NPC/Act4/PetarusVanja", "Metadata/NPC/Act5/Lani", "Metadata/NPC/Act6/Lilly",
        "Metadata/NPC/Act7/YeenaTown", "Metadata/NPC/Act8/ClarissaTown", "Metadata/NPC/Act9/PetarusVanja",
        "Metadata/NPC/Act10/Lani", "Metadata/NPC/Act10/Lilly", "Metadata/NPC/Epilogue/Lani",
        "Metadata/NPC/Epilogue/Lilly", "Metadata/NPC/Epilogue/Lilly2", "Metadata/NPC/Epilogue/LillyHideout", "Metadata/NPC/Act3/Siosa",
    };

    public static bool Sells(string npcId) => Vendors.Contains(npcId);

    readonly Dictionary<string,int> sellable = new[] { false, true }
        .SelectMany(support => new[] { 1, 2, 3, 4 }.SelectMany(colour => items.ShopGems(colour, support)))
        .ToDictionary(t => t.Id,t => t.Row,StringComparer.Ordinal);
    readonly Lazy<IReadOnlySet<int>> hardModeRows = new(() => HardModeGemRows(folder));

    /// <summary>The NPC's gem stock at character level/quest state: BaseItemTypes rows, in row order. Empty before the
    /// character has completed a quest that offers gems (or when campaign Lilly/Siosa's unlock is missing).
    /// Hideout Lilly serves every character who can visit her; her availability is the hideout unlock.</summary>
    public IReadOnlyList<int> Stock(string npcId, int classRow, IReadOnlySet<string> flags, bool hardMode = false, int level = 100, LeagueRules? rules = null)
    {
        bool hideoutLilly = npcId.Equals("Metadata/NPC/Epilogue/LillyHideout",StringComparison.OrdinalIgnoreCase);
        bool lilly = npcId.EndsWith("/Lilly", StringComparison.OrdinalIgnoreCase)
            || npcId.EndsWith("/Lilly2", StringComparison.OrdinalIgnoreCase)
            || npcId.EndsWith("/LillyHideout", StringComparison.OrdinalIgnoreCase);
        bool siosa = npcId.EndsWith("/Siosa", StringComparison.OrdinalIgnoreCase);
        if ((lilly && !hideoutLilly && !flags.Contains(LillyUnlock)) || (siosa && !flags.Contains(SiosaUnlock))) return [];
        rules ??= LeagueRules.Standard;
        if (!rules.SellsGemsAt(npcId)) return [];
        // The HardMode index lists what its vendor exchanges.
        if (hardMode)
            return [.. sellable.Values.Intersect(hardModeRows.Value).Where(row => items.Row(row) is { } type
                && rules.AllowsGemBase(type.Id)
                && items.Gem(type) is { Variants.Count: > 0 } gem && (rules.SellsSupportGems || !gem.IsSupport)
                && skills.PlayerLevelReq(gem.Variant(0).Effect, 1) <= level).Order()];
        if (lilly) return Filter(VendorCatalog.Default.Gems("Lilly Roth")
            .Where(rule => sellable.ContainsKey(rule.Base)).Select(rule => sellable[rule.Base]).Distinct().Order());
        var available = npcs.AvailableRewards(flags);
        bool Done(int row) => available.Contains(row) || rewards.Find(row)?.TakenFlag is { } taken && flags.Contains(taken);
        string vendor = npcId.Contains("/Act1/") ? "Nessa" : npcId.Contains("/Act2/") ? "Yeena"
            : npcId.Contains("/Act3/") ? "Clarissa" : npcId.Contains("/Act4/") ? "Petarus and Vanja" : "";
        if (siosa) vendor = "Siosa";
        var catalog = VendorCatalog.Default;
        var selected = vendor.Length > 0 ? catalog.Gems(vendor)
            : new[] { "Nessa", "Yeena", "Clarissa", "Petarus and Vanja" }.SelectMany(catalog.Gems);
        var rows = new SortedSet<int>();
        foreach (var rule in selected)
            if ((siosa || rule.Class == classRow) && (siosa || rule.Offers.Any(Done))
                && sellable.TryGetValue(rule.Base,out int row)) rows.Add(row);
        return Filter(rows);

        IReadOnlyList<int> Filter(IEnumerable<int> selectedRows) => [.. selectedRows.Where(row =>
            items.Row(row) is { } type && rules.AllowsGemBase(type.Id)
            && (rules.SellsSupportGems || items.Gem(type)?.IsSupport != true))];
    }

    static IReadOnlySet<int> HardModeGemRows(string folder)
    {
        var gems = ClientTables.Table("SkillGems");
        return ClientTables.Table("IndexableSkillGems").Where(r => !r.IsNull("IndexRuthless"))
            .SelectMany(r => r.Ints("SkillGem2"))
            .Where(row => row >= 0 && row < gems.Count)
            .Select(row => gems[row].Int("BaseItemTypesKey")).ToHashSet();
    }

    /// <summary>A vendor's price for one unlevelled gem: the currency by the gem's level-1 character level requirement
    /// (the official vendor tiers: 1–7 Scroll of Wisdom, 8–15 Orb of Transmutation, 16–27 Orb of Alteration, 28–37 Orb of
    /// Chance, 38+ Orb of Alchemy), one of it.</summary>
    public (string Currency, int Amount) Price(ItemCatalog.BaseType type)
    {
        int required = items.Gem(type) is { Variants.Count: > 0 } gem ? skills.PlayerLevelReq(gem.Variant(0).Effect, 1) : 0;
        return (CurrencyIds[Tier(required)], 1);
    }

    public static int Tier(int requiredLevel) =>
        requiredLevel <= 7 ? 0 : requiredLevel <= 15 ? 1 : requiredLevel <= 27 ? 2 : requiredLevel <= 37 ? 3 : 4;
}
