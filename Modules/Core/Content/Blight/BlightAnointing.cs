using GameServer.Core.Protocol.Instance;
using GameServer.Modding;

namespace GameServer.Modules.Blight;

/// <summary>
/// Full Blight Oil Anointing engine (all 576 <c>BlightCraftingRecipes</c>) and <b>Oil Extractor</b> (<c>Action = "extract_oil"</c>):
/// - <b>Type 0 (Amulets &amp; Anointable Uniques, 472 recipes, 3 oils):</b> Amulets, 7 <c>BlightCraftingUniques</c>
///   (Sporeguard, The Stampede, Breathstealer, 3 Cowls [cap 2], Stranglegasp [cap 4]), and any item with <c>local_can_be_anointed</c>.
///   Supports all 31 Prismatic Oil recipes including <b>Worship the Blightheart</b> keystone.
/// - <b>Type 1 (Rings, 91 recipes, 2 oils):</b> Applies <c>BlightEnchantment*</c> tower modifier enchantments.
/// - <b>Type 2 (Blighted &amp; Blight-ravaged Maps, 13 recipes, 1 oil each):</b> Applies up to 3 oil enchants on Blighted maps
///   and up to 9 oil enchants (max 3 of the same oil) on Blight-ravaged maps (supports 1–3 oils in one click).
/// - <b>Special Oils:</b> Corrupted items require a <b>Tainted Oil</b> (<c>UseType = 1</c>); Mirrored items require a
///   <b>Reflective Oil</b> (<c>UseType = 2</c>).
/// - <b>Both surfaces:</b> Sister Cassia's <c>BlightCraftingWindow</c> (rows 36/37) AND the Blight Stash Tab
///   (cell 13 + cells 15..17, 78, gated on <c>QuestFlags 2058 CassiaOnAnointingOils</c>).
/// - <b>Oil Extractor (C→S 0x0071):</b> Destroys an anointed item (including corrupted items!) and returns a random oil
///   from its enchantment recipe.
/// </summary>
public static class BlightAnointing
{
    public const byte CraftingInputRow = 36;  // Inventories row 36: BlightCraftingInput (4x1)
    public const byte CraftingItemRow = 37;   // Inventories row 37: BlightCraftingItem (2x4)

    public const int TypeAmuletOrUnique = 0;
    public const int TypeRing = 1;
    public const int TypeInfectedMap = 2;

    public const string OilExtractorId = "Metadata/Items/Currency/CurrencyExtractOil";

    /// <summary>Classifies an item for Blight anointing: its crafting type (0/1/2), max enchantment cap, and whether it is a Blight-ravaged map.</summary>
    public static bool TryClassifyTarget(Item item, ItemCatalog catalog, out int craftingType, out int maxEnchants, out bool isUberMap)
    {
        craftingType = -1;
        maxEnchants = 0;
        isUberMap = false;
        if (!item.Identified) return false;
        if (catalog.ByHash(item.TypeHash) is not { } bt) return false;

        // 1. Blighted or Blight-ravaged Map (Type 2: cap 3 on Blighted, cap 9 [max 3 same] on Blight-ravaged).
        if (CurrencyEffects.IsMap(item, bt))
        {
            if (BlightTables.UberInfectedMapModHash != 0 && item.Mods.Any(m => m.Hash == BlightTables.UberInfectedMapModHash))
            {
                craftingType = TypeInfectedMap;
                maxEnchants = 9;
                isUberMap = true;
                return true;
            }
            if (BlightTables.InfectedMapModHash != 0 && item.Mods.Any(m => m.Hash == BlightTables.InfectedMapModHash))
            {
                craftingType = TypeInfectedMap;
                maxEnchants = 3;
                return true;
            }
            return false;
        }

        int extraEnchants = Math.Max(0, ItemModStats.Local(item, catalog.Mods).GetValueOrDefault(Stat.LocalItemCanHaveXAdditionalEnchantments));

        // 2. Ring (Type 1: 2 oils).
        if (bt.Class == "Ring")
        {
            craftingType = TypeRing;
            maxEnchants = 1 + extraEnchants;
            return true;
        }

        // 3. Amulet, BlightCraftingUniques (7 uniques), or any item with local_can_be_anointed (Type 0: 3 oils).
        bool isAmulet = bt.Class == "Amulet";
        bool isBlightUnique = item.Rarity == ItemRarity.Unique
            && item.NameWords.Any(BlightTables.AnointableUniqueWordHashes.Contains);
        bool hasCanBeAnointed = ItemModStats.Local(item, catalog.Mods).GetValueOrDefault(Stat.LocalCanBeAnointed) > 0;
        if (isAmulet || isBlightUnique || hasCanBeAnointed)
        {
            craftingType = TypeAmuletOrUnique;
            maxEnchants = 1 + extraEnchants; // 1 normal, 2 Cowls, 4 Stranglegasp
            return true;
        }
        return false;
    }

    /// <summary>Matches a sorted set of standard oil rows (<c>BlightCraftingItems._row</c>) against all 576 <c>BlightCraftingRecipes</c>.</summary>
    public static BlightTables.CraftingRecipe? FindRecipe(int craftingType, IReadOnlyList<int> oilRows)
    {
        if (oilRows.Count == 0) return null;
        int[] sorted = [.. oilRows];
        Array.Sort(sorted);
        foreach (var r in BlightTables.Recipes)
        {
            if (r.CraftingType != craftingType || r.OilRows.Length != sorted.Length) continue;
            bool match = true;
            for (int i = 0; i < sorted.Length; i++)
                if (r.OilRows[i] != sorted[i]) { match = false; break; }
            if (match) return r;
        }
        return null;
    }

    /// <summary>
    /// Pure core anointing transaction: validates item eligibility, corrupted (Tainted Oil) / mirrored (Reflective Oil)
    /// requirements, recipe matching, enchant caps (1 / 2 Cowls / 4 Stranglegasp / 3 Blighted / 9 Blight-ravaged max 3 same),
    /// and duplicate-notable prevention. Returns the newly enchanted item and which input oil indices are consumed.
    /// </summary>
    public static bool TryAnoint(Item target, IReadOnlyList<Item> inputOils, ItemCatalog catalog,
        out Item? result, out List<int> consumedOilIndices, out string reason)
    {
        result = null;
        consumedOilIndices = [];
        reason = "";

        if (!TryClassifyTarget(target, catalog, out int craftingType, out int maxEnchants, out bool isUberMap))
        {
            reason = "target item cannot be anointed";
            return false;
        }

        // Partition placed oils into standard (UseType 0), Tainted (UseType 1), and Reflective (UseType 2).
        var standardIndices = new List<(int Index, BlightTables.OilItem Oil)>();
        int taintedIndex = -1, reflectiveIndex = -1;
        for (int i = 0; i < inputOils.Count; i++)
        {
            if (catalog.ByHash(inputOils[i].TypeHash) is not { } bt
                || !BlightTables.OilByItemId.TryGetValue(bt.Id, out var oil))
            {
                reason = "non-oil item in oil input slot";
                return false;
            }
            if (oil.UseType == 0) standardIndices.Add((i, oil));
            else if (oil.UseType == 1 && taintedIndex < 0) taintedIndex = i;
            else if (oil.UseType == 2 && reflectiveIndex < 0) reflectiveIndex = i;
            else
            {
                reason = "duplicate special oil in input";
                return false;
            }
        }

        // Enforce Corrupted (Tainted Oil) and Mirrored (Reflective Oil) rules.
        if (target.Mirrored)
        {
            if (reflectiveIndex < 0) { reason = "mirrored items require a Reflective Oil"; return false; }
            consumedOilIndices.Add(reflectiveIndex);
        }
        else if (reflectiveIndex >= 0)
        {
            reason = "Reflective Oil can only be used on mirrored items";
            return false;
        }

        if (target.Corrupted)
        {
            if (taintedIndex < 0) { reason = "corrupted items require a Tainted Oil"; return false; }
            consumedOilIndices.Add(taintedIndex);
        }
        else if (taintedIndex >= 0)
        {
            reason = "Tainted Oil can only be used on corrupted items";
            return false;
        }

        if (craftingType == TypeAmuletOrUnique)
        {
            if (standardIndices.Count != 3) { reason = "amulet/unique anointing requires exactly 3 oils"; return false; }
            var recipe = FindRecipe(TypeAmuletOrUnique, [.. standardIndices.Select(x => x.Oil.Row)]);
            if (recipe is null || recipe.PassiveGraphId == 0) { reason = "unknown amulet anoint recipe"; return false; }

            var grantedHashes = BlightTables.GrantedPassiveModHashes;
            if (grantedHashes.Length == 0) { reason = "GrantedPassive mods not loaded"; return false; }
            var grantedHashSet = new HashSet<ushort>(grantedHashes);

            var mods = target.Mods.ToList();
            var existingAnoints = new List<(int ModIdx, ushort Hash, ushort GraphId)>();
            for (int m = 0; m < mods.Count; m++)
            {
                if (mods[m].Section == ModSection.Enchant && grantedHashSet.Contains(mods[m].Hash) && mods[m].Values.Count > 0)
                {
                    ushort gid = unchecked((ushort)mods[m].Values[0]);
                    if (gid == recipe.PassiveGraphId)
                    {
                        reason = "item already has this notable anointed";
                        return false;
                    }
                    existingAnoints.Add((m, mods[m].Hash, gid));
                }
            }

            // Remove oldest anoint if already at cap (1 normal, 2 Cowls, 4 Stranglegasp).
            ushort chosenHash;
            if (existingAnoints.Count >= maxEnchants)
            {
                var replace = existingAnoints[0];
                chosenHash = replace.Hash;
                mods.RemoveAt(replace.ModIdx);
            }
            else
            {
                var usedHashes = existingAnoints.Select(e => e.Hash).ToHashSet();
                chosenHash = grantedHashes.FirstOrDefault(h => !usedHashes.Contains(h));
                if (chosenHash == 0) chosenHash = grantedHashes[0];
            }

            // Encode PassiveSkillGraphId as signed short inside [-32768, 32767] matching Mods.dat GrantedPassive range.
            int encodedGraphId = unchecked((short)recipe.PassiveGraphId);
            mods.Add(new ItemMod(ModSection.Enchant, chosenHash, [encodedGraphId]));
            foreach (var s in standardIndices) consumedOilIndices.Add(s.Index);
            result = target with { Mods = mods };
            return true;
        }

        if (craftingType == TypeRing)
        {
            if (standardIndices.Count != 2) { reason = "ring anointing requires exactly 2 oils"; return false; }
            var recipe = FindRecipe(TypeRing, [.. standardIndices.Select(x => x.Oil.Row)]);
            if (recipe is null || recipe.ModHash == 0) { reason = "unknown ring anoint recipe"; return false; }

            var ringModHashes = BlightTables.Recipes.Where(r => r.CraftingType == TypeRing && r.ModHash != 0).Select(r => r.ModHash).ToHashSet();
            var mods = target.Mods.ToList();
            var existing = new List<int>();
            for (int m = 0; m < mods.Count; m++)
                if (mods[m].Section == ModSection.Enchant && ringModHashes.Contains(mods[m].Hash))
                    existing.Add(m);

            if (existing.Count >= maxEnchants)
                mods.RemoveAt(existing[0]);

            mods.Add(BuildEnchantMod(recipe.ModHash, catalog.Mods));
            foreach (var s in standardIndices) consumedOilIndices.Add(s.Index);
            result = target with { Mods = mods };
            return true;
        }

        // Type 2: Blighted Map (cap 3) or Blight-ravaged Map (cap 9, max 3 of the same oil). Supports 1..3 oils in one click!
        if (standardIndices.Count is < 1 or > 3)
        {
            reason = "map anointing requires 1 to 3 oils";
            return false;
        }
        var mapModHashes = BlightTables.Recipes.Where(r => r.CraftingType == TypeInfectedMap && r.ModHash != 0).Select(r => r.ModHash).ToHashSet();
        var mapMods = target.Mods.ToList();
        int existingMapAnoints = mapMods.Count(m => m.Section == ModSection.Enchant && mapModHashes.Contains(m.Hash));
        if (existingMapAnoints + standardIndices.Count > maxEnchants)
        {
            reason = $"map already has {existingMapAnoints}/{maxEnchants} oil anointments";
            return false;
        }

        var toAdd = new List<ItemMod>();
        var perModCount = new Dictionary<ushort, int>();
        foreach (var m in mapMods)
            if (m.Section == ModSection.Enchant && mapModHashes.Contains(m.Hash))
                perModCount[m.Hash] = perModCount.GetValueOrDefault(m.Hash) + 1;

        foreach (var (idx, oil) in standardIndices)
        {
            var recipe = FindRecipe(TypeInfectedMap, [oil.Row]);
            if (recipe is null || recipe.ModHash == 0) { reason = $"oil {oil.Name} has no map anoint recipe"; return false; }
            int nextSame = perModCount.GetValueOrDefault(recipe.ModHash) + 1;
            if (isUberMap && nextSame > 3)
            {
                reason = $"Blight-ravaged maps allow at most 3 of the same oil ({oil.Name})";
                return false;
            }
            perModCount[recipe.ModHash] = nextSame;
            toAdd.Add(BuildEnchantMod(recipe.ModHash, catalog.Mods));
            consumedOilIndices.Add(idx);
        }
        mapMods.AddRange(toAdd);
        result = target with { Mods = mapMods };
        return true;
    }

    static ItemMod BuildEnchantMod(ushort modHash, ModCatalog mods)
    {
        if (mods.ByHash(modHash) is not { } row) return new ItemMod(ModSection.Enchant, modHash, [1]);
        int[] values = new int[row.Stats.Count];
        for (int i = 0; i < row.Stats.Count; i++) values[i] = row.Stats[i].Min;
        return new ItemMod(ModSection.Enchant, modHash, values);
    }

    /// <summary>
    /// Oil Extractor (<c>CurrencyExtractOil</c>, <c>Action = "extract_oil"</c>): inspects an anointed Amulet, Ring,
    /// or Blight Unique (corrupted items ARE allowed; mirrored items and maps are refused), and returns
    /// one random Oil used in its recipe. Caller destroys the item and consumes 1 Oil Extractor.
    /// </summary>
    public static bool TryExtractOil(Item target, ItemCatalog catalog, Random rng, out Item? extractedOil, out string reason)
    {
        extractedOil = null;
        reason = "";
        if (!target.Identified) { reason = "cannot extract oil from an unidentified item"; return false; }
        if (target.Mirrored) { reason = "cannot extract oil from a mirrored item"; return false; }
        if (catalog.ByHash(target.TypeHash) is { } targetBt && CurrencyEffects.IsMap(target, targetBt))
        {
            reason = "cannot extract oil from a map";
            return false;
        }

        var grantedHashes = new HashSet<ushort>(BlightTables.GrantedPassiveModHashes);
        var recipeOils = new List<int>();

        foreach (var mod in target.Mods)
        {
            if (mod.Section != ModSection.Enchant) continue;
            if (grantedHashes.Contains(mod.Hash) && mod.Values.Count > 0)
            {
                ushort gid = unchecked((ushort)mod.Values[0]);
                var rec = BlightTables.Recipes.FirstOrDefault(r => r.CraftingType == TypeAmuletOrUnique && r.PassiveGraphId == gid);
                if (rec is not null) recipeOils.AddRange(rec.OilRows);
            }
            else
            {
                var rec = BlightTables.Recipes.FirstOrDefault(r => r.CraftingType == TypeRing && r.ModHash == mod.Hash);
                if (rec is not null) recipeOils.AddRange(rec.OilRows);
            }
        }

        if (recipeOils.Count == 0)
        {
            reason = "target item has no Blight anointments";
            return false;
        }

        int pickedOilRow = recipeOils[rng.Next(recipeOils.Count)];
        var oil = BlightTables.Oils.FirstOrDefault(o => o.Row == pickedOilRow);
        if (oil is null || catalog.Find(oil.Id) is not { Supported: true } bt)
        {
            reason = "extracted oil base type not found";
            return false;
        }
        extractedOil = ItemCatalog.Create(bt) with { ItemLevel = Math.Max((byte)1, target.ItemLevel), StackSize = 1 };
        return true;
    }

    /// <summary>Executes an anoint from Sister Cassia's <c>BlightCraftingWindow</c> (rows 36 &amp; 37) atomically and sends 0x00bf updates.</summary>
    public static async Task<bool> AnointFromCassiaWindow(Player player)
    {
        var items = ItemsOf(player);
        var catalog = ItemsFor(player);
        var targetInv = items.Items(CraftingItemRow).FirstOrDefault();
        var oilInvs = items.Items(CraftingInputRow).OrderBy(i => i.X).ToList();
        if (targetInv is null || oilInvs.Count == 0) return false;

        if (!TryAnoint(targetInv.Item, [.. oilInvs.Select(o => o.Item)], catalog, out var crafted, out var consumedIndices, out string reason)
            || crafted is null)
        {
            Console.WriteLine($"[blight] {player.Connection.Remote} anoint (Cassia window) refused: {reason}");
            return false;
        }

        InventoryItem? updatedTarget = null;
        var removedOils = new List<uint>();
        var shrunkOils = new List<InventoryItem>();
        Saves.Atomically(() =>
        {
            if (items.Remove(CraftingItemRow, targetInv.Id) is null) return;
            updatedTarget = items.Put(CraftingItemRow, targetInv with { Item = crafted });
            foreach (int idx in consumedIndices.Distinct())
            {
                var oil = oilInvs[idx];
                if (oil.Item.StackSize > 1)
                {
                    if (items.SetStackSize(CraftingInputRow, oil.Id, oil.Item.StackSize - 1) is { } shrunk)
                        shrunkOils.Add(shrunk);
                }
                else
                {
                    items.Remove(CraftingInputRow, oil.Id);
                    removedOils.Add(oil.Id);
                }
            }
        });
        if (updatedTarget is null) return false;

        uint itemInvId = PlayerInventories.InventoryId(CraftingItemRow);
        uint oilInvId = PlayerInventories.InventoryId(CraftingInputRow);
        await Send(player, InstanceItems.InventoryChange(itemInvId, items.NextRevision(CraftingItemRow), [targetInv.Id], [updatedTarget]),
            $"0x00bf Blight anoint item {updatedTarget.Id} in Cassia window");
        await Send(player, InstanceItems.InventoryChange(oilInvId, items.NextRevision(CraftingInputRow),
            [.. removedOils, .. shrunkOils.Select(s => s.Id)], shrunkOils),
            $"0x00bf Blight anoint consumed {consumedIndices.Count} oil(s) in Cassia window");
        await BlightCassia.GrantFlags(player, [BlightTables.QuestFlagIds.HaveOwnedOil, BlightTables.QuestFlagIds.AnointingUnlocked]);
        return true;
    }

    /// <summary>Executes an anoint directly inside the Blight Stash Tab (cell 13 + input cells 15..17, 78) atomically.</summary>
    public static async Task<bool> AnointFromBlightStash(Player player, AccountStash stash, int tab)
    {
        if (tab < 0 || tab >= stash.Count || stash.TabType(tab) != StashRules.Blight || stash.IsPublic(tab)) return false;
        if (!ClientHasQuestFlag(player, BlightTables.QuestFlagIds.AnointingUnlocked))
        {
            Console.WriteLine($"[blight] {player.Connection.Remote} Blight stash anoint refused: CassiaOnAnointingOils flag not unlocked yet");
            return false;
        }
        var rules = ItemsFor(player).Stash;
        var tabItems = stash.Inventory(tab).Items;
        var targetInv = tabItems.FirstOrDefault(i => rules.TakesAnything(StashRules.Blight, i.X, i.Y));
        var oilInvs = tabItems.Where(i => rules.IsInputSlot(StashRules.Blight, i.X, i.Y)).OrderBy(i => i.X).ToList();
        if (targetInv is null || oilInvs.Count == 0) return false;

        if (!TryAnoint(targetInv.Item, [.. oilInvs.Select(o => o.Item)], ItemsFor(player), out var crafted, out var consumedIndices, out string reason)
            || crafted is null)
        {
            Console.WriteLine($"[blight] {player.Connection.Remote} anoint (Blight tab {tab}) refused: {reason}");
            return false;
        }

        InventoryItem? placedTarget = null;
        var gone = new List<uint>();
        var now = new List<InventoryItem>();
        Saves.Atomically(() =>
        {
            if (stash.Take(tab, targetInv.Id) is not { } taken) return;
            if (stash.Place(tab, targetInv.X, targetInv.Y, taken with { Item = crafted }, ShapeOf, out _) is not var (placed, _))
                throw new InvalidOperationException("Failed to re-place anointed item in Blight tab");
            placedTarget = placed;
            gone.Add(targetInv.Id);
            now.Add(placed);
            foreach (int idx in consumedIndices.Distinct())
            {
                var oil = oilInvs[idx];
                gone.Add(oil.Id);
                if (oil.Item.StackSize > 1)
                {
                    if (stash.Shrink(tab, oil.Id, 1) is { } shrunk) now.Add(shrunk);
                }
                else
                {
                    stash.Take(tab, oil.Id);
                }
            }
        });
        if (placedTarget is null) return false;

        await SendTabChange(player, stash, tab, gone, now,
            $"0x00bf Blight stash tab {tab}: anointed item {placedTarget.Id}, consumed {consumedIndices.Count} oil(s)");
        return true;
    }

    /// <summary>GameEvents.Packet(0x026b): Anoint button click from Cassia's window or the Blight Stash Tab.</summary>
    public static async Task<bool> HandleAnointPacket(Player player, byte[] bytes)
    {
        if (bytes.Length < InstanceBlight.AnointLength) return false;
        if (player.PlayerLife == 0 || player.Closed || player.LeavingArea) return true;
        // Try Cassia's BlightCraftingWindow first if an item sits in row 37.
        if (ItemsOf(player).Items(CraftingItemRow).Count > 0)
            return await AnointFromCassiaWindow(player);
        // Otherwise check the player's open Blight Stash Tab.
        if (StashOf(player) is { } stash)
        {
            var rules = ItemsFor(player).Stash;
            for (int tab = 0; tab < stash.Count; tab++)
            {
                if (stash.TabType(tab) != StashRules.Blight) continue;
                if (stash.Inventory(tab).Items.Any(i => rules.TakesAnything(StashRules.Blight, i.X, i.Y)))
                    return await AnointFromBlightStash(player, stash, tab);
            }
        }
        return false;
    }

    /// <summary>GameEvents.Packet(0x0071): intercepts Oil Extractor (<c>Action == "extract_oil"</c>) before Items.cs so
    /// corrupted anointed items work cleanly while all other currencies pass through untouched.</summary>
    public static async Task<bool> HandleOilExtractorPacket(Player player, byte[] bytes)
    {
        if (bytes.Length < InstanceItems.UseItemOnLength) return false;
        var use = InstanceItems.ReadUseItemOn(bytes);
        var items = ItemsOf(player);
        var stash = StashOf(player);
        var (usedSpot, used) = Locate(items, stash, use.InventoryId, use.ItemId);
        if (used is null) return false;
        var catalog = ItemsFor(player);
        if (catalog.ByHash(used.Item.TypeHash) is not { } usedType) return false;
        if (usedType.Id != OilExtractorId && catalog.CurrencyAction(usedType) != "extract_oil")
            return false;

        // From here on, this 0x0071 belongs exclusively to Oil Extractor.
        var (targetSpot, target) = Locate(items, stash, use.TargetInventoryId, use.TargetId);
        if (target is null) return true;
        if (usedSpot.Tab is int pubTab && stash.IsPublic(pubTab)) return true;
        if (targetSpot.Tab is int tPubTab && stash.IsPublic(tPubTab)) return true;
        if (Worn(targetSpot)) return true;
        if (!TryExtractOil(target.Item, catalog, AreaRng(player), out var extracted, out string reason) || extracted is null)
        {
            Console.WriteLine($"[blight] {player.Connection.Remote} <= 0x0071 Oil Extractor on item {target.Id} refused: {reason}");
            return true;
        }

        // Pre-verify destination space BEFORE consuming the Oil Extractor or destroying the target item!
        bool useCursor = items.Items(PlayerInventories.Cursor).Count == 0;
        if (!useCursor && !items.Fits(PlayerInventories.MainInventory, extracted, ShapeOf))
        {
            Console.WriteLine($"[blight] {player.Connection.Remote} <= 0x0071 Oil Extractor refused: no inventory space for extracted oil");
            return true;
        }

        // Atomically consume 1 Oil Extractor, destroy target item, and place extracted Oil on cursor (or main inventory).
        InventoryItem? leftExtractor = null;
        var destRemoved = new List<uint>();
        var destAdded = new List<InventoryItem>();
        byte oilDestRow = useCursor ? PlayerInventories.Cursor : PlayerInventories.MainInventory;
        Saves.Atomically(() =>
        {
            // Place oil first so if a concurrent race filled the inventory we abort before mutating currency/target.
            if (useCursor && items.Items(PlayerInventories.Cursor).Count == 0)
            {
                oilDestRow = PlayerInventories.Cursor;
                destAdded.Add(items.Put(PlayerInventories.Cursor, new InventoryItem(0, 0, 0, extracted)));
            }
            else
            {
                oilDestRow = PlayerInventories.MainInventory;
                var stacked = items.AddStacked(PlayerInventories.MainInventory, extracted, ShapeOf);
                if (!stacked.Any || stacked.Left > 0) return;
                foreach (var ch in stacked.Changed) { destRemoved.Add(ch.Id); destAdded.Add(ch); }
                destAdded.AddRange(stacked.Added);
            }

            // Consume 1 Oil Extractor.
            if (usedSpot.Tab is int uTab)
            {
                if (used.Item.StackSize > 1) leftExtractor = stash.Shrink(uTab, used.Id, 1);
                else stash.Take(uTab, used.Id);
            }
            else
            {
                if (used.Item.StackSize > 1) leftExtractor = items.SetStackSize(usedSpot.Row, used.Id, used.Item.StackSize - 1);
                else items.Remove(usedSpot.Row, used.Id);
            }

            // Destroy target item.
            if (targetSpot.Tab is int tTab) stash.Take(tTab, target.Id);
            else items.Remove(targetSpot.Row, target.Id);
        });
        if (destAdded.Count == 0) return true;

        // Send 0x00bf updates for currency spot, destroyed target spot, and destination row.
        if (usedSpot.Tab is int ut)
            await SendTabChange(player, stash, ut, [used.Id], leftExtractor is { } le ? [le] : [], "0x00bf Oil Extractor used");
        else
            await Send(player, InstanceItems.InventoryChange(PlayerInventories.InventoryId(usedSpot.Row), items.NextRevision(usedSpot.Row),
                [used.Id], leftExtractor is { } le ? [le] : []), "0x00bf Oil Extractor used");

        if (targetSpot.Tab is int tt)
            await SendTabChange(player, stash, tt, [target.Id], [], "0x00bf Oil Extractor destroyed target");
        else
            await Send(player, InstanceItems.InventoryChange(PlayerInventories.InventoryId(targetSpot.Row), items.NextRevision(targetSpot.Row),
                [target.Id], []), "0x00bf Oil Extractor destroyed target");

        await Send(player, InstanceItems.InventoryChange(PlayerInventories.InventoryId(oilDestRow), items.NextRevision(oilDestRow),
            destRemoved, destAdded), $"0x00bf Oil Extractor extracted {catalog.ByHash(extracted.TypeHash)?.Name ?? "Oil"}");
        await BlightCassia.GrantFlags(player, [BlightTables.QuestFlagIds.HaveOwnedOil]);
        return true;
    }

    /// <summary>GameEvents.InventoryPlacementRefusal: validates items placed into Cassia's BlightCraftingInput (36) &amp; BlightCraftingItem (37).</summary>
    public static string? ValidateCraftingPlacement(InventoryPlacement placement)
    {
        var catalog = ItemsFor(placement.Player);
        if (placement.Row == CraftingInputRow)
        {
            if (catalog.ByHash(placement.Item.TypeHash) is not { } bt || !BlightTables.OilByItemId.ContainsKey(bt.Id))
                return "Only Blight Oils can be placed in anoint input slots";
            return null;
        }
        if (placement.Row == CraftingItemRow)
        {
            if (!TryClassifyTarget(placement.Item, catalog, out _, out _, out _))
                return "Only Amulets, Rings, Blighted Maps, or anointable Uniques can be placed in the anoint slot";
            return null;
        }
        return null;
    }

    /// <summary>Creates a randomly anointed Rare Ring or Amulet (for <c>map_blight_bosses_drop_X_anointed_items</c>).</summary>
    public static Item? RollRandomAnointedJewellery(Player player, int level, Random rng)
    {
        var catalog = ItemsFor(player);
        var rolls = RollsFor(player);
        bool amulet = rng.Next(2) == 0;
        var bases = catalog.All.Where(t => t.Supported && t.Class == (amulet ? "Amulet" : "Ring") && t.DropLevel <= level && ItemDrops.Droppable(t)).ToList();
        if (bases.Count == 0) return null;
        var baseItem = rolls.Roll(ItemCatalog.Create(bases[rng.Next(bases.Count)]) with { ItemLevel = (byte)Math.Clamp(level, 1, 100) }, ItemRarity.Rare, rng);
        var pool = BlightTables.Recipes.Where(r => r.CraftingType == (amulet ? TypeAmuletOrUnique : TypeRing)
            && (amulet ? r.PassiveGraphId != 0 : r.ModHash != 0)).ToList();
        if (pool.Count == 0) return baseItem;
        var pick = pool[rng.Next(pool.Count)];
        var oilItems = new List<Item>();
        foreach (int oilRow in pick.OilRows)
        {
            var oil = BlightTables.Oils.FirstOrDefault(o => o.Row == oilRow);
            if (oil is null || catalog.Find(oil.Id) is not { Supported: true } obt) return baseItem;
            oilItems.Add(ItemCatalog.Create(obt));
        }
        return TryAnoint(baseItem, oilItems, catalog, out var anointed, out _, out _) && anointed is not null ? anointed : baseItem;
    }
}
