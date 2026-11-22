using GameServer.Core.Protocol.Instance;

namespace GameServer.Modules.Crucible;

/// <summary>
/// Evaluates Crucible <c>SellBonus</c> weapon passive nodes when a weapon or shield is sold to a vendor.
/// Covers all 29 <c>Type = 2</c> rows in <c>WeaponPassiveSkills</c>, including <c>SellNodeDouble</c> doubling
/// and <c>SellPriceMagmaticOre</c> / <c>SellPriceSpecificUnique</c>.
/// </summary>
public static class CrucibleSellRewards
{
    static readonly Dictionary<uint, string> CurrencyByStat = new()
    {
        [Stat.WeaponTreeLocalSellPriceScouringOrb] = "Metadata/Items/Currency/CurrencyConvertToNormal",
        [Stat.WeaponTreeLocalSellPriceChaosOrb] = "Metadata/Items/Currency/CurrencyRerollRare",
        [Stat.WeaponTreeLocalSellPriceOrbOfRegret] = "Metadata/Items/Currency/CurrencyPassiveRefund",
        [Stat.WeaponTreeLocalSellPriceRegalOrb] = "Metadata/Items/Currency/CurrencyUpgradeMagicToRare",
        [Stat.WeaponTreeLocalSellPriceVaalOrb] = "Metadata/Items/Currency/CurrencyCorrupt",
        [Stat.WeaponTreeLocalSellPriceGemcuttersPrism] = "Metadata/Items/Currency/CurrencyGemQuality",
        [Stat.WeaponTreeLocalSellPriceBlessedOrb] = "Metadata/Items/Currency/CurrencyRerollImplicit",
        [Stat.WeaponTreeLocalSellPriceAwakenedSextant] = "Metadata/Items/Currency/CurrencyAddAtlasModHigh",
        [Stat.WeaponTreeLocalSellPriceOrbOfAnnulment] = "Metadata/Items/Currency/CurrencyRemoveMod",
        [Stat.WeaponTreeLocalSellPriceExaltedOrb] = "Metadata/Items/Currency/CurrencyAddModToRare",
        [Stat.WeaponTreeLocalSellPriceDivineOrb] = "Metadata/Items/Currency/CurrencyModValues",
        [Stat.WeaponTreeLocalSellPriceSacredOrb] = "Metadata/Items/Currency/CurrencyRerollDefences",
        [Stat.WeaponTreeLocalSellPriceIgneousGeode] = CrucibleTables.IgneousGeodeId,
        [Stat.WeaponTreeLocalSellPriceCrystallineGeode] = CrucibleTables.CrystallineGeodeId,
        [Stat.WeaponTreeLocalSellPriceForAdditionalCrucibleExperience] = CrucibleTables.MagmaticOreId,
    };

    const uint DoubleStat = Stat.WeaponTreeLocalSellPriceOfTreeNodesDoubled;
    const uint SpecificUniqueStat = Stat.WeaponTreeLocalSellPriceForAdditionalSpecificUnique;

    /// <summary>
    /// Computes bonus vendor items granted by <paramref name="allocatedMods"/> on a sold weapon/shield.
    /// Returns an empty list if no SellBonus node is allocated.
    /// </summary>
    public static List<Item> Evaluate(
        IReadOnlyList<ItemMod> allocatedMods,
        ItemCatalog.BaseType? soldType,
        ItemCatalog catalog,
        UniqueCatalog uniques,
        Random random)
    {
        if (allocatedMods.Count == 0) return [];

        bool doubled = false;
        var uniqueBases = new List<ItemCatalog.BaseType>();
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var itemMod in allocatedMods)
        {
            if (catalog.Mods.ByHash(itemMod.Hash) is not { } mod) continue;
            for (int i = 0; i < mod.Stats.Count; i++)
            {
                uint statId = mod.Stats[i].Id;
                int value = i < itemMod.Values.Count ? itemMod.Values[i] : mod.Stats[i].Min;
                if (statId == DoubleStat && value > 0)
                {
                    doubled = true;
                }
                else if (statId == SpecificUniqueStat)
                {
                    // Stat description 10790: 0 is "an additional Unique of this Base Type", any other value names
                    // the base through WeaponPassiveTreeUniqueBaseTypes.
                    var named = value == 0 ? soldType
                        : CrucibleTables.Current.SellUniqueBaseRows.TryGetValue(value, out int baseRow) ? catalog.Row(baseRow) : null;
                    if (named is not null) uniqueBases.Add(named);
                }
                else if (CurrencyByStat.TryGetValue(statId, out string? baseId) && value > 0)
                {
                    counts[baseId] = checked(counts.GetValueOrDefault(baseId) + value);
                }
            }
        }

        if (counts.Count == 0 && uniqueBases.Count == 0) return [];

        int multiplier = doubled ? 2 : 1;
        var rewards = new List<Item>();

        foreach (var (baseId, rawCount) in counts)
        {
            if (catalog.Find(baseId) is not { Supported: true } type) continue;
            int total = rawCount * multiplier;
            int maxStack = Math.Max(1, (int)type.MaxStack);
            while (total > 0)
            {
                int stack = Math.Min(total, maxStack);
                var item = ItemCatalog.Create(type) with { StackSize = (ushort)stack };
                if (baseId.Equals(CrucibleTables.MagmaticOreId, StringComparison.OrdinalIgnoreCase))
                    item = item with { StoredExperience = CrucibleTables.SellNodeOreExperience };
                rewards.Add(item);
                total -= stack;
            }
        }

        foreach (var named in uniqueBases)
        {
            var ofBase = uniques.All.Where(d => d.BaseTypeId == named.Id).ToList();
            for (int i = 0; i < multiplier && ofBase.Count > 0; i++)
                if (uniques.Create(ofBase[random.Next(ofBase.Count)], random) is { } unique) rewards.Add(unique);
        }

        return rewards;
    }
}
