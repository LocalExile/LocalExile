using GameServer.Core.Protocol.Instance;

/// <summary>
/// Retail Path of Exile 3.29 Vaal Orb (<c>Metadata/Items/Currency/CurrencyCorrupt</c>) corruption rules.
/// Lives in the Core Game module so policy stays out of the Gateway framework (<c>ItemRolls</c>), while
/// <c>CurrencyMoreCorrupt</c> and <c>CurrencyIncursionCorrupt1</c> keep their separate paths through
/// <see cref="CurrencyEffects.TryAction"/>.
/// </summary>
public static class VaalCorruption
{
    const string CrimsonJewel = "Metadata/Items/Jewels/JewelStr";
    const string ViridianJewel = "Metadata/Items/Jewels/JewelDex";
    const string CobaltJewel = "Metadata/Items/Jewels/JewelInt";
    const string MapKeyPrefix = "Metadata/Items/Maps/MapKeyTier";
    const string MapKeyVaalTemple = "Metadata/Items/Maps/MapKeyVaalTemple";
    const string MapWorldsPrefix = "Metadata/Items/Maps/MapWorlds";
    const string MapWorldsVaalTemple = "Metadata/Items/Maps/MapWorldsVaalTemple";

    const int JewelDomain = 10;
    const int AbyssJewelDomain = 13;
    const int MaximumCorruptedFlaskQuality = 40;

    // Boss/Simulacrum/recipe unique jewels that carry Corrupted=true in UniqueCatalog but cannot be created by a Vaal Orb.
    static readonly HashSet<string> ExcludedCorruptedUniqueJewels = new(StringComparer.OrdinalIgnoreCase)
    {
        "Forbidden Flame",
        "Forbidden Flesh",
        "Split Personality",
        "The Adorned",
    };

    /// <summary>
    /// Applies a Vaal Orb to <paramref name="item"/> using <paramref name="random"/> (deterministic under Hinekora's Lock
    /// foresight). Returns <c>null</c> when the target is refused before spending the orb.
    /// </summary>
    public static Item? Apply(ItemCatalog catalog, ItemRolls rolls, UniqueCatalog uniques,
        MapDevice? maps, Func<string, double>? uniqueWeight, Item item, Random random)
    {
        if (item.Corrupted || item.Mirrored || item.Vestigial) return null;
        if (catalog.ByHash(item.TypeHash) is not { Supported: true } type) return null;

        if (item.Components.Contains("SkillGem"))
            return CorruptGem(catalog, type, item, random);

        // Non-gem targets require Mods, modifiable base, and must be identified (retail refuses unidentified targets).
        if (!item.Components.Contains("Mods") || !type.Components.Contains("Mods") || type.Unmodifiable || !item.Identified)
            return null;

        if (item.Components.Contains("Tincture") || type.Class == "Tincture")
            return null;

        if (item.Components.Contains("Flask"))
            return type.CanBeCorrupted ? CorruptFlask(item, random) : null;

        if (CurrencyEffects.IsMap(item, type))
            return CorruptMap(catalog, rolls, maps, type, item, random);

        if (type.Class is "Jewel" or "AbyssJewel")
            return type.CanBeCorrupted ? CorruptJewel(rolls, uniques, uniqueWeight, type, item, random) : null;

        if ((type.CanBeMirrored || type.Class == "FishingRod") && type.CanBeCorrupted)
            return CorruptEquipment(catalog, rolls, type, item, random);

        return null;
    }

    // Gems: 4 equal outcomes — no change, level +/-1 (above cap is experience -1), quality +/-(1..10) clamped 0..23,
    // or Vaal variant conversion preserving transfigured skill, level, quality and flags.
    static Item? CorruptGem(ItemCatalog catalog, ItemCatalog.BaseType type, Item item, Random random)
    {
        if (catalog.Gem(type) is not { MaxLevel: > 0, Variants.Count: > 0 } gem) return null;

        var corrupted = item with { Corrupted = true };
        int level = gem.Level(item.GemExperience);
        switch (random.Next(4))
        {
            case 1:
                int next = Math.Max(1, level + (random.Next(2) == 0 ? -1 : 1));
                return next == level ? corrupted
                    : corrupted with { GemExperience = next > gem.MaxLevel ? -1 : gem.ExperienceFor(next) };
            case 2:
                int quality = random.Next(1, 11) * (random.Next(2) == 0 ? -1 : 1);
                return corrupted with { Quality = (byte)Math.Clamp(item.Quality + quality, 0, 23) };
            case 3:
                if (gem.VaalBaseRow is int row && catalog.Row(row) is { Supported: true } vaalType
                    && catalog.Gem(vaalType) is { MaxLevel: > 0 } vaalGem
                    && vaalGem.Variants.FirstOrDefault(v => v.Creatable && v.Effect2 == gem.Variant(item.GemVariant).Effect) is { } variant)
                {
                    return corrupted with
                    {
                        TypeHash = vaalType.Hash,
                        Components = vaalType.Components,
                        VisualIdentity = vaalType.VisualIdentity,
                        GemVariant = variant.Index,
                        GemExperience = vaalGem.Level(item.GemExperience) == level ? item.GemExperience
                            : vaalGem.ExperienceFor(level),
                    };
                }
                break;
        }
        return corrupted;
    }

    // Flasks (3.24+): corrupts the flask and shifts quality by -10% to +10% (clamped 0..40%, so 20% can reach 30%).
    static Item CorruptFlask(Item item, Random random)
    {
        if (!item.Components.Contains("Quality")) return item with { Corrupted = true };
        int delta = random.Next(-10, 11);
        return item with
        {
            Corrupted = true,
            Quality = (byte)Math.Clamp(item.Quality + delta, 0, MaximumCorruptedFlaskQuality),
        };
    }

    // Equipment (Weapons, Armour, Shields, Quivers, Rings, Amulets, Belts, Fishing Rods — Normal/Magic/Rare/Unique):
    // 4 equal outcomes:
    //   0: no change
    //   1: 3.29 socket colour corruption (turn one random white socket non-white; fizzles to no-change when none)
    //   2: GenerationType=5 corrupted implicit (replaces one implicit when Eldritch implicits are present, else replaces implicits)
    //   3: brick to 6-affix corrupted rare (preserving fractured mods, restoring base art & stripping unique hidden mods,
    //      and rerolling sockets/links with 1/36 max-socket full-link chance when no gems are socketed)
    static Item CorruptEquipment(ItemCatalog catalog, ItemRolls rolls, ItemCatalog.BaseType type, Item item, Random random)
    {
        switch (random.Next(4))
        {
            case 0:
                return item with { Corrupted = true };

            case 1:
                return CorruptEquipmentSocketColour(catalog, type, item, random);

            case 2:
                return CorruptEquipmentImplicit(catalog, rolls, type, item, random);

            default:
                return BrickEquipmentToRare(catalog, rolls, type, item, random);
        }
    }

    static Item CorruptEquipmentSocketColour(ItemCatalog catalog, ItemCatalog.BaseType type, Item item, Random random)
    {
        var whites = new List<int>();
        for (int i = 0; i < item.Sockets.Count; i++)
            if (item.Sockets[i].Colour == SocketColour.White)
                whites.Add(i);

        if (whites.Count == 0) return item with { Corrupted = true };

        var sockets = item.Sockets.ToArray();
        int at = whites[random.Next(whites.Count)];
        sockets[at] = sockets[at] with { Colour = SocketColours.Colour(catalog.Requirements(type), random) };
        return item with { Sockets = sockets, Corrupted = true };
    }

    static Item CorruptEquipmentImplicit(ItemCatalog catalog, ItemRolls rolls, ItemCatalog.BaseType type, Item item, Random random)
    {
        var pool = rolls.CorruptedImplicitPool(type, item.ItemLevel);
        if (pool.Count == 0) return item with { Corrupted = true };

        var chosen = PickWeighted(pool, random).Roll(ModSection.Implicit, random);
        if (rolls.HasEldritchImplicit(item))
        {
            // Retail: corrupting an Eldritch-implicit item replaces only one randomly chosen existing implicit.
            var implicitIndices = new List<int>();
            for (int i = 0; i < item.Mods.Count; i++)
                if (item.Mods[i].Section == ModSection.Implicit)
                    implicitIndices.Add(i);

            var mods = item.Mods.ToList();
            if (implicitIndices.Count > 0)
                mods[implicitIndices[random.Next(implicitIndices.Count)]] = chosen;
            else
                mods.Insert(0, chosen);
            return item with { Mods = mods, Corrupted = true };
        }

        var replaced = ReplaceImplicits(item, chosen) with { Corrupted = true };
        // Replacing a socket-granting base implicit (Stygian Vise / Unset Ring) removes empty implicit sockets.
        if (replaced.Sockets.Count > 0 && !replaced.Sockets.Any(s => s.Gem is not null)
            && (type.Id == AbyssJewels.StygianVise || type.HasOneSocket))
        {
            var local = ItemModStats.Local(replaced, catalog.Mods);
            if (!local.ContainsKey(Stat.LocalHasXAbyssSockets) && !local.ContainsKey(Stat.LocalHasXSockets))
                replaced = replaced with { Sockets = [], Links = null };
        }
        return replaced;
    }

    static Item BrickEquipmentToRare(ItemCatalog catalog, ItemRolls rolls, ItemCatalog.BaseType type, Item item, Random random)
    {
        var baseItem = PrepareForRareBrick(type, item);
        if (rolls.Pool(type, baseItem).Count == 0) return item with { Corrupted = true };

        var bricked = rolls.Roll(baseItem, ItemRarity.Rare, random, respectMetamods: false);
        var max = rolls.RareMaxAffixes(bricked);
        int targetAffixes = max.Prefix + max.Suffix;
        while (bricked.Mods.Count(m => m.Section == ModSection.Explicit) < targetAffixes
            && rolls.AddAffix(bricked, max, random, respectMetamods: false) is { } added)
        {
            bricked = added;
        }

        // Reroll or reconcile sockets & links when no gems/jewels are socketed (never delete player gems).
        if (!bricked.Sockets.Any(s => s.Gem is not null))
        {
            int capacity = CurrencyEffects.SocketCapacity(bricked, type);
            if (capacity >= 2 && !type.HasOneSocket && type.Id != AbyssJewels.StygianVise)
            {
                int maxSockets = Math.Min(CurrencyEffects.MaximumSockets(bricked, type), SocketLayout.MaximumSockets);
                if (maxSockets >= 2 && random.Next(36) == 0)
                {
                    var requirements = catalog.Requirements(type);
                    var sockets = new ItemSocket[maxSockets];
                    for (int i = 0; i < maxSockets; i++)
                        sockets[i] = new(SocketColours.Roll(requirements, bricked.ItemLevel, bricked.Quality, random));
                    bricked = bricked with { Sockets = sockets, Links = [(byte)maxSockets] };
                }
                else
                {
                    var (sockets, links) = catalog.DropSockets(type, bricked.ItemLevel, random);
                    bricked = bricked with { Sockets = sockets, Links = links };
                }
            }
            else
            {
                // Reconcile single-socket, Stygian Vise, or socketless bases from surviving local mods so bricking
                // uniques with extra/abyssal/custom sockets (e.g. Darkness Enthroned, Voidfletcher, Uul-Netol's Vow)
                // strips sockets whose granting unique mods were removed.
                var local = ItemModStats.Local(bricked, catalog.Mods);
                if (type.Id == AbyssJewels.StygianVise)
                {
                    bricked = AbyssJewels.StygianSockets(bricked, local);
                }
                else
                {
                    int abyssSockets = Math.Clamp(local.GetValueOrDefault(Stat.LocalHasXAbyssSockets), 0, SocketLayout.MaximumSockets);
                    int fixedSockets = Math.Clamp(local.GetValueOrDefault(Stat.LocalHasXSockets), 0, SocketLayout.MaximumSockets);
                    if (abyssSockets > 0)
                        bricked = AbyssJewels.EquipmentSockets(bricked, type, catalog, local, random);
                    else if (fixedSockets > 0 && type.Components.Contains("Sockets"))
                    {
                        var requirements = catalog.Requirements(type);
                        var fixedList = new ItemSocket[fixedSockets];
                        for (int i = 0; i < fixedSockets; i++)
                            fixedList[i] = new(SocketColours.Roll(requirements, bricked.ItemLevel, bricked.Quality, random));
                        bricked = bricked with { Sockets = fixedList, Links = ItemCatalog.RollSocketLinks(fixedSockets, random) };
                    }
                    else
                        bricked = bricked with { Sockets = [], Links = null };
                }
            }
        }

        return bricked with { Corrupted = true, Identified = true };
    }

    // Maps & MapKeys (Normal/Magic/Rare/Unique):
    // Non-unique 4 equal outcomes:
    //   0: no change
    //   1: unidentified (preserving existing mods; Normal maps stay identified)
    //   2: 8-mod corrupted rare (4 prefixes + 4 suffixes)
    //   3: reroll rare (4-6 affixes) + tier upgrade (+1 tier for T1-15, T16 -> Vaal Temple Map)
    // Unique maps:
    //   0/3: no change, 1: unidentified (keeping unique mods), 2: brick to 4-6 mod rare of the same base.
    static Item CorruptMap(ItemCatalog catalog, ItemRolls rolls, MapDevice? maps, ItemCatalog.BaseType type, Item item, Random random)
    {
        if (item.Rarity == ItemRarity.Unique)
        {
            switch (random.Next(4))
            {
                case 1:
                    return item with { Corrupted = true, Identified = false };
                case 2:
                {
                    var baseItem = PrepareForRareBrick(type, item);
                    if (rolls.Pool(type, baseItem).Count == 0) return item with { Corrupted = true };
                    return rolls.Roll(baseItem, ItemRarity.Rare, random, respectMetamods: false) with
                    {
                        Corrupted = true,
                        Identified = true,
                    };
                }
                default:
                    return item with { Corrupted = true };
            }
        }

        switch (random.Next(4))
        {
            case 0:
                return item with { Corrupted = true };

            case 1:
                return item with
                {
                    Corrupted = true,
                    Identified = item.Rarity == ItemRarity.Normal,
                };

            case 2:
            {
                if (rolls.Pool(type, item).Count == 0) return item with { Corrupted = true };
                return rolls.Roll(item, ItemRarity.Rare, random, shape: (8, 4), respectMetamods: false) with
                {
                    Corrupted = true,
                    Identified = true,
                };
            }

            default:
                return CorruptMapRerollAndUpgradeTier(catalog, rolls, maps, type, item, random);
        }
    }

    static Item CorruptMapRerollAndUpgradeTier(ItemCatalog catalog, ItemRolls rolls, MapDevice? maps,
        ItemCatalog.BaseType type, Item item, Random random)
    {
        var nextType = UpgradeMapTierBase(catalog, maps, type, random);
        var effectiveType = nextType ?? type;
        var target = item;
        if (nextType is not null && nextType.Hash != type.Hash)
        {
            target = target with
            {
                TypeHash = nextType.Hash,
                Components = nextType.Components,
                VisualIdentity = nextType.VisualIdentity,
            };
            if (nextType.Implicits is { Count: > 0 } nextImplicits)
            {
                var nonImplicits = target.Mods.Where(m => m.Section != ModSection.Implicit).ToList();
                var rolledImplicits = nextImplicits.Select(m => m.Roll(ModSection.Implicit, random));
                target = target with { Mods = [.. rolledImplicits, .. nonImplicits] };
            }
        }

        if (rolls.Pool(effectiveType, target).Count == 0)
            return target with { Corrupted = true, Identified = true };

        return rolls.Roll(target, ItemRarity.Rare, random, respectMetamods: false) with
        {
            Corrupted = true,
            Identified = true,
        };
    }

    static ItemCatalog.BaseType? UpgradeMapTierBase(ItemCatalog catalog, MapDevice? maps, ItemCatalog.BaseType type, Random random)
    {
        var atlas = maps?.Atlas;
        if (atlas?.TierOf(type.Hash) is int keyTier)
        {
            if (atlas.IsSpecialKey(type.Hash)) return null;
            if (keyTier is >= 1 and <= 15)
            {
                if (catalog.Find($"{MapKeyPrefix}{keyTier + 1}") is { Supported: true } nextKey) return nextKey;
                if (atlas.KeyOfTier(keyTier + 1) is uint nextHash && catalog.ByHash(nextHash) is { Supported: true } byHash)
                    return byHash;
                return null;
            }
            if (keyTier == 16)
                return catalog.Find(MapKeyVaalTemple) is { Supported: true } vaalTemple ? vaalTemple : null;
            return null;
        }

        if (maps?.AreaOf(type.Hash) is { } area && type.Id.StartsWith(MapWorldsPrefix, StringComparison.Ordinal))
        {
            if (area.Tier is >= 1 and <= 15)
            {
                int nextTier = area.Tier + 1;
                var candidates = catalog.All.Where(b => b.Supported
                    && b.Id.StartsWith(MapWorldsPrefix, StringComparison.Ordinal)
                    && maps.AreaOf(b.Hash)?.Tier == nextTier).ToList();
                if (candidates.Count > 0) return candidates[random.Next(candidates.Count)];
                return catalog.Find($"{MapKeyPrefix}{nextTier}") is { Supported: true } fallbackKey ? fallbackKey : null;
            }
            if (area.Tier == 16 && !type.Id.Equals(MapWorldsVaalTemple, StringComparison.OrdinalIgnoreCase))
                return catalog.Find(MapWorldsVaalTemple) is { Supported: true } legacyTemple ? legacyTemple : null;
        }

        return null;
    }

    // Jewels (Standard Crimson/Viridian/Cobalt, Abyss, Cluster, Prismatic, Timeless, Unique):
    // 4 equal outcomes:
    //   0: no change
    //   1: GenerationType=5 corrupted implicit (Domain 10 for Jewel incl. Cluster, Domain 13 for AbyssJewel; preserves Cluster enchants)
    //   2: brick to corrupted rare (3-4 affixes, preserves fractured mods & Cluster enchants)
    //   3: transform to a corruption-exclusive unique jewel matching base (Crimson/Viridian/Cobalt only; Abyss/Cluster/etc. -> no change)
    static Item CorruptJewel(ItemRolls rolls, UniqueCatalog uniques, Func<string, double>? uniqueWeight,
        ItemCatalog.BaseType type, Item item, Random random)
    {
        switch (random.Next(4))
        {
            case 0:
                return item with { Corrupted = true };

            case 1:
            {
                int domain = type.Class == "AbyssJewel" ? AbyssJewelDomain : JewelDomain;
                var pool = rolls.CorruptedImplicitPool(type, item.ItemLevel, domainOverride: domain);
                if (pool.Count == 0) return item with { Corrupted = true };
                var chosen = PickWeighted(pool, random).Roll(ModSection.Implicit, random);
                return ReplaceImplicits(item, chosen) with { Corrupted = true };
            }

            case 2:
            {
                var baseItem = PrepareForRareBrick(type, item);
                // Bricking a unique Cluster Jewel (e.g. Voices) back to rare needs base Cluster passive/theme enchants.
                if (type.Cluster is { } cluster && !baseItem.Mods.Any(m => m.Section == ModSection.Enchant))
                    baseItem = baseItem with { Mods = [.. cluster.Roll(random), .. baseItem.Mods] };
                if (rolls.Pool(type, baseItem).Count == 0) return item with { Corrupted = true };
                return rolls.Roll(baseItem, ItemRarity.Rare, random, respectMetamods: false) with
                {
                    Corrupted = true,
                    Identified = true,
                };
            }

            default:
                return TransformToCorruptedUniqueJewel(uniques, uniqueWeight, type, item, random);
        }
    }

    internal static IReadOnlyList<UniqueCatalog.Definition> CorruptedUniqueJewelPool(UniqueCatalog uniques, string baseTypeId)
    {
        if (baseTypeId is not (CrimsonJewel or ViridianJewel or CobaltJewel))
            return [];

        var result = new List<UniqueCatalog.Definition>();
        foreach (var def in uniques.All)
        {
            if (!def.Corrupted || def.Mirrored || def.EncounterOnly
                || !def.BaseTypeId.Equals(baseTypeId, StringComparison.OrdinalIgnoreCase))
                continue;
            if (def.Name.StartsWith("Replica ", StringComparison.Ordinal) || ExcludedCorruptedUniqueJewels.Contains(def.Name))
                continue;
            result.Add(def);
        }
        return result;
    }

    static Item TransformToCorruptedUniqueJewel(UniqueCatalog uniques, Func<string, double>? uniqueWeight,
        ItemCatalog.BaseType type, Item item, Random random)
    {
        var eligible = CorruptedUniqueJewelPool(uniques, type.Id);
        if (eligible.Count == 0) return item with { Corrupted = true };

        var candidates = new List<(UniqueCatalog.Definition Definition, double Weight)>(eligible.Count);
        foreach (var def in eligible)
        {
            double w = uniqueWeight?.Invoke(def.Name) ?? 1.0;
            candidates.Add((def, w > 0 ? w : 1.0));
        }

        double pick = random.NextDouble() * candidates.Sum(c => c.Weight);
        var chosen = candidates[^1].Definition;
        foreach (var (def, weight) in candidates)
        {
            if (pick < weight) { chosen = def; break; }
            pick -= weight;
        }

        return uniques.Create(chosen, random) is { } created
            ? created with { ItemLevel = item.ItemLevel, Corrupted = true, Identified = true }
            : item with { Corrupted = true };
    }

    static Item PrepareForRareBrick(ItemCatalog.BaseType type, Item item)
    {
        var mods = item.Rarity == ItemRarity.Unique
            ? item.Mods.Where(m => m.Section != ModSection.Hidden).ToArray()
            : item.Mods;
        return item with
        {
            VisualIdentity = type.VisualIdentity,
            Mods = mods,
        };
    }

    static Item ReplaceImplicits(Item item, ItemMod implicitMod)
    {
        var kept = item.Mods.Where(m => m.Section != ModSection.Implicit).ToList();
        int insertAt = kept.FindLastIndex(m => m.Section == ModSection.Enchant) + 1;
        kept.Insert(insertAt, implicitMod);
        return item with { Mods = kept };
    }

    static ModCatalog.Mod PickWeighted(IReadOnlyList<(ModCatalog.Mod Mod, int Weight)> pool, Random random)
    {
        long pick = random.NextInt64(pool.Sum(p => (long)p.Weight));
        foreach (var (mod, weight) in pool)
        {
            if (pick < weight) return mod;
            pick -= weight;
        }
        return pool[^1].Mod;
    }
}
