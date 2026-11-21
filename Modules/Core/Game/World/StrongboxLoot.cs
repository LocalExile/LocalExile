using GameServer.Core.Protocol.Instance;
using static StrongboxCatalog;
using static Game;
using static Program;

/// <summary>
/// What an opened box drops: its base item count (<see cref="Definition.Items"/>) × (100 + <c>map_strongbox_item_quantity_+%</c>
/// + the box's <c>chest_item_quantity_+%</c>) / 100 × the league's item drop multiplier (a fraction is a chance of one more),
/// of the kind its Chests row names, at the area level plus the box's <c>chest_dropped_item_level_+</c>, through the
/// server's drop pipeline (<see cref="ItemDrops"/>, the item rolls). A box of no kind is the general table
/// (<see cref="ItemDrops.Container"/>). Then the contents its mods and the area add (additional normal, magic, rare and
/// unique items, utility flasks, Vaal gems, scarabs, fragments, a Vaal Orb), and what they do to every item (identified,
/// quality, sockets, links, mirrored copies; the atlas's "… are duplicated" stats).
/// </summary>
public static class StrongboxLoot
{
    // Artisan's: the quality currencies of the general currency table (ItemDrops.Currency), at their weights there.
    static readonly HashSet<string> QualityCurrency = new(StringComparer.Ordinal)
    {
        "Metadata/Items/Currency/CurrencyArmourQuality", "Metadata/Items/Currency/CurrencyWeaponQuality",
        "Metadata/Items/Currency/CurrencyFlaskQuality", "Metadata/Items/Currency/CurrencyGemQuality",
    };

    static readonly HashSet<string> ArmourClasses = new(StringComparer.Ordinal) { "Body Armour", "Helmet", "Gloves", "Boots", "Shield" };
    static readonly HashSet<string> JewelleryClasses = new(StringComparer.Ordinal) { "Ring", "Amulet" };

    /// <summary>The scarab stats of the "of Scarabs" suffixes (chest_drop_additional_&lt;word&gt;_scarabs) and the
    /// BaseItemTypes family (Metadata/Items/Scarabs/Scarab&lt;family&gt;…) each names.</summary>
    static readonly Dictionary<string, string> ScarabFamilies = new(StringComparer.Ordinal)
    {
        ["abyss"] = "Abyss", ["anarchy"] = "Anarchy", ["bestiary"] = "Beasts", ["betrayal"] = "Betrayal", ["beyond"] = "Beyond",
        ["blight"] = "Blight", ["breach"] = "Breach", ["cartography"] = "Maps", ["delirium"] = "Delirium",
        ["divination"] = "DivinationCards", ["domination"] = "Domination", ["essence"] = "Essence", ["expedition"] = "Expedition",
        ["harvest"] = "Harvest", ["incursion"] = "Incursion", ["influence"] = "Influence", ["legion"] = "Legion", ["misc"] = "Misc",
        ["reliquary"] = "Uniques", ["ritual"] = "Ritual", ["strongbox"] = "Strongbox", ["sulphite"] = "Sulphite",
        ["torment"] = "Torment", ["ultimatum"] = "Ultimatum",
    };

    /// <summary>The unique families a unique box's mods add one of, by stat: the warband caches' uniques and the Strange
    /// Barrel's poison uniques and Weylam's War Chest's pirate uniques, as poedb lists them (the client data names no
    /// family: https://poedb.tw/us/Mutewind_Cache, …/Redblade_Cache, …/Brinerot_Cache, …/Renegades_Cache,
    /// …/Strange_Barrel; Weylam's from …/Weylams_War_Chest's loot table, read as its pirate uniques).</summary>
    static readonly (string Stat, HashSet<string> Names)[] UniqueFamilies =
    [
        ("chest_drop_additional_cold_warband_uniques", ["Mutewind Pennant", "Mutewind Seal", "Mutewind Whispersteps"]),
        ("chest_drop_additional_fire_warband_uniques", ["Redblade Band", "Redblade Tramplers", "Redblade Banner"]),
        ("chest_drop_additional_lightning_warband_uniques", ["Brinerot Flag", "Brinerot Whalers", "Brinerot Mark"]),
        ("chest_drop_additional_chaos_warband_uniques", ["The Pariah", "Steppan Eard", "Broken Faith"]),
        ("chest_number_of_additional_spider_uniques_to_drop", ["Mark of the Doubting Knight", "Mortem Morsu", "Snakebite",
            "The Consuming Dark", "Bino's Kitchen Knife", "Dendrobate", "The Wasp Nest", "Coated Shrapnel", "Taproot",
            "The Embalmer", "Coralito's Signature"]),
        ("chest_number_of_additional_pirate_uniques_to_drop", ["Mao Kun", "Wheel of the Stormsail", "The Princess",
            "Fairgraves' Tricorne", "Roth's Reach", "Rotgut"]),
    ];

    /// <summary>A divination card's reward as world.db's divination_card_rewards gives it.</summary>
    public sealed record CardReward(string Card, string Kind, bool Corrupted)
    {
        public int VaalVesselWeight { get; init; }
    }

    internal static IReadOnlyList<CardReward> CardRewards() => GameData.Store is not { } store ? [] :
        [.. store.LoadRows("divination_card_rewards", "card_id")
            .Where(r => Convert.ToInt64(r["enabled"]) != 0)
            .Select(r => new CardReward((string)r["card_id"]!, (string)r["reward_kind"]!, Convert.ToInt64(r["corrupted"]) != 0)
                { VaalVesselWeight = Convert.ToInt32(r["vaal_vessel_weight"]) })];

    /// <param name="corrupted">The box is corrupted (a Vaal Orb, <c>map_strongboxes_are_corrupted</c>): its contents drop
    /// corrupted where they can be (poewiki, Strongbox: "Corrupted strongboxes (e.g. from using a Vaal Orb) will cause all of the
    /// strongbox's contents to also be corrupted, if possible"). Only the flag: no corruption outcome is rolled.</param>
    /// <param name="boxStat">A stat of the box's own mods (its implicits and rolled explicits).</param>
    public static List<ItemDrops.Drop> Roll(ClientState conn, Definition box, Func<string, int> boxStat, int level,
        MapMods stats, Random rng, bool corrupted = false)
    {
        int Stat(string name) => stats.Get(name);
        var catalog = ItemsFor(conn);
        var drops = DropsFor(conn);
        var rolls = RollsFor(conn);
        var rules = LeagueFor(conn).Rules;
        level = Math.Clamp(level + Math.Max(0, boxStat("chest_dropped_item_level_+")), 1, 100);
        double quantity = box.Items * Math.Max(0, 100 + Stat("map_strongbox_item_quantity_+%") + boxStat("chest_item_quantity_+%") + boxStat("chest_hidden_item_quantity_+%")) / 100.0
            * Math.Max(0, rules.ItemDropMultiplier);
        int count = Math.Max(1, (int)Math.Floor(quantity) + (rng.NextDouble() < quantity - Math.Floor(quantity) ? 1 : 0));
        // The box's "more rarity" on top of its hidden rarity: one increased value for the drop table.
        double moreRarity = 1 + boxStat("chest_item_rarity_+%_final_from_mod") / 100.0;
        int increasedRarity = (int)Math.Round((100 + boxStat("chest_hidden_item_rarity_+%") + boxStat("chest_item_rarity_+%")) * moreRarity) - 100;
        var chances = ItemDrops.WithIncreasedRarity(ItemDrops.RarityChances(MonsterRarity.Rare), increasedRarity, rules.EquipmentRarityMultiplier);
        var result = new List<ItemDrops.Drop>();
        var gems = box.Kind == Contents.Gem ? GemSources.DropPool(catalog) : null;
        bool supportsOnly = boxStat("chest_drops_only_support_gems") != 0;
        ItemCatalog.BaseType[] bases = box.Kind switch
        {
            Contents.Armour => Bases(t => ArmourClasses.Contains(t.Class)),
            Contents.Weapon => Bases(t => t.Components.Contains("Weapon")),
            Contents.Jewellery => Bases(t => JewelleryClasses.Contains(t.Class)),
            Contents.Jewel => [.. catalog.All.Where(t => ItemDrops.Droppable(t) && t.DropLevel <= level && t.Class is "Jewel" or "AbyssJewel")],
            Contents.Flask => [.. catalog.All.Where(t => ItemDrops.Droppable(t) && t.DropLevel <= level && t.Class.EndsWith("Flask", StringComparison.Ordinal))],
            Contents.Gem => [.. catalog.All.Where(t => t.Supported && t.DropLevel <= level && gems!.Contains(t.Row)
                && (!supportsOnly || t.Class == "Support Skill Gem"))],
            _ => [],
        };
        var currency = box.Kind is Contents.Currency or Contents.QualityCurrency
            ? ItemDrops.Currency.Where(c => box.Kind == Contents.Currency || QualityCurrency.Contains(c.Id))
                .Select(c => (Type: catalog.Find(c.Id), c.Weight)).Where(c => c.Type is { Supported: true } && c.Type.DropLevel <= level)
                .Select(c => (Type: c.Type!, c.Weight)).ToArray()
            : [];
        // "All contained Scarabs are the same type" (Operative's): one scarab type for the box.
        ItemCatalog.BaseType? sameScarab = null;
        // "Contains no regular drops" (the unique boxes that hold only their own contents).
        if (boxStat("chest_no_regular_drops") != 0) { }
        else if (box.Kind == Contents.General) result.AddRange(drops.Container(level, count, increasedRarity, rng));
        else
            for (int i = 0; i < count; i++)
                if (One() is { } drop) result.Add(drop);

        // The contents the box's mods and the area add.
        int normal = boxStat("chest_drop_additional_normal_items_up_to"), magic = boxStat("chest_drop_additional_magic_items_up_to");
        int rare = boxStat("chest_drop_additional_rare_items_up_to") + Stat("map_strongboxes_drop_x_additional_rare_items");
        for (int i = 0; i < normal; i++) if (Equipment(Bases(_ => true), ItemRarity.Normal) is { } d) result.Add(d);
        for (int i = 0; i < magic; i++) if (Equipment(Bases(_ => true), ItemRarity.Magic) is { } d) result.Add(d);
        for (int i = 0; i < rare; i++) if (Equipment(Bases(_ => true), ItemRarity.Rare) is { } d) result.Add(d);
        int uniques = boxStat("chest_drop_additional_unqiue_items"), poolUniques = boxStat("chest_drop_additional_number_of_uniques_of_drop_pool_types");
        for (int i = 0; i < uniques; i++) if (Unique(_ => true) is { } d) result.Add(d);
        for (int i = 0; i < poolUniques; i++) if (Unique(KindClass) is { } d) result.Add(d);
        // The unique boxes' own contents: a family of uniques, unique maps, rare Maraketh weapons.
        for (int i = 0; i < boxStat("chest_number_of_additional_kaom_uniques_to_drop"); i++)
            if (Unique(_ => true, u => u.Name.StartsWith("Kaom's", StringComparison.Ordinal)) is { } d) result.Add(d);
        foreach (var (stat, names) in UniqueFamilies)
            for (int i = 0; i < boxStat(stat); i++)
                if (Unique(_ => true, u => names.Contains(u.Name)) is { } d) result.Add(d);
        for (int i = 0; i < boxStat("chest_drop_additional_unique_maps"); i++)
            if (Unique(t => t.Class == "Map") is { } d) result.Add(d);
        for (int i = 0; i < boxStat("chest_drop_additional_rare_maraketh_weapons"); i++)
            if (Equipment(Bases(t => t.Components.Contains("Weapon") && t.Tags.Contains("maraketh")), ItemRarity.Rare) is { } d) result.Add(d);
        // Diviner's suffixes: cards whose reward is a unique item, a corrupted item, or currency (divination_card_rewards).
        AddCards(boxStat("chest_drop_additional_unique_item_divination_cards"), r => r.Kind == "unique");
        AddCards(boxStat("chest_drop_additional_corrupted_item_divination_cards"), r => r.Corrupted);
        AddCards(boxStat("chest_drop_additional_currency_item_divination_cards"), r => r.Kind == "currency");
        var flasks = catalog.All.Where(t => t.Supported && t.DropLevel <= level && t.Class == "UtilityFlask").ToArray();
        for (int i = 0; i < boxStat("chest_drops_additional_utility_flasks") && flasks.Length > 0; i++)
        {
            var type = flasks[rng.Next(flasks.Length)];
            result.Add(new(ItemCatalog.Create(type) with { ItemLevel = (byte)level }, type));
        }
        var vaal = catalog.All.Where(t => t.Supported && t.DropLevel <= level && t.Id.StartsWith("Metadata/Items/Gems/SkillGemVaal", StringComparison.Ordinal)).ToArray();
        for (int i = 0; i < boxStat("chest_drops_extra_vaal_gems") && vaal.Length > 0; i++)
        {
            var type = vaal[rng.Next(vaal.Length)];
            result.Add(new(ItemCatalog.Create(type) with { ItemLevel = 1, Corrupted = true }, type));
        }
        foreach (var (word, family) in ScarabFamilies)
            for (int i = 0; i < boxStat($"chest_drop_additional_{word}_scarabs"); i++)
                if (Scarab(family) is { } d) result.Add(d);
        var fragments = catalog.All.Where(t => t.Class == "MapFragment" && ItemDrops.Droppable(t) && t.DropLevel <= level
            && !t.Id.Contains("/Scarabs/", StringComparison.Ordinal) && !t.Name.StartsWith("Allflame Ember", StringComparison.Ordinal)).ToArray();
        for (int i = 0; i < boxStat("chest_drop_additional_map_fragments") && fragments.Length > 0; i++)
        {
            var type = fragments[rng.Next(fragments.Length)];
            result.Add(new(ItemCatalog.Create(type), type));
        }
        if (Stat("map_strongboxes_vaal_orb_drop_chance_%") is int vaalOrb and > 0 && rng.Next(100) < vaalOrb
            && catalog.Find("Metadata/Items/Currency/CurrencyCorrupt") is { Supported: true } orb)
            result.Add(new(ItemCatalog.Create(orb), orb));

        // What the box does to every item.
        // "Contained Gems have Experience": each gem drops with a random experience, at a level from 1 to its cap
        // (at most 20), anywhere within that level (the user's account of the retail box; the stat is only a flag).
        if (boxStat("chest_gems_drop_with_experience") != 0)
            for (int i = 0; i < result.Count; i++)
                if (catalog.Gem(result[i].Type) is { Thresholds.Count: > 0 } gem && result[i].Item.GemExperience >= 0)
                {
                    int top = Math.Min(20, gem.MaxLevel), gemLevel = rng.Next(1, top + 1);
                    int low = gem.ExperienceFor(gemLevel), high = gemLevel < top ? gem.ExperienceFor(gemLevel + 1) : low + 1;
                    result[i] = result[i] with { Item = result[i].Item with { GemExperience = low + rng.Next(Math.Max(1, high - low)) } };
                }
        // Deshret's Storm, "Contained Weapons are Animated": its weapons drop identified, ready for Animate Weapon (the
        // user's account of the retail box: "considered identified and animated upon opening").
        bool animated = boxStat("weapons_drop_animated") != 0;
        if (animated)
            for (int i = 0; i < result.Count; i++)
                if (result[i].Type.Components.Contains("Weapon") && !result[i].Item.Identified)
                    result[i] = result[i] with { Item = result[i].Item with { Identified = true } };
        bool identified = boxStat("chest_items_drop_identified") != 0;
        int quality = Math.Clamp(boxStat("chest_dropped_items_have_quality_%"), 0, 20);
        int sockets = boxStat("dropped_items_have_additional_sockets");
        bool linked = boxStat("dropped_items_are_fully_linked") != 0;
        for (int i = 0; i < result.Count; i++)
        {
            var (item, type) = (result[i].Item, result[i].Type);
            bool equipment = ItemDrops.EquipmentClasses.Contains(type.Class);
            if (identified && !item.Identified) item = item with { Identified = true };
            if (quality > item.Quality && (equipment || catalog.Gem(type) is not null || type.Class.EndsWith("Flask", StringComparison.Ordinal)))
                item = item with { Quality = (byte)quality };
            if (equipment && type.Components.Contains("Sockets") && item.Sockets.Count > 0 && (sockets > 0 || linked))
            {
                var socketList = item.Sockets.ToList();
                var links = (item.Links ?? [.. socketList.Select(_ => (byte)1)]).ToList();
                for (int s = 0; s < sockets && socketList.Count < SystemSettings.Default.SocketMax(type.Class); s++)
                {
                    socketList.Add(new(SocketColours.Roll(catalog.Requirements(type), level, 0, rng)));
                    links.Add(1);
                }
                if (linked) links = [(byte)socketList.Count];
                item = item with { Sockets = socketList, Links = links };
            }
            result[i] = new(item, type);
        }
        // Copies: "Contains Mirrored Items" (Kalandra) and the area's mirrored drops copy every item, mirrored where it
        // can be; the atlas's duplication stats copy their kind.
        bool mirrored = boxStat("dropped_items_are_duplicated") != 0 || Stat("map_strongbox_items_dropped_are_mirrored") != 0;
        var copies = result.Where(d => mirrored
            || d.Type.Class == "StackableCurrency" && Stat("map_strongboxes_currency_is_duplicated") != 0
            || d.Type.Class is "Map" or "MapKey" && Stat("map_strongboxes_maps_are_duplicated") != 0
            || d.Type.Class == "DivinationCard" && Stat("map_strongboxes_divination_cards_are_duplicated") != 0
            || catalog.Gem(d.Type) is not null && Stat("map_strongboxes_gems_are_duplicated") != 0).ToList();
        if (mirrored)
            for (int i = 0; i < result.Count; i++)
                if (result[i].Type.MaxStack <= 1) result[i] = result[i] with { Item = result[i].Item with { Mirrored = true } };
        result.AddRange(copies.Select(c => mirrored && c.Type.MaxStack <= 1 ? c with { Item = c.Item with { Mirrored = true } } : c));
        if (corrupted || boxStat("dropped_items_are_corrupted") != 0)
            for (int i = 0; i < result.Count; i++)
                if (result[i].Type.MaxStack <= 1) result[i] = result[i] with { Item = result[i].Item with { Corrupted = true } };
        return result;

        ItemDrops.Drop? One()
        {
            switch (box.Kind)
            {
                case Contents.Card: return drops.Card(rng, level);
                case Contents.Scarab:
                {
                    if (sameScarab is not null) return new(ItemCatalog.Create(sameScarab) with { StackSize = 1 }, sameScarab);
                    var drop = drops.Scarab(rng, level);
                    if (drop is not null && boxStat("chest_all_dropped_scarabs_are_same_content_type") != 0) sameScarab = drop.Type;
                    return drop;
                }
                case Contents.Map:
                    return MapBase(conn, catalog, level) is { } map
                        ? new(ItemCatalog.Create(map) with { ItemLevel = (byte)level }, map) : null;
                case Contents.Currency or Contents.QualityCurrency:
                {
                    double pick = rng.NextDouble() * currency.Sum(c => c.Weight);
                    foreach (var (type, weight) in currency)
                        if ((pick -= weight) <= 0) return new(ItemCatalog.Create(type) with { StackSize = 1 }, type);
                    return currency.Length > 0 ? new(ItemCatalog.Create(currency[^1].Type) with { StackSize = 1 }, currency[^1].Type) : null;
                }
                case Contents.Gem:
                {
                    if (bases.Length == 0) return null;
                    var type = bases[rng.Next(bases.Length)];
                    // A dropped gem is unlevelled (ItemDrops: its level comes from its experience).
                    return new(ItemCatalog.Create(type) with { ItemLevel = 1 }, type);
                }
                default: return Equipment(bases, null);
            }
        }

        // A piece of equipment of these bases: of the given rarity, or by the box's rarity chances (rare, else magic).
        ItemDrops.Drop? Equipment(ItemCatalog.BaseType[] pool, ItemRarity? rarity)
        {
            if (pool.Length == 0) return null;
            var type = pool[rng.Next(pool.Length)];
            var (socketList, links) = catalog.DropSockets(type, level, rng);
            var item = ItemCatalog.Create(type, socketList, links, random: rng) with { ItemLevel = (byte)level };
            rarity ??= rng.NextDouble() < chances.Rare ? ItemRarity.Rare : rng.NextDouble() < chances.Magic ? ItemRarity.Magic : ItemRarity.Normal;
            if (rarity != ItemRarity.Normal) item = rolls.Roll(item, rarity.Value, rng) with { Identified = false };
            return new(item, type);
        }

        // A unique of these bases (and names) by its weight; one is certain when any qualifies (the weights are absolute,
        // so PickUnique may decline: then one of the pool at random).
        ItemDrops.Drop? Unique(Func<ItemCatalog.BaseType, bool> kind, Func<UniqueCatalog.Definition, bool>? named = null)
        {
            var uniqueCatalog = UniquesFor(conn);
            var pool = uniqueCatalog.All.Where(u => (named is null ? !u.EncounterOnly : named(u)) && catalog.Find(u.BaseTypeId) is { } t
                && (named is not null || t.DropLevel <= level) && ItemDrops.Droppable(t) && kind(t)).ToArray();
            if (pool.Length == 0) return null;
            var chosen = drops.PickUnique(pool, rng) ?? (named is null ? null : pool[rng.Next(pool.Length)]);
            if (chosen is null || uniqueCatalog.Create(chosen, rng) is not { } item
                || catalog.ByHash(item.TypeHash) is not { } type) return null;
            return new(item with { ItemLevel = (byte)level, Identified = false }, type);
        }

        void AddCards(int count, Func<CardReward, bool> wanted)
        {
            if (count <= 0) return;
            var pool = CardRewards().Where(wanted).Select(r => catalog.Find(r.Card))
                .OfType<ItemCatalog.BaseType>().Where(t => t.Supported && t.DropLevel <= level).ToArray();
            for (int i = 0; i < count && pool.Length > 0; i++)
            {
                var type = pool[rng.Next(pool.Length)];
                result.Add(new(ItemCatalog.Create(type) with { StackSize = 1 }, type));
            }
        }

        // The box kind's own classes, for "additional Unique Items" of its drop pool types.
        bool KindClass(ItemCatalog.BaseType t) => box.Kind switch
        {
            Contents.Armour => ArmourClasses.Contains(t.Class),
            Contents.Weapon => t.Components.Contains("Weapon"),
            Contents.Jewellery => JewelleryClasses.Contains(t.Class),
            Contents.Map => t.Class is "Map" or "MapKey",
            _ => true,
        };

        ItemDrops.Drop? Scarab(string family)
        {
            string prefix = "Metadata/Items/Scarabs/Scarab" + family;
            // The current scarabs, not the retired Rusted / Polished / Gilded / Winged tiers.
            var pool = catalog.All.Where(t => t.Supported && t.DropLevel <= level && t.Id.StartsWith(prefix, StringComparison.Ordinal)
                && t.Name.Split(' ')[0] is not ("Rusted" or "Polished" or "Gilded" or "Winged")).ToArray();
            if (pool.Length == 0) return drops.Scarab(rng, level);
            var type = pool[rng.Next(pool.Length)];
            return new(ItemCatalog.Create(type) with { StackSize = 1, ItemLevel = (byte)level }, type);
        }

        ItemCatalog.BaseType[] Bases(Func<ItemCatalog.BaseType, bool> kind) =>
            [.. catalog.All.Where(t => ItemDrops.Droppable(t) && t.DropLevel <= level && ItemDrops.EquipmentClasses.Contains(t.Class) && kind(t))];
    }

    // Our generic container map uses the open map's tier, or the area level outside a map run.
    internal static ItemCatalog.BaseType? MapBase(ClientState conn, ItemCatalog catalog, int level)
    {
        int tier = MapRunOf(conn)?.Area.Tier is int open and > 0 ? open : Math.Clamp(level - 67, 1, 16);
        return MapDevices.Value.Atlas?.KeyOfTier(tier) is uint hash && catalog.ByHash(hash) is { Supported: true } type
            ? type : null;
    }
}
