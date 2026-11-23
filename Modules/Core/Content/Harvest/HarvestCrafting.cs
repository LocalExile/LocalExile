using System.Numerics;
using System.Text.RegularExpressions;
using GameServer.Core;
using GameServer.Core.Protocol.Instance;
using GameServer.Modding;

namespace GameServer.Modules.Harvest;

/// <summary>
/// HarvestCraftOptions.dat (the HarvestCraftOptions client table): all 74 recipes the native Horticrafting Station
/// lists (item reforges, augments, modifier conversions, weapon/body-armour quality enchants, essence/currency/fragment
/// set exchanges, divination card rerolls and gambles, influence rerolls, corrupted map rerolls, Ruthless support-gem
/// transforms and Facetor's Lens gem sacrifices).
/// Costs are the exact lifeforce types and amounts from the table plus Sacred Lifeforce and Crystallised Rancour,
/// scaled by stack size (<c>IsProportionalToStackSize</c>) and <c>HarvestCraftCostScalingByBasetype</c> (3.29.3c
/// 0x141d56670 / 0x141d56350).
/// GameMode is the column Mods and BestiaryRecipes share: 0 every league, 1 not in Ruthless, 2 Ruthless only.
/// </summary>
public sealed class HarvestCrafting
{
    public enum Operation
    {
        Unsupported,
        ReforgeTag,
        ReforgeAffinity,
        AugmentTag,
        Convert,
        AddEnchant,
        ExchangeEssence,
        ExchangeSet,
        RerollDivinationCard,
        GambleDivinationCard,
        RerollInfluenceType,
        ReforgeInfluence,
        RerollCorruptedMap,
        ReforgeSupportGem,
        SacrificeGemForLens,
    }

    public sealed record Recipe(ushort Hash, string Id, Operation Operation, string? Tag, int Affinity,
        IReadOnlyDictionary<string, string> Convert, IReadOnlyList<(uint Currency, int Amount)> Costs,
        int GameMode = 0, bool NoInfluence = false)
    {
        public int Row { get; init; }
        public bool ProportionalToStackSize { get; init; }
        public IReadOnlySet<string> AllowedClasses { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public string? EnchantModId { get; init; }
        public IReadOnlyList<IReadOnlyList<ItemCatalog.BaseType>> ExchangeSets { get; init; } = [];
        public bool UseSupportGems { get; init; }
        public bool AllowCorruptedInput { get; init; }
        public bool CorruptedOnly { get; init; }
        public int ExperiencePercent { get; init; }

        public bool Supported => Operation != Operation.Unsupported;
        public bool InMode(bool hardMode) => GameMode switch { 1 => !hardMode, 2 => hardMode, _ => true };
    }

    /// <summary>Result of a Horticrafting Station craft: the replacement item (or null when the craft legitimately
    /// destroyed the item, such as DivinationCardGamble rolling 0) and the exact costs to charge.</summary>
    public sealed record HarvestOutcome(Item? Item, IReadOnlyList<(uint Currency, int Amount)> Costs);

    // The five lifeforce/boss currencies, resolved once from BaseItemTypes (the table has only the type number).
    public const string Wild = "Metadata/Items/Currency/HarvestSeedRed",
        Vivid = "Metadata/Items/Currency/HarvestSeedGreen",
        Primal = "Metadata/Items/Currency/HarvestSeedBlue",
        Sacred = "Metadata/Items/Currency/HarvestSeedBoss",
        Rancour = "Metadata/Items/Currency/HarvestSeedFaridun",
        FacetorsLensId = "Metadata/Items/Currency/CurrencyAddGemExperience";

    static readonly string[] Metamods =
    [
        "item_generation_cannot_change_prefixes", "item_generation_cannot_change_suffixes",
        "item_generation_cannot_roll_attack_affixes", "item_generation_cannot_roll_caster_affixes",
    ];

    static readonly ItemInfluence[] ClassicInfluences =
    [
        ItemInfluence.Shaper, ItemInfluence.Elder, ItemInfluence.Crusader,
        ItemInfluence.Redeemer, ItemInfluence.Hunter, ItemInfluence.Warlord,
    ];

    const ItemInfluence AllClassicInfluences =
        ItemInfluence.Shaper | ItemInfluence.Elder | ItemInfluence.Crusader |
        ItemInfluence.Redeemer | ItemInfluence.Hunter | ItemInfluence.Warlord;

    sealed class TableData
    {
        public static readonly TableData Empty = new([], new Dictionary<ushort, Recipe>(),
            new Dictionary<(int, uint), int>(), new Dictionary<uint, IReadOnlyList<ItemCatalog.BaseType>>(),
            [], new Dictionary<uint, (int, bool)>(), new Dictionary<int, IReadOnlyList<ItemCatalog.BaseType>>(),
            new Dictionary<(bool, int), IReadOnlyList<ItemCatalog.BaseType>>(), null);

        public TableData(
            IReadOnlyList<Recipe> all,
            IReadOnlyDictionary<ushort, Recipe> byHash,
            IReadOnlyDictionary<(int CraftRow, uint BaseHash), int> costScaling,
            IReadOnlyDictionary<uint, IReadOnlyList<ItemCatalog.BaseType>> essenceTiers,
            IReadOnlyList<ItemCatalog.BaseType> divinationCards,
            IReadOnlyDictionary<uint, (int Tier, bool IsMapKey)> mapsByHash,
            IReadOnlyDictionary<int, IReadOnlyList<ItemCatalog.BaseType>> legacyMapsByTier,
            IReadOnlyDictionary<(bool IsSupport, int Colour), IReadOnlyList<ItemCatalog.BaseType>> gemsByKindAndColour,
            ItemCatalog.BaseType? facetorsLens)
        {
            All = all;
            ByHash = byHash;
            CostScaling = costScaling;
            EssenceTiers = essenceTiers;
            DivinationCards = divinationCards;
            MapsByHash = mapsByHash;
            LegacyMapsByTier = legacyMapsByTier;
            GemsByKindAndColour = gemsByKindAndColour;
            FacetorsLens = facetorsLens;
        }

        public IReadOnlyList<Recipe> All { get; }
        public IReadOnlyDictionary<ushort, Recipe> ByHash { get; }
        public IReadOnlyDictionary<(int CraftRow, uint BaseHash), int> CostScaling { get; }
        public IReadOnlyDictionary<uint, IReadOnlyList<ItemCatalog.BaseType>> EssenceTiers { get; }
        public IReadOnlyList<ItemCatalog.BaseType> DivinationCards { get; }
        public IReadOnlyDictionary<uint, (int Tier, bool IsMapKey)> MapsByHash { get; }
        public IReadOnlyDictionary<int, IReadOnlyList<ItemCatalog.BaseType>> LegacyMapsByTier { get; }
        public IReadOnlyDictionary<(bool IsSupport, int Colour), IReadOnlyList<ItemCatalog.BaseType>> GemsByKindAndColour { get; }
        public ItemCatalog.BaseType? FacetorsLens { get; }
    }

    readonly IItemCrafting _items;
    readonly ItemCatalog _catalog;
    readonly ItemRolls _rolls;
    readonly bool _hardMode;
    readonly TableData _data;

    public HarvestCrafting(ItemCatalog catalog, ItemRolls rolls, string folder = "data", bool hardMode = false)
    {
        _items = new ModuleItemCrafting(catalog, rolls);
        _catalog = catalog;
        _rolls = rolls;
        _hardMode = hardMode;
        _data = Load(_items, _catalog);
    }

    public HarvestCrafting(IItemCrafting items, string folder = "data", bool hardMode = false)
    {
        _items = items;
        _catalog = new ItemCatalog(folder, hardMode);
        _rolls = new ItemRolls(_catalog, folder);
        _hardMode = hardMode;
        _data = Load(_items, _catalog);
    }

    public IReadOnlyList<Recipe> All => _data.All;
    public Recipe? ByHash(ushort hash) => _data.ByHash.GetValueOrDefault(hash);

    /// <summary>Computes the effective lifeforce/Sacred/Rancour cost for applying <paramref name="recipe"/> to
    /// <paramref name="item"/>, applying <c>IsProportionalToStackSize</c> and <c>HarvestCraftCostScalingByBasetype</c>
    /// (3.29.3c 0x141d56670 / 0x141d56350). Returns null on integer overflow.</summary>
    public IReadOnlyList<(uint Currency, int Amount)>? ScaledCosts(Recipe recipe, Item item)
    {
        long mult = recipe.ProportionalToStackSize ? Math.Max(1, (int)item.StackSize) : 1;
        if (_data.CostScaling.TryGetValue((recipe.Row, item.TypeHash), out int scale) && scale > 0)
            mult *= scale;
        if (mult == 1) return recipe.Costs;
        var scaled = new (uint Currency, int Amount)[recipe.Costs.Count];
        for (int i = 0; i < recipe.Costs.Count; i++)
        {
            var (currency, amount) = recipe.Costs[i];
            long total = (long)amount * mult;
            if (total > int.MaxValue) return null;
            scaled[i] = (currency, (int)total);
        }
        return scaled;
    }

    /// <summary>The crafted item and its exact cost, or null when the recipe is unknown, unsupported, does not fit this
    /// item, or destroyed the item (use <see cref="ApplyHarvest"/> to distinguish item destruction from refusal).</summary>
    public CraftResult? Apply(ushort hash, Item item, Random rng) =>
        ApplyHarvest(hash, item, rng) is { Item: { } crafted, Costs: var costs } ? new(crafted, costs) : null;

    /// <summary>Applies the Harvest recipe to <paramref name="item"/>. Returns null when refused (nothing changed or
    /// spent). Returns a <see cref="HarvestOutcome"/> with <see cref="HarvestOutcome.Item"/> set to null when the craft
    /// succeeded and consumed/destroyed the item (e.g. DivinationCardGamble rolling 0).</summary>
    public HarvestOutcome? ApplyHarvest(ushort hash, Item item, Random rng)
    {
        if (ByHash(hash) is not { } recipe || !recipe.Supported || !recipe.InMode(_hardMode)) return null;
        if (ScaledCosts(recipe, item) is not { } costs) return null;
        return recipe.Operation switch
        {
            Operation.ReforgeTag or Operation.ReforgeAffinity or Operation.AugmentTag or Operation.Convert =>
                ApplyItemCraft(recipe, item, rng) is { } crafted ? new(crafted, costs) : null,
            Operation.AddEnchant =>
                ApplyEnchant(recipe, item, rng) is { } enchanted ? new(enchanted, costs) : null,
            Operation.ExchangeEssence =>
                ApplyEssenceExchange(item, rng) is { } essence ? new(essence, costs) : null,
            Operation.ExchangeSet =>
                ApplySetExchange(recipe, item, rng) is { } swapped ? new(swapped, costs) : null,
            Operation.RerollDivinationCard =>
                ApplyRerollDivinationCard(item, rng) is { } card ? new(card, costs) : null,
            Operation.GambleDivinationCard =>
                ApplyDivinationCardGamble(item, rng, costs),
            Operation.RerollInfluenceType =>
                ApplyRerollInfluenceType(recipe, item, rng) is { } reinfluenced ? new(reinfluenced, costs) : null,
            Operation.ReforgeInfluence =>
                ApplyReforgeInfluence(item, rng) is { } forged ? new(forged, costs) : null,
            Operation.RerollCorruptedMap =>
                ApplyRerollCorruptedMap(item, rng) is { } map ? new(map, costs) : null,
            Operation.ReforgeSupportGem =>
                ApplyReforgeGem(recipe, item, rng) is { } gem ? new(gem, costs) : null,
            Operation.SacrificeGemForLens =>
                ApplySacrificeGemForLens(recipe, item) is { } lens ? new(lens, costs) : null,
            _ => null,
        };
    }

    /// <summary>Returns a player-facing refusal hint when a recipe was rejected for a specific stack-size limit
    /// (such as swapping a fossil stack larger than the smallest MaxStack in the set), or null otherwise.</summary>
    public string? RefusalHint(ushort hash, Item? item)
    {
        if (item is null || item.Corrupted || item.Mirrored || item.StackSize < 1) return null;
        if (ByHash(hash) is not { Supported: true } recipe || !recipe.InMode(_hardMode)) return null;
        int? maxAllowed = recipe.Operation switch
        {
            Operation.ExchangeEssence when _data.EssenceTiers.TryGetValue(item.TypeHash, out var tier) && tier.Count >= 2 =>
                MinMaxStack(tier),
            Operation.ExchangeSet => ExchangeSetFor(recipe, item.TypeHash) is { } set ? MinMaxStack(set) : null,
            _ => null,
        };
        return maxAllowed is int limit && item.StackSize > limit
            ? $"Split this stack to {limit} or fewer before changing its type."
            : null;
    }

    static bool IsHarvestItemCraftBase(ItemCatalog.BaseType type) =>
        type.ModDomain is 1 or 10 or 13 or 21 && !string.Equals(type.Class, "HiddenItem", StringComparison.OrdinalIgnoreCase);

    Item? ApplyItemCraft(Recipe recipe, Item item, Random rng)
    {
        if (item.Corrupted || item.Mirrored || !item.Identified || item.Rarity == ItemRarity.Unique ||
            HasSocketedGems(item) || !_items.Modifiable(item) || _items.HasExplicitStat(item, Metamods))
            return null;
        if (_catalog.ByHash(item.TypeHash) is not { } type || !IsHarvestItemCraftBase(type))
            return null;
        // "Reforge a Rare item": the item in the slot must already be rare. Augments are "from a non-Influenced item"
        // (excluding both classic Atlas influences and Searing Exarch / Eater of Worlds Eldritch implicits).
        if (recipe.Operation is Operation.ReforgeTag or Operation.ReforgeAffinity && item.Rarity != ItemRarity.Rare ||
            recipe.NoInfluence && (item.Influences != ItemInfluence.None || _rolls.HasEldritchImplicit(item))) return null;
        Item? result = recipe.Operation switch
        {
            Operation.ReforgeTag => _items.RollRequiredTag(item, ItemRarity.Rare, rng, recipe.Tag!),
            Operation.ReforgeAffinity => _items.RollAffinity(item, ItemRarity.Rare, rng, recipe.Affinity),
            Operation.AugmentTag => Augment(item, recipe.Tag!, rng),
            Operation.Convert => _items.ConvertMod(item, recipe.Convert, rng),
            _ => null,
        };
        return result is null ? null : ItemRolls.ClearForesee(result with { Identified = true });
    }

    Item? Augment(Item item, string tag, Random rng)
    {
        if (item.Rarity is not (ItemRarity.Magic or ItemRarity.Rare) ||
            _items.Annul(item, rng) is not { } removed) return null;
        (int Prefix, int Suffix) max = item.Rarity == ItemRarity.Magic ? (1, 1) : _items.RareMaxAffixes(item);
        return _items.AddTaggedAffix(removed, max, rng, tag);
    }

    // 3.29.3c 0x141d5e680: identified, non-corrupted, non-mirrored, modifiable base in the allowed ItemClasses list.
    // Unique weapons and body armours are permitted (client validator does not restrict rarity or socketed gems).
    Item? ApplyEnchant(Recipe recipe, Item item, Random rng)
    {
        if (item.Corrupted || item.Mirrored || item.Vestigial || !item.Identified || !item.Components.Contains("Mods") ||
            recipe.EnchantModId is null) return null;
        var catalog = _catalog;
        if (catalog.ByHash(item.TypeHash) is not { Supported: true, Unmodifiable: false } type ||
            !recipe.AllowedClasses.Contains(type.Class) ||
            catalog.Mods.ById(recipe.EnchantModId) is not { } enchantMod)
            return null;
        var rolled = enchantMod.Roll(ModSection.Enchant, rng);
        var mods = new List<ItemMod>(item.Mods.Count + 1);
        foreach (var mod in item.Mods)
            if (mod.Section != ModSection.Enchant) mods.Add(mod);
        mods.Add(rolled);
        return ItemRolls.ClearForesee(item with { Mods = mods });
    }

    // 3.29.3c 0x141d5ce00: any Essence row whose EssenceType is not NotEssence, swapped to another of the same tier.
    // Note: essence, set (oils/fossils/catalysts/fragments) and divination-card exchanges currently roll uniformly over
    // the eligible target pool rather than using retail server-only drop weights.
    Item? ApplyEssenceExchange(Item item, Random rng)
    {
        if (item.Corrupted || item.Mirrored || item.StackSize < 1 ||
            !_data.EssenceTiers.TryGetValue(item.TypeHash, out var tier) || tier.Count < 2)
            return null;
        if (item.StackSize > MinMaxStack(tier)) return null;
        var target = PickOther(tier, item.TypeHash, rng);
        if (target is null) return null;
        return ItemCatalog.Create(target, random: rng) with
        {
            StackSize = item.StackSize,
        };
    }

    // 3.29.3c 0x141d5ca00: exchange an item for a different item in the same set.
    // Stacks larger than the smallest MaxStack in the set (e.g. >10 for delvefossils, where rare fossils cap at 10)
    // are refused before lifeforce is charged so items are never silently truncated or overcharged.
    Item? ApplySetExchange(Recipe recipe, Item item, Random rng)
    {
        if (item.Corrupted || item.Mirrored || item.StackSize < 1) return null;
        var set = ExchangeSetFor(recipe, item.TypeHash);
        if (set is null || item.StackSize > MinMaxStack(set)) return null;
        var target = PickOther(set, item.TypeHash, rng);
        if (target is null) return null;
        return ItemCatalog.Create(target, random: rng) with
        {
            StackSize = item.StackSize,
        };
    }

    static IReadOnlyList<ItemCatalog.BaseType>? ExchangeSetFor(Recipe recipe, uint typeHash)
    {
        foreach (var set in recipe.ExchangeSets)
        {
            if (set.Count < 2) continue;
            foreach (var entry in set)
                if (entry.Hash == typeHash) return set;
        }
        return null;
    }

    static int MinMaxStack(IReadOnlyList<ItemCatalog.BaseType> pool)
    {
        int min = int.MaxValue;
        foreach (var entry in pool)
        {
            int cap = Math.Max(1, (int)entry.MaxStack);
            if (cap < min) min = cap;
        }
        return min == int.MaxValue ? 1 : min;
    }

    // 3.29.3c 0x141d5d810: single Divination Card swapped to a different drop-enabled Divination Card.
    Item? ApplyRerollDivinationCard(Item item, Random rng)
    {
        if (item.Corrupted || item.Mirrored || item.StackSize != 1 ||
            _catalog.ByHash(item.TypeHash) is not { Class: "DivinationCard" })
            return null;
        var pool = _data.DivinationCards;
        if (pool.Count < 2) return null;
        var target = PickOther(pool, item.TypeHash, rng);
        return target is null ? null : ItemCatalog.Create(target, random: rng) with { StackSize = 1 };
    }

    // 3.29.3c 0x141d5d6f0: Divination Card with MaxStack > 1 and 1 <= StackSize <= MaxStack / 2.
    // Uniformly returns 0..2*StackSize cards of the same card (0 destroys the item in the slot).
    HarvestOutcome? ApplyDivinationCardGamble(Item item, Random rng, IReadOnlyList<(uint Currency, int Amount)> costs)
    {
        if (item.Corrupted || item.Mirrored ||
            _catalog.ByHash(item.TypeHash) is not { Class: "DivinationCard" } type ||
            type.MaxStack <= 1)
            return null;
        int maxInput = type.MaxStack / 2;
        if (item.StackSize < 1 || item.StackSize > maxInput) return null;
        int rolled = rng.Next(0, item.StackSize * 2 + 1);
        if (rolled == 0) return new(null, costs);
        return new(ItemRolls.ClearForesee(item with { StackSize = (ushort)Math.Min(rolled, (int)type.MaxStack) }), costs);
    }

    // 3.29.3c 0x141d5bb00: Normal, Magic or Rare influenced item in the allowed equipment classes, non-synthesised,
    // non-fractured, no Eldritch implicits, no metamods, no socketed gems. Keeps the same number of classic influences
    // with a different influence set and reforges modifiers for its rarity.
    Item? ApplyRerollInfluenceType(Recipe recipe, Item item, Random rng)
    {
        var catalog = _catalog;
        var rolls = _rolls;
        if (item.Corrupted || item.Mirrored || !item.Identified || item.Rarity == ItemRarity.Unique ||
            item.Synthesised || item.FracturedCount > 0 || HasSocketedGems(item) ||
            (item.Influences & ~AllClassicInfluences) != ItemInfluence.None ||
            !_items.Modifiable(item) || _items.HasExplicitStat(item, Metamods) || rolls.HasEldritchImplicit(item) ||
            catalog.ByHash(item.TypeHash) is not { } type || !recipe.AllowedClasses.Contains(type.Class))
            return null;
        ItemInfluence current = item.Influences & AllClassicInfluences;
        int count = BitOperations.PopCount((uint)current);
        if (count <= 0 || count >= ClassicInfluences.Length) return null;
        ItemInfluence next = PickDifferentInfluences(current, count, rng);
        var updated = item with { Influences = next };
        if (item.Rarity == ItemRarity.Normal)
            return ItemRolls.ClearForesee(updated with { Identified = true });
        if (rolls.Pool(type, updated).Count == 0) return null;
        return ItemRolls.ClearForesee(rolls.Roll(updated, item.Rarity, rng) with { Identified = true });
    }

    // 3.29.3c 0x141d5e030: Rare identified influenced item with item level >= 68, modifiable, no metamods, no socketed gems.
    // Reforges as Rare guaranteeing at least one influence-only affix matching the item's influence(s).
    Item? ApplyReforgeInfluence(Item item, Random rng)
    {
        var catalog = _catalog;
        var rolls = _rolls;
        if (item.Corrupted || item.Mirrored || !item.Identified || item.Rarity != ItemRarity.Rare ||
            item.ItemLevel < 68 || (item.Influences & AllClassicInfluences) == ItemInfluence.None ||
            HasSocketedGems(item) || !_items.Modifiable(item) || _items.HasExplicitStat(item, Metamods) ||
            catalog.ByHash(item.TypeHash) is not { } type)
            return null;
        var fractured = item.FracturedIndexes().Select(i => catalog.Mods.ByHash(item.Mods[i].Hash))
            .OfType<ModCatalog.Mod>().ToList();
        var (maxPrefix, maxSuffix) = rolls.RareMaxAffixes(item);
        int fracturedPrefixes = fractured.Count(m => m.GenerationType == ItemRolls.Prefix);
        int fracturedSuffixes = fractured.Count(m => m.GenerationType == ItemRolls.Suffix);
        var influencePool = rolls.Pool(type, item)
            .Where(p => p.Mod.WeightFor(type.Tags) == 0 &&
                (p.Mod.GenerationType == ItemRolls.Prefix ? fracturedPrefixes < maxPrefix : fracturedSuffixes < maxSuffix) &&
                !fractured.Any(f => f.Hash == p.Mod.Hash || f.Families.Intersect(p.Mod.Families).Any()))
            .ToList();
        if (influencePool.Count == 0) return null;
        var guaranteed = PickWeighted(influencePool, rng);
        return ItemRolls.ClearForesee(rolls.Roll(item, ItemRarity.Rare, rng, guaranteed: guaranteed) with { Identified = true });
    }

    // 3.29.3c 0x141d5f880: Rare Corrupted Identified map -> freshly rolled Rare Corrupted map of the same tier.
    Item? ApplyRerollCorruptedMap(Item item, Random rng)
    {
        var catalog = _catalog;
        var rolls = _rolls;
        if (!item.Corrupted || item.Mirrored || item.Vestigial || !item.Identified || item.Rarity != ItemRarity.Rare ||
            !item.Components.Contains("Mods") ||
            !_data.MapsByHash.TryGetValue(item.TypeHash, out var info) ||
            catalog.ByHash(item.TypeHash) is not { Supported: true } sourceType)
            return null;
        ItemCatalog.BaseType targetType;
        if (info.IsMapKey)
            targetType = sourceType;
        else
        {
            if (!_data.LegacyMapsByTier.TryGetValue(info.Tier, out var sameTier) || sameTier.Count == 0)
                return null;
            targetType = PickOther(sameTier, item.TypeHash, rng) ?? sameTier[0];
        }
        byte itemLevel = item.ItemLevel > 1 ? item.ItemLevel : (byte)Math.Clamp(67 + info.Tier, 68, 84);
        var fresh = ItemCatalog.Create(targetType, random: rng) with { ItemLevel = itemLevel };
        if (rolls.Pool(targetType, fresh).Count == 0) return null;
        return ItemRolls.ClearForesee(rolls.Roll(fresh, ItemRarity.Rare, rng) with { Corrupted = true, Identified = true });
    }

    // 3.29.3c 0x141d634a0: Ruthless support gem transform -> random different ordinary support gem of the same colour
    // (Red/Green/Blue; White refused), preserving gem level, XP (clamped to target max level) and quality.
    Item? ApplyReforgeGem(Recipe recipe, Item item, Random rng)
    {
        var catalog = _catalog;
        if ((!recipe.AllowCorruptedInput && item.Corrupted) || item.Mirrored || item.GemVariant != 0 ||
            catalog.ByHash(item.TypeHash) is not { } sourceType ||
            catalog.Gem(sourceType) is not { } sourceGem ||
            sourceGem.IsSupport != recipe.UseSupportGems ||
            sourceGem.Colour is < 1 or > 3 ||
            sourceGem.IsVaalOrAwakened || sourceGem.IsAwakened || sourceGem.IsExceptional)
            return null;
        if (!_data.GemsByKindAndColour.TryGetValue((recipe.UseSupportGems, sourceGem.Colour), out var pool) ||
            pool.Count < 2)
            return null;
        var targetType = PickOther(pool, item.TypeHash, rng);
        if (targetType is null || catalog.Gem(targetType) is not { } targetGem) return null;
        int sourceLevel = sourceGem.Level(item.GemExperience);
        int targetExp = PreserveGemExperience(sourceGem, targetGem, item.GemExperience, sourceLevel);
        return ItemCatalog.Create(targetType, random: rng) with
        {
            Quality = item.Quality,
            GemExperience = targetExp,
            GemFlags = (byte)(item.GemFlags & ~0x20),
            Corrupted = item.Corrupted,
        };
    }

    // 3.29.3c 0x141d5d890: sacrifice a (corrupted) gem to store percent (50%) of its accumulated XP in a Facetor's Lens.
    Item? ApplySacrificeGemForLens(Recipe recipe, Item item)
    {
        var catalog = _catalog;
        if (item.Mirrored || (recipe.CorruptedOnly && !item.Corrupted) ||
            catalog.ByHash(item.TypeHash) is not { } sourceType ||
            catalog.Gem(sourceType) is not { } gem ||
            _data.FacetorsLens is not { Supported: true } lensType)
            return null;
        long totalXp = item.GemExperience == -1
            ? (gem.Thresholds.Count > 0 ? gem.Thresholds[^1] : 0)
            : Math.Max(0, item.GemExperience);
        long stored = totalXp * recipe.ExperiencePercent / 100;
        if (stored < 1) return null;
        return ItemCatalog.Create(lensType) with
        {
            StackSize = 1,
            GemExperience = (int)Math.Min(stored, int.MaxValue),
        };
    }

    static bool HasSocketedGems(Item item)
    {
        foreach (var socket in item.Sockets)
            if (socket.Gem is not null) return true;
        return false;
    }

    static int PreserveGemExperience(ItemCatalog.GemType source, ItemCatalog.GemType target, int experience, int sourceLevel)
    {
        if (target.Thresholds.Count == 0) return 0;
        int maxLevel = target.MaxLevel;
        if (experience < 0 || sourceLevel >= source.MaxLevel) return target.Thresholds[^1];
        int clampedLevel = Math.Clamp(sourceLevel, 1, maxLevel);
        int floor = target.ExperienceFor(clampedLevel);
        int cap = clampedLevel >= maxLevel ? target.Thresholds[^1]
            : Math.Max(floor, target.ExperienceFor(clampedLevel + 1) - 1);
        return Math.Clamp(experience, floor, cap);
    }

    static ItemInfluence PickDifferentInfluences(ItemInfluence current, int count, Random rng)
    {
        Span<ItemInfluence> choices = stackalloc ItemInfluence[20];
        int found = 0;
        for (int mask = 1; mask < (1 << ClassicInfluences.Length); mask++)
        {
            if (BitOperations.PopCount((uint)mask) != count) continue;
            var candidate = (ItemInfluence)mask;
            if (candidate != current) choices[found++] = candidate;
        }
        return choices[rng.Next(found)];
    }

    static ItemCatalog.BaseType? PickOther(IReadOnlyList<ItemCatalog.BaseType> pool, uint currentHash, Random rng)
    {
        int count = 0;
        foreach (var item in pool)
            if (item.Hash != currentHash) count++;
        if (count == 0) return null;
        int pick = rng.Next(count);
        foreach (var item in pool)
        {
            if (item.Hash == currentHash) continue;
            if (pick-- == 0) return item;
        }
        return null;
    }

    static T PickWeighted<T>(IReadOnlyList<(T Item, int Weight)> choices, Random rng)
    {
        long total = 0;
        foreach (var (_, weight) in choices) total += weight;
        long pick = rng.NextInt64(total);
        foreach (var (item, weight) in choices)
        {
            if (pick < weight) return item;
            pick -= weight;
        }
        return choices[^1].Item;
    }

    static TableData Load(IItemCrafting crafting, ItemCatalog catalog)
    {
        if (!ClientTables.Has("HarvestCraftOptions")) return TableData.Empty;
        var rows = ClientTables.Table("HarvestCraftOptions").ToList();

        var lifeforce = new[]
        {
            (Type: 1, Hash: crafting.TypeHash(Wild) ?? 0),
            (Type: 2, Hash: crafting.TypeHash(Vivid) ?? 0),
            (Type: 3, Hash: crafting.TypeHash(Primal) ?? 0),
        };
        uint sacred = crafting.TypeHash(Sacred) ?? 0, rancour = crafting.TypeHash(Rancour) ?? 0;

        var costScaling = LoadCostScaling(catalog, out var scalingByCraft);
        var exchangeLookup = new ExchangeSetLoader(catalog, scalingByCraft);
        var essenceTiers = LoadEssenceTiers(catalog);
        var divinationCards = LoadDivinationCards(catalog);
        var (mapsByHash, legacyMapsByTier) = LoadMaps(catalog);
        var gemsByKindAndColour = LoadGems(catalog);
        var facetorsLens = catalog.Find(FacetorsLensId);
        var itemClasses = LoadItemClasses();

        // Conversion recipes point at a target by element; the reverse row lists the target's ModType groups, which is
        // how a source group is mapped to the same-tier group in the other element.
        var conversions = new Dictionary<int, (string[] Sources, string From, string To)>();
        foreach (var row in rows)
        {
            if (row.Text("Command") != "convert_mod") continue;
            var parts = (row.Text("Parameters") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
            int convert = Array.IndexOf(parts, "CONVERT");
            if (convert < 1 || convert + 2 >= parts.Length) continue;
            conversions[row.Int("HASH16")] = (parts[..convert],
                parts[convert + 1].ToLowerInvariant(), parts[convert + 2].ToLowerInvariant());
        }

        var modTypes = crafting.ModTypes;
        var result = new List<Recipe>(rows.Count);
        var byHash = new Dictionary<ushort, Recipe>(rows.Count);
        foreach (var row in rows)
        {
            int rowIndex = row.Index;
            string id = row.Text("Id") ?? "";
            string command = row.Text("Command") ?? "";
            var parts = (row.Text("Parameters") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var costs = Costs(row.Int("LifeforceType"), row.Int("LifeforceCost"),
                row.Int("SacredCost"), lifeforce, sacred, row.Int("RancourCost"), rancour);
            Operation operation = Operation.Unsupported;
            string? tag = null;
            int affinity = 0;
            IReadOnlyDictionary<string, string> convertMap = new Dictionary<string, string>();
            IReadOnlySet<string> allowedClasses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string? enchantModId = null;
            IReadOnlyList<IReadOnlyList<ItemCatalog.BaseType>> exchangeSets = [];
            bool useSupportGems = false, allowCorrupted = false, corruptedOnly = false;
            int experiencePercent = 0;

            if (costs is null) { /* an unknown lifeforce currency: the recipe stays unsupported */ }
            else if (command == "reroll_with_mod" && parts.Length > 0)
            {
                operation = Operation.ReforgeTag;
                tag = parts[0].ToLowerInvariant();
            }
            else if (command == "reroll_with_current_tags_affinity_multiplier" && parts.Length > 0 &&
                int.TryParse(parts[0], out affinity))
            {
                operation = Operation.ReforgeAffinity;
            }
            else if (command == "remove_type_and_add_type_mod")
            {
                int forIndex = Array.IndexOf(parts, "FOR");
                if (forIndex >= 0 && forIndex + 1 < parts.Length)
                {
                    operation = Operation.AugmentTag;
                    tag = parts[forIndex + 1].ToLowerInvariant();
                }
            }
            else if (command == "convert_mod" && conversions.TryGetValue(row.Int("HASH16"), out var c))
            {
                // The reverse recipes (fire -> cold) list the target groups. Both the resistance and the damage pair use
                // the same elements, so every reverse source list is a candidate; the replaced/stripped name picks the
                // right group (FireResistance vs FireDamage).
                var targetIds = conversions.Values.Where(x => x.From == c.To && x.To == c.From)
                    .SelectMany(x => x.Sources).ToList();
                var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (string source in c.Sources)
                {
                    string replaced = Regex.Replace(source, c.From, c.To, RegexOptions.IgnoreCase);
                    string? target = targetIds.FirstOrDefault(t => t.Equals(replaced, StringComparison.OrdinalIgnoreCase))
                        ?? targetIds.FirstOrDefault(t => StripToken(t, c.To).Equals(StripToken(source, c.From), StringComparison.OrdinalIgnoreCase));
                    if (target is not null && modTypes.Contains(source) && modTypes.Contains(target))
                        map[source] = target;
                }
                if (map.Count > 0) { operation = Operation.Convert; convertMap = map; }
            }
            else if (command == "add_enchant_to_class")
            {
                int enchantsIndex = Array.IndexOf(parts, "ENCHANTS");
                if (enchantsIndex >= 1 && enchantsIndex + 1 < parts.Length)
                {
                    var classes = ParseClasses(parts[..enchantsIndex], itemClasses);
                    string modId = parts[enchantsIndex + 1];
                    if (classes.Count > 0 && catalog.Mods.ById(modId) is not null)
                    {
                        operation = Operation.AddEnchant;
                        allowedClasses = classes;
                        enchantModId = modId;
                    }
                }
            }
            else if (command == "exchange_essences_for_other_essence_of_same_tier")
            {
                if (essenceTiers.Count > 0) operation = Operation.ExchangeEssence;
            }
            else if (command == "exchange_item_in_set_for_other_item_in_set" && parts.Length > 0)
            {
                var sets = new List<IReadOnlyList<ItemCatalog.BaseType>>();
                foreach (string token in parts)
                {
                    if (token.Equals("single", StringComparison.OrdinalIgnoreCase)) continue;
                    var resolved = exchangeLookup.Resolve(token, rowIndex);
                    if (resolved.Count >= 2) sets.Add(resolved);
                }
                if (sets.Count > 0) { operation = Operation.ExchangeSet; exchangeSets = sets; }
            }
            else if (command == "reroll_into_new_div_card")
            {
                if (divinationCards.Count >= 2) operation = Operation.RerollDivinationCard;
            }
            else if (command == "reroll_num_div_cards")
            {
                operation = Operation.GambleDivinationCard;
            }
            else if (command == "reroll_influence_types" && parts.Length > 0)
            {
                var classes = ParseClasses(parts, itemClasses);
                if (classes.Count > 0) { operation = Operation.RerollInfluenceType; allowedClasses = classes; }
            }
            else if (command == "reroll_with_influence_mod")
            {
                operation = Operation.ReforgeInfluence;
            }
            else if (command == "sacrifice_corrupted_map_for_new_corrupted_map")
            {
                if (mapsByHash.Count > 0) operation = Operation.RerollCorruptedMap;
            }
            else if (command == "change_gem_to_random_alternate_gem_of_same_colour")
            {
                useSupportGems = parts.Contains("use_support_gems", StringComparer.OrdinalIgnoreCase);
                allowCorrupted = parts.Contains("input_is_corrupted", StringComparer.OrdinalIgnoreCase);
                if (gemsByKindAndColour.Keys.Any(k => k.IsSupport == useSupportGems))
                    operation = Operation.ReforgeSupportGem;
            }
            else if (command == "sacrifice_gem_for_currency")
            {
                corruptedOnly = parts.Contains("corrupted_only", StringComparer.OrdinalIgnoreCase);
                int expIndex = Array.IndexOf(parts, "experience");
                if (expIndex >= 0 && expIndex + 1 < parts.Length && int.TryParse(parts[expIndex + 1], out int pct) &&
                    pct > 0 && facetorsLens is { Supported: true })
                {
                    operation = Operation.SacrificeGemForLens;
                    experiencePercent = pct;
                }
            }

            var recipe = new Recipe((ushort)row.Int("HASH16"), id, operation, tag, affinity, convertMap,
                costs ?? [], row.Int("GameMode"), parts.Contains("noinfluence", StringComparer.OrdinalIgnoreCase))
            {
                Row = rowIndex,
                ProportionalToStackSize = row.BoolOrNull("IsProportionalToStackSize") == true,
                AllowedClasses = allowedClasses,
                EnchantModId = enchantModId,
                ExchangeSets = exchangeSets,
                UseSupportGems = useSupportGems,
                AllowCorruptedInput = allowCorrupted,
                CorruptedOnly = corruptedOnly,
                ExperiencePercent = experiencePercent,
            };
            result.Add(recipe);
            byHash.TryAdd(recipe.Hash, recipe);
        }
        return new(result, byHash, costScaling, essenceTiers, divinationCards, mapsByHash, legacyMapsByTier,
            gemsByKindAndColour, facetorsLens);
    }

    static HashSet<string> LoadItemClasses()
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!ClientTables.Has("ItemClasses")) return result;
        foreach (var row in ClientTables.Table("ItemClasses"))
            if (row.Text("Id") is { Length: > 0 } id) result.Add(id);
        return result;
    }

    static HashSet<string> ParseClasses(IEnumerable<string> tokens, HashSet<string> knownClasses)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string token in tokens)
        {
            if (knownClasses.Contains(token)) { result.Add(token); continue; }
            string spaced = token.Replace('_', ' ');
            if (knownClasses.Contains(spaced)) result.Add(spaced);
        }
        return result;
    }

    static Dictionary<(int CraftRow, uint BaseHash), int> LoadCostScaling(ItemCatalog catalog,
        out Dictionary<int, List<ItemCatalog.BaseType>> byCraft)
    {
        var result = new Dictionary<(int, uint), int>();
        byCraft = new();
        if (!ClientTables.Has("HarvestCraftCostScalingByBasetype")) return result;
        foreach (var row in ClientTables.Table("HarvestCraftCostScalingByBasetype"))
        {
            if (row.IntOrNull("Craft") is not int craft ||
                row.IntOrNull("BaseItemType") is not int baseRow ||
                row.IntOrNull("Scale") is not int scale || scale <= 0 ||
                catalog.Row(baseRow) is not { Supported: true } type) continue;
            result[(craft, type.Hash)] = scale;
            if (!byCraft.TryGetValue(craft, out var list)) byCraft[craft] = list = [];
            if (list.All(x => x.Hash != type.Hash)) list.Add(type);
        }
        return result;
    }

    static Dictionary<uint, IReadOnlyList<ItemCatalog.BaseType>> LoadEssenceTiers(ItemCatalog catalog)
    {
        var result = new Dictionary<uint, IReadOnlyList<ItemCatalog.BaseType>>();
        if (!ClientTables.Has("Essences")) return result;
        int notEssenceRow = -1;
        if (ClientTables.Has("EssenceType"))
        {
            foreach (var et in ClientTables.Table("EssenceType"))
                if (et.Text("Id") == "NotEssence") { notEssenceRow = et.Index; break; }
        }
        var byTier = new Dictionary<int, List<ItemCatalog.BaseType>>();
        foreach (var row in ClientTables.Table("Essences"))
        {
            if (row.IntOrNull("EssenceTypeKey") is int et && et == notEssenceRow) continue;
            if (row.IntOrNull("BaseItemTypesKey") is not int baseRow ||
                catalog.Row(baseRow) is not { Supported: true } type) continue;
            int level = row.Int("Level");
            if (!byTier.TryGetValue(level, out var list)) byTier[level] = list = [];
            if (list.All(x => x.Hash != type.Hash)) list.Add(type);
        }
        foreach (var list in byTier.Values)
        {
            if (list.Count < 2) continue;
            IReadOnlyList<ItemCatalog.BaseType> shared = list;
            foreach (var type in list) result[type.Hash] = shared;
        }
        return result;
    }

    static IReadOnlyList<ItemCatalog.BaseType> LoadDivinationCards(ItemCatalog catalog)
    {
        if (ClientTables.Has("DivinationCardStashTabLayout"))
        {
            var enabled = new List<ItemCatalog.BaseType>();
            var seen = new HashSet<uint>();
            foreach (var row in ClientTables.Table("DivinationCardStashTabLayout"))
            {
                if (row.BoolOrNull("IsInGame") != true || row.BoolOrNull("IsEnabled") != true) continue;
                if (row.IntOrNull("StoredItem") is int baseRow &&
                    catalog.Row(baseRow) is { Supported: true, Class: "DivinationCard" } type &&
                    seen.Add(type.Hash))
                    enabled.Add(type);
            }
            if (enabled.Count >= 2) return enabled;
        }
        return [.. catalog.All.Where(t => t.Supported && t.Class == "DivinationCard").DistinctBy(t => t.Hash)];
    }

    static (Dictionary<uint, (int Tier, bool IsMapKey)> ByHash, Dictionary<int, IReadOnlyList<ItemCatalog.BaseType>> LegacyByTier)
        LoadMaps(ItemCatalog catalog)
    {
        var byHash = new Dictionary<uint, (int, bool)>();
        var legacyByTier = new Dictionary<int, List<ItemCatalog.BaseType>>();
        if (ClientTables.Has("MapKeys"))
        {
            foreach (var row in ClientTables.Table("MapKeys"))
            {
                if (row.BoolOrNull("IsSpecial") == true) continue;
                if (row.IntOrNull("BaseItemType") is not int baseRow ||
                    row.IntOrNull("MapTier") is not int tier || tier < 1 || tier > 16 ||
                    catalog.Row(baseRow) is not { Supported: true } type) continue;
                byHash.TryAdd(type.Hash, (tier, true));
            }
        }
        if (ClientTables.Has("Maps"))
        {
            foreach (var row in ClientTables.Table("Maps"))
            {
                if (row.IntOrNull("BaseItemTypesKey") is not int baseRow ||
                    row.IntOrNull("Regular_WorldAreasKey") is null ||
                    row.IntOrNull("Tier") is not int tier || tier < 1 || tier > 16 ||
                    catalog.Row(baseRow) is not { Supported: true } type) continue;
                byHash.TryAdd(type.Hash, (tier, false));
                if (!legacyByTier.TryGetValue(tier, out var list)) legacyByTier[tier] = list = [];
                if (list.All(x => x.Hash != type.Hash)) list.Add(type);
            }
        }
        return (byHash, legacyByTier.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<ItemCatalog.BaseType>)kv.Value));
    }

    static Dictionary<(bool IsSupport, int Colour), IReadOnlyList<ItemCatalog.BaseType>> LoadGems(ItemCatalog catalog)
    {
        var grouped = new Dictionary<(bool, int), List<ItemCatalog.BaseType>>();
        foreach (var type in catalog.All)
        {
            if (!type.Supported || type.DropLevel <= 0 ||
                type.Name.Contains("Playtest", StringComparison.OrdinalIgnoreCase) ||
                type.Id.Contains("Royale", StringComparison.OrdinalIgnoreCase) ||
                catalog.Gem(type) is not { } gem ||
                gem.Colour is < 1 or > 3 || gem.MaxLevel < 20 ||
                gem.IsVaalOrAwakened || gem.IsAwakened || gem.IsExceptional ||
                gem.Variants is not [{ Creatable: true }, ..])
                continue;
            var key = (gem.IsSupport, gem.Colour);
            if (!grouped.TryGetValue(key, out var list)) grouped[key] = list = [];
            if (list.All(x => x.Hash != type.Hash)) list.Add(type);
        }
        return grouped.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<ItemCatalog.BaseType>)kv.Value);
    }

    /// <summary>Resolves Harvest <c>exchange_item_in_set_for_other_item_in_set</c> set tokens (from server-only
    /// <c>Data/ItemSets.dat</c> referenced at 3.29.3c 0x141d5cc19) from the client.db tables.</summary>
    sealed class ExchangeSetLoader
    {
        readonly ItemCatalog _catalog;
        readonly IReadOnlyDictionary<int, List<ItemCatalog.BaseType>> _scalingByCraft;
        readonly Dictionary<string, List<ItemCatalog.BaseType>> _byExchangeCategory;
        readonly HashSet<int> _inCurrencyExchange;
        readonly List<ItemCatalog.BaseType> _fragmentLayout;
        readonly List<ItemCatalog.BaseType> _deliriumOrbs;
        readonly List<ItemCatalog.BaseType> _basicOils;

        public ExchangeSetLoader(ItemCatalog catalog, IReadOnlyDictionary<int, List<ItemCatalog.BaseType>> scalingByCraft)
        {
            _catalog = catalog;
            _scalingByCraft = scalingByCraft;
            _byExchangeCategory = new(StringComparer.OrdinalIgnoreCase);
            _inCurrencyExchange = [];
            if (ClientTables.Has("CurrencyExchange") && ClientTables.Has("CurrencyExchangeCategories"))
            {
                var catIds = ClientTables.Table("CurrencyExchangeCategories").Select(r => r.Text("Id") ?? "").ToList();
                void AddCat(int catRow, ItemCatalog.BaseType type)
                {
                    if ((uint)catRow >= (uint)catIds.Count || catIds[catRow].Length == 0) return;
                    if (!_byExchangeCategory.TryGetValue(catIds[catRow], out var list))
                        _byExchangeCategory[catIds[catRow]] = list = [];
                    if (list.All(x => x.Hash != type.Hash)) list.Add(type);
                }
                foreach (var row in ClientTables.Table("CurrencyExchange"))
                {
                    if (row.IntOrNull("Item") is not int baseRow ||
                        catalog.Row(baseRow) is not { Supported: true } type) continue;
                    _inCurrencyExchange.Add(baseRow);
                    if (row.IntOrNull("Category") is int cat) AddCat(cat, type);
                    if (row.IntOrNull("SubCategory") is int sub) AddCat(sub, type);
                }
            }

            _fragmentLayout = [];
            if (ClientTables.Has("FragmentStashTabLayout"))
            {
                var seen = new HashSet<uint>();
                foreach (var row in ClientTables.Table("FragmentStashTabLayout"))
                    foreach (int baseRow in row.Ints("StoredItems"))
                        if (catalog.Row(baseRow) is { Supported: true } type && seen.Add(type.Hash))
                            _fragmentLayout.Add(type);
            }

            _deliriumOrbs = [];
            if (ClientTables.Has("DeliriumStashTabLayout"))
            {
                var seen = new HashSet<uint>();
                foreach (var row in ClientTables.Table("DeliriumStashTabLayout"))
                    if (row.IntOrNull("StoredItem") is int baseRow &&
                        (_inCurrencyExchange.Count == 0 || _inCurrencyExchange.Contains(baseRow)) &&
                        catalog.Row(baseRow) is { Supported: true } type &&
                        type.Id.StartsWith("Metadata/Items/Currency/CurrencyAfflictionOrb", StringComparison.OrdinalIgnoreCase) &&
                        seen.Add(type.Hash))
                        _deliriumOrbs.Add(type);
            }

            _basicOils = [];
            if (ClientTables.Has("BlightCraftingItems"))
            {
                var seen = new HashSet<uint>();
                foreach (var row in ClientTables.Table("BlightCraftingItems"))
                    if (row.Int("Tier") > 0 && row.IntOrNull("Oil") is int baseRow &&
                        catalog.Row(baseRow) is { Supported: true } type && seen.Add(type.Hash))
                        _basicOils.Add(type);
            }
        }

        public IReadOnlyList<ItemCatalog.BaseType> Resolve(string token, int craftRow) => token switch
        {
            "delvefossils" => ExchangeOrFallback("DelveFossils",
                () => Prefix("Metadata/Items/Currency/CurrencyDelveCrafting")),
            "mushrunebasic" => _basicOils,
            "catalyst" => ResolveCatalysts(),
            "HarvestTimelessSplinterExchange" => ByTag("legion_splinter"),
            "HarvestTimelessEmblemExchange" => _scalingByCraft.TryGetValue(craftRow, out var emblems) && emblems.Count >= 2
                ? emblems
                : FragmentPrefix("Metadata/Items/MapFragments/CurrencyLegionFragment", excludeSuffix: "Uber"),
            "afflictionorbs" => _deliriumOrbs,
            "astrolabes" => ResolveAstrolabes(),
            "HarvestShaperFragmentExchange" => ExchangeOrFallback("FragmentsShaper",
                () => FragmentPrefix("Metadata/Items/MapFragments/CurrencyShaperFragment")),
            "HarvestElderFragmentExchange" => FragmentPrefix("Metadata/Items/MapFragments/CurrencyElderFragment"),
            "HarvestConquerorFragmentExchange" => ExchangeOrFallback("FragmentsSirus",
                () => FragmentPrefix("Metadata/Items/MapFragments/CurrencySirusFragment")),
            "HarvestSacrificeFragmentExchange" => FragmentPrefix("Metadata/Items/MapFragments/CurrencyVaalFragment1_"),
            "HarvestMortalSacrificeFragmentExchange" => FragmentPrefix("Metadata/Items/MapFragments/CurrencyVaalFragment2_"),
            "HarvestUberElderFragmentExchange" => ByIds(
                "Metadata/Items/MapFragments/CurrencyUberElderFragment1",
                "Metadata/Items/MapFragments/CurrencyUberElderFragment2"),
            "HarvestUberShaperFragmentExchange" => ByIds(
                "Metadata/Items/MapFragments/CurrencyUberElderFragment3",
                "Metadata/Items/MapFragments/CurrencyUberElderFragment4"),
            _ => ByTag(token),
        };

        IReadOnlyList<ItemCatalog.BaseType> ExchangeOrFallback(string category, Func<IReadOnlyList<ItemCatalog.BaseType>> fallback) =>
            _byExchangeCategory.TryGetValue(category, out var list) && list.Count >= 2 ? list : fallback();

        IReadOnlyList<ItemCatalog.BaseType> ResolveCatalysts()
        {
            var tagged = ByTag("catalyst");
            var standard = tagged.Where(t => _catalog.AlternateQualityOf(t) is not null).ToList();
            return standard.Count >= 2 ? standard : [.. tagged.Where(t => !t.Id.EndsWith("Vaal", StringComparison.OrdinalIgnoreCase))];
        }

        IReadOnlyList<ItemCatalog.BaseType> ResolveAstrolabes()
        {
            if (_byExchangeCategory.TryGetValue("CurrencyAstrolabe", out var list))
            {
                var leagueSpecific = list.Where(t => !t.Id.Equals("Metadata/Items/Currency/AstrolabeGeneric", StringComparison.OrdinalIgnoreCase)).ToList();
                if (leagueSpecific.Count >= 2) return leagueSpecific;
            }
            return [.. Prefix("Metadata/Items/Currency/Astrolabe")
                .Where(t => !t.Id.Equals("Metadata/Items/Currency/AstrolabeGeneric", StringComparison.OrdinalIgnoreCase) &&
                    !t.Id.Equals("Metadata/Items/Currency/AstrolabeSettlers", StringComparison.OrdinalIgnoreCase))];
        }

        IReadOnlyList<ItemCatalog.BaseType> Prefix(string prefix) =>
            [.. _catalog.All.Where(t => t.Supported && t.Id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).DistinctBy(t => t.Hash)];

        IReadOnlyList<ItemCatalog.BaseType> FragmentPrefix(string prefix, string? excludeSuffix = null)
        {
            var source = _fragmentLayout.Count > 0 ? (IEnumerable<ItemCatalog.BaseType>)_fragmentLayout : _catalog.All;
            return [.. source.Where(t => t.Supported &&
                t.Id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                (excludeSuffix is null || !t.Id.EndsWith(excludeSuffix, StringComparison.OrdinalIgnoreCase)) &&
                !t.Id.EndsWith("Complete", StringComparison.OrdinalIgnoreCase)).DistinctBy(t => t.Hash)];
        }

        IReadOnlyList<ItemCatalog.BaseType> ByTag(string tag) =>
            [.. _catalog.All.Where(t => t.Supported && t.Tags.Contains(tag)).DistinctBy(t => t.Hash)];

        IReadOnlyList<ItemCatalog.BaseType> ByIds(params string[] ids)
        {
            var list = new List<ItemCatalog.BaseType>(ids.Length);
            foreach (string id in ids)
                if (_catalog.Find(id) is { Supported: true } type) list.Add(type);
            return list;
        }
    }

    static IReadOnlyList<(uint Currency, int Amount)>? Costs(int type, int amount, int sacred,
        (int Type, uint Hash)[] lifeforce, uint sacredHash, int rancour, uint rancourHash)
    {
        var costs = new List<(uint, int)>();
        if (amount > 0)
        {
            uint currency = lifeforce.FirstOrDefault(l => l.Type == type).Hash;
            if (currency == 0) return null;
            costs.Add((currency, amount));
        }
        if (sacred > 0)
        {
            if (sacredHash == 0) return null;
            costs.Add((sacredHash, sacred));
        }
        if (rancour > 0)
        {
            if (rancourHash == 0) return null;
            costs.Add((rancourHash, rancour));
        }
        return costs;
    }

    static string StripToken(string id, string token) => Regex.Replace(id, token, "", RegexOptions.IgnoreCase);
}
