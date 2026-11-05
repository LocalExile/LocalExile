using GameServer.Core.Protocol.Instance;

/// <summary>
/// Currency used on an item (C→S 0x71, Agents/docs/protocol/13-items.md §4b): what each handled orb does to the target.
/// </summary>
static class CurrencyEffects
{
    public const string ChromaticOrb = "Metadata/Items/Currency/CurrencyRerollSocketColours";
    public const string JewellersOrb = "Metadata/Items/Currency/CurrencyRerollSocketNumbers";
    public const string OrbOfFusing = "Metadata/Items/Currency/CurrencyRerollSocketLinks";
    public const string OrbOfAlchemy = "Metadata/Items/Currency/CurrencyUpgradeToRare";
    public const string ChaosOrb = "Metadata/Items/Currency/CurrencyRerollRare";
    public const string DivineOrb = "Metadata/Items/Currency/CurrencyModValues";
    public const string BlessedOrb = "Metadata/Items/Currency/CurrencyRerollImplicit";
    public const string OrbOfScouring = "Metadata/Items/Currency/CurrencyConvertToNormal";
    public const string OrbOfTransmutation = "Metadata/Items/Currency/CurrencyUpgradeToMagic";
    public const string OrbOfAlteration = "Metadata/Items/Currency/CurrencyRerollMagic";
    public const string OrbOfAugmentation = "Metadata/Items/Currency/CurrencyAddModToMagic";
    public const string RegalOrb = "Metadata/Items/Currency/CurrencyUpgradeMagicToRare";
    public const string ExaltedOrb = "Metadata/Items/Currency/CurrencyAddModToRare";
    public const string OrbOfAnnulment = "Metadata/Items/Currency/CurrencyRemoveMod";
    public const string AncientOrb = "Metadata/Items/Currency/CurrencyRerollUnique";
    public const string LesserEldritchIchor = "Metadata/Items/Currency/CurrencyEldritchIchor1";
    public const string GreaterEldritchIchor = "Metadata/Items/Currency/CurrencyEldritchIchor2";
    public const string GrandEldritchIchor = "Metadata/Items/Currency/CurrencyEldritchIchor3";
    public const string ExceptionalEldritchIchor = "Metadata/Items/Currency/CurrencyEldritchIchor4";
    public const string LesserEldritchEmber = "Metadata/Items/Currency/CurrencyEldritchEmber1";
    public const string GreaterEldritchEmber = "Metadata/Items/Currency/CurrencyEldritchEmber2";
    public const string GrandEldritchEmber = "Metadata/Items/Currency/CurrencyEldritchEmber3";
    public const string ExceptionalEldritchEmber = "Metadata/Items/Currency/CurrencyEldritchEmber4";

    public const string EldritchChaosOrb = "Metadata/Items/Currency/CurrencyEldritchRerollRare";
    public const string EldritchExaltedOrb = "Metadata/Items/Currency/CurrencyEldritchAddModToRare";

    /// <summary>Orb of Unmaking (CurrencyItems Action atlas_skill_point_respec): used from the bag (C→S 0x58), it refunds
    /// Atlas passive points. See Game.UseItem, the local rule is there.</summary>
    public const string OrbOfUnmaking = "Metadata/Items/Currency/CurrencyAtlasPassiveRefund";

    /// <summary>Eldritch Chaos Orb, the documented LocalExile rule: a Chaos Orb (all unprotected explicit affixes
    /// rerolled, new tiers and values) on a rare item that carries an Eldritch implicit; the implicit stays. An item
    /// without one is refused before the orb is spent.</summary>
    public static Item? EldritchChaos(Item item, ItemRolls rolls, Random random) =>
        rolls.HasEldritchImplicit(item) ? rolls.Chaos(item, random) : null;

    /// <summary>Eldritch Exalted Orb, the documented LocalExile rule: an Exalted Orb (one more explicit affix within the
    /// rare limits) on a rare item that carries an Eldritch implicit; the implicit stays. An item without one is
    /// refused before the orb is spent.</summary>
    public static Item? EldritchExalted(Item item, ItemRolls rolls, Random random) =>
        rolls.HasEldritchImplicit(item) ? rolls.Exalted(item, random) : null;

    public const string EldritchAnnulmentOrb = "Metadata/Items/Currency/CurrencyEldritchRemoveMod";

    /// <summary>Eldritch Orb of Annulment, the documented LocalExile rule: an Orb of Annulment (one random unprotected
    /// explicit affix leaves, the implicits stay) on a magic or rare item that carries an Eldritch implicit; the implicit
    /// stays. An item without one is refused before the orb is spent.</summary>
    public static Item? EldritchAnnulment(Item item, ItemRolls rolls, Random random) =>
        rolls.HasEldritchImplicit(item) ? rolls.Annul(item, random) : null;

    public const string OrbOfBinding = "Metadata/Items/Currency/CurrencyUpgradeToRareAndSetSockets";

    /// <summary>Orb of Binding, the documented LocalExile rule: a normal, uncorrupted weapon or armour piece that can hold
    /// four sockets becomes a rare item (as Orb of Alchemy, 4-6 affixes and a rare name) with four sockets, all linked,
    /// their colours from the base's requirements. Refused before spending when the item is not normal, cannot hold four
    /// sockets or has a gem in one.</summary>
    public static Item? Binding(Item item, ItemCatalog.BaseType type, ItemRolls rolls,
        (int Str, int Dex, int Int) requirements, Random random)
    {
        if (item.Rarity != ItemRarity.Normal || item.Corrupted || item.Mirrored) return null;
        if (!type.Components.Contains("Weapon") && !type.Components.Contains("Armour")) return null;
        if (SocketCapacity(item, type) < 4 || MaximumSockets(item, type) < 4 || item.Sockets.Any(s => s.Gem is not null))
            return null;
        var sockets = Enumerable.Range(0, 4).Select(_ => new ItemSocket(SocketColours.Colour(requirements, random))).ToArray();
        return rolls.Alchemy(item with { Sockets = sockets, Links = [4] }, random);
    }

    public const string TaintedChaosOrb = "Metadata/Items/Currency/CurrencyHellscapeRerollRare";
    public const string TaintedExaltedOrb = "Metadata/Items/Currency/CurrencyHellscapeAddModToRare";
    public const string TaintedChromaticOrb = "Metadata/Items/Currency/CurrencyHellscapeRerollSocketColours";
    public const string TaintedOrbOfFusing = "Metadata/Items/Currency/CurrencyHellscapeRerollSocketLinks";
    public const string TaintedJewellersOrb = "Metadata/Items/Currency/CurrencyHellscapeRerollSocketNumbers";
    public const string TaintedArmourersScrap = "Metadata/Items/Currency/CurrencyHellscapeArmourQuality";
    public const string TaintedBlacksmithsWhetstone = "Metadata/Items/Currency/CurrencyHellscapeWeaponQuality";

    /// <summary>The Tainted (Scourge) currencies this class applies: the ordinary effect, but only on a corrupted item.</summary>
    public static readonly IReadOnlySet<string> TaintedIds = new HashSet<string>
    {
        TaintedChaosOrb, TaintedExaltedOrb, TaintedChromaticOrb, TaintedOrbOfFusing, TaintedJewellersOrb,
        TaintedArmourersScrap, TaintedBlacksmithsWhetstone,
    };

    /// <summary>Tainted currency, the documented LocalExile rule: the ordinary effect on a corrupted item, which stays
    /// corrupted. A non-corrupted or mirrored item is refused before the orb is spent, and so is anything the ordinary
    /// effect refuses (wrong rarity, no room, maximum sockets, a gem in the sockets, and so on). The item is handed to the
    /// ordinary effect with its corruption flag cleared, then the result is corrupted again.</summary>
    public static Item? Tainted(string id, Item item, ItemCatalog.BaseType type, ItemRolls rolls,
        (int Str, int Dex, int Int) requirements, Random random)
    {
        if (!item.Corrupted || item.Mirrored) return null;
        var plain = item with { Corrupted = false };
        Item? result = id switch
        {
            TaintedChaosOrb => rolls.Chaos(plain, random),
            TaintedExaltedOrb => rolls.Exalted(plain, random),
            TaintedChromaticOrb => Chromatic(plain, requirements, random),
            TaintedOrbOfFusing => Fusing(plain, type, random),
            TaintedJewellersOrb => Jewellers(plain, type, random),
            TaintedArmourersScrap => ArmourerScrap(plain),
            TaintedBlacksmithsWhetstone => Whetstone(plain),
            _ => null,
        };
        return result is { } changed ? changed with { Corrupted = true } : null;
    }

    /// <summary>The Eater of Worlds implicit tier an Eldritch Ichor adds (Lesser 1 … Exceptional 4).</summary>
    public static int? EldritchIchorTier(string id) => id switch
    {
        LesserEldritchIchor => 1, GreaterEldritchIchor => 2, GrandEldritchIchor => 3, ExceptionalEldritchIchor => 4,
        _ => null,
    };

    /// <summary>The Searing Exarch implicit tier an Eldritch Ember adds (Lesser 1 … Exceptional 4).</summary>
    public static int? EldritchEmberTier(string id) => id switch
    {
        LesserEldritchEmber => 1, GreaterEldritchEmber => 2, GrandEldritchEmber => 3, ExceptionalEldritchEmber => 4,
        _ => null,
    };

    /// <summary>Eldritch Ichor: adds an Eater of Worlds implicit to body armour, boots, gloves or a helmet, replacing
    /// the other implicit modifiers except Searing Exarch ones. Normal, magic and rare items only.</summary>
    public static Item? EldritchIchor(Item item, ItemCatalog.BaseType type, int tier, ItemRolls rolls, Random random) =>
        rolls.EldritchIchor(item, type, tier, random);

    /// <summary>Eldritch Ember: adds a Searing Exarch implicit to body armour, boots, gloves or a helmet, replacing
    /// the other implicit modifiers except Eater of Worlds ones. Normal, magic and rare items only.</summary>
    public static Item? EldritchEmber(Item item, ItemCatalog.BaseType type, int tier, ItemRolls rolls, Random random) =>
        rolls.EldritchEmber(item, type, tier, random);

    /// <summary>Orb of Annulment: one random unprotected explicit affix leaves a magic or rare item; implicits, the
    /// other affixes, rarity and name stay. Corrupted/mirrored/unsupported/unmodifiable bases and items without an
    /// eligible explicit are refused before the inventory handler spends an orb.</summary>
    public static Item? Annulment(Item item, ItemRolls rolls, Random random) => rolls.Annul(item, random);

    /// <summary>Exalted Orb: a modifiable rare item keeps its affixes and name and gets one more within its rare
    /// limits (three per side, two for jewels). Flasks are magic-only; non-rare items, unknown/unmodifiable bases and
    /// full items are refused before the inventory handler spends an orb.</summary>
    public static Item? Exalted(Item item, ItemRolls rolls, Random random) => rolls.Exalted(item, random);

    /// <summary>Regal Orb: a modifiable magic item becomes rare with its affixes kept, one more affix from the rare
    /// pool (at most three of each kind) and a rare name. Flasks are magic-only; anything else, or no affix that
    /// fits, is refused before the inventory handler spends the orb.</summary>
    public static Item? Regal(Item item, ItemRolls rolls, Random random) => rolls.Regal(item, random);

    /// <summary>Orb of Augmentation: a modifiable magic item with a free prefix or suffix (at most one of each) gets
    /// one more affix from the ordinary weighted pool; its affixes and rarity stay. Anything else is refused before
    /// the inventory handler spends an orb.</summary>
    public static Item? Augmentation(Item item, ItemRolls rolls, Random random) => rolls.Augment(item, random);

    /// <summary>Orb of Alteration: a modifiable magic item gets new affixes, 1-2 with at most one prefix and one
    /// suffix, each tier drawn by weight. Unknown/unmodifiable bases, non-magic items and empty pools are refused
    /// before the inventory handler spends an orb.</summary>
    public static Item? Alteration(Item item, ItemRolls rolls, Random random) => rolls.Alter(item, random);

    /// <summary>Orb of Transmutation: a modifiable normal item becomes identified magic, with 1-2 weighted affixes.
    /// Admission and the ordinary pool are validated before the inventory handler spends an orb.</summary>
    public static Item? Transmutation(Item item, ItemRolls rolls, Random random) => rolls.Transmute(item, random);

    /// <summary>Remove unprotected explicit mods, preserving quality, sockets and locked affixes.</summary>
    public static Item? Scouring(Item item, ItemRolls rolls) => rolls.Scour(item);

    public const string ArmourersScrap = "Metadata/Items/Currency/CurrencyArmourQuality";
    public const int MaximumQuality = 20;

    public const string SacredOrb = "Metadata/Items/Currency/CurrencyRerollDefences";

    /// <summary>Reroll only the shared base-defence roll of uncorrupted, unmirrored armour with a variable native range.
    /// A successful roll can repeat the old value; all other item state stays intact.</summary>
    public static Item? Sacred(Item item, ItemCatalog.BaseDefences? defences, Random random) =>
        !item.Components.Contains("Armour") || !item.Components.Contains("Mods") || item.Corrupted || item.Mirrored
            || defences is not { HasVariableRange: true } ? null
            : item with { DefenceBaseRoll = (byte)random.Next(Item.MaximumDefenceBaseRoll + 1) };

    /// <summary>Armourer's Scrap: +1% quality on an armour piece (any rarity), up to 20%; the client derives the higher
    /// armour / evasion / energy shield from the quality. Refused for other items or at 20%.</summary>
    public static Item? ArmourerScrap(Item item) =>
        !item.Components.Contains("Armour") || !item.Components.Contains("Quality") || item.Quality >= MaximumQuality ? null
            : item with { Quality = (byte)(item.Quality + 1) };

    public const string VaalOrb = "Metadata/Items/Currency/CurrencyCorrupt";

    /// <summary>Vaal Orb, the documented LocalExile rule: a modifiable weapon, armour piece or jewellery (ring,
    /// amulet, belt) that is not unique or corrupted/mirrored is always corrupted. Normal/magic become rare; rare
    /// items replace 1-5 affixes or reroll all of them. One white socket is coloured. Missing/empty pools and
    /// unsupported or unmodifiable bases are refused before the inventory handler spends an orb.</summary>
    public static Item? Vaal(Item item, ItemCatalog.BaseType type, (int Str, int Dex, int Int) requirements, ItemRolls rolls, Random random) =>
        rolls.Vaal(item, type, requirements, random);

    public const string CartographersChisel = "Metadata/Items/Currency/CurrencyMapQuality";

    /// <summary>CurrencyItems.Action values this class applies. A new table row with the same action (another Maven
    /// chisel, another Vaal, another quality orb) is picked up without an Id list.</summary>
    public static bool HandlesAction(string? action) =>
        action is { Length: > 0 } && (action.StartsWith("add_cleansing_fire_implicit_", StringComparison.Ordinal)
            || action.StartsWith("add_great_tangle_implicit_", StringComparison.Ordinal)
            || action is "add_map_alt_quality" or "add_armour_quality" or "add_weapon_quality" or "add_flask_quality"
                or "add_gem_quality" or "corrupt_item" or "special_corrupt_item"
                or "identify" or "reroll" or "transmute_to_magic" or "transmute_to_rare" or "transmute_to_random"
                or "add_mod_to_magic" or "reroll_magic" or "convert_to_normal" or "add_mod_to_rare"
                or "upgrade_magic_to_rare" or "reroll_socket_colours" or "reroll_socket_links" or "reroll_socket_numbers"
                or "reroll_mod_values" or "reroll_implicit_mod" or "reroll_variable_defences"
                or "remove_random_mod" or "fracture_random_mod" or "add_flask_seal" or "add_flask_injector");

    public static bool Recognizes(ItemCatalog items, ItemCatalog.BaseType type, bool essence = false) =>
        essence || HandlesAction(items.CurrencyAction(type))
        || type.Id is "Metadata/Items/Currency/CurrencyIdentification" or ChromaticOrb or JewellersOrb or OrbOfFusing
            or OrbOfAlchemy or ChaosOrb or DivineOrb or BlessedOrb or OrbOfScouring or OrbOfTransmutation
            or OrbOfAlteration or OrbOfAugmentation or RegalOrb or ExaltedOrb or OrbOfAnnulment or AncientOrb
            or ArmourersScrap or SacredOrb or BlacksmithsWhetstone or OrbOfChance or VaalOrb or GlassblowersBauble
            or GemcuttersPrism or FracturingOrb or CartographersChisel or EldritchChaosOrb or EldritchExaltedOrb
            or EldritchAnnulmentOrb or OrbOfBinding or OrbOfUnmaking
            or TaintedChaosOrb or TaintedExaltedOrb or TaintedChromaticOrb or TaintedOrbOfFusing or TaintedJewellersOrb
            or TaintedArmourersScrap or TaintedBlacksmithsWhetstone
            or LesserEldritchIchor or GreaterEldritchIchor or GrandEldritchIchor or ExceptionalEldritchIchor
            or LesserEldritchEmber or GreaterEldritchEmber or GrandEldritchEmber or ExceptionalEldritchEmber
            or FlaskEnchantments.Enkindling or FlaskEnchantments.Instilling;

    static int? ActionTier(string action, string prefix) =>
        action.StartsWith(prefix, StringComparison.Ordinal)
        && int.TryParse(action[prefix.Length..], out int n) && n > 0 ? n : null;

    /// <summary>Apply a CurrencyItems.Action. True when this action is implemented (the item may still be null because
    /// the target was refused). Id constants stay only as a fallback when the table has no Action.</summary>
    public static bool TryAction(ItemCatalog items, ItemRolls rolls, Item target, ItemCatalog.BaseType currency,
        Random random, out Item? result, UniqueCatalog.StatRoll? uniqueRolls = null)
    {
        result = null;
        if (items.CurrencyAction(currency) is not { } action || !HandlesAction(action)) return false;
        var targetType = items.ByHash(target.TypeHash);
        result = action switch
        {
            "identify" => target.Identified || !target.Components.Contains("Mods") ? null : target with { Identified = true },
            "add_map_alt_quality" => targetType is not null && items.AlternateQualityOf(currency) is byte qualityType
                ? Chisel(target, targetType, qualityType) : null,
            "add_armour_quality" => ArmourerScrap(target),
            "add_weapon_quality" => Whetstone(target),
            "add_flask_quality" => Glassblower(target),
            "add_gem_quality" => Gemcutter(target),
            "corrupt_item" or "special_corrupt_item" => targetType is not null
                ? Vaal(target, targetType, items.Requirements(targetType), rolls, random) : null,
            "reroll" => Chaos(target, rolls, random),
            "transmute_to_magic" => Transmutation(target, rolls, random),
            "transmute_to_rare" => Alchemy(target, rolls, random),
            "transmute_to_random" => Chance(target, rolls, random),
            "add_mod_to_magic" => Augmentation(target, rolls, random),
            "reroll_magic" => Alteration(target, rolls, random),
            "convert_to_normal" => Scouring(target, rolls),
            "add_mod_to_rare" => Exalted(target, rolls, random),
            "upgrade_magic_to_rare" => Regal(target, rolls, random),
            "reroll_socket_colours" => targetType is not null
                ? Chromatic(target, items.Requirements(targetType), random) : null,
            "reroll_socket_links" => targetType is not null ? Fusing(target, targetType, random) : null,
            "reroll_socket_numbers" => targetType is not null ? Jewellers(target, targetType, random) : null,
            "reroll_mod_values" => rolls.Reforge(target, random, uniqueRolls),
            "reroll_implicit_mod" => rolls.Bless(target, random, uniqueRolls),
            "reroll_variable_defences" => targetType is not null
                ? Sacred(target, items.Defences(targetType), random) : null,
            "remove_random_mod" => Annulment(target, rolls, random),
            "fracture_random_mod" => Fracture(target, rolls, random),
            "add_flask_seal" or "add_flask_injector" => targetType is not null
                ? FlaskEnchantments.Apply(target, targetType, items.Mods, action == "add_flask_injector", random) : null,
            _ when ActionTier(action, "add_cleansing_fire_implicit_") is int ember && targetType is not null =>
                EldritchEmber(target, targetType, ember, rolls, random),
            _ when ActionTier(action, "add_great_tangle_implicit_") is int ichor && targetType is not null =>
                EldritchIchor(target, targetType, ichor, rolls, random),
            _ => null,
        };
        return true;
    }

    /// <summary>A map item: the Map or MapKey component (and the ItemClasses Id as fallback), so a new generic key
    /// base is admitted without naming each map.</summary>
    public static bool IsMap(ItemCatalog.BaseType type) =>
        type.Components.Contains("Map") || type.Components.Contains("MapKey")
        || type.Class is MapDevice.MapClass or MapDevice.MapKeyClass;

    public static bool IsMap(Item item, ItemCatalog.BaseType type) =>
        item.Components.Contains("Map") || item.Components.Contains("MapKey") || IsMap(type);

    /// <summary>Any map quality chisel (CurrencyItems Action add_map_alt_quality): +1% quality of that
    /// AlternateQualityTypes row, up to 20%. A different type replaces the old one. Refused for non-maps, or at 20% of
    /// the same type.</summary>
    public static Item? Chisel(Item item, ItemCatalog.BaseType type, byte qualityType) =>
        !IsMap(item, type) ? null
        : item.Quality >= MaximumQuality && item.AlternateQualityType == qualityType ? null
        : item with
        {
            Quality = item.Quality >= MaximumQuality ? item.Quality : (byte)(item.Quality + 1),
            AlternateQualityType = qualityType,
        };

    public const string GlassblowersBauble = "Metadata/Items/Currency/CurrencyFlaskQuality";

    /// <summary>Glassblower's Bauble: +1% quality on a flask (any rarity), up to 20%. Refused for other items or at 20%.</summary>
    public static Item? Glassblower(Item item) =>
        !item.Components.Contains("Flask") || !item.Components.Contains("Quality") || item.Quality >= MaximumQuality ? null
            : item with { Quality = (byte)(item.Quality + 1) };

    public const string FracturingOrb = "Metadata/Items/Currency/CurrencyFractureRare";

    /// <summary>Hinekora's Lock: puts an item into the foreseeing state (ItemRolls.ApplyLock).</summary>
    public const string HinekorasLock = "Metadata/Items/Currency/CurrencyHinekorasLock";

    /// <summary>Fracturing Orb (as the official one): a rare item with at least four explicit mods, not corrupted,
    /// mirrored or fractured yet, on a base and class the game allows to fracture, has one random explicit mod
    /// fractured: it moves to the front of the explicits and from then on no currency removes or changes it (Scouring,
    /// Chaos, Annulment, Divine, Vaal …).</summary>
    public static Item? Fracture(Item item, ItemRolls rolls, Random random) => rolls.Fracture(item, random);

    public const string MirrorOfKalandra = "Metadata/Items/Currency/CurrencyDuplicate";

    /// <summary>Mirror of Kalandra (as the official one): a mirrored copy of a non-unique equipment item that is neither
    /// corrupted nor mirrored, with every property kept (mods and their values, fractured mods, quality, sockets, links,
    /// socketed gems, name). The original stays as it was. Null when the item cannot be mirrored.</summary>
    public static Item? Mirror(Item item, ItemRolls rolls) => rolls.Mirror(item);

    public const string GemcuttersPrism = "Metadata/Items/Currency/CurrencyGemQuality";

    /// <summary>Gemcutter's Prism: +1% quality on a gem (active or support) that is not corrupted, up to 20%. Refused for
    /// other items, corrupted gems or at 20%.</summary>
    public static Item? Gemcutter(Item item) =>
        !item.Components.Contains("SkillGem") || !item.Components.Contains("Quality") || item.Corrupted || item.Quality >= MaximumQuality ? null
            : item with { Quality = (byte)(item.Quality + 1) };

    public const string OrbOfChance = "Metadata/Items/Currency/CurrencyUpgradeRandomly";

    /// <summary>Orb of Chance, the documented LocalExile rule (not the official unique outcome table): a modifiable
    /// normal weapon or armour piece becomes rare (30%: 4-6 affixes and a rare name) or magic (2-4 affixes, at most
    /// two of each kind), identified. Corrupted/mirrored/unsupported/unmodifiable targets and empty pools are refused
    /// before the inventory handler spends an orb.</summary>
    public static Item? Chance(Item item, ItemRolls rolls, Random random) => rolls.Chance(item, random);

    public const string BlacksmithsWhetstone = "Metadata/Items/Currency/CurrencyWeaponQuality";

    /// <summary>Blacksmith's Whetstone: +1% quality on a weapon (any rarity), up to 20%; the client derives the higher
    /// physical damage from the quality. Refused for other items or at 20%.</summary>
    public static Item? Whetstone(Item item) =>
        !item.Components.Contains("Weapon") || !item.Components.Contains("Quality") || item.Quality >= MaximumQuality ? null
            : item with { Quality = (byte)(item.Quality + 1) };

    /// <summary>Orb of Alchemy: a modifiable normal item becomes an identified rare one with 4-6 new weighted
    /// affixes (CoE's 2/3, 1/4, 1/12) and a rare name. Flasks are magic-only; unknown/unmodifiable bases, non-normal
    /// items and empty pools are refused before the inventory handler spends an orb.</summary>
    public static Item? Alchemy(Item item, ItemRolls rolls, Random random) => rolls.Alchemy(item, random);

    /// <summary>Chaos Orb: a modifiable rare item gets all unprotected explicit affixes rerolled (4-6 new ones, new
    /// tiers and values) and a new rare name; implicits stay. Flasks are magic-only; non-rare items, unknown/
    /// unmodifiable bases and empty pools are refused before the inventory handler spends an orb.</summary>
    public static Item? Chaos(Item item, ItemRolls rolls, Random random) => rolls.Chaos(item, random);

    /// <summary>Change only links; socket colours, gems and all other properties stay intact.</summary>
    public static Item? Fusing(Item item, ItemCatalog.BaseType type, Random random)
    {
        int count = item.Sockets.Count;
        if (count < 2 || count > 6 || count > SocketCapacity(item, type)) return null;
        IReadOnlyList<byte> old = item.Links ?? [.. Enumerable.Repeat((byte)1, count)];
        // Small fully linked items can be unlinked; protect completed five- and six-socket items.
        if (count >= 5 && old.Count == 1 && old[0] == count) return null;

        // At most 32 possible partitions. Weight by the largest linked group, then divide that weight among
        // its layouts. These are initial server weights, not recovered official probabilities.
        double[] weights = [0, 500, 3500, 4500, 1400, 90, 10];
        var layouts = new List<byte[]>();
        int[] sizes = new int[7];
        for (int mask = 0; mask < 1 << (count - 1); mask++)
        {
            var groups = new List<byte> { 1 };
            for (int edge = 0; edge < count - 1; edge++)
                if ((mask & (1 << edge)) != 0) groups[^1]++;
                else groups.Add(1);
            var layout = groups.ToArray();
            layouts.Add(layout);
            sizes[layout.Max()]++;
        }
        var choices = layouts.Where(g => !g.SequenceEqual(old)).Select(g =>
            (Links: g, Weight: weights[g.Max()] / sizes[g.Max()] * Math.Pow(1 + item.Quality / 100.0, g.Max() - 1))).ToArray();
        double roll = random.NextDouble() * choices.Sum(c => c.Weight);
        foreach (var choice in choices)
        {
            roll -= choice.Weight;
            if (roll < 0) return item with { Links = choice.Links };
        }
        return item with { Links = choices[^1].Links };
    }

    // Initial local rules: 1, 2, 3, 4 or 6 sockets (five deliberately excluded).
    // Weights and link chances are approximations, not recovered client/server constants.
    public static int MaximumSockets(Item item, ItemCatalog.BaseType type)
    {
        int levelLimit = item.ItemLevel >= 50 ? 6 : item.ItemLevel >= 25 ? 4 : item.ItemLevel >= 2 ? 3 : 2;
        return Math.Min(SocketCapacity(item, type), levelLimit);
    }

    public static int SocketCapacity(Item item, ItemCatalog.BaseType type)
    {
        if (!item.Components.Contains("Sockets") || !type.Components.Contains("Sockets")) return 0;
        // The class setting is the cap, not the inventory cell count: a wand is 1x3 cells but may be given six
        // sockets by the setting, as the old Game_SOCKET_CAPACITY dev flag already allowed. The client accepts it.
        return SystemSettings.Default.SocketMax(type.Class);
    }

    /// <summary>Reroll the socket count and link groups. Refused targets spend no currency. 3.29: only the sockets the
    /// reroll adds are white; sockets that stay keep their colours (13-items.md, "Socket colours (3.29)").</summary>
    public static Item? Jewellers(Item item, ItemCatalog.BaseType type, Random random)
    {
        int max = MaximumSockets(item, type);
        if (max == 0 || item.Sockets.Count >= SocketCapacity(item, type) || item.Sockets.Any(s => s.Gem is not null)) return null;
        (int Count, int Weight)[] choices = [(1, 50), (2, 120), (3, 100), (4, 30), (6, 1)];
        var allowed = choices.Where(c => c.Count <= max && c.Count != item.Sockets.Count).ToArray();
        if (allowed.Length == 0) return null;
        int roll = random.Next(allowed.Sum(c => c.Weight));
        int count = allowed[0].Count;
        foreach (var choice in allowed)
        {
            if (roll < choice.Weight) { count = choice.Count; break; }
            roll -= choice.Weight;
        }
        var sockets = new List<ItemSocket>();
        var links = new List<byte> { 1 };
        for (int i = 0; i < count; i++)
        {
            sockets.Add(i < item.Sockets.Count ? item.Sockets[i] : new(SocketColour.White));
            if (i == 0) continue;
            if (random.Next(2) == 0) links[^1]++;
            else links.Add(1);
        }
        return item with { Sockets = sockets, Links = links };
    }

    /// <summary>
    /// Chromatic Orb (3.29): one socket is forced non-white, the rest roll normally (white, or a colour at the item's
    /// chance, which the item's quality raises). Gems and links stay.
    /// </summary>
    public static Item? Chromatic(Item item, (int Str, int Dex, int Int) requirements, Random random)
    {
        if (item.Sockets.Count == 0) return null;
        int forced = random.Next(item.Sockets.Count);
        var sockets = item.Sockets.Select((socket, i) => socket with
        {
            Colour = i == forced ? SocketColours.Colour(requirements, random)
                : SocketColours.Roll(requirements, item.ItemLevel, item.Quality, random, chromatic: true),
        }).ToArray();
        return item with { Sockets = sockets };
    }
}
