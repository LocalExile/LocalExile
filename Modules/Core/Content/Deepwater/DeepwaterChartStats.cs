using GameServer.Core;
using GameServer.Core.Protocol.Instance;

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
    static readonly (string Stat, string ItemId)[] RareCurrencyStats =
    [
        ("chart_rare_monster_drop_x_additional_ancient", "Metadata/Items/Currency/CurrencyRerollUnique"),
        ("chart_rare_monster_drop_x_additional_annulment", "Metadata/Items/Currency/CurrencyRemoveMod"),
        ("chart_rare_monster_drop_x_additional_blessed", "Metadata/Items/Currency/CurrencyRerollImplicit"),
        ("chart_rare_monster_drop_x_additional_chaos", "Metadata/Items/Currency/CurrencyRerollRare"),
        ("chart_rare_monster_drop_x_additional_chromatic", "Metadata/Items/Currency/CurrencyRerollSocketColours"),
        ("chart_rare_monster_drop_x_additional_divine", "Metadata/Items/Currency/CurrencyModValues"),
        ("chart_rare_monster_drop_x_additional_exalted", "Metadata/Items/Currency/CurrencyAddModToRare"),
        ("chart_rare_monster_drop_x_additional_gemcutters", "Metadata/Items/Currency/CurrencyGemQuality"),
        ("chart_rare_monster_drop_x_additional_regal", "Metadata/Items/Currency/CurrencyUpgradeMagicToRare"),
        ("chart_rare_monster_drop_x_additional_regret", "Metadata/Items/Currency/CurrencyPassiveRefund"),
        ("chart_rare_monster_drop_x_additional_vaal", "Metadata/Items/Currency/CurrencyCorrupt"),
    ];

    static readonly Lazy<ItemCatalog.BaseType[]> SupportGems = new(() =>
    {
        var list = new List<ItemCatalog.BaseType>();
        for (int row = 0; Program.Items.Row(row) is { } t; row++)
            if (t.Class == "Support Skill Gem" && ItemDrops.Droppable(t)) list.Add(t);
        return [.. list];
    });

    // Cached droppable uniques per jewellery class for chart_{ring,amulet,belt}_to_unique_*_%_chance.
    sealed record JewelleryUniques(
        IReadOnlyList<UniqueCatalog.Definition> Source,
        UniqueCatalog.Definition[] Rings,
        UniqueCatalog.Definition[] Amulets,
        UniqueCatalog.Definition[] Belts);
    static volatile JewelleryUniques? _jewelleryUniques;

    /// <summary>Builds <see cref="DeepwaterCellRules"/> for one cell from its effective stats (cell + global) and,
    /// on a Voyage, its cell-only stats (so stats already applied via <c>MapModsOf</c> on 1×1 Charts are never
    /// double-counted).</summary>
    public static DeepwaterCellRules ParseCell(
        IReadOnlyDictionary<string, int> effective,
        IReadOnlyDictionary<string, int>? cellOnly,
        bool voyage)
    {
        int expPct = effective.GetValueOrDefault("map_experience_gain_+%");
        double expFactor = expPct == 0 ? 1.0 : Math.Max(0, 100 + expPct) / 100.0;
        int sulphurFound = effective.GetValueOrDefault("map_deepwater_league_resource_found_+%");
        bool freeLanterns = effective.GetValueOrDefault("chart_do_not_consume_lanterns_on_placement") > 0;
        // On a 1×1 Chart, MapModsOf(conn) already holds cellOnly + global; on a Voyage, MapModsOf holds global only.
        int magicExtra = voyage && cellOnly is not null
            ? Math.Max(0, cellOnly.GetValueOrDefault("map_magic_monster_num_additional_modifiers"))
            : 0;
        bool rareEssence = effective.GetValueOrDefault("map_rare_monsters_trapped_in_essences") > 0;
        bool rareSulphur = effective.GetValueOrDefault("chart_rare_monsters_drop_dead_mans_sulphur") > 0;

        // Scarab and currency uber more-drop mods: MapModApply.ExtraDropsFromMap handles MapModsOf(conn)
        // (1×1 Chart + Voyage global), so only apply cellOnly on Voyages to avoid double-counting.
        double scarabFactor = 1.0;
        double currencyFactor = 1.0;
        if (voyage && cellOnly is not null)
        {
            int scarabPct = cellOnly.GetValueOrDefault("map_scarab_drop_chance_+%_final_from_uber_mod");
            if (scarabPct > 0) scarabFactor = (100 + scarabPct) / 100.0;
            int currencyPct = cellOnly.GetValueOrDefault("map_currency_drop_chance_+%_final_from_uber_mod");
            if (currencyPct > 0) currencyFactor = (100 + currencyPct) / 100.0;
        }

        List<(string ItemId, int Count)>? rareDrops = null;
        for (int i = 0; i < RareCurrencyStats.Length; i++)
        {
            var (stat, itemId) = RareCurrencyStats[i];
            int count = effective.GetValueOrDefault(stat);
            if (count > 0) (rareDrops ??= []).Add((itemId, Math.Min(count, 20)));
        }
        int rareScarab = Math.Clamp(effective.GetValueOrDefault("chart_rare_monster_drop_x_additional_scarab"), 0, 20);
        int rareSupport = Math.Clamp(effective.GetValueOrDefault("chart_rare_monster_drop_support_gem_chance_%"), 0, 100);

        int equipToGold = Math.Clamp(effective.GetValueOrDefault("chart_monster_equipment_drops_converted_to_gold_%"), 0, 100);
        bool toStackedDecks = effective.GetValueOrDefault("chart_dropped_currency_converted_to_stacked_decks") > 0;
        bool noEquip = effective.GetValueOrDefault("chart_monsters_drop_no_equipment") > 0;
        bool noFlask = effective.GetValueOrDefault("chart_monsters_drop_no_flask") > 0;
        bool dropCorrupted = effective.GetValueOrDefault("map_items_drop_corrupted") > 0;
        int fracturedPct = Math.Clamp(effective.GetValueOrDefault("map_dropped_items_are_fractured_chance_%"), 0, 100);
        int maxQualityFlaskPct = Math.Clamp(effective.GetValueOrDefault("map_flasks_drop_with_max_quality_chance_%"), 0, 100);
        int ringUniquePct = Math.Clamp(effective.GetValueOrDefault("chart_ring_to_unique_ring_%_chance"), 0, 100);
        int amuletUniquePct = Math.Clamp(effective.GetValueOrDefault("chart_amulet_to_unique_amulet_%_chance"), 0, 100);
        int beltUniquePct = Math.Clamp(effective.GetValueOrDefault("chart_belt_to_unique_belt_%_chance"), 0, 100);

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
            var cellOnly = plan.Voyage ? DeepwaterPlan.Named(plan.CellStats[i]) : null;
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

    /// <summary>Picks a random droppable support gem whose DropLevel fits <paramref name="level"/>.</summary>
    public static Item? RollSupportGem(int level, Random rng)
    {
        var all = SupportGems.Value;
        int eligible = 0;
        for (int i = 0; i < all.Length; i++)
            if (all[i].DropLevel <= level) eligible++;
        if (eligible == 0) return null;
        int target = rng.Next(eligible);
        for (int i = 0; i < all.Length; i++)
        {
            if (all[i].DropLevel > level) continue;
            if (target-- == 0) return ItemCatalog.Create(all[i]) with { ItemLevel = 1 };
        }
        return null;
    }

    public static bool IsFlaskClass(string itemClass) =>
        itemClass.EndsWith("Flask", StringComparison.Ordinal);

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
        Random rng)
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
                if (RollUniqueJewellery(catalog, uniques, d.Type.Class, d.Item.ItemLevel > 0 ? d.Item.ItemLevel : monsterLevel, rng) is { } uniqueDrop)
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

    static ItemDrops.Drop? RollUniqueJewellery(ItemCatalog catalog, UniqueCatalog uniques, string itemClass, int itemLevel, Random rng)
    {
        var all = uniques.All;
        var index = _jewelleryUniques;
        if (index is null || !ReferenceEquals(index.Source, all))
        {
            var rings = new List<UniqueCatalog.Definition>();
            var amulets = new List<UniqueCatalog.Definition>();
            var belts = new List<UniqueCatalog.Definition>();
            foreach (var def in all)
            {
                if (def.EncounterOnly) continue;
                if (catalog.Find(def.BaseTypeId) is not { Supported: true } baseType) continue;
                if (baseType.Class == "Ring") rings.Add(def);
                else if (baseType.Class == "Amulet") amulets.Add(def);
                else if (baseType.Class == "Belt") belts.Add(def);
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
        var picked = pool[rng.Next(pool.Length)];
        byte ilvl = (byte)Math.Clamp(itemLevel, 1, 100);
        if (uniques.Create(picked with { ItemLevel = ilvl }, rng) is not { } created) return null;
        var type = catalog.ByHash(created.TypeHash);
        return type is null ? null : new ItemDrops.Drop(created with { Identified = false }, type);
    }
}
