using GameServer.Core.Protocol.Instance;

namespace GameServer.Modules.Crucible;

/// <summary>
/// Crucible Weapon Passive Tree generation, XP progression, node allocation/unallocation, Magmatic Ore application,
/// and Forge of the Titans tree combining.
/// </summary>
public static class CrucibleTreeEngine
{
    // ItemClasses rows the client's check 0x1424b1c30 lets carry a tree, typed in: its weapon-class table
    // 0x142fe77a0 is not decoded. Shield (26) and FishingRod (37) are named in that check.
    static readonly HashSet<int> OneHandWeaponClasses = [6, 7, 8, 9, 10, 11, 12, 32, 56];
    static readonly HashSet<int> TwoHandWeaponClasses = [13, 14, 15, 16, 17, 37, 57];
    const int ShieldClass = 26;

    const uint UniqueShieldTreeStat = Stat.LocalUniqueCanHaveShieldWeaponPassiveTree;
    const uint Unique2HSwordTreeStat = Stat.LocalUniqueCanHaveTwoHandedSwordWeaponPassiveTree;
    const uint UniqueStaffTreeStat = Stat.LocalUniqueCanHaveCrucibleUniqueStaffPassiveTreeMods;

    public enum WeaponCategory { None, OneHand, TwoHand, Shield }

    /// <summary>Weapon/shield category used for eligibility and Forge of the Titans combining compatibility.</summary>
    public static WeaponCategory CategoryOf(Item item, ItemCatalog.BaseType type, ModCatalog mods)
    {
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

    static bool HasModStat(Item item, ModCatalog mods, uint statId)
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
                allowMutations: false, random) is { } chosen)
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
        if (masks.Layout == 0)
            return new WeaponPassiveTreeState(BalanceItemLevel: (uint)ilvl, Experience: 0, HasTree: false);
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
            if (!current.HasTree)
                return item with { WeaponPassiveTree = current with { Experience = newXp } };
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
        if (target.Corrupted || target.Mirrored || target.Vestigial) return null;
        if (target.WeaponPassiveTree is not { HasTree: true } tree) return null;
        if (!CrucibleTreeTopology.CanUnallocate(tree.Allocation, col, row)) return null;

        var balance = CrucibleTables.Current.BalanceFor((int)Math.Max(1, tree.BalanceItemLevel));
        ushort newAlloc = (ushort)(tree.Allocation & ~CrucibleTreeTopology.BitMask(col, row));
        // Our reading of ClientStrings 6811 ("All Experience towards it will be lost"): back to the bar before it.
        uint newXp = col == 0 ? 0u : (uint)balance.BarToUnlock(col - 1);
        if (tree.Experience < newXp) newXp = tree.Experience;

        for (int c = col; c < CrucibleTreeTopology.Columns; c++)
            tree = tree.WithPendingRow(c, -1);

        var updated = tree with
        {
            Allocation = newAlloc,
            Experience = newXp,
        };
        var result = target with { WeaponPassiveTree = updated };
        return result.Foreseeing ? ItemRolls.ClearForesee(result) : result;
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

    /// <summary>The Remnant modifiers that change a combine in this Forge of the Titans.</summary>
    public sealed record CombineModifiers(bool CannotDowngradeTiers, int ExtraUpgradeTierChance, bool MoreLikelySkillsKept, bool Quality30)
    {
        public static CombineModifiers From(MapMods mods) => new(
            CannotDowngradeTiers: mods.Get(Stat.MapCrucibleCombiningTiersCannotBeDowngraded) > 0,
            ExtraUpgradeTierChance: Math.Max(0, mods.Get(Stat.MapCrucibleCombiningPlusChancePctForUpgradedTiers)),
            MoreLikelySkillsKept: mods.Get(Stat.MapCrucibleCombiningMoreLikelySkillsAreKept) > 0,
            Quality30: mods.Get(Stat.MapCrucibleCombiningItemHas30Quality) > 0);
    }

    // ESTIMATE, in per mille: the only measured combining odds, counted over 30 to 40 combines by the player Poe Noob
    // (poewiki "Crucible Passive Skill Tree" gives the same model without numbers). GGG never published any.
    const int KeptOnBothSides = 930, KeptOnOneSide = 700;
    const int AllocatedSideChosen = 700;
    const int MutateChance = 66, TierUpChance = 21, TierDownChance = 16;
    // Our own number, GGG never published it: what the Remnant's "more likely to be retained" adds to both keep chances.
    const int RetainedMore = 150;

    /// <summary>Why the combining bench refuses this pair: the ClientStrings row the client's window shows for it, 0
    /// when it enables Forge. The client's checks in their order: each item <c>0x1424bb530</c>, the surviving item
    /// <c>0x1424bb770</c>, the pair <c>0x1424bb850</c>. The uber bench lifts the unique and corrupted checks.</summary>
    public static int CombineRefusal(
        Item top, ItemCatalog.BaseType topType,
        Item bottom, ItemCatalog.BaseType bottomType,
        ModCatalog mods, bool uber)
    {
        foreach (var (item, type) in new[] { (bottom, bottomType), (top, topType) })
        {
            if (item.SkinTransferSource is not null) return 6384;
            if (type.Components.Contains("Transformed")) return 1265;
            if (!item.Components.Contains("Mods")) return 1237;
            if (!item.Identified) return 1211;
            if (item.Sockets.Any(s => s.Gem is not null)) return 1229;
            // The client tests the tree's layout ([+0x140] == 0): a tree whose first skill could not be filled is none.
            if (item.WeaponPassiveTree is not { HasTree: true } tree || (tree.Layout & 1) == 0 || tree.NodeAt(0, 0) is null) return 6785;
            if (item.Mirrored) return 1223;
            if (uber) continue;
            if (item.Rarity == ItemRarity.Unique) return 1392;
            if (item.Corrupted && !HasModStat(item, mods, Stat.LocalItemAllowModificationWhileCorrupted)) return 1203;
        }
        if (bottomType.Unmodifiable) return 6401;
        if (!CanHaveTree(bottom, bottomType, mods)) return 6792;
        if (CrucibleTables.Current.ClassCategory(topType.ClassRow) != CrucibleTables.Current.ClassCategory(bottomType.ClassRow)) return 6789;
        if (top.Rarity == ItemRarity.Unique || bottom.Rarity == ItemRarity.Unique)
        {
            static IEnumerable<ushort> Explicit(Item item) => item.Mods.Where(m => m.Section == ModSection.Explicit).Select(m => m.Hash);
            if (top.Rarity != bottom.Rarity || top.TypeHash != bottom.TypeHash || !Explicit(top).SequenceEqual(Explicit(bottom))) return 6791;
        }
        return 0;
    }

    /// <summary>
    /// Combines <paramref name="top"/> (destroyed) onto <paramref name="bottom"/> (survives), returning the updated
    /// bottom item and the list of node mutation events for S→C <c>0x029a</c>.
    /// </summary>
    public static (Item Result, IReadOnlyList<InstanceCrucible.NodeMutation> Mutations) CombineTrees(
        Item top,
        Item bottom,
        ItemCatalog.BaseType bottomType,
        ModCatalog mods,
        CombineModifiers modifiers,
        Random random)
    {
        var tables = CrucibleTables.Current;
        var tTop = top.WeaponPassiveTree!;
        var tBot = bottom.WeaponPassiveTree!;

        // Ours: a new or stepped skill may be of the higher item level of the two.
        int ilvl = Math.Clamp((int)Math.Max(top.ItemLevel, bottom.ItemLevel), 1, 100);
        var tags = RollTagsFor(bottom, bottomType, mods);

        var slotNode = new WeaponPassiveTreeNode?[CrucibleTreeTopology.MaxSlots];
        var slotMut = new InstanceCrucible.NodeMutationFlags[CrucibleTreeTopology.MaxSlots];
        var slotAllocCandidate = new bool[CrucibleTreeTopology.MaxSlots];

        ushort rawLayout = 0;
        ushort rawLeft = 0;
        ushort rawRight = 0;

        // A mutation gives a skill neither tree has.
        var usedFamilies = new HashSet<int>();
        var usedSkillIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in tTop.Nodes.Concat(tBot.Nodes))
        {
            if (ResolveSkill(tables, mods, node.ModHash) is { } skill) usedSkillIds.Add(skill.Id);
            if (mods.ByHash(node.ModHash) is { } mod) usedFamilies.UnionWith(mod.Families);
        }

        int retained = modifiers.MoreLikelySkillsKept ? RetainedMore : 0;
        int tierUp = TierUpChance + modifiers.ExtraUpgradeTierChance * 10;
        int tierDown = modifiers.CannotDowngradeTiers ? 0 : TierDownChance;

        for (int bit = 0; bit < CrucibleTreeTopology.MaxSlots; bit++)
        {
            var (col, row) = CrucibleTreeTopology.Coords(bit);
            var topNode = tTop.NodeAt(col, row);
            var botNode = tBot.NodeAt(col, row);
            if (topNode is null && botNode is null) continue;
            bool both = topNode is not null && botNode is not null;
            if (random.Next(1000) >= (both ? KeptOnBothSides : KeptOnOneSide) + retained) continue;

            bool topAlloc = tTop.IsAllocated(col, row);
            bool botAlloc = tBot.IsAllocated(col, row);
            int topChance = topAlloc == botAlloc ? 500 : topAlloc ? AllocatedSideChosen : 1000 - AllocatedSideChosen;
            var chosen = (both ? random.Next(1000) < topChance ? topNode : botNode : topNode ?? botNode)!;
            var mutFlags = InstanceCrucible.NodeMutationFlags.None;

            int roll = random.Next(1000);
            if (roll < MutateChance)
            {
                // Only where both trees have a skill (poewiki).
                if (both && PickSkillForColumn(tables, mods, tags, ilvl, col + 1, usedFamilies, usedSkillIds,
                    allowMutations: true, random) is { } mutated)
                {
                    chosen = new WeaponPassiveTreeNode(mutated.Mod.Hash, mutated.Mod.Roll(ModSection.Implicit, random).Values);
                    usedSkillIds.Add(mutated.Skill.Id);
                    usedFamilies.UnionWith(mutated.Mod.Families);
                    mutFlags = InstanceCrucible.NodeMutationFlags.Mutated;
                }
            }
            else if (roll < MutateChance + tierUp + tierDown && ResolveSkill(tables, mods, chosen.ModHash) is { } skill)
            {
                bool up = roll < MutateChance + tierUp;
                if (TryStepTier(tables, mods, skill, up ? +1 : -1, ilvl, random) is { } stepped)
                {
                    chosen = stepped;
                    mutFlags = up ? InstanceCrucible.NodeMutationFlags.UpgradedTier : InstanceCrucible.NodeMutationFlags.DowngradedTier;
                }
            }

            slotNode[bit] = chosen;
            slotMut[bit] = mutFlags;
            slotAllocCandidate[bit] = topAlloc || botAlloc;
            rawLayout |= (ushort)(1 << bit);
            // Ours: a kept skill has the links of both trees.
            rawLeft |= (ushort)((tTop.LeftParentConnections | tBot.LeftParentConnections) & (1 << bit));
            rawRight |= (ushort)((tTop.RightParentConnections | tBot.RightParentConnections) & (1 << bit));
        }

        // Ours: the first skill always survives, a tree has a root.
        if ((rawLayout & 1) == 0)
        {
            rawLayout |= 1;
            slotNode[0] = (tBot.NodeAt(0, 0) ?? tTop.NodeAt(0, 0))!;
            slotAllocCandidate[0] = true;
        }

        var clean = CrucibleTreeTopology.Normalize(rawLayout, rawLeft, rawRight);
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
        // Our own rule, GGG never published one: the result keeps the higher experience of the two, a skill allocated
        // on either side stays allocated where the path allows, and no unlocked column stays empty.
        uint newXp = Math.Min((uint)balance.Bar5, Math.Max(tTop.Experience, tBot.Experience));
        int unlocked = CrucibleTreeTopology.UnlockedDepth(newXp, balance);

        ushort newAlloc = 0;
        for (int col = 0; col < unlocked; col++)
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
        if (modifiers.Quality30 && result.Components.Contains("Quality") && result.Quality < 30)
            result = result with { Quality = 30 };
        return (result, mutations);
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
        Random random)
    {
        var pool = new List<(CrucibleTables.SkillRow Skill, ModCatalog.Mod Mod, int Weight)>();
        int totalWeight = 0;

        foreach (var skill in tables.Skills)
        {
            if (Array.IndexOf(skill.SpawnColumns, spawnCol) < 0) continue;
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
        return null;
    }
}
