using GameServer.Core.Protocol.Instance;

namespace GameServer.Modules.Crucible;

/// <summary>
/// Crucible Weapon Passive Tree generation, XP progression, node allocation/unallocation, Magmatic Ore application,
/// Antique/Scouring Furnace operations, and Forge of the Titans tree combining with mutations &amp; Remnant modifiers.
/// </summary>
public static class CrucibleTreeEngine
{
    // ItemClasses rows the client's check 0x1424b1c30 lets carry a tree, typed in: its weapon-class table
    // 0x142fe77a0 is not decoded. Shield (26) and FishingRod (37) are named in that check.
    static readonly HashSet<int> OneHandWeaponClasses = [6, 7, 8, 9, 10, 11, 12, 32, 56];
    static readonly HashSet<int> TwoHandWeaponClasses = [13, 14, 15, 16, 17, 37, 57];
    const int ShieldClass = 26;
    const int QuiverClass = 20;

    const string UniqueShieldTreeStat = "local_unique_can_have_shield_weapon_passive_tree";
    const string Unique2HSwordTreeStat = "local_unique_can_have_two_handed_sword_weapon_passive_tree";
    const string UniqueStaffTreeStat = "local_unique_can_have_crucible_unique_staff_passive_tree_mods";

    public enum WeaponCategory { None, OneHand, TwoHand, Shield }

    /// <summary>Weapon/shield category used for eligibility and Forge of the Titans combining compatibility.</summary>
    public static WeaponCategory CategoryOf(Item item, ItemCatalog.BaseType type, ModCatalog mods)
    {
        if (type.ClassRow == QuiverClass) return WeaponCategory.None;
        if (type.ClassRow == ShieldClass) return WeaponCategory.Shield;
        if (OneHandWeaponClasses.Contains(type.ClassRow)) return WeaponCategory.OneHand;
        if (TwoHandWeaponClasses.Contains(type.ClassRow)) return WeaponCategory.TwoHand;

        // Special Crucible uniques (e.g. unique helmet with shield tree or special 2H sword tree).
        if (HasModStat(item, mods, UniqueShieldTreeStat)) return WeaponCategory.Shield;
        if (HasModStat(item, mods, Unique2HSwordTreeStat)) return WeaponCategory.TwoHand;
        return WeaponCategory.None;
    }

    /// <summary>Whether this item can carry a Crucible Weapon Passive Tree at all. By class alone, uniques too
    /// (<c>0x1424b1c30</c>): <c>WeaponPassiveTreeUniqueBaseTypes</c> only names the bases of the sell node.</summary>
    public static bool CanHaveTree(Item item, ItemCatalog.BaseType type, ModCatalog mods) =>
        item.Components.Contains("Mods") && CategoryOf(item, type, mods) != WeaponCategory.None;

    /// <summary>Whether an item gains experience at an in-zone Crucible Forge. ClientStrings 6786: not a corrupted or
    /// mirrored item, and a unique only with a tree forged onto it elsewhere.</summary>
    public static bool CanChannelInForge(Item item, ItemCatalog.BaseType type, ModCatalog mods) =>
        !item.Corrupted && !item.Mirrored && CanHaveTree(item, type, mods)
        && (item.Rarity != ItemRarity.Unique || item.WeaponPassiveTree is { HasTree: true });

    /// <summary>Effective mod-rolling tag set for an item (base tags + special unique Crucible tags).</summary>
    public static HashSet<string> RollTagsFor(Item item, ItemCatalog.BaseType type, ModCatalog mods)
    {
        var tags = new HashSet<string>(type.Tags, StringComparer.Ordinal) { "default" };
        if (HasModStat(item, mods, UniqueShieldTreeStat))
        {
            tags.Add("shield");
        }
        if (HasModStat(item, mods, Unique2HSwordTreeStat))
        {
            tags.Add("weapon");
            tags.Add("two_hand_weapon");
            tags.Add("sword");
        }
        if (HasModStat(item, mods, UniqueStaffTreeStat))
        {
            tags.Add("crucible_unique_staff");
        }
        return tags;
    }

    static bool HasModStat(Item item, ModCatalog mods, string statId)
    {
        foreach (var im in item.Mods)
        {
            if (mods.ByHash(im.Hash) is not { } mod) continue;
            foreach (var s in mod.Stats)
                if (s.Id == statId) return true;
        }
        return false;
    }

    /// <summary>Generates a fresh revealed Weapon Passive Tree, without experience, for an eligible weapon or shield.
    /// Rarity changes nothing (GGG's Recently Asked Questions post).</summary>
    public static WeaponPassiveTreeState GenerateTree(Item item, ItemCatalog.BaseType type, ModCatalog mods, Random random)
    {
        int ilvl = Math.Clamp(item.ItemLevel == 0 ? 1 : item.ItemLevel, 1, 100);
        var tables = CrucibleTables.Current;
        var tags = RollTagsFor(item, type, mods);

        var masks = CrucibleTreeTopology.GenerateLayout(ilvl, random);
        var bits = CrucibleTreeTopology.BitsInOrder(masks.Layout);
        var usedFamilies = new HashSet<int>();
        var usedSkillIds = new HashSet<string>(StringComparer.Ordinal);
        var rolledByBit = new Dictionary<int, WeaponPassiveTreeNode>(bits.Count);

        ushort filledLayout = masks.Layout;
        foreach (int bit in bits)
        {
            var (col, _) = CrucibleTreeTopology.Coords(bit);
            if (PickSkillForColumn(tables, mods, tags, ilvl, col + 1, usedFamilies, usedSkillIds,
                allowMutations: false, forceSellBonus: false, random) is { } chosen)
            {
                usedSkillIds.Add(chosen.Skill.Id);
                foreach (int fam in chosen.Mod.Families) usedFamilies.Add(fam);
                var rolled = chosen.Mod.Roll(ModSection.Implicit, random);
                rolledByBit[bit] = new WeaponPassiveTreeNode(chosen.Mod.Hash, rolled.Values);
            }
            else
            {
                // Prune unfillable slot instead of emitting invalid ModHash = 0.
                filledLayout &= (ushort)~(1 << bit);
            }
        }

        masks = CrucibleTreeTopology.Normalize(filledLayout, masks.LeftParents, masks.RightParents);
        var finalBits = CrucibleTreeTopology.BitsInOrder(masks.Layout);
        var nodes = new List<WeaponPassiveTreeNode>(finalBits.Count);
        foreach (int bit in finalBits) nodes.Add(rolledByBit[bit]);

        return new WeaponPassiveTreeState(
            BalanceItemLevel: (uint)ilvl,
            Experience: 0,
            HasTree: true,
            Layout: masks.Layout,
            Allocation: 0,
            LeftParentConnections: masks.LeftParents,
            RightParentConnections: masks.RightParents,
            PendingCol0: -1, PendingCol1: -1, PendingCol2: -1, PendingCol3: -1, PendingCol4: -1,
            Nodes: nodes);
    }

    /// <summary>
    /// Adds Crucible experience to an item. An item without a tree gets one when the first bar is reached (GGG's
    /// Crucible video, 1:41); every column the experience unlocks gets its skill.
    /// </summary>
    public static Item AwardExperience(
        Item item,
        ItemCatalog.BaseType type,
        ModCatalog mods,
        uint xpDelta,
        Random random,
        out bool revealedNow,
        out int newlyAllocatedCount)
    {
        revealedNow = false;
        newlyAllocatedCount = 0;
        if (xpDelta == 0 || !CanHaveTree(item, type, mods)) return item;

        var current = item.WeaponPassiveTree;
        uint balanceLevel = current is { BalanceItemLevel: > 0 }
            ? current.BalanceItemLevel : (uint)Math.Clamp(item.ItemLevel == 0 ? 1 : item.ItemLevel, 1, 100);
        var balance = CrucibleTables.Current.BalanceFor((int)balanceLevel);
        uint newXp = (uint)Math.Min((ulong)Math.Max(1, balance.Bar5), (ulong)(current?.Experience ?? 0) + xpDelta);

        if (current is not { HasTree: true })
        {
            if (item.Rarity == ItemRarity.Unique) return item;
            if (newXp < (uint)balance.Bar1)
                return item with { WeaponPassiveTree = new WeaponPassiveTreeState(balanceLevel, newXp, HasTree: false) };
            current = GenerateTree(item, type, mods, random) with { BalanceItemLevel = balanceLevel };
            revealedNow = true;
        }

        return item with
        {
            WeaponPassiveTree = ResolvePendingAllocations(current with { Experience = newXp }, balance, out newlyAllocatedCount),
        };
    }

    /// <summary>Allocates a skill in every unlocked column that has none: its pending row, else our default row. No
    /// client message allocates a node, and a pending row of an unlocked column is dropped on load (<c>0x1424bcb60</c>).</summary>
    public static WeaponPassiveTreeState ResolvePendingAllocations(
        WeaponPassiveTreeState tree,
        CrucibleTables.LevelBalance balance,
        out int allocatedCount)
    {
        allocatedCount = 0;
        if (!tree.HasTree) return tree;

        ushort alloc = tree.Allocation;
        int depth = CrucibleTreeTopology.UnlockedDepth(tree.Experience, balance);
        for (int col = 0; col < depth; col++)
        {
            // The client wipes the pending row of a reached bar whether or not it allocated (0x1424bce1f).
            int row = tree.PendingRow(col);
            tree = tree.WithPendingRow(col, -1);
            if (CrucibleTreeTopology.AllocatedRowInColumn(alloc, col) >= 0) continue;
            if (!CrucibleTreeTopology.CanAllocate(tree.Layout, alloc, tree.LeftParentConnections, tree.RightParentConnections,
                tree.Experience, balance, col, row))
                row = DefaultRow(tree, alloc, col);
            if (row < 0) continue;
            alloc |= CrucibleTreeTopology.BitMask(col, row);
            allocatedCount++;
        }

        return tree with { Allocation = alloc };
    }

    /// <summary>The row allocated in an unlocked column without a usable pending row, -1 when the path ended before it.
    /// Our own default: the first connected child by row that has a connected child itself, else the first.</summary>
    static int DefaultRow(WeaponPassiveTreeState tree, ushort alloc, int col)
    {
        if (col == 0) return tree.HasNode(0, 0) ? 0 : -1;
        int parent = CrucibleTreeTopology.AllocatedRowInColumn(alloc, col - 1);
        if (parent < 0) return -1;
        var children = CrucibleTreeTopology.ConnectedChildren(tree.Layout, tree.LeftParentConnections, tree.RightParentConnections, col - 1, parent);
        foreach (int row in children)
            if (CrucibleTreeTopology.ConnectedChildren(tree.Layout, tree.LeftParentConnections, tree.RightParentConnections, col, row).Count > 0)
                return row;
        return children.Count > 0 ? children[0] : -1;
    }

    /// <summary>Makes <c>(col, row)</c> the player's choice: allocated at once in an unlocked column without a skill,
    /// the pending row in a locked column. Null when the node cannot be chosen.</summary>
    public static WeaponPassiveTreeState? SelectOrAllocateNode(WeaponPassiveTreeState tree, int col, int row)
    {
        if (!tree.HasTree || !CrucibleTreeTopology.ValidCoords(col, row) || !tree.HasNode(col, row))
            return null;

        var balance = CrucibleTables.Current.BalanceFor((int)Math.Max(1, tree.BalanceItemLevel));
        if (col < CrucibleTreeTopology.UnlockedDepth(tree.Experience, balance))
            return CrucibleTreeTopology.CanAllocate(tree.Layout, tree.Allocation, tree.LeftParentConnections, tree.RightParentConnections,
                tree.Experience, balance, col, row)
                ? ResolvePendingAllocations(tree.WithPendingRow(col, (sbyte)row), balance, out _)
                : null;

        // Selectable as in the client's tree (state 3 of 0x1403f2530): linked to the row chosen in the column before.
        if (col > 0)
        {
            int before = CrucibleTreeTopology.AllocatedRowInColumn(tree.Allocation, col - 1);
            if (before < 0) before = tree.PendingRow(col - 1);
            if (before < 0 || !CrucibleTreeTopology.IsConnectedToParent(tree.Layout, tree.LeftParentConnections, tree.RightParentConnections, col, row, before))
                return null;
        }

        // The client's press handler 0x1403f7bf0 drops the choices of the deeper columns as well.
        for (int deeper = col + 1; deeper < CrucibleTreeTopology.Columns; deeper++)
            tree = tree.WithPendingRow(deeper, -1);
        return tree.WithPendingRow(col, (sbyte)row);
    }

    /// <summary>
    /// Unallocates the deepest allocated node <c>(col, row)</c> using an Orb of Scouring (C→S <c>0x0116</c>).
    /// Resets XP to the previous column's bar threshold so the player can re-channel another branch.
    /// </summary>
    public static Item? UnallocateWithScouring(Item target, int col, int row)
    {
        if (target.WeaponPassiveTree is not { HasTree: true } tree) return null;
        if (!CrucibleTreeTopology.CanUnallocate(tree.Allocation, col, row)) return null;

        var balance = CrucibleTables.Current.BalanceFor((int)Math.Max(1, tree.BalanceItemLevel));
        ushort newAlloc = (ushort)(tree.Allocation & ~CrucibleTreeTopology.BitMask(col, row));
        // Our reading of ClientStrings 6811 ("All Experience towards it will be lost"): back to the bar before it.
        uint newXp = col == 0 ? 0u : (uint)balance.BarToUnlock(col - 1);
        if (tree.Experience < newXp) newXp = tree.Experience;

        var updated = tree.WithPendingRow(col, -1) with
        {
            Allocation = newAlloc,
            Experience = newXp,
        };
        return target with { WeaponPassiveTree = updated };
    }

    /// <summary>Applies a Magmatic Ore: its stored experience as it is, because the ore shows that number
    /// (ClientStrings 5210). Null when the ore does nothing.</summary>
    public static Item? ApplyMagmaticOre(Item target, ItemCatalog.BaseType type, ModCatalog mods, Random random, uint storedExperience)
    {
        if (storedExperience == 0 || !CanChannelInForge(target, type, mods)) return null;
        // ClientStrings 6797: "Crucible Passive Skill Tree is already at maximum experience".
        if (target.WeaponPassiveTree is { HasTree: true } tree
            && tree.Experience >= (uint)CrucibleTables.Current.BalanceFor((int)Math.Max(1, tree.BalanceItemLevel)).Bar5)
            return null;
        return AwardExperience(target, type, mods, storedExperience, random, out _, out _);
    }

    /// <summary>Remnant combining modifiers active in the current Forge of the Titans instance.</summary>
    public sealed record CombineModifiers(
        bool CannotDowngradeTiers,
        int ExtraUpgradeTierChance,
        bool MoreLikelySkillsKept,
        bool AdditionalSellNode,
        bool Quality30,
        bool CorruptionImplicit,
        bool FullyLinked,
        bool RerollModValues)
    {
        public static CombineModifiers From(MapMods mods) => new(
            CannotDowngradeTiers: mods.Get("map_crucible_combining_tiers_cannot_be_downgraded") > 0,
            ExtraUpgradeTierChance: Math.Max(0, mods.Get("map_crucible_combining_+_chance_%_for_upgraded_tiers")),
            MoreLikelySkillsKept: mods.Get("map_crucible_combining_more_likely_skills_are_kept") > 0,
            AdditionalSellNode: mods.Get("map_crucible_combining_additional_sell_node") > 0,
            Quality30: mods.Get("map_crucible_combining_item_has_30_quality") > 0,
            CorruptionImplicit: mods.Get("map_crucible_combining_item_has_corruption_implicit") > 0,
            FullyLinked: mods.Get("map_crucible_combining_item_is_fully_linked") > 0,
            RerollModValues: mods.Get("map_crucible_combining_item_has_mod_values_rerolled") > 0);
    }

    /// <summary>Checks whether two items are eligible to be combined at the Crucible Combining Bench.</summary>
    public static bool CanCombinePair(
        Item top, ItemCatalog.BaseType topType,
        Item bottom, ItemCatalog.BaseType bottomType,
        ModCatalog mods, bool uber, out string reason)
    {
        reason = "";
        if (top.WeaponPassiveTree is not { HasTree: true } || bottom.WeaponPassiveTree is not { HasTree: true })
        {
            reason = "Both items must have revealed Crucible Passive Trees.";
            return false;
        }
        if (top.Mirrored || bottom.Mirrored)
        {
            reason = "Mirrored items cannot be combined.";
            return false;
        }
        bool anyUnique = top.Rarity == ItemRarity.Unique || bottom.Rarity == ItemRarity.Unique;
        if (anyUnique)
        {
            if (!uber)
            {
                reason = "This Crucible Forge cannot combine Unique items.";
                return false;
            }
            if (top.Rarity != ItemRarity.Unique || bottom.Rarity != ItemRarity.Unique || top.TypeHash != bottom.TypeHash)
            {
                reason = "Unique items can only be combined with another copy of the same Unique base.";
                return false;
            }
        }
        var topCat = CategoryOf(top, topType, mods);
        var botCat = CategoryOf(bottom, bottomType, mods);
        if (topCat == WeaponCategory.None || topCat != botCat)
        {
            reason = "Both items must be of the same weapon or shield category.";
            return false;
        }
        return true;
    }

    /// <summary>
    /// Combines <paramref name="top"/> (destroyed) onto <paramref name="bottom"/> (survives), returning the updated
    /// bottom item and the list of node mutation events for S→C <c>0x029a</c>.
    /// </summary>
    public static (Item Result, IReadOnlyList<InstanceCrucible.NodeMutation> Mutations) CombineTrees(
        Item top,
        Item bottom,
        ItemCatalog.BaseType bottomType,
        ItemCatalog catalog,
        ItemRolls rolls,
        UniqueCatalog uniques,
        CombineModifiers modifiers,
        Random random)
    {
        var tables = CrucibleTables.Current;
        var mods = catalog.Mods;
        var tTop = top.WeaponPassiveTree!;
        var tBot = bottom.WeaponPassiveTree!;

        int ilvl = Math.Clamp((int)Math.Max(top.ItemLevel, bottom.ItemLevel), 1, 100);
        var tags = RollTagsFor(bottom, bottomType, mods);

        var slotNode = new WeaponPassiveTreeNode?[CrucibleTreeTopology.MaxSlots];
        var slotMut = new InstanceCrucible.NodeMutationFlags[CrucibleTreeTopology.MaxSlots];
        var slotAllocCandidate = new bool[CrucibleTreeTopology.MaxSlots];

        ushort rawLayout = 0;
        ushort rawLeft = 0;
        ushort rawRight = 0;

        var usedFamilies = new HashSet<int>();
        var usedSkillIds = new HashSet<string>(StringComparer.Ordinal);

        int allocKeepChance = modifiers.MoreLikelySkillsKept ? 85 : 70;
        int unallocKeepChance = modifiers.MoreLikelySkillsKept ? 55 : 40;
        int upgradeChance = Math.Clamp(10 + modifiers.ExtraUpgradeTierChance, 0, 85);
        int sameSkillUpgradeChance = Math.Clamp(25 + modifiers.ExtraUpgradeTierChance, 0, 95);

        for (int bit = 0; bit < CrucibleTreeTopology.MaxSlots; bit++)
        {
            var (col, row) = CrucibleTreeTopology.Coords(bit);
            int spawnCol = col + 1;
            bool hasTop = tTop.HasNode(col, row);
            bool hasBot = tBot.HasNode(col, row);
            if (!hasTop && !hasBot) continue;

            var topNode = hasTop ? tTop.NodeAt(col, row) : null;
            var botNode = hasBot ? tBot.NodeAt(col, row) : null;
            bool topAlloc = hasTop && tTop.IsAllocated(col, row);
            bool botAlloc = hasBot && tBot.IsAllocated(col, row);

            WeaponPassiveTreeNode? chosen = null;
            var mutFlags = InstanceCrucible.NodeMutationFlags.None;

            if (topNode is not null && botNode is not null)
            {
                var topSkill = ResolveSkill(tables, mods, topNode.ModHash);
                var botSkill = ResolveSkill(tables, mods, botNode.ModHash);

                if (topSkill is not null && botSkill is not null && topSkill.Id == botSkill.Id)
                {
                    // Matching skill in both trees: keep the higher tier and roll for a tier upgrade.
                    chosen = topSkill.Tier >= botSkill.Tier ? topNode : botNode;
                    var higherSkill = topSkill.Tier >= botSkill.Tier ? topSkill : botSkill;
                    if (random.Next(100) < sameSkillUpgradeChance && TryStepTier(tables, mods, higherSkill, +1, ilvl, random) is { } upgraded)
                    {
                        chosen = upgraded;
                        mutFlags |= InstanceCrucible.NodeMutationFlags.UpgradedTier;
                    }
                }
                else
                {
                    int wTop = topAlloc ? (modifiers.MoreLikelySkillsKept ? 80 : 70) : 30;
                    int wBot = botAlloc ? (modifiers.MoreLikelySkillsKept ? 80 : 70) : 30;
                    chosen = random.Next(wTop + wBot) < wTop ? topNode : botNode;
                }
                slotAllocCandidate[bit] = topAlloc || botAlloc;
            }
            else
            {
                bool isAlloc = topAlloc || botAlloc;
                int keepChance = col == 0 ? 100 : (isAlloc ? allocKeepChance : unallocKeepChance);
                if (random.Next(100) < keepChance)
                {
                    chosen = topNode ?? botNode;
                    slotAllocCandidate[bit] = isAlloc;
                }
            }

            if (chosen is null) continue;

            // Avoid duplicate mod family across slots; if duplicated, mutate into a fresh skill.
            var chosenMod = mods.ByHash(chosen.ModHash);
            var chosenSkill = ResolveSkill(tables, mods, chosen.ModHash);
            bool duplicate = (chosenSkill is not null && usedSkillIds.Contains(chosenSkill.Id))
                || (chosenMod is not null && chosenMod.Families.Any(usedFamilies.Contains));

            if (duplicate || (mutFlags == InstanceCrucible.NodeMutationFlags.None && random.Next(100) < 10))
            {
                if (PickSkillForColumn(tables, mods, tags, ilvl, spawnCol, usedFamilies, usedSkillIds,
                    allowMutations: true, forceSellBonus: false, random) is { } mutated)
                {
                    chosen = new WeaponPassiveTreeNode(mutated.Mod.Hash, mutated.Mod.Roll(ModSection.Implicit, random).Values);
                    chosenSkill = mutated.Skill;
                    chosenMod = mutated.Mod;
                    mutFlags |= InstanceCrucible.NodeMutationFlags.Mutated;
                }
            }
            else if (mutFlags == InstanceCrucible.NodeMutationFlags.None && chosenSkill is not null)
            {
                int tierRoll = random.Next(100);
                if (tierRoll < upgradeChance && TryStepTier(tables, mods, chosenSkill, +1, ilvl, random) is { } up)
                {
                    chosen = up;
                    mutFlags |= InstanceCrucible.NodeMutationFlags.UpgradedTier;
                }
                else if (!modifiers.CannotDowngradeTiers && tierRoll < upgradeChance + 8
                    && TryStepTier(tables, mods, chosenSkill, -1, ilvl, random) is { } down)
                {
                    chosen = down;
                    mutFlags |= InstanceCrucible.NodeMutationFlags.DowngradedTier;
                }
            }

            if (chosenSkill is not null) usedSkillIds.Add(chosenSkill.Id);
            if (chosenMod is not null)
                foreach (int fam in chosenMod.Families) usedFamilies.Add(fam);

            slotNode[bit] = chosen;
            slotMut[bit] = mutFlags;
            rawLayout |= (ushort)(1 << bit);
            rawLeft |= (ushort)(((tTop.LeftParentConnections | tBot.LeftParentConnections) & (1 << bit)));
            rawRight |= (ushort)(((tTop.RightParentConnections | tBot.RightParentConnections) & (1 << bit)));
        }

        // Guarantee root node (0, 0) exists.
        if ((rawLayout & 1) == 0)
        {
            rawLayout |= 1;
            slotNode[0] = tBot.NodeAt(0, 0) ?? tTop.NodeAt(0, 0)
                ?? GenerateTree(bottom, bottomType, mods, random).NodeAt(0, 0)!;
        }

        var clean = CrucibleTreeTopology.Normalize(rawLayout, rawLeft, rawRight);

        // Optional Remnant mod: guarantee at least one SellBonus node in columns 3-4 (col index 2-3).
        if (modifiers.AdditionalSellNode)
        {
            bool hasSell = false;
            var candidateBits = new List<int>();
            foreach (int b in CrucibleTreeTopology.BitsInOrder(clean.Layout))
            {
                var (c, _) = CrucibleTreeTopology.Coords(b);
                if (c is 2 or 3) candidateBits.Add(b);
                if (slotNode[b] is { } sn && ResolveSkill(tables, mods, sn.ModHash)?.Type == CrucibleTables.SkillTypeSellBonus)
                    hasSell = true;
            }
            if (!hasSell && candidateBits.Count > 0)
            {
                int targetBit = candidateBits[random.Next(candidateBits.Count)];
                var (tc, _) = CrucibleTreeTopology.Coords(targetBit);
                if (PickSkillForColumn(tables, mods, tags, ilvl, tc + 1, usedFamilies, usedSkillIds,
                    allowMutations: false, forceSellBonus: true, random) is { } sell)
                {
                    slotNode[targetBit] = new WeaponPassiveTreeNode(sell.Mod.Hash, sell.Mod.Roll(ModSection.Implicit, random).Values);
                    slotMut[targetBit] |= InstanceCrucible.NodeMutationFlags.Mutated;
                }
            }
        }

        // Assemble final ascending-bit-order mod list (pruning any null slot so ModHash=0 is never emitted),
        // mutations list, and preserved valid allocations.
        ushort validLayout = clean.Layout;
        foreach (int b in CrucibleTreeTopology.BitsInOrder(clean.Layout))
            if (slotNode[b] is null) validLayout &= (ushort)~(1 << b);
        clean = CrucibleTreeTopology.Normalize(validLayout, clean.LeftParents, clean.RightParents);

        var finalBits = CrucibleTreeTopology.BitsInOrder(clean.Layout);
        var finalNodes = new List<WeaponPassiveTreeNode>(finalBits.Count);
        var mutations = new List<InstanceCrucible.NodeMutation>();
        foreach (int b in finalBits)
        {
            finalNodes.Add(slotNode[b]!);
            if (slotMut[b] != InstanceCrucible.NodeMutationFlags.None)
            {
                var (c, r) = CrucibleTreeTopology.Coords(b);
                mutations.Add(new InstanceCrucible.NodeMutation((uint)c, (uint)r, slotMut[b]));
            }
        }

        uint newBalanceLevel = (uint)Math.Clamp(bottom.ItemLevel == 0 ? ilvl : bottom.ItemLevel, 1, 100);
        var balance = tables.BalanceFor((int)newBalanceLevel);
        // Our own rule, GGG never published one: the result keeps the higher experience of the two, and no unlocked column stays empty.
        uint newXp = Math.Min((uint)balance.Bar5, Math.Max(tTop.Experience, tBot.Experience));
        int unlocked = CrucibleTreeTopology.UnlockedDepth(newXp, balance);

        // Preserve a single valid connected allocated path from surviving allocated candidates.
        ushort newAlloc = 0;
        for (int col = 0; col < unlocked && col < CrucibleTreeTopology.Columns; col++)
        {
            for (int row = 0; row <= col; row++)
            {
                int b = CrucibleTreeTopology.BitIndex(col, row);
                if (((clean.Layout >> b) & 1) == 0 || !slotAllocCandidate[b]) continue;
                if (CrucibleTreeTopology.CanAllocate(clean.Layout, newAlloc, clean.LeftParents, clean.RightParents, newXp, balance, col, row))
                {
                    newAlloc |= (ushort)(1 << b);
                    break;
                }
            }
        }

        var combinedTree = new WeaponPassiveTreeState(
            BalanceItemLevel: newBalanceLevel,
            Experience: newXp,
            HasTree: true,
            Layout: clean.Layout,
            Allocation: newAlloc,
            LeftParentConnections: clean.LeftParents,
            RightParentConnections: clean.RightParents,
            PendingCol0: -1, PendingCol1: -1, PendingCol2: -1, PendingCol3: -1, PendingCol4: -1,
            Nodes: finalNodes);
        combinedTree = ResolvePendingAllocations(combinedTree, balance, out _);

        Item result = bottom with { WeaponPassiveTree = combinedTree };

        // Apply Remnant item-level combining modifiers to the surviving bottom item.
        if (modifiers.Quality30 && result.Components.Contains("Quality") && result.Quality < 30)
            result = result with { Quality = 30 };

        if (modifiers.FullyLinked && result.Sockets.Count > 1)
            result = result with { Links = [(byte)result.Sockets.Count] };

        if (modifiers.RerollModValues && !result.Corrupted)
        {
            if (rolls.Reforge(result, random, uniques.RollsOf(result)) is { } reforged)
                result = reforged with { WeaponPassiveTree = combinedTree };
        }

        if (modifiers.CorruptionImplicit && !result.Corrupted)
        {
            if (CurrencyEffects.Vaal(result, bottomType, catalog.Requirements(bottomType), rolls, random) is { } corrupted)
                result = corrupted with { WeaponPassiveTree = combinedTree };
            else
                result = result with { Corrupted = true };
        }

        return (result, mutations);
    }

    /// <summary>Applies an Antique Furnace (add tree to Unique) or Scouring Furnace (remove tree from non-Unique).</summary>
    public static Item? ApplyUniqueForge(Item target, ItemCatalog.BaseType type, ModCatalog mods, uint mode, Random random, out string reason)
    {
        reason = "";
        if (target.Mirrored)
        {
            reason = "Mirrored items cannot be modified here.";
            return null;
        }

        if (mode == InstanceCrucible.ScouringForgeMode)
        {
            if (target.Rarity == ItemRarity.Unique)
            {
                reason = "The Scouring Furnace can only remove trees from non-Unique items.";
                return null;
            }
            if (target.WeaponPassiveTree is null)
            {
                reason = "This item has no Crucible Passive Tree to remove.";
                return null;
            }
            return target with { WeaponPassiveTree = null };
        }

        // Antique Furnace (mode 0 = non-corrupted unique; mode 1 = corrupted unique allowed).
        if (target.Rarity != ItemRarity.Unique)
        {
            reason = "The Antique Furnace can only add a Crucible Passive Tree to a Unique item.";
            return null;
        }
        if (mode == InstanceCrucible.AntiqueForgeMode && target.Corrupted)
        {
            reason = "This Antique Furnace cannot modify Corrupted Unique items.";
            return null;
        }
        if (target.WeaponPassiveTree is { HasTree: true })
        {
            reason = "This Unique item already has a Crucible Passive Tree.";
            return null;
        }
        if (!CanHaveTree(target, type, mods))
        {
            reason = "This Unique item cannot receive a Crucible Passive Tree.";
            return null;
        }

        var tree = GenerateTree(target, type, mods, random);
        return target with { WeaponPassiveTree = tree };
    }

    static CrucibleTables.SkillRow? ResolveSkill(CrucibleTables tables, ModCatalog mods, ushort modHash) =>
        mods.ByHash(modHash) is { } mod && tables.SkillByModRow.TryGetValue(mod.Row, out var skill) ? skill : null;

    static WeaponPassiveTreeNode? TryStepTier(
        CrucibleTables tables, ModCatalog mods, CrucibleTables.SkillRow current, int delta, int ilvl, Random random)
    {
        if (!tables.SkillsByFamilyId.TryGetValue(current.Id, out var tiers) || tiers.Count <= 1) return null;
        int targetTier = current.Tier + delta;
        var match = tiers.FirstOrDefault(t => t.Tier == targetTier && mods.Row(t.ModRow) is { } m && m.Level <= ilvl);
        if (match is null || mods.Row(match.ModRow) is not { } mod) return null;
        var rolled = mod.Roll(ModSection.Implicit, random);
        return new WeaponPassiveTreeNode(mod.Hash, rolled.Values);
    }

    static (CrucibleTables.SkillRow Skill, ModCatalog.Mod Mod)? PickSkillForColumn(
        CrucibleTables tables,
        ModCatalog mods,
        HashSet<string> tags,
        int ilvl,
        int spawnCol,
        HashSet<int> usedFamilies,
        HashSet<string> usedSkillIds,
        bool allowMutations,
        bool forceSellBonus,
        Random random)
    {
        var pool = new List<(CrucibleTables.SkillRow Skill, ModCatalog.Mod Mod, int Weight)>();
        int totalWeight = 0;

        foreach (var skill in tables.Skills)
        {
            if (Array.IndexOf(skill.SpawnColumns, spawnCol) < 0) continue;
            if (forceSellBonus && skill.Type != CrucibleTables.SkillTypeSellBonus) continue;
            if (mods.Row(skill.ModRow) is not { } mod) continue;
            if (mod.Level > ilvl) continue;
            // GenerationType 32 is WEAPON_TREE_RECOMBINED in the schema: those skills exist only through combining.
            if (!allowMutations && mod.GenerationType == 32) continue;
            // Our own rule, GGG never published one: no skill and no mod family twice in a tree.
            if (usedSkillIds.Contains(skill.Id) || mod.Families.Any(usedFamilies.Contains)) continue;

            int weight = mod.WeightFor(tags);
            if (weight <= 0) continue;

            pool.Add((skill, mod, weight));
            totalWeight += weight;
        }

        if (totalWeight <= 0) return null;
        int roll = random.Next(totalWeight);
        foreach (var (skill, mod, weight) in pool)
        {
            if (roll < weight) return (skill, mod);
            roll -= weight;
        }
        var last = pool[^1];
        return (last.Skill, last.Mod);
    }
}
