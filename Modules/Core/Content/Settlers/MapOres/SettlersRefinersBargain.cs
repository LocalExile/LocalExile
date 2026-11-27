using GameServer.Core.Protocol.Instance;
using GameServer.Modding;

namespace GameServer.Modules.Settlers.MapOres;

/// <summary>
/// Implements the <c>Refiner's Bargain</c> Atlas Keystone (<c>PassiveSkills</c> row 3571):
/// <list type="bullet">
/// <item>Sulphite Deposits grant random Settlers Ore (10% of Sulphite amount) instead of Sulphite.</item>
/// <item>Harvest Monsters grant random Kingsmarch Crops (250% of Lifeforce amount) instead of Lifeforce.</item>
/// <item>Possessed Monster equipment drops have a 5% chance to be converted into Thaumaturgic Dust.</item>
/// </list>
/// </summary>
public static class SettlersRefinersBargain
{
    static readonly VillageResourceKind[] Crops =
    [
        VillageResourceKind.Wheat,
        VillageResourceKind.Corn,
        VillageResourceKind.Pumpkin,
        VillageResourceKind.Globulora,
        VillageResourceKind.Orbisfera,
    ];

    static readonly HashSet<string> EquipmentClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Claw", "Dagger", "Rune Dagger", "Wand", "One Hand Sword", "Thrusting One Hand Sword",
        "One Hand Axe", "One Hand Mace", "Sceptre", "Bow", "Staff", "Warstaff",
        "Two Hand Sword", "Two Hand Axe", "Two Hand Mace", "FishingRod",
        "Helmet", "Body Armour", "Gloves", "Boots", "Shield", "Quiver",
        "Amulet", "Ring", "Belt",
    };

    public static bool IsEquipment(ItemCatalog.BaseType type) => EquipmentClasses.Contains(type.Class);

    /// <summary>
    /// Refiner's Bargain Sulphite interceptor: converts mined Sulphite into a random Settlers Ore (or Bar under Refinement).
    /// </summary>
    public static bool InterceptSulphite(Player player, int sulphiteAmount, MapMods? mods)
    {
        if (mods is null || mods.Get(Stat.MapSulphiteGrantsRandomOreInstead) <= 0) return false;
        int pct = mods.Get(Stat.MapSulphiteGrantsRandomOrePctOfSulphite);
        if (pct <= 0) pct = 10;

        var rawStats = player.Instance?.KeptAreaStats ?? AreaLeagueStats(player) ?? new Dictionary<uint, int>();
        var oreStats = MapOreStats.From(rawStats);
        int level = Math.Clamp(MapRunOf(player)?.Area.Level is > 0 and int ml ? ml : ServerLevels.AreaLevel(player.Layout.Id), 1, 100);
        var rng = AreaRng(player);

        var oreKind = oreStats.PickOreType(rng, level);
        double oreFoundScale = Math.Max(0.1, (100.0 + oreStats.OreFoundPlusPct) / 100.0);
        long amount = Math.Max(1L, (long)Math.Round(sulphiteAmount * (pct / 100.0) * oreFoundScale * VillageResourceHooks.OreFoundMultiplier));
        bool refined = oreStats.OreFoundAsBarsInstead;
        var resource = refined ? MapOreTables.RefinedBarOf(oreKind) : MapOreTables.PendingResourceOf(oreKind);

        VillageResourceHooks.GrantResource(player, resource, amount, "refiners-bargain:sulphite");
        _ = PlayerOf(player).Tell($"Refiner's Bargain: converted Sulphite into {amount:N0} {MapOreTables.DisplayName(oreKind, refined)}!");
        return true;
    }

    /// <summary>
    /// Refiner's Bargain Harvest callback: grants a random Kingsmarch Crop (default 250% of Lifeforce amount).
    /// </summary>
    public static void GrantHarvestCrop(Player player, int lifeforceAmount, MapMods? mods)
    {
        if (lifeforceAmount <= 0 || mods is null || mods.Get(Stat.MapHarvestMonstersGrantRandomCropsInstead) <= 0) return;
        int pct = mods.Get(Stat.MapHarvestMonstersGrantPctJuiceRandomCrop);
        if (pct <= 0) pct = 250;

        long amount = Math.Max(1L, (long)Math.Round(lifeforceAmount * (pct / 100.0)));
        var crop = Crops[AreaRng(player).Next(Crops.Length)];
        VillageResourceHooks.GrantResource(player, crop, amount, "refiners-bargain:harvest");
    }

    /// <summary>
    /// <see cref="GameEvents.Drops"/> hook: attaches a post-roll transform to Possessed monster kills so
    /// equipment drops have <c>map_possessed_monster_equipment_drops_converted_to_dust_%_chance</c> to become Dust.
    /// </summary>
    public static void OnDrops(DropRoll roll)
    {
        if (roll.Monster.ObjectId == 0 || roll.Player.Instance is null) return;
        int chancePct = roll.Player.Instance.KeptAreaStats is { } kept
            ? kept.GetValueOrDefault(Stat.MapPossessedMonsterEquipmentDropsConvertedToDustPctChance)
            : (MapModsOf(roll.Player)?.Get(Stat.MapPossessedMonsterEquipmentDropsConvertedToDustPctChance) ?? 0);
        if (chancePct <= 0) return;

        if (!roll.Player.Area.Monsters.TryGetValue(roll.Monster.ObjectId, out var monster)
            || !TormentCatalog.Has(monster.Mods, grip: true))
            return;

        roll.AddTransform(drops => ConvertPossessedDrops(roll.Player, drops, chancePct, AreaRng(roll.Player)));
    }

    /// <summary>
    /// Converts eligible equipment items in <paramref name="drops"/> to Thaumaturgic Dust at <paramref name="chancePct"/>%.
    /// </summary>
    public static long ConvertPossessedDrops(Player player, List<ItemDrops.Drop> drops, int chancePct, Random rng)
    {
        if (chancePct <= 0 || drops.Count == 0) return 0;
        long totalDust = 0;

        for (int i = drops.Count - 1; i >= 0; i--)
        {
            var drop = drops[i];
            if (!IsEquipment(drop.Type)) continue;
            if (chancePct < 100 && rng.Next(100) >= chancePct) continue;

            long dust = EstimateDisenchantDust(drop.Item, drop.Type);
            if (dust <= 0) continue;
            drops.RemoveAt(i);
            totalDust += dust;
        }

        if (totalDust > 0)
            VillageResourceHooks.GrantResource(player, VillageResourceKind.MagicDust, totalDust, "refiners-bargain:possessed-dust");
        return totalDust;
    }

    /// <summary>
    /// Computes an equipment item's exact 3.29.3c Thaumaturgic Dust disenchant value (<c>0x1424c6af0</c>):
    /// <list type="bullet">
    /// <item>Unique: <c>round(VillageUniqueDisenchantValues[uniqueName].Value * 500)</c> (fallback 500).</item>
    /// <item>Magic/Rare: sum of <c>VillageBalancePerLevelShared[mod.RequiredLevel].__col6</c> across explicit mods
    /// (normal items fall back to a single base-level mod entry).</item>
    /// <item>Bonus %: <c>quality * 2 + popcount(influences) * 50 + corruptedImplicits * 50</c>.</item>
    /// <item>ItemLevel multiplier: <c>ilvl &lt; 46 =&gt; 50</c>; <c>46..67 =&gt; ((ilvl * 50 - 2300) / 22) + 50</c>;
    /// <c>68+ =&gt; min(500, ((ilvl * 100 - 6800) / 4) + 100)</c>.</item>
    /// </list>
    /// </summary>
    public static long EstimateDisenchantDust(Item item, ItemCatalog.BaseType type)
    {
        int ilvl = Math.Clamp(item.ItemLevel > 0 ? item.ItemLevel : type.DropLevel, 1, 100);
        long baseDust;

        if (item.Rarity == ItemRarity.Unique)
        {
            double? tableVal = MapOreTables.UniqueDisenchantBaseValue(item.NameWords);
            baseDust = tableVal is > 0 ? (long)Math.Round(tableVal.Value * 500.0) : 500L;
        }
        else
        {
            long sum = 0;
            int explicitCount = 0;
            var modCatalog = Items?.Mods;
            foreach (var mod in item.Mods)
            {
                if (mod.Section != ModSection.Explicit) continue;
                explicitCount++;
                int reqLevel = modCatalog?.ByHash(mod.Hash)?.Level is > 0 and int ml ? Math.Clamp(ml, 1, 100) : ilvl;
                sum += MapOreTables.BaseOreAmount(reqLevel); // VillageBalancePerLevelShared.__col6 (Disenchant Mod Dust base)
            }
            if (explicitCount == 0)
            {
                // Unidentified/synthetic fallback: approximate expected explicit mod count by rarity.
                int fallbackMods = item.Rarity switch
                {
                    ItemRarity.Rare => 4,
                    ItemRarity.Magic => 2,
                    _ => 1,
                };
                sum = (long)MapOreTables.BaseOreAmount(ilvl) * fallbackMods;
            }
            baseDust = sum;
        }

        int influenceCount = System.Numerics.BitOperations.PopCount((uint)item.Influences);
        int corruptedImplicits = item.Corrupted ? item.Mods.Count(m => m.Section == ModSection.Implicit) : 0;
        long bonusPct = Math.Clamp((int)item.Quality, 0, 30) * 2L + influenceCount * 50L + corruptedImplicits * 50L;

        long ilvlMult = ilvl < 46 ? 50L
            : ilvl < 68 ? ((ilvl * 50L - 2300L) / 22L) + 50L
            : Math.Min(500L, ((ilvl * 100L - 6800L) / 4L) + 100L);

        long finalDust = (((baseDust * (100L + bonusPct)) / 100L) * ilvlMult) / 100L;
        return Math.Max(1L, finalDust);
    }
}
