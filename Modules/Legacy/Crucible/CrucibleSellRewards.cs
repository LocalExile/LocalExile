using GameServer.Core.Protocol.Instance;

namespace GameServer.Modules.Crucible;

/// <summary>
/// Evaluates Crucible <c>SellBonus</c> weapon passive nodes when a weapon or shield is sold to a vendor.
/// Covers all 29 <c>Type = 2</c> rows in <c>WeaponPassiveSkills</c>, including <c>SellNodeDouble</c> doubling
/// and <c>SellPriceMagmaticOre</c> / <c>SellPriceSpecificUnique</c>.
/// </summary>
public static class CrucibleSellRewards
{
    static readonly Dictionary<string, string> CurrencyByStat = new(StringComparer.Ordinal)
    {
        ["weapon_tree_local_sell_price_scouring_orb"] = "Metadata/Items/Currency/CurrencyConvertToNormal",
        ["weapon_tree_local_sell_price_chaos_orb"] = "Metadata/Items/Currency/CurrencyRerollRare",
        ["weapon_tree_local_sell_price_orb_of_regret"] = "Metadata/Items/Currency/CurrencyPassiveRefund",
        ["weapon_tree_local_sell_price_regal_orb"] = "Metadata/Items/Currency/CurrencyUpgradeMagicToRare",
        ["weapon_tree_local_sell_price_vaal_orb"] = "Metadata/Items/Currency/CurrencyCorrupt",
        ["weapon_tree_local_sell_price_gemcutters_prism"] = "Metadata/Items/Currency/CurrencyGemQuality",
        ["weapon_tree_local_sell_price_blessed_orb"] = "Metadata/Items/Currency/CurrencyRerollImplicit",
        ["weapon_tree_local_sell_price_awakened_sextant"] = "Metadata/Items/Currency/CurrencyAddAtlasModHigh",
        ["weapon_tree_local_sell_price_orb_of_annulment"] = "Metadata/Items/Currency/CurrencyRemoveMod",
        ["weapon_tree_local_sell_price_exalted_orb"] = "Metadata/Items/Currency/CurrencyAddModToRare",
        ["weapon_tree_local_sell_price_divine_orb"] = "Metadata/Items/Currency/CurrencyModValues",
        ["weapon_tree_local_sell_price_sacred_orb"] = "Metadata/Items/Currency/CurrencyRerollDefences",
        ["weapon_tree_local_sell_price_igneous_geode"] = CrucibleTables.IgneousGeodeId,
        ["weapon_tree_local_sell_price_crystalline_geode"] = CrucibleTables.CrystallineGeodeId,
        ["weapon_tree_local_sell_price_for_additional_crucible_experience"] = CrucibleTables.MagmaticOreId,
    };

    const string DoubleStat = "weapon_tree_local_sell_price_of_tree_nodes_doubled";
    const string SpecificUniqueStat = "weapon_tree_local_sell_price_for_additional_specific_unique";

    /// <summary>
    /// Computes bonus vendor items granted by <paramref name="allocatedMods"/> on a sold weapon/shield.
    /// Returns an empty list if no SellBonus node is allocated.
    /// </summary>
    public static List<Item> Evaluate(
        IReadOnlyList<ItemMod> allocatedMods,
        ItemCatalog catalog,
        UniqueCatalog uniques,
        Random random)
    {
        if (allocatedMods.Count == 0) return [];

        bool doubled = false;
        int specificUniques = 0;
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var itemMod in allocatedMods)
        {
            if (catalog.Mods.ByHash(itemMod.Hash) is not { } mod) continue;
            for (int i = 0; i < mod.Stats.Count; i++)
            {
                string statId = mod.Stats[i].Id;
                int value = i < itemMod.Values.Count ? itemMod.Values[i] : mod.Stats[i].Min;
                if (statId == DoubleStat && value > 0)
                {
                    doubled = true;
                }
                else if (statId == SpecificUniqueStat)
                {
                    specificUniques++;
                }
                else if (CurrencyByStat.TryGetValue(statId, out string? baseId) && value > 0)
                {
                    counts[baseId] = checked(counts.GetValueOrDefault(baseId) + value);
                }
            }
        }

        if (counts.Count == 0 && specificUniques == 0) return [];

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
                rewards.Add(ItemCatalog.Create(type) with { StackSize = (ushort)stack });
                total -= stack;
            }
        }

        if (specificUniques > 0)
        {
            int rolls = specificUniques * multiplier;
            var allUniques = uniques.CollectionItems(random);
            if (allUniques.Count > 0)
            {
                for (int i = 0; i < rolls; i++)
                {
                    var pick = allUniques[random.Next(allUniques.Count)].Item;
                    if (pick is not null) rewards.Add(pick);
                }
            }
        }

        return rewards;
    }
}
