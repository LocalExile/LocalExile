using GameServer.Core.Protocol.Instance;

/// <summary>
/// Vendor-specific quest/class gem membership from the reviewed snapshot, joined to native quest offers.
/// Siosa and Lilly use their own all-class unlocks; hard mode keeps its native gem index.
/// </summary>
sealed class GemVendorStock(ItemCatalog items, QuestRewardCatalog rewards, NpcCatalog npcs, SkillCatalog skills, string folder = "data")
{
    static readonly string[] CurrencyIds =
    [
        "Metadata/Items/Currency/CurrencyIdentification", "Metadata/Items/Currency/CurrencyUpgradeToMagic",
        "Metadata/Items/Currency/CurrencyRerollMagic", "Metadata/Items/Currency/CurrencyUpgradeRandomly",
        "Metadata/Items/Currency/CurrencyUpgradeToRare",
    ];
    /// <summary>Lilly's gem shop opens with this flag (a6q4 "Fallen from Grace").</summary>
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

    readonly HashSet<int> sellable =
    [
        .. new[] { false, true }.SelectMany(support => new[] { 1, 2, 3, 4 }
            .SelectMany(colour => items.ShopGems(colour, support))).Select(t => t.Row),
    ];

    /// <summary>The NPC's gem stock at character level/quest state: BaseItemTypes rows, in row order. Empty before the
    /// character has completed a quest that offers gems (or when Lilly/Siosa's own unlock is missing).</summary>
    public IReadOnlyList<int> Stock(string npcId, int classRow, IReadOnlySet<string> flags, bool hardMode = false, int level = 100, LeagueRules? rules = null)
    {
        bool lilly = npcId.EndsWith("/Lilly", StringComparison.OrdinalIgnoreCase)
            || npcId.EndsWith("/Lilly2", StringComparison.OrdinalIgnoreCase)
            || npcId.EndsWith("/LillyHideout", StringComparison.OrdinalIgnoreCase);
        bool siosa = npcId.EndsWith("/Siosa", StringComparison.OrdinalIgnoreCase);
        if ((lilly && !flags.Contains(LillyUnlock)) || (siosa && !flags.Contains(SiosaUnlock))) return [];
        rules ??= LeagueRules.Standard;
        if (!rules.SellsGemsAt(npcId)) return [];
        // The HardMode index lists what its vendor exchanges.
        if (hardMode)
            return [.. sellable.Intersect(HardModeGemRows(folder)).Where(row => items.Row(row) is { } type
                && rules.AllowsGemBase(type.Id)
                && items.Gem(type) is { Variants.Count: > 0 } gem && (rules.SellsSupportGems || !gem.IsSupport)
                && skills.PlayerLevelReq(gem.Variant(0).Effect, 1) <= level).Order()];
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
                && items.Find(rule.Base) is { } type && sellable.Contains(type.Row)) rows.Add(type.Row);
        if (lilly)
        {
            rows.Clear();
            foreach (var rule in catalog.Gems("Lilly Roth"))
                if (items.Find(rule.Base) is { } type && sellable.Contains(type.Row)) rows.Add(type.Row);
        }
        return [.. rows.Where(row => items.Row(row) is { } type && rules.AllowsGemBase(type.Id)
            && (rules.SellsSupportGems || items.Gem(type)?.IsSupport != true))];
    }

    static IReadOnlySet<int> HardModeGemRows(string folder)
    {
        using var index = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(Path.Combine(folder, "tables/IndexableSkillGems.json")));
        using var gems = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(Path.Combine(folder, "tables/SkillGems.json")));
        return index.RootElement.EnumerateArray().Where(r => r.GetProperty("IndexRuthless").ValueKind == System.Text.Json.JsonValueKind.Number)
            .SelectMany(r => r.GetProperty("SkillGem2").EnumerateArray().Select(x => x.GetInt32()))
            .Where(row => row >= 0 && row < gems.RootElement.GetArrayLength())
            .Select(row => gems.RootElement[row].GetProperty("BaseItemTypesKey").GetInt32()).ToHashSet();
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
