using GameServer.Core;
using GameServer.Core.Protocol.Instance;
using GameServer.Modding;

namespace GameServer.Modules.Deepwater;

/// <summary>Precomputed per-cell chart and border stat rules for an Allflame run (built once at <c>StartRun</c>, O(1)
/// indexed and 0-alloc on the per-kill / per-hit hot path).</summary>
public readonly record struct DeepwaterCellRules(
    double ExpFactor,
    int SulphurFoundPct,
    bool FreeLanterns,
    int MagicExtraMods,
    bool RareTrappedInEssence,
    bool RareDropSulphur,
    double ExtraScarabFactor,
    double ExtraCurrencyFactor,
    (string ItemId, int Count)[]? RareCurrencyDrops,
    int RareExtraScarab,
    int RareSupportGemChance,
    int EquipToGoldPct,
    bool CurrencyToStackedDecks,
    bool NoEquipment,
    bool NoFlask,
    bool DropCorrupted,
    int FracturedChancePct,
    int MaxQualityFlaskChancePct,
    int RingToUniquePct,
    int AmuletToUniquePct,
    int BeltToUniquePct)
{
    public static readonly DeepwaterCellRules Empty = new(
        ExpFactor: 1.0, SulphurFoundPct: 0, FreeLanterns: false, MagicExtraMods: 0,
        RareTrappedInEssence: false, RareDropSulphur: false,
        ExtraScarabFactor: 1.0, ExtraCurrencyFactor: 1.0,
        RareCurrencyDrops: null, RareExtraScarab: 0, RareSupportGemChance: 0,
        EquipToGoldPct: 0, CurrencyToStackedDecks: false, NoEquipment: false, NoFlask: false,
        DropCorrupted: false, FracturedChancePct: 0, MaxQualityFlaskChancePct: 0,
        RingToUniquePct: 0, AmuletToUniquePct: 0, BeltToUniquePct: 0);

    public bool HasTransforms =>
        EquipToGoldPct > 0 || CurrencyToStackedDecks || NoEquipment || NoFlask
        || DropCorrupted || FracturedChancePct > 0 || MaxQualityFlaskChancePct > 0
        || RingToUniquePct > 0 || AmuletToUniquePct > 0 || BeltToUniquePct > 0;

    public bool HasDropHooks =>
        ExtraScarabFactor > 1.0 || ExtraCurrencyFactor > 1.0
        || RareCurrencyDrops is { Length: > 0 } || RareExtraScarab > 0 || RareSupportGemChance > 0
        || HasTransforms;

    public bool Any =>
        ExpFactor != 1.0 || SulphurFoundPct != 0 || FreeLanterns || MagicExtraMods > 0
        || RareTrappedInEssence || RareDropSulphur || HasDropHooks;
}

/// <summary>Parses Domain 39 (Chart) and Domain 40 (Voyage Border) stats into precomputed per-cell
/// <see cref="DeepwaterCellRules"/> and applies post-roll drop transforms (<c>DropRoll.AddTransform</c>) and
/// rare-monster drops without allocating on inactive kills.</summary>
public static class DeepwaterChartStats
{
    public const string StackedDeckId = "Metadata/Items/DivinationCards/DivinationCardDeck";
    const string SulphurItemId = "Metadata/Items/Currency/CurrencyDeepwater";
    const string DeepwaterItemPrefix = "Metadata/Items/Deepwater/";

    // Exact 11 currency-orb stats from Domain 40 (Mods WHERE Domain = 40, DeepwaterBorderRareMonster*).
    static readonly (uint Stat, string ItemId)[] RareCurrencyStats =
    [
        (Stat.ChartRareMonsterDropXAdditionalAncient, "Metadata/Items/Currency/CurrencyRerollUnique"),
        (Stat.ChartRareMonsterDropXAdditionalAnnulment, "Metadata/Items/Currency/CurrencyRemoveMod"),
        (Stat.ChartRareMonsterDropXAdditionalBlessed, "Metadata/Items/Currency/CurrencyRerollImplicit"),
        (Stat.ChartRareMonsterDropXAdditionalChaos, "Metadata/Items/Currency/CurrencyRerollRare"),
        (Stat.ChartRareMonsterDropXAdditionalChromatic, "Metadata/Items/Currency/CurrencyRerollSocketColours"),
        (Stat.ChartRareMonsterDropXAdditionalDivine, "Metadata/Items/Currency/CurrencyModValues"),
        (Stat.ChartRareMonsterDropXAdditionalExalted, "Metadata/Items/Currency/CurrencyAddModToRare"),
        (Stat.ChartRareMonsterDropXAdditionalGemcutters, "Metadata/Items/Currency/CurrencyGemQuality"),
        (Stat.ChartRareMonsterDropXAdditionalRegal, "Metadata/Items/Currency/CurrencyUpgradeMagicToRare"),
        (Stat.ChartRareMonsterDropXAdditionalRegret, "Metadata/Items/Currency/CurrencyPassiveRefund"),
        (Stat.ChartRareMonsterDropXAdditionalVaal, "Metadata/Items/Currency/CurrencyCorrupt"),
    ];

    static ItemCatalog.BaseType[] SupportGems = [];

    public static void LoadGameData()
    {
        var list = new List<ItemCatalog.BaseType>();
        foreach (int row in GemSources.DropPool(Program.Items, "data"))
            if (Program.Items.Row(row) is { Class: "Support Skill Gem", DropLevel: >= 1 } t && ItemDrops.Droppable(t))
                list.Add(t);
        SupportGems = [.. list];
    }

    // Cached droppable uniques per jewellery class for chart_{ring,amulet,belt}_to_unique_*_%_chance.
    sealed record JewelleryUniques(
        IReadOnlyList<UniqueCatalog.Definition> Source,
        (UniqueCatalog.Definition Def, int DropLevel)[] Rings,
        (UniqueCatalog.Definition Def, int DropLevel)[] Amulets,
        (UniqueCatalog.Definition Def, int DropLevel)[] Belts);
    static volatile JewelleryUniques? _jewelleryUniques;

    /// <summary>Builds <see cref="DeepwaterCellRules"/> for one cell from its effective stats (cell + global) and,
    /// on a Voyage, its cell-only stats (so stats already applied via <c>MapModsOf</c> on 1×1 Charts are never
    /// double-counted).</summary>
    public static DeepwaterCellRules ParseCell(
        IReadOnlyDictionary<uint, int> effective,
        IReadOnlyDictionary<uint, int>? cellOnly,
        bool voyage)
    {
        int expPct = effective.GetValueOrDefault(Stat.MapExperienceGainPlusPct);
        double expFactor = expPct == 0 ? 1.0 : Math.Max(0, 100 + expPct) / 100.0;
        int sulphurFound = effective.GetValueOrDefault(Stat.MapDeepwaterLeagueResourceFoundPlusPct);
        bool freeLanterns = effective.GetValueOrDefault(Stat.ChartDoNotConsumeLanternsOnPlacement) > 0;
        // On a 1×1 Chart, MapModsOf(player) already holds cellOnly + global; on a Voyage, MapModsOf holds global only.
        int magicExtra = voyage && cellOnly is not null
            ? Math.Max(0, cellOnly.GetValueOrDefault(Stat.MapMagicMonsterNumAdditionalModifiers))
            : 0;
        bool rareEssence = effective.GetValueOrDefault(Stat.MapRareMonstersTrappedInEssences) > 0;
        bool rareSulphur = effective.GetValueOrDefault(Stat.ChartRareMonstersDropDeadMansSulphur) > 0;

        // Scarab and currency uber more-drop mods: MapModApply.ExtraDropsFromMap handles MapModsOf(player)
        // (1×1 Chart + Voyage global), so only apply cellOnly on Voyages to avoid double-counting.
        double scarabFactor = 1.0;
        double currencyFactor = 1.0;
        if (voyage && cellOnly is not null)
        {
            int scarabPct = cellOnly.GetValueOrDefault(Stat.MapScarabDropChancePlusPctFinalFromUberMod);
            if (scarabPct > 0) scarabFactor = (100 + scarabPct) / 100.0;
            int currencyPct = cellOnly.GetValueOrDefault(Stat.MapCurrencyDropChancePlusPctFinalFromUberMod);
            if (currencyPct > 0) currencyFactor = (100 + currencyPct) / 100.0;
        }

        List<(string ItemId, int Count)>? rareDrops = null;
        for (int i = 0; i < RareCurrencyStats.Length; i++)
        {
            var (stat, itemId) = RareCurrencyStats[i];
            int count = effective.GetValueOrDefault(stat);
            if (count > 0) (rareDrops ??= []).Add((itemId, Math.Min(count, 20)));
        }
        int rareScarab = Math.Clamp(effective.GetValueOrDefault(Stat.ChartRareMonsterDropXAdditionalScarab), 0, 20);
        int rareSupport = Math.Clamp(effective.GetValueOrDefault(Stat.ChartRareMonsterDropSupportGemChancePct), 0, 100);

        int equipToGold = Math.Clamp(effective.GetValueOrDefault(Stat.ChartMonsterEquipmentDropsConvertedToGoldPct), 0, 100);
        bool toStackedDecks = effective.GetValueOrDefault(Stat.ChartDroppedCurrencyConvertedToStackedDecks) > 0;
        bool noEquip = effective.GetValueOrDefault(Stat.ChartMonstersDropNoEquipment) > 0;
        bool noFlask = effective.GetValueOrDefault(Stat.ChartMonstersDropNoFlask) > 0;
        bool dropCorrupted = effective.GetValueOrDefault(Stat.MapItemsDropCorrupted) > 0;
        int fracturedPct = Math.Clamp(effective.GetValueOrDefault(Stat.MapDroppedItemsAreFracturedChancePct), 0, 100);
        int maxQualityFlaskPct = Math.Clamp(effective.GetValueOrDefault(Stat.MapFlasksDropWithMaxQualityChancePct), 0, 100);
        int ringUniquePct = Math.Clamp(effective.GetValueOrDefault(Stat.ChartRingToUniqueRingPctChance), 0, 100);
        int amuletUniquePct = Math.Clamp(effective.GetValueOrDefault(Stat.ChartAmuletToUniqueAmuletPctChance), 0, 100);
        int beltUniquePct = Math.Clamp(effective.GetValueOrDefault(Stat.ChartBeltToUniqueBeltPctChance), 0, 100);

        return new DeepwaterCellRules(
            ExpFactor: expFactor,
            SulphurFoundPct: sulphurFound,
            FreeLanterns: freeLanterns,
            MagicExtraMods: magicExtra,
            RareTrappedInEssence: rareEssence,
            RareDropSulphur: rareSulphur,
            ExtraScarabFactor: scarabFactor,
            ExtraCurrencyFactor: currencyFactor,
            RareCurrencyDrops: rareDrops?.ToArray(),
            RareExtraScarab: rareScarab,
            RareSupportGemChance: rareSupport,
            EquipToGoldPct: equipToGold,
            CurrencyToStackedDecks: toStackedDecks,
            NoEquipment: noEquip,
            NoFlask: noFlask,
            DropCorrupted: dropCorrupted,
            FracturedChancePct: fracturedPct,
            MaxQualityFlaskChancePct: maxQualityFlaskPct,
            RingToUniquePct: ringUniquePct,
            AmuletToUniquePct: amuletUniquePct,
            BeltToUniquePct: beltUniquePct);
    }

    /// <summary>Builds the per-cell rules array for a planned run (returns null when no cell has any active rule,
    /// keeping the kill hot path a single null check).</summary>
    internal static (DeepwaterCellRules[]? Rules, bool HasDrops, bool HasExp, bool HasRareSulphur) BuildFor(DeepwaterPlan? plan)
    {
        if (plan is null || plan.CellStats.Count == 0) return (null, false, false, false);
        int count = plan.CellStats.Count;
        var rules = new DeepwaterCellRules[count];
        bool any = false, hasDrops = false, hasExp = false, hasRareSulphur = false;
        for (int i = 0; i < count; i++)
        {
            var effective = plan.Effective(i);
            var cellOnly = plan.Voyage ? plan.CellStats[i] : null;
            var r = ParseCell(effective, cellOnly, plan.Voyage);
            rules[i] = r;
            if (r.Any) any = true;
            if (r.HasDropHooks) hasDrops = true;
            if (r.ExpFactor != 1.0) hasExp = true;
            if (r.RareDropSulphur) hasRareSulphur = true;
        }
        return any ? (rules, hasDrops, hasExp, hasRareSulphur) : (null, false, false, false);
    }

    /// <summary>Whether placing a lantern inside cell <paramref name="cell"/> consumes a lantern charge
    /// (<c>false</c> when the cell carries <c>chart_do_not_consume_lanterns_on_placement</c>).</summary>
    public static bool ConsumesLantern(DeepwaterCellRules[]? cellRules, int cell) =>
        !(cellRules is not null && (uint)cell < (uint)cellRules.Length && cellRules[cell].FreeLanterns);

    /// <summary>Picks a random droppable support gem whose DropLevel fits <paramref name="level"/> and league rules allow.</summary>
    public static Item? RollSupportGem(int level, Random rng, LeagueRules? league = null)
    {
        var all = SupportGems;
        int eligible = 0;
        for (int i = 0; i < all.Length; i++)
            if (all[i].DropLevel <= level && league?.AllowsGemBase(all[i].Id) != false) eligible++;
        if (eligible == 0) return null;
        int target = rng.Next(eligible);
        for (int i = 0; i < all.Length; i++)
        {
            if (all[i].DropLevel > level || league?.AllowsGemBase(all[i].Id) == false) continue;
            if (target-- == 0) return ItemCatalog.Create(all[i]) with { ItemLevel = 1 };
        }
        return null;
    }

    public static bool IsFlaskClass(string itemClass) =>
        itemClass is "LifeFlask" or "ManaFlask" or "HybridFlask" or "UtilityFlask" or "UtilityFlaskCritical";

    /// <summary>Applies all active post-roll drop transforms for <paramref name="rules"/> to <paramref name="drops"/>
    /// in deterministic order: no-equipment/no-flask filters, equipment→gold, currency→Stacked Decks,
    /// jewellery→unique upgrades, max-quality flasks, fractured equipment, and corrupted drops.</summary>
    public static void ApplyDropTransforms(
        List<ItemDrops.Drop> drops,
        ItemCatalog catalog,
        UniqueCatalog? uniques,
        in DeepwaterCellRules rules,
        int monsterLevel,
        MonsterRarity monsterRarity,
        Random rng,
        Func<string, double>? uniqueWeight = null)
    {
        if (drops.Count == 0 || !rules.HasTransforms) return;

        // 1. No-equipment / no-flask filters first so removed items are not converted or mutated.
        if (rules.NoEquipment || rules.NoFlask)
        {
            for (int i = drops.Count - 1; i >= 0; i--)
            {
                string cls = drops[i].Type.Class;
                if ((rules.NoEquipment && ItemDrops.EquipmentClasses.Contains(cls))
                    || (rules.NoFlask && IsFlaskClass(cls)))
                {
                    drops.RemoveAt(i);
                }
            }
            if (drops.Count == 0) return;
        }

        // 2. Equipment → Gold conversion.
        if (rules.EquipToGoldPct > 0 && catalog.Find(ItemDrops.GoldId) is { Supported: true } goldType)
        {
            for (int i = 0; i < drops.Count; i++)
            {
                if (!ItemDrops.EquipmentClasses.Contains(drops[i].Type.Class)) continue;
                if (rules.EquipToGoldPct < 100 && rng.Next(100) >= rules.EquipToGoldPct) continue;
                double rarityScale = drops[i].Item.Rarity switch
                {
                    ItemRarity.Magic => 1.5,
                    ItemRarity.Rare => 2.5,
                    ItemRarity.Unique => 5.0,
                    _ => 1.0,
                };
                int amount = Math.Max(1, (int)Math.Round((8 + 2.5 * Math.Max(1, monsterLevel)) * rarityScale * (0.75 + 0.5 * rng.NextDouble())));
                drops[i] = new ItemDrops.Drop(
                    ItemCatalog.Create(goldType) with { StackSize = (ushort)Math.Min(amount, ushort.MaxValue) },
                    goldType);
            }
        }

        // 3. Dropped Currency → Stacked Decks (excluding Gold, Stacked Decks, Sulphur, and Deepwater items/Ducats).
        if (rules.CurrencyToStackedDecks && catalog.Find(StackedDeckId) is { Supported: true } deckType)
        {
            int maxStack = Math.Max(1, (int)deckType.MaxStack);
            int count = drops.Count;
            for (int i = 0; i < count; i++)
            {
                var d = drops[i];
                if (d.Type.Class != "StackableCurrency") continue;
                if (d.Type.Id == StackedDeckId || d.Type.Id == SulphurItemId
                    || d.Type.Id.StartsWith(DeepwaterItemPrefix, StringComparison.Ordinal))
                    continue;
                int total = Math.Max(1, (int)d.Item.StackSize);
                int first = Math.Min(total, maxStack);
                drops[i] = new ItemDrops.Drop(ItemCatalog.Create(deckType) with { StackSize = (ushort)first }, deckType);
                for (int left = total - first; left > 0; left -= maxStack)
                    drops.Add(new ItemDrops.Drop(ItemCatalog.Create(deckType) with { StackSize = (ushort)Math.Min(left, maxStack) }, deckType));
            }
        }

        // 4. Ring / Amulet / Belt → Unique jewellery conversion.
        if (uniques is not null && (rules.RingToUniquePct > 0 || rules.AmuletToUniquePct > 0 || rules.BeltToUniquePct > 0))
        {
            for (int i = 0; i < drops.Count; i++)
            {
                var d = drops[i];
                if (d.Item.Rarity == ItemRarity.Unique || d.Item.Mirrored || d.Item.Corrupted
                    || d.Type.Components.Contains("Quest"))
                    continue;
                int chance = d.Type.Class switch
                {
                    "Ring" => rules.RingToUniquePct,
                    "Amulet" => rules.AmuletToUniquePct,
                    "Belt" => rules.BeltToUniquePct,
                    _ => 0,
                };
                if (chance <= 0 || (chance < 100 && rng.Next(100) >= chance)) continue;
                if (RollUniqueJewellery(catalog, uniques, d.Type.Class, d.Item.ItemLevel > 0 ? d.Item.ItemLevel : monsterLevel, rng, uniqueWeight) is { } uniqueDrop)
                    drops[i] = uniqueDrop;
            }
        }

        // 5. Flasks drop with maximum quality (20%).
        if (rules.MaxQualityFlaskChancePct > 0)
        {
            for (int i = 0; i < drops.Count; i++)
            {
                var d = drops[i];
                if (!IsFlaskClass(d.Type.Class) || d.Item.Quality >= 20) continue;
                if (rules.MaxQualityFlaskChancePct < 100 && rng.Next(100) >= rules.MaxQualityFlaskChancePct) continue;
                drops[i] = d with { Item = d.Item with { Quality = 20 } };
            }
        }

        // 6. Fractured equipment drops (Magic or Rare equipment with at least one explicit mod).
        if (rules.FracturedChancePct > 0)
        {
            for (int i = 0; i < drops.Count; i++)
            {
                var d = drops[i];
                if (d.Item.FracturedCount > 0 || d.Item.Corrupted || d.Item.Mirrored
                    || d.Item.Influences != ItemInfluence.None || d.Item.Synthesised || !d.Type.CanBeFractured
                    || !ItemDrops.EquipmentClasses.Contains(d.Type.Class))
                    continue;
                if (d.Item.Rarity is not (ItemRarity.Magic or ItemRarity.Rare)) continue;
                bool hasExplicit = false;
                var mods = d.Item.Mods;
                for (int m = 0; m < mods.Count; m++)
                {
                    if (mods[m].Section == ModSection.Explicit) { hasExplicit = true; break; }
                }
                if (!hasExplicit) continue;
                if (rules.FracturedChancePct < 100 && rng.Next(100) >= rules.FracturedChancePct) continue;
                drops[i] = d with { Item = d.Item with { FracturedCount = 1 } };
            }
        }

        // 7. Items drop corrupted (uses Core/Game's shared CorruptDrops rule).
        if (rules.DropCorrupted)
            global::Game.CorruptDrops(drops);
    }

    static ItemDrops.Drop? RollUniqueJewellery(ItemCatalog catalog, UniqueCatalog uniques, string itemClass, int itemLevel, Random rng, Func<string, double>? uniqueWeight)
    {
        var all = uniques.All;
        var index = _jewelleryUniques;
        if (index is null || !ReferenceEquals(index.Source, all))
        {
            var rings = new List<(UniqueCatalog.Definition Def, int DropLevel)>();
            var amulets = new List<(UniqueCatalog.Definition Def, int DropLevel)>();
            var belts = new List<(UniqueCatalog.Definition Def, int DropLevel)>();
            foreach (var def in all)
            {
                if (def.EncounterOnly) continue;
                if (catalog.Find(def.BaseTypeId) is not { Supported: true } baseType) continue;
                if (baseType.Class == "Ring") rings.Add((def, baseType.DropLevel));
                else if (baseType.Class == "Amulet") amulets.Add((def, baseType.DropLevel));
                else if (baseType.Class == "Belt") belts.Add((def, baseType.DropLevel));
            }
            _jewelleryUniques = index = new(all, [.. rings], [.. amulets], [.. belts]);
        }
        var pool = itemClass switch
        {
            "Ring" => index.Rings,
            "Amulet" => index.Amulets,
            "Belt" => index.Belts,
            _ => [],
        };
        if (pool.Length == 0) return null;
        double total = 0;
        for (int i = 0; i < pool.Length; i++)
        {
            if (pool[i].DropLevel > itemLevel) continue;
            double w = uniqueWeight is null ? 1.0 : uniqueWeight(pool[i].Def.Name);
            if (double.IsFinite(w) && w > 0) total += w;
        }
        UniqueCatalog.Definition? picked = null;
        if (total > 0)
        {
            double roll = rng.NextDouble() * total;
            for (int i = 0; i < pool.Length; i++)
            {
                if (pool[i].DropLevel > itemLevel) continue;
                double w = uniqueWeight is null ? 1.0 : uniqueWeight(pool[i].Def.Name);
                if (!double.IsFinite(w) || w <= 0) continue;
                picked = pool[i].Def;
                if ((roll -= w) < 0) break;
            }
        }
        if (picked is null) return null;
        byte ilvl = (byte)Math.Clamp(itemLevel, 1, 100);
        if (uniques.Create(picked with { ItemLevel = ilvl }, rng) is not { } created) return null;
        var type = catalog.ByHash(created.TypeHash);
        return type is null ? null : new ItemDrops.Drop(created with { Identified = false }, type);
    }
}
