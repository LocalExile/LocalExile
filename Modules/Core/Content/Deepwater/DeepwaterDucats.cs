using System.Runtime.CompilerServices;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Modules.Deepwater;

/// <summary>The 11 Allflame crafting Ducats (used exclusively inside Vesper's Allflame Crafting window, never by direct
/// right-click). All mod pools are resolved once lazily per <see cref="ModCatalog"/> so crafting performs zero per-craft
/// table scans.</summary>
public static class DeepwaterDucats
{
    public const string Merrick = "Metadata/Items/Deepwater/DeepwaterAbyssCurrency";           // #5418
    public const string Cyaxan = "Metadata/Items/Deepwater/DeepwaterMercenaryCurrency";        // #5419
    public const string UnusedDucat = "Metadata/Items/Deepwater/DeepwaterAtlasEncounterCurrency"; // #5420 [DNT] UNUSED
    public const string Changeling = "Metadata/Items/Deepwater/DeepwaterBlankCurrency";        // #5438
    public const string Genteel = "Metadata/Items/Deepwater/DeepwaterAttributeSwap";           // #5439
    public const string Kishara = "Metadata/Items/Deepwater/DeepwaterItemSplit";               // #5440
    public const string Telesia = "Metadata/Items/Deepwater/DeepwaterChartReroll";             // #5441
    public const string Rotmother = "Metadata/Items/Deepwater/DeepwaterSaboteur";              // #5442
    public const string Brinehook = "Metadata/Items/Deepwater/DeepwaterPantheon";              // #5443
    public const string Katakohi = "Metadata/Items/Deepwater/DeepwaterTatttoo";                // #5444 (GGG triple-t)
    public const string Tzamoto = "Metadata/Items/Deepwater/DeepwaterGhostbuster";             // #5445
    public const string Ukatoa = "Metadata/Items/Deepwater/DeepwaterEldritch";                 // #5446

    public static readonly uint MerrickHash = InstanceEntities.ObjectTypeHash(Merrick);
    public static readonly uint CyaxanHash = InstanceEntities.ObjectTypeHash(Cyaxan);
    public static readonly uint UnusedDucatHash = InstanceEntities.ObjectTypeHash(UnusedDucat);
    public static readonly uint ChangelingHash = InstanceEntities.ObjectTypeHash(Changeling);
    public static readonly uint GenteelHash = InstanceEntities.ObjectTypeHash(Genteel);
    public static readonly uint KisharaHash = InstanceEntities.ObjectTypeHash(Kishara);
    public static readonly uint TelesiaHash = InstanceEntities.ObjectTypeHash(Telesia);
    public static readonly uint RotmotherHash = InstanceEntities.ObjectTypeHash(Rotmother);
    public static readonly uint BrinehookHash = InstanceEntities.ObjectTypeHash(Brinehook);
    public static readonly uint KatakohiHash = InstanceEntities.ObjectTypeHash(Katakohi);
    public static readonly uint TzamotoHash = InstanceEntities.ObjectTypeHash(Tzamoto);
    public static readonly uint UkatoaHash = InstanceEntities.ObjectTypeHash(Ukatoa);

    /// <summary>All 11 shipped crafting Ducats (for chests and random Ducat drops).</summary>
    public static readonly string[] AllDucats =
        [Merrick, Cyaxan, Changeling, Genteel, Kishara, Telesia, Rotmother, Brinehook, Katakohi, Tzamoto, Ukatoa];

    /// <summary>The 10 functional Ducats Changeling's Ducat can turn into (excluding Changeling itself and [DNT] #5420).</summary>
    public static readonly string[] FunctionalDucats =
        [Merrick, Cyaxan, Genteel, Kishara, Telesia, Rotmother, Brinehook, Katakohi, Tzamoto, Ukatoa];

    static readonly HashSet<uint> DucatHashes =
    [
        MerrickHash, CyaxanHash, ChangelingHash, GenteelHash, KisharaHash,
        TelesiaHash, RotmotherHash, BrinehookHash, KatakohiHash, TzamotoHash, UkatoaHash,
    ];

    public static bool IsDucat(uint typeHash) => DucatHashes.Contains(typeHash);

    const int AspectFamily = 1047;
    const int HazardBeltFamily = 7814;
    const int InfamousDomain = 41;
    const int DeepwaterModDomain = 42;
    const string ImmutableImplicitStat = "local_implicit_mod_cannot_be_changed";

    static readonly HashSet<string> AmuletTagOnly = ["amulet"];

    // All 25 single-attribute (Str, Dex, Int) mod triplets in 3.29.3 Mods.dat, matched 1-to-1 by tier and group.
    static readonly (string Str, string Dex, string Int)[] GenteelTriplets =
    [
        // Flat Equipment (Domain 1, gen 2)
        ("Strength1", "Dexterity1", "Intelligence1"),
        ("Strength2", "Dexterity2", "Intelligence2"),
        ("Strength3", "Dexterity3", "Intelligence3"),
        ("Strength4", "Dexterity4", "Intelligence4"),
        ("Strength5", "Dexterity5", "Intelligence5"),
        ("Strength6", "Dexterity6", "Intelligence6"),
        ("Strength7", "Dexterity7", "Intelligence7"),
        ("Strength8", "Dexterity8", "Intelligence8"),
        ("Strength9", "Dexterity9", "Intelligence9"),
        ("Strength10", "Dexterity10", "Intelligence10"),
        // Flat Abyss Jewel (Domain 13, gen 2)
        ("AbyssStrengthJewel1_", "AbyssDexterityJewel1_", "AbyssIntelligenceJewel1"),
        // % Attribute Conqueror Body/Amulet (Domain 1, gen 2)
        ("PercentageStrengthBodyInfluence1", "PercentageDexterityBodyInfluence1", "PercentageIntelligenceBodyInfluence1"),
        ("PercentageStrengthBodyInfluence2_", "PercentageDexterityBodyInfluence2", "PercentageIntelligenceBodyInfluence2___"),
        // % Attribute Hunter (Domain 1, gen 2)
        ("PercentageStrengthInfluence1", "PercentageDexterityInfluence1_", "PercentageIntelligenceInfluence1_"),
        ("PercentageStrengthInfluence2_", "PercentageDexterityInfluence2", "PercentageIntelligenceInfluence2"),
        // % Attribute Elder/Shaper (Domain 1, gen 2)
        ("PercentageStrengthUber1_", "PercentageDexterityUber1", "PercentageIntelligenceUber1"),
        ("PercentageStrengthUber2__", "PercentageDexterityUber2", "PercentageIntelligenceUber2"),
        // Cluster Jewel Large/Medium (Domain 21, gen 2)
        ("AfflictionJewelSmallPassivesGrantStr", "AfflictionJewelSmallPassivesGrantDex_", "AfflictionJewelSmallPassivesGrantInt_"),
        ("AfflictionJewelSmallPassivesGrantStr2_", "AfflictionJewelSmallPassivesGrantDex2", "AfflictionJewelSmallPassivesGrantInt2_"),
        ("AfflictionJewelSmallPassivesGrantStr3_", "AfflictionJewelSmallPassivesGrantDex3_", "AfflictionJewelSmallPassivesGrantInt3"),
        // Cluster Jewel Small (Domain 21, gen 2)
        ("AfflictionJewelSmallPassivesGrantStrSmall_", "AfflictionJewelSmallPassivesGrantDexSmall_", "AfflictionJewelSmallPassivesGrantIntSmall_"),
        ("AfflictionJewelSmallPassivesGrantStrSmall2", "AfflictionJewelSmallPassivesGrantDexSmall2", "AfflictionJewelSmallPassivesGrantIntSmall2"),
        ("AfflictionJewelSmallPassivesGrantStrSmall3", "AfflictionJewelSmallPassivesGrantDexSmall3", "AfflictionJewelSmallPassivesGrantIntSmall3______"),
        ("AfflictionJewelSmallPassivesGrantStrSmall4_", "AfflictionJewelSmallPassivesGrantDexSmall4_", "AfflictionJewelSmallPassivesGrantIntSmall4_"),
        ("AfflictionJewelSmallPassivesGrantStrSmall5", "AfflictionJewelSmallPassivesGrantDexSmall5", "AfflictionJewelSmallPassivesGrantIntSmall5"),
    ];

    sealed class Pools
    {
        public required ModCatalog.Mod[] Brinehook { get; init; }
        public required ModCatalog.Mod[] Cyaxan { get; init; }
        public required ModCatalog.Mod[] KatakohiStr { get; init; }
        public required ModCatalog.Mod[] KatakohiDex { get; init; }
        public required ModCatalog.Mod[] KatakohiInt { get; init; }
        public required (ModCatalog.Mod Mod, int Weight)[] Rotmother { get; init; }
        public required Dictionary<ushort, (ModCatalog.Mod A, ModCatalog.Mod B)> Genteel { get; init; }
        public required (ModCatalog.Mod Mod, int Weight)[] Ukatoa { get; init; }
    }

    static readonly ConditionalWeakTable<ModCatalog, Pools> PoolCache = new();

    static Pools PoolsOf(ModCatalog mods) => PoolCache.GetValue(mods, BuildPools);

    static Pools BuildPools(ModCatalog mods)
    {
        var brinehook = new List<ModCatalog.Mod>();
        var cyaxan = new List<ModCatalog.Mod>();
        var katStr = new List<ModCatalog.Mod>();
        var katDex = new List<ModCatalog.Mod>();
        var katInt = new List<ModCatalog.Mod>();
        var rotmother = new List<(ModCatalog.Mod Mod, int Weight)>();
        var ukatoa = new List<(ModCatalog.Mod Mod, int Weight)>();

        foreach (var m in mods.All)
        {
            if (m.Domain == InfamousDomain && m.GenerationType is ItemRolls.Prefix or ItemRolls.Suffix)
            {
                cyaxan.Add(m);
            }
            else if (m.Domain == DeepwaterModDomain)
            {
                if (m.GenerationType == ItemRolls.Suffix && m.Families.Contains(AspectFamily))
                    brinehook.Add(m);
                else if (m.GenerationType is ItemRolls.Prefix or ItemRolls.Suffix && m.Families.Contains(HazardBeltFamily))
                {
                    int w = m.SpawnWeights.FirstOrDefault(s => s.Tag == "deepwater_hazard_belt").Weight;
                    if (w > 0) rotmother.Add((m, w));
                }
                else if (m.GenerationType == 3)
                {
                    if (m.SpawnWeights.Any(s => s.Tag == "deepwater_str_tatt" && s.Weight > 0)) katStr.Add(m);
                    else if (m.SpawnWeights.Any(s => s.Tag == "deepwater_dex_tatt" && s.Weight > 0)) katDex.Add(m);
                    else if (m.SpawnWeights.Any(s => s.Tag == "deepwater_int_tatt" && s.Weight > 0)) katInt.Add(m);
                }
            }
            else if (m.Domain == 1 && m.GenerationType is ItemRolls.ExarchImplicit or ItemRolls.EaterImplicit && IsEldritchTier1To4(m.Id))
            {
                int w = m.WeightFor(AmuletTagOnly);
                if (w > 0) ukatoa.Add((m, w));
            }
        }

        var genteel = new Dictionary<ushort, (ModCatalog.Mod A, ModCatalog.Mod B)>();
        foreach (var (sId, dId, iId) in GenteelTriplets)
        {
            if (mods.ById(sId) is not { } s || mods.ById(dId) is not { } d || mods.ById(iId) is not { } i) continue;
            genteel[s.Hash] = (d, i);
            genteel[d.Hash] = (s, i);
            genteel[i.Hash] = (s, d);
        }

        return new Pools
        {
            Brinehook = [.. brinehook],
            Cyaxan = [.. cyaxan],
            KatakohiStr = [.. katStr],
            KatakohiDex = [.. katDex],
            KatakohiInt = [.. katInt],
            Rotmother = [.. rotmother],
            Genteel = genteel,
            Ukatoa = [.. ukatoa],
        };
    }

    static bool IsEldritchTier1To4(string id)
    {
        for (int tier = 1; tier <= 4; tier++)
            if (id.EndsWith($"EldritchImplicit{tier}", StringComparison.Ordinal)
                || id.EndsWith($"UniquePresence{tier}", StringComparison.Ordinal)
                || id.EndsWith($"PinnaclePresence{tier}", StringComparison.Ordinal))
                return true;
        return false;
    }

    /// <summary>Applies one of the 10 item-producing Ducats (all except Tzamoto's Ducat, which uses
    /// <see cref="ApplyTzamoto"/> because it can destroy the target with 0 copies). Returns null when the target is
    /// refused before spending anything.</summary>
    public static Item? Apply(ItemCatalog catalog, ItemRolls rolls, uint ducatHash, Item? target, ItemCatalog.BaseType? targetType, Random random)
    {
        if (ducatHash == ChangelingHash) return ApplyChangeling(catalog, random);
        if (target is null || targetType is null) return null;
        if (target.Corrupted || target.Mirrored || target.Vestigial || !target.Components.Contains("Mods") || targetType.Unmodifiable)
            return null;

        if (ducatHash == BrinehookHash) return ApplyBrinehook(catalog, rolls, target, targetType, random);
        if (ducatHash == CyaxanHash) return ApplyCyaxan(catalog, rolls, target, targetType, random);
        if (ducatHash == KatakohiHash) return ApplyKatakohi(catalog, target, targetType, random);
        if (ducatHash == KisharaHash) return ApplyKishara(rolls, target, targetType, random);
        if (ducatHash == MerrickHash) return ApplyMerrick(rolls, target, targetType, random);
        if (ducatHash == RotmotherHash) return ApplyRotmother(catalog, rolls, target, targetType, random);
        if (ducatHash == TelesiaHash) return ApplyTelesia(catalog, target, targetType, random);
        if (ducatHash == GenteelHash) return ApplyGenteel(catalog, target, random);
        if (ducatHash == UkatoaHash) return ApplyUkatoa(catalog, rolls, target, targetType, random);
        return null;
    }

    /// <summary>Tzamoto's Ducat (#5445): requires Intangibility &gt; 0. 50% clears Intangibility (returns 1 copy),
    /// 50% destroys the item (returns null with <paramref name="destroyed"/> = true).</summary>
    public static Item? ApplyTzamoto(Item target, Random random, out bool destroyed)
    {
        destroyed = false;
        if (target.Corrupted || target.Mirrored || target.Vestigial || target.Intangibility == 0) return null;
        if (random.Next(2) == 0)
            return ItemRolls.ClearForesee(target) with { Intangibility = 0 };
        destroyed = true;
        return null;
    }

    // 1. The Changeling's Ducat (#5438): no target item; yields 1 uniformly random functional Ducat (G: uniform weights).
    static Item? ApplyChangeling(ItemCatalog catalog, Random random)
    {
        string id = FunctionalDucats[random.Next(FunctionalDucats.Length)];
        return catalog.Find(id) is { Supported: true } type
            ? ItemCatalog.Create(type, random: random) with { StackSize = 1 } : null;
    }

    // 2. Brinehook's Ducat (#5443): adds 1 of 4 Domain=42 Pantheon Aspect suffixes to a Magic or Rare non-flask/jewel/chart item.
    static Item? ApplyBrinehook(ItemCatalog catalog, ItemRolls rolls, Item target, ItemCatalog.BaseType targetType, Random random)
    {
        if (target.Rarity is not (ItemRarity.Magic or ItemRarity.Rare)) return null;
        if (target.Components.Contains("Flask") || targetType.Class is "Jewel" or "AbyssJewel" || DeepwaterCharts.IsChart(targetType))
            return null;
        if (targetType.ModDomain != 1) return null;
        var pools = PoolsOf(catalog.Mods);
        if (pools.Brinehook.Length == 0) return null;

        int maxSuffix = target.Rarity == ItemRarity.Magic ? 1 : rolls.RareMaxAffixes(target).Suffix;
        int suffixes = 0;
        foreach (var m in target.Mods)
        {
            if (catalog.Mods.ByHash(m.Hash) is not { } row) continue;
            if (row.Families.Contains(AspectFamily)) return null;
            if (m.Section == ModSection.Explicit && row.GenerationType == ItemRolls.Suffix) suffixes++;
        }
        if (suffixes >= maxSuffix) return null;

        var chosen = pools.Brinehook[random.Next(pools.Brinehook.Length)];
        var mods = target.Mods.ToList();
        mods.Add(chosen.Roll(ModSection.Explicit, random));
        return target with { Mods = mods, Identified = true };
    }

    // 3. Cyaxan's Ducat (#5419): reforges a Rare item with 1 guaranteed Domain=41 Infamous mod matching the base's slot tag.
    static Item? ApplyCyaxan(ItemCatalog catalog, ItemRolls rolls, Item target, ItemCatalog.BaseType targetType, Random random)
    {
        if (target.Rarity != ItemRarity.Rare || target.Components.Contains("Flask")) return null;
        var pools = PoolsOf(catalog.Mods);
        if (pools.Cyaxan.Length == 0) return null;

        var fractured = target.FracturedIndexes();
        var fixedMods = target.Mods.Where((_, i) => fractured.Contains(i))
            .Select(m => catalog.Mods.ByHash(m.Hash)).OfType<ModCatalog.Mod>().ToList();
        var (maxP, maxS) = rolls.RareMaxAffixes(target);

        var eligible = new List<(ModCatalog.Mod Mod, int Weight)>();
        foreach (var m in pools.Cyaxan)
        {
            int w = m.WeightFor(targetType.Tags);
            if (w <= 0) continue;
            if (fixedMods.Any(f => f.Hash == m.Hash || f.Families.Intersect(m.Families).Any())) continue;
            int sideCount = fixedMods.Count(f => f.GenerationType == m.GenerationType);
            if (sideCount >= (m.GenerationType == ItemRolls.Prefix ? maxP : maxS)) continue;
            eligible.Add((m, w));
        }
        if (eligible.Count == 0) return null;

        // G: guaranteed Infamous mod ignores target item level, matching Essence behaviour.
        var picked = WeightedPick(eligible, random);
        return rolls.Roll(target, ItemRarity.Rare, random, guaranteed: picked) with { Identified = true };
    }

    // 4. Katakohi's Ducat (#5444): replaces Gloves enchants with 1-3 Carved in Jade enchants (by base attribute alignment)
    //    and trims sockets to Math.Min(sockets, 4 - k) since each enchant applies local_maximum_sockets_+(-1).
    static Item? ApplyKatakohi(ItemCatalog catalog, Item target, ItemCatalog.BaseType targetType, Random random)
    {
        if (targetType.Class != "Gloves" || target.Rarity == ItemRarity.Unique) return null;
        if (target.Sockets.Any(s => s.Gem is not null)) return null;
        var pools = PoolsOf(catalog.Mods);

        var tags = targetType.Tags;
        bool wantStr = tags.Contains("str_armour") || tags.Contains("str_dex_armour") || tags.Contains("str_int_armour");
        bool wantDex = tags.Contains("dex_armour") || tags.Contains("str_dex_armour") || tags.Contains("dex_int_armour");
        bool wantInt = tags.Contains("int_armour") || tags.Contains("str_int_armour") || tags.Contains("dex_int_armour");
        if (tags.Contains("ward_armour") || (!wantStr && !wantDex && !wantInt))
            (wantStr, wantDex, wantInt) = (true, true, true);

        var candidates = new List<ModCatalog.Mod>();
        if (wantStr) candidates.AddRange(pools.KatakohiStr);
        if (wantDex) candidates.AddRange(pools.KatakohiDex);
        if (wantInt) candidates.AddRange(pools.KatakohiInt);
        if (candidates.Count == 0) return null;

        int wantCount = Math.Min(random.Next(1, 4), candidates.Count);
        var picked = new List<ModCatalog.Mod>(wantCount);
        var usedFamilies = new HashSet<int>();
        while (picked.Count < wantCount && candidates.Count > 0)
        {
            int idx = random.Next(candidates.Count);
            var mod = candidates[idx];
            candidates.RemoveAt(idx);
            if (mod.Families.Any(usedFamilies.Contains)) continue;
            picked.Add(mod);
            foreach (int fam in mod.Families) usedFamilies.Add(fam);
        }
        if (picked.Count == 0) return null;

        var mods = target.Mods.Where(m => m.Section != ModSection.Enchant).ToList();
        foreach (var enchant in picked) mods.Add(enchant.Roll(ModSection.Enchant, random));

        int maxSockets = Math.Max(0, 4 - picked.Count);
        var sockets = target.Sockets;
        var links = target.Links;
        if (sockets.Count > maxSockets)
        {
            sockets = [.. sockets.Take(maxSockets)];
            links = TrimLinks(links, maxSockets);
        }

        return target with { Mods = mods, Sockets = sockets, Links = links };
    }

    static IReadOnlyList<byte>? TrimLinks(IReadOnlyList<byte>? oldLinks, int maxSockets)
    {
        if (maxSockets <= 0) return null;
        if (oldLinks is null || oldLinks.Count == 0) return null;
        var rebuilt = new List<byte>();
        int remaining = maxSockets;
        foreach (byte group in oldLinks)
        {
            if (remaining <= 0) break;
            byte take = (byte)Math.Min(group, remaining);
            if (take > 0)
            {
                rebuilt.Add(take);
                remaining -= take;
            }
        }
        while (remaining-- > 0) rebuilt.Add(1);
        return rebuilt;
    }

    // 5. Kishara's Ducat (#5440): strips a non-fractured Rare item down to 1 uniformly random explicit mod while staying
    //    Rare with a fresh rare name and without setting the Split flag.
    static Item? ApplyKishara(ItemRolls rolls, Item target, ItemCatalog.BaseType targetType, Random random)
    {
        if (target.Rarity != ItemRarity.Rare || target.FracturedCount > 0) return null;
        var explicits = target.Mods.Where(m => m.Section == ModSection.Explicit).ToList();
        if (explicits.Count == 0) return null;
        var chosen = explicits[random.Next(explicits.Count)];
        var kept = target.Mods.Where(m => m.Section != ModSection.Explicit).ToList();
        kept.Add(chosen);
        return target with { Mods = kept, Rarity = ItemRarity.Rare, Identified = true, NameWords = rolls.RareName(targetType, random) };
    }

    // 6. Merrick's Ducat (#5418): adds 1 explicit mod to a Rare Abyss Jewel (allowing a 5th mod, up to 3P/3S) and corrupts it.
    static Item? ApplyMerrick(ItemRolls rolls, Item target, ItemCatalog.BaseType targetType, Random random)
    {
        if (target.Rarity != ItemRarity.Rare || targetType.Class != "AbyssJewel") return null;
        if (target.Mods.Count(m => m.Section == ModSection.Explicit) >= 5) return null;
        return rolls.AddAffix(target, (3, 3), random) is { } added
            ? added with { Corrupted = true, Identified = true } : null;
    }

    // 7. Rotmother's Ducat (#5442): adds 1 Domain=42 hazard-belt mod (fam 7814) to an open prefix or suffix of a Rare Belt.
    static Item? ApplyRotmother(ItemCatalog catalog, ItemRolls rolls, Item target, ItemCatalog.BaseType targetType, Random random)
    {
        if (target.Rarity != ItemRarity.Rare || targetType.Class != "Belt") return null;
        var pools = PoolsOf(catalog.Mods);
        if (pools.Rotmother.Length == 0) return null;

        var (maxP, maxS) = rolls.RareMaxAffixes(target);
        int prefixes = 0, suffixes = 0;
        foreach (var m in target.Mods)
        {
            if (catalog.Mods.ByHash(m.Hash) is not { } row) continue;
            if (row.Families.Contains(HazardBeltFamily)) return null;
            if (m.Section != ModSection.Explicit) continue;
            if (row.GenerationType == ItemRolls.Prefix) prefixes++;
            else if (row.GenerationType == ItemRolls.Suffix) suffixes++;
        }
        bool openP = prefixes < maxP, openS = suffixes < maxS;
        if (!openP && !openS) return null;

        var eligible = pools.Rotmother
            .Where(p => (p.Mod.GenerationType == ItemRolls.Prefix && openP) || (p.Mod.GenerationType == ItemRolls.Suffix && openS))
            .ToList();
        if (eligible.Count == 0) return null;

        var chosen = WeightedPick(eligible, random);
        return InsertExplicitMod(target, catalog.Mods, chosen, random) with { Identified = true };
    }

    // 8. Telesia's Ducat (#5441): rerolls the DeepwaterRooms room on an uncharted Chart, keeping AreaLevel, Rotation, rarity and mods.
    static Item? ApplyTelesia(ItemCatalog catalog, Item target, ItemCatalog.BaseType targetType, Random random)
    {
        if (!DeepwaterCharts.IsChart(targetType) || DeepwaterLocker.Charted(catalog, target) || target.Chart is not { } chart)
            return null;
        return target with { Chart = chart with { Room = DeepwaterCharts.Room(targetType.Row, random, areaLevel: chart.AreaLevel) } };
    }

    // 9. The Genteel's Ducat (#5439): replaces 1 non-fractured explicit single-attribute mod with a same-tier counterpart of
    //    another attribute whose family is not already on the item.
    static Item? ApplyGenteel(ItemCatalog catalog, Item target, Random random)
    {
        if (target.Rarity is not (ItemRarity.Magic or ItemRarity.Rare)) return null;
        var pools = PoolsOf(catalog.Mods);
        var fractured = target.FracturedIndexes();
        var eligible = new List<(int Index, List<ModCatalog.Mod> Targets)>();

        for (int i = 0; i < target.Mods.Count; i++)
        {
            if (target.Mods[i].Section != ModSection.Explicit || fractured.Contains(i)) continue;
            if (!pools.Genteel.TryGetValue(target.Mods[i].Hash, out var pair)) continue;

            var otherFamilies = new HashSet<int>();
            for (int j = 0; j < target.Mods.Count; j++)
            {
                if (j == i || target.Mods[j].Section != ModSection.Explicit) continue;
                if (catalog.Mods.ByHash(target.Mods[j].Hash) is { } other)
                    foreach (int fam in other.Families) otherFamilies.Add(fam);
            }

            var valid = new List<ModCatalog.Mod>(2);
            if (!pair.A.Families.Any(otherFamilies.Contains)) valid.Add(pair.A);
            if (!pair.B.Families.Any(otherFamilies.Contains)) valid.Add(pair.B);
            if (valid.Count > 0) eligible.Add((i, valid));
        }
        if (eligible.Count == 0) return null;

        var (index, choices) = eligible[random.Next(eligible.Count)];
        var replacement = choices[random.Next(choices.Count)];
        var mods = target.Mods.ToList();
        mods[index] = replacement.Roll(ModSection.Explicit, random);
        return target with { Mods = mods, Identified = true };
    }

    // 11. Ukatoa's Ducat (#5446): replaces all implicit mods of a non-unique, uninfluenced, non-eldritch, modifiable-implicit
    //     Amulet with 1 T1-T4 Exarch or Eater amulet implicit.
    static Item? ApplyUkatoa(ItemCatalog catalog, ItemRolls rolls, Item target, ItemCatalog.BaseType targetType, Random random)
    {
        if (targetType.Class != "Amulet" || target.Rarity == ItemRarity.Unique) return null;
        if (target.Influences != ItemInfluence.None || rolls.HasEldritchImplicit(target)) return null;
        if (!target.Mods.Any(m => m.Section == ModSection.Implicit)) return null;
        if (ItemModStats.LocalValue(target, catalog.Mods, ImmutableImplicitStat) != 0) return null;

        var pools = PoolsOf(catalog.Mods);
        if (pools.Ukatoa.Length == 0) return null;

        var chosen = WeightedPick(pools.Ukatoa, random);
        var mods = target.Mods.Where(m => m.Section != ModSection.Implicit).ToList();
        mods.Insert(0, chosen.Roll(ModSection.Implicit, random));
        return target with { Mods = mods };
    }

    /// <summary>Inserts one explicit mod preserving prefix-before-suffix and fractured-first order (matching
    /// <see cref="ItemRolls"/>).</summary>
    internal static Item InsertExplicitMod(Item item, ModCatalog mods, ModCatalog.Mod added, Random random)
    {
        var list = item.Mods.ToList();
        int at = added.GenerationType == ItemRolls.Prefix
            ? list.FindLastIndex(m => m.Section == ModSection.Explicit && mods.ByHash(m.Hash)?.GenerationType == ItemRolls.Prefix) + 1
            : list.Count;
        if (at == 0) at = list.FindIndex(m => m.Section == ModSection.Explicit) is int e and >= 0 ? e : list.Count;
        if (item.FracturedIndexes() is { Count: > 0 } f) at = Math.Max(at, f.Max() + 1);
        list.Insert(at, added.Roll(ModSection.Explicit, random));
        return item with { Mods = list };
    }

    internal static ModCatalog.Mod WeightedPick(IReadOnlyList<(ModCatalog.Mod Mod, int Weight)> pool, Random random)
    {
        long total = 0;
        for (int i = 0; i < pool.Count; i++) total += pool[i].Weight;
        long roll = random.NextInt64(Math.Max(1, total));
        for (int i = 0; i < pool.Count; i++)
        {
            if (roll < pool[i].Weight) return pool[i].Mod;
            roll -= pool[i].Weight;
        }
        return pool[^1].Mod;
    }
}
