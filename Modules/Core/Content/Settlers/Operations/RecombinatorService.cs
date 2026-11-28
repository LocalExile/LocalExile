using System.Collections.Concurrent;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Modules.Settlers.Operations;

/// <summary>
/// Full 3.29.3c Dual-Mode Recombinator (Predictable &amp; Unpredictable modes) backed by <c>RecombinatorInput</c>
/// (inventory row 92, 8x4: left half x=0..3, right half x=4..7) and <c>RecombinatorOutput</c> (inventory row 93, 4x4).
/// <para>
/// Implements exact wire codecs and cost formulas verified in <c>Client/Game.exe</c> (<c>0x141d6c8f0</c>, <c>0x141d6d700</c>,
/// <c>0x141d6e21b</c>):
/// <list type="bullet">
/// <item>Predictable mode (<c>wire mode == 0</c>, requires selected mods): <c>gold = RecombGoldBase[avgIlvl]</c>,
/// <c>dust = (int)(pow(sum(selected.__col5), 0.75) * 3)</c>.</item>
/// <item>Unpredictable mode (<c>wire mode == 1</c>, forbids selected mods): <c>modScale = (allExplicitCount + 10) * 10</c>,
/// <c>gold = (modScale * RecombGoldBase[avgIlvl]) / 100</c>, <c>dust = (modScale * RecombDustBase[avgModLvl]) / 100</c>.</item>
/// <item>Idol discount (<c>AtlasRelic</c>): <c>gold &gt;&gt;= 1</c>, <c>dust &gt;&gt;= 2</c>.</item>
/// </list>
/// </para>
/// </summary>
public sealed class RecombinatorService(IOperationsHost host)
{
    const int LeftSlotX = 0;
    const int RightSlotX = 4;

    readonly ConcurrentDictionary<Player, Lock> _playerGates = new();

    // PoE 3.25/3.29 Unpredictable recombinator mod count probability weights per pool size N (0..6).
    static readonly double[][] UnpredictableCountProbs =
    [
        [1.00, 0.00, 0.00, 0.00], // N=0
        [0.41, 0.59, 0.00, 0.00], // N=1: 59% 1 mod
        [0.00, 0.67, 0.33, 0.00], // N=2: 67% 1 mod, 33% 2 mods
        [0.00, 0.39, 0.51, 0.10], // N=3: 39% 1, 51% 2, 10% 3
        [0.00, 0.11, 0.58, 0.31], // N=4: 11% 1, 58% 2, 31% 3
        [0.00, 0.00, 0.43, 0.57], // N=5: 43% 2, 57% 3
        [0.00, 0.00, 0.28, 0.72], // N=6: 28% 2, 72% 3
    ];

    public void ClearPlayerState(Player player) => _playerGates.TryRemove(player, out _);

    Lock GateFor(Player player) => _playerGates.GetOrAdd(player, static _ => new Lock());

    public string? ValidateInputPlacement(Player player, Item item)
    {
        OperationsData.EnsureLoaded();
        var catalog = ItemsFor(player);
        var bt = catalog.ByHash(item.TypeHash);
        if (bt is null || !OperationsData.IsRecombinableClass(bt.Class))
            return "this item class cannot be placed in the Recombinator";
        if (item.Corrupted) return "corrupted items cannot be recombined";
        if (item.Mirrored) return "mirrored items cannot be recombined";
        if (!item.Identified) return "unidentified items cannot be recombined";
        if (item.Rarity == ItemRarity.Unique) return "unique items cannot be recombined";
        foreach (var s in item.Sockets)
            if (s.Gem is not null) return "remove socketed gems before recombining";
        return null;
    }

    public string? ValidateInputPlacementAt(Player player, Item item, InventoryItem? replaced, byte x, byte y)
    {
        var bt = ItemsFor(player).ByHash(item.TypeHash);
        if (bt is null) return "unknown item";
        bool leftHalf = x < RightSlotX;
        int maxRight = leftHalf ? RightSlotX : 8;
        if (x + bt.Width > maxRight || y + bt.Height > 4)
            return "item does not fit within one Recombinator input half";

        var existing = ItemsOf(player).Items(PlayerInventories.RecombinatorInput);
        foreach (var other in existing)
        {
            if (replaced is not null && other.Id == replaced.Id) continue;
            bool otherLeft = other.X < RightSlotX;
            if (otherLeft == leftHalf)
                return "only one item may be placed in each Recombinator input half";
        }
        return null;
    }

    public async Task OnInputInventoryChangedAsync(Player player)
    {
        if (GetInputPair(player) is null)
            await Send(player, InstanceVillage.RecombinationChance(0, mode: 0), "0x0293 recombinator chance 0 (incomplete inputs)");
    }

    /// <summary>
    /// Exact cost formula verified at <c>0x141d6d700</c> + Idol discount at <c>0x141d6e21b</c>:
    /// Predictable (<c>mode == 0</c>) uses <c>pow(sum(selected.__col5), 0.75) * 3</c>;
    /// Unpredictable (<c>mode == 1</c>) uses <c>modScale = (allExplicitCount + 10) * 10</c>.
    /// </summary>
    public static (uint GoldCost, uint DustCost) ComputeCosts(
        Item item0,
        Item item1,
        IReadOnlyList<ModCatalog.Mod> selectedMods,
        bool predictableMode,
        ItemCatalog catalog)
    {
        OperationsData.EnsureLoaded();
        int avgIlvl = Math.Clamp((item0.ItemLevel + item1.ItemLevel) / 2, 1, 100);
        var ilvlBalance = OperationsData.BalanceForLevel(avgIlvl);

        long goldCost;
        long dustCost;

        if (predictableMode)
        {
            // Wire mode == 0 (Predictable): gold = __col4[avgIlvl], dust = floor(pow(sum(selected.__col5), 0.75) * 3).
            long rawDustSum = 0;
            foreach (var def in selectedMods)
                rawDustSum += OperationsData.BalanceForLevel(Math.Clamp(def.Level, 1, 100)).RecombDustBase;

            goldCost = ilvlBalance.RecombGoldBase;
            double exp = OperationsData.RecombinatorModDustCostExponentTimes100 / 100.0;
            double mult = OperationsData.RecombinatorModDustCostMultiplier;
            dustCost = rawDustSum > 0 ? (long)(Math.Pow(rawDustSum, exp) * mult) : 0;
        }
        else
        {
            // Wire mode == 1 (Unpredictable): modScale = (allExplicitCount + 10) * 10 over ALL explicit mods on both items.
            int modCount = 0;
            long levelSum = 0;
            foreach (var mod in item0.Mods)
            {
                if (mod.Section != ModSection.Explicit) continue;
                modCount++;
                int lvl = catalog.Mods.ByHash(mod.Hash)?.Level ?? item0.ItemLevel;
                levelSum += Math.Clamp(lvl, 1, 100);
            }
            foreach (var mod in item1.Mods)
            {
                if (mod.Section != ModSection.Explicit) continue;
                modCount++;
                int lvl = catalog.Mods.ByHash(mod.Hash)?.Level ?? item1.ItemLevel;
                levelSum += Math.Clamp(lvl, 1, 100);
            }

            int avgModLvl = modCount > 0 ? Math.Clamp((int)(levelSum / modCount), 1, 100) : 1;
            long modScale = (modCount + 10L) * 10L;
            goldCost = (modScale * ilvlBalance.RecombGoldBase) / 100L;
            dustCost = (modScale * OperationsData.BalanceForLevel(avgModLvl).RecombDustBase) / 100L;
        }

        // Idol discount (ItemClass == AtlasRelic, row 94, 0x141d6e21b): gold >>= 1, dust >>= 2.
        var bt0 = catalog.ByHash(item0.TypeHash);
        if (bt0 is { Class: "AtlasRelic" })
        {
            goldCost >>= 1;
            dustCost >>= 2;
        }

        return ((uint)Math.Clamp(goldCost, 0, uint.MaxValue), (uint)Math.Clamp(dustCost, 0, uint.MaxValue));
    }

    /// <summary>Computes Predictable Mode success chance in basis points (0..10000 = 0.00%..100.00%).</summary>
    public static uint ComputePredictableChanceBps(
        Item item0,
        Item item1,
        IReadOnlyList<(ItemMod Mod, ModCatalog.Mod Def)> selected,
        ItemCatalog catalog)
    {
        var bt0 = catalog.ByHash(item0.TypeHash);
        var bt1 = catalog.ByHash(item1.TypeHash);
        if (bt0 is null || bt1 is null || !AreClassesCompatible(bt0.Class, bt1.Class)) return 0;
        if (selected.Count == 0) return 0;

        int prefixes = 0, suffixes = 0;
        var seenFamilies = new HashSet<int>();
        double combinedWeightFactor = 1.0;

        foreach (var (_, def) in selected)
        {
            if (def.GenerationType == 1) prefixes++;
            else if (def.GenerationType == 2) suffixes++;

            foreach (int fam in def.Families)
                if (!seenFamilies.Add(fam)) return 0;

            int w0 = def.WeightFor(bt0.Tags);
            int w1 = def.WeightFor(bt1.Tags);
            int avgWeight = Math.Max(1, (w0 + w1) / 2);
            double weightFactor = Math.Clamp(0.45 + 0.14 * Math.Log10(avgWeight), 0.35, 0.92);
            double tierPenalty = Math.Clamp(1.0 - (def.Level / 180.0), 0.55, 0.98);
            combinedWeightFactor *= weightFactor * tierPenalty;
        }

        int maxAffixes = bt0.Class.Contains("Jewel", StringComparison.OrdinalIgnoreCase) ? 2 : 3;
        if (prefixes > maxAffixes || suffixes > maxAffixes) return 0;

        double baseBonus = item0.TypeHash == item1.TypeHash ? 1.12 : 1.0;
        double rawChance = Math.Pow(combinedWeightFactor, 0.72) * baseBonus;
        return (uint)Math.Clamp(Math.Round(rawChance * 10000.0), 100, 9900);
    }

    /// <summary>C→S <c>0x010b</c>: Ctrl+click quick-move into/out of Recombinator inventories 92/93.</summary>
    public async Task<bool> HandleQuickMoveAsync(Player player, byte[] message)
    {
        InstanceVillage.RecombinatorQuickMove move;
        try { move = InstanceVillage.ReadRecombinatorQuickMove(message); }
        catch (InvalidDataException) { return false; }

        var inv = ItemsOf(player);
        var catalog = ItemsFor(player);

        if (move.IntoRecombinator)
        {
            byte srcRow;
            InventoryItem? removed;
            InventoryItem placed;

            lock (GateFor(player))
            {
                srcRow = move.SourceFlag != 0 && ExpandedInventoryGranted(player)
                    ? PlayerInventories.ExpandedInventory
                    : PlayerInventories.MainInventory;
                var srcItem = inv.Items(srcRow).FirstOrDefault(i => i.Id == move.ItemId)
                    ?? inv.Items(PlayerInventories.MainInventory).FirstOrDefault(i => i.Id == move.ItemId);
                if (srcItem is null) return true;
                if (srcRow != PlayerInventories.MainInventory && inv.Items(srcRow).All(i => i.Id != srcItem.Id))
                    srcRow = PlayerInventories.MainInventory;

                if (ValidateInputPlacement(player, srcItem.Item) is { } err)
                {
                    _ = player.Commands?.Reply(err);
                    return true;
                }

                var existingInputs = inv.Items(PlayerInventories.RecombinatorInput);
                bool leftOccupied = existingInputs.Any(i => i.X < RightSlotX);
                bool rightOccupied = existingInputs.Any(i => i.X >= RightSlotX || (i.X + (catalog.ByHash(i.Item.TypeHash)?.Width ?? 1)) > RightSlotX);
                byte dstX;
                if (!leftOccupied) dstX = LeftSlotX;
                else if (!rightOccupied) dstX = RightSlotX;
                else return true;

                var bt = catalog.ByHash(srcItem.Item.TypeHash);
                if (bt is null || bt.Width > 4 || bt.Height > 4) return true;

                removed = inv.Remove(srcRow, srcItem.Id);
                if (removed is null) return true;
                placed = inv.Put(PlayerInventories.RecombinatorInput, removed with { X = dstX, Y = 0 });
            }

            await Send(player, InstanceItems.InventoryChange(PlayerInventories.InventoryId(srcRow),
                inv.NextRevision(srcRow), [removed.Id], []),
                $"0x00bf recombinator quick-move out of row {srcRow}");
            await Send(player, InstanceItems.InventoryChange(PlayerInventories.InventoryId(PlayerInventories.RecombinatorInput),
                inv.NextRevision(PlayerInventories.RecombinatorInput), [], [placed]),
                $"0x00bf recombinator quick-move into input ({placed.X}, 0)");
            return true;
        }
        else
        {
            byte srcRow = move.IsOutputInventory ? PlayerInventories.RecombinatorOutput : PlayerInventories.RecombinatorInput;
            InventoryItem? removed;
            IReadOnlyDictionary<byte, PlayerInventories.Stacking> stacked;

            lock (GateFor(player))
            {
                var srcItem = inv.Items(srcRow).FirstOrDefault(i => i.Id == move.ItemId);
                if (srcItem is null) return true;

                var targetRows = ExpandedInventoryGranted(player)
                    ? new[] { PlayerInventories.MainInventory, PlayerInventories.ExpandedInventory }
                    : new[] { PlayerInventories.MainInventory };

                var preview = inv.StackAcross(targetRows, srcItem.Item, ShapeOf, preview: true);
                if (preview.Values.Min(s => s.Left) > 0) return true;

                removed = inv.Remove(srcRow, srcItem.Id);
                if (removed is null) return true;
                stacked = inv.StackAcross(targetRows, removed.Item, ShapeOf, preview: false);
            }

            await Send(player, InstanceItems.InventoryChange(PlayerInventories.InventoryId(srcRow),
                inv.NextRevision(srcRow), [removed.Id], []),
                $"0x00bf recombinator quick-move out of row {srcRow}");
            foreach (var (dstRow, st) in stacked)
            {
                if (!st.Any) continue;
                await Send(player, InstanceItems.InventoryChange(PlayerInventories.InventoryId(dstRow),
                    inv.NextRevision(dstRow), st.RemovedIds, st.AddedItems),
                    $"0x00bf recombinator quick-move into row {dstRow}");
            }
            if (srcRow == PlayerInventories.RecombinatorInput)
                await OnInputInventoryChangedAsync(player);
            return true;
        }
    }

    /// <summary>C→S <c>0x028f</c>: preview chance (<c>Execute=false</c> → <c>S→C 0x0293</c>) or craft (<c>Execute=true</c> → <c>S→C 0x0291</c>).</summary>
    public async Task<bool> HandleRecombinateAsync(Player player, byte[] message)
    {
        InstanceVillage.RecombinateRequest req;
        try { req = InstanceVillage.ReadRecombinate(message); }
        catch (InvalidDataException) { return false; }

        OperationsData.EnsureLoaded();
        var catalog = ItemsFor(player);
        // Wire mode byte: 0 = Predictable, 1 = Unpredictable (verified at 0x141d6c8f0 / 0x141d6d700).
        byte wireMode = (byte)(req.PredictableMode ? 0 : 1);

        if (!req.Execute)
        {
            var previewPair = GetInputPair(player);
            uint previewBps = 0;
            if (previewPair is var (p0, p1))
            {
                var pSelected = CollectSelectedMods(p0.Item, req.Slot0SelectedModIndices, p1.Item, req.Slot1SelectedModIndices, catalog);
                previewBps = req.PredictableMode
                    ? ComputePredictableChanceBps(p0.Item, p1.Item, pSelected, catalog)
                    : 10_000u;
            }
            await Send(player, InstanceVillage.RecombinationChance(previewBps, wireMode),
                $"0x0293 recombinator chance {previewBps / 100.0:0.00}% (mode={wireMode})");
            return true;
        }

        // Execute path: serialize per-player and atomically claim/remove BOTH inputs BEFORE spending Dust/Gold so
        // concurrent moves cannot burn currency for nothing or duplicate inputs.
        var inv = ItemsOf(player);
        var (account, league) = VillageStore.ScopeOf(player);
        InventoryItem? removed0 = null, removed1 = null;
        List<(ItemMod Mod, ModCatalog.Mod Def)> selected;
        uint chanceBps, goldCost, dustCost;
        uint? goldLeftAfter = null;
        ushort errorCode = 0;

        lock (GateFor(player))
        {
            var pair = GetInputPair(player);
            if (pair is not var (slot0, slot1)) { errorCode = 1; selected = []; chanceBps = 0; goldCost = 0; dustCost = 0; }
            else if (inv.Items(PlayerInventories.RecombinatorOutput).Count > 0) { errorCode = 2; selected = []; chanceBps = 0; goldCost = 0; dustCost = 0; }
            else
            {
                var bt0 = catalog.ByHash(slot0.Item.TypeHash);
                var bt1 = catalog.ByHash(slot1.Item.TypeHash);
                selected = CollectSelectedMods(slot0.Item, req.Slot0SelectedModIndices, slot1.Item, req.Slot1SelectedModIndices, catalog);
                chanceBps = req.PredictableMode
                    ? ComputePredictableChanceBps(slot0.Item, slot1.Item, selected, catalog)
                    : 10_000u;
                (goldCost, dustCost) = ComputeCosts(slot0.Item, slot1.Item, [.. selected.Select(s => s.Def)], req.PredictableMode, catalog);

                if (bt0 is null || bt1 is null || !AreClassesCompatible(bt0.Class, bt1.Class)
                    || ValidateInputPlacement(player, slot0.Item) is not null
                    || ValidateInputPlacement(player, slot1.Item) is not null)
                {
                    errorCode = 3;
                }
                else if (req.PredictableMode && chanceBps == 0)
                {
                    errorCode = 4;
                }
                else if (!req.PredictableMode && (req.Slot0SelectedModIndices.Length > 0 || req.Slot1SelectedModIndices.Length > 0))
                {
                    errorCode = 4;
                }
                else if (PersonalStashOf(player).Gold < goldCost)
                {
                    errorCode = 5;
                }
                else if (host.GetResource(account, league, OperationsData.MagicDustResourceRow) < dustCost)
                {
                    errorCode = 6;
                }
                else
                {
                    // Claim both input items FIRST; if either disappeared concurrently, restore the other and abort cleanly.
                    removed0 = inv.Remove(PlayerInventories.RecombinatorInput, slot0.Id);
                    removed1 = inv.Remove(PlayerInventories.RecombinatorInput, slot1.Id);
                    if (removed0 is null || removed1 is null)
                    {
                        if (removed0 is not null) inv.Put(PlayerInventories.RecombinatorInput, removed0);
                        if (removed1 is not null) inv.Put(PlayerInventories.RecombinatorInput, removed1);
                        removed0 = null;
                        removed1 = null;
                        errorCode = 1;
                    }
                    else
                    {
                        // Spend Dust, then Gold; rollback inputs + dust if either fails.
                        if (dustCost > 0 && !host.TrySpendResources(account, league, [(OperationsData.MagicDustResourceRow, dustCost)]))
                        {
                            inv.Put(PlayerInventories.RecombinatorInput, removed0);
                            inv.Put(PlayerInventories.RecombinatorInput, removed1);
                            removed0 = null;
                            removed1 = null;
                            errorCode = 6;
                        }
                        else if (goldCost > 0)
                        {
                            if (PersonalStashOf(player).TrySpendGold(goldCost) is not uint left)
                            {
                                if (dustCost > 0) host.AddResource(account, league, OperationsData.MagicDustResourceRow, dustCost);
                                inv.Put(PlayerInventories.RecombinatorInput, removed0);
                                inv.Put(PlayerInventories.RecombinatorInput, removed1);
                                removed0 = null;
                                removed1 = null;
                                errorCode = 5;
                            }
                            else
                            {
                                goldLeftAfter = left;
                            }
                        }
                    }
                }
            }
        }

        if (errorCode != 0 || removed0 is null || removed1 is null)
        {
            await Send(player, InstanceVillage.RecombinationResult(false, errorOrStatusCode: errorCode == 0 ? (ushort)1 : errorCode),
                $"0x0291 recombinator error code={errorCode}");
            return true;
        }

        if (goldLeftAfter is uint gl)
            await Send(player, InstanceEntities.Gold(gl), $"0x02aa gold: {gl} (recombinator -{goldCost})");

        await Send(player, InstanceItems.InventoryChange(
            PlayerInventories.InventoryId(PlayerInventories.RecombinatorInput),
            inv.NextRevision(PlayerInventories.RecombinatorInput),
            [removed0.Id, removed1.Id], []),
            $"0x00bf recombinator consumed inputs {removed0.Id}, {removed1.Id}");

        var rng = Random.Shared;
        bool succeeded;
        Item? resultItem = null;

        if (req.PredictableMode)
        {
            succeeded = rng.Next(10_000) < chanceBps;
            if (succeeded)
                resultItem = BuildPredictableResult(removed0.Item, removed1.Item, selected, catalog, RollsFor(player), rng);
        }
        else
        {
            succeeded = true;
            resultItem = BuildUnpredictableResult(removed0.Item, removed1.Item, catalog, RollsFor(player), rng);
        }

        if (succeeded && resultItem is not null)
        {
            var placed = inv.Add(PlayerInventories.RecombinatorOutput, 0, 0, resultItem);
            await Send(player, InstanceItems.InventoryChange(
                PlayerInventories.InventoryId(PlayerInventories.RecombinatorOutput),
                inv.NextRevision(PlayerInventories.RecombinatorOutput),
                [], [placed]),
                $"0x00bf recombinator output item {placed.Id}");
        }

        await Send(player, InstanceVillage.RecombinationResult(succeeded, errorOrStatusCode: 0),
            $"0x0291 recombinator result (mode={wireMode}, success={succeeded}, chance={chanceBps / 100.0:0.00}%)");

        if (dustCost > 0 && OperationsSubsystem.IsVillageArea(player))
            await host.SyncFullVillageStateAsync(player);

        return true;
    }

    (InventoryItem Slot0, InventoryItem Slot1)? GetInputPair(Player player)
    {
        var items = ItemsOf(player).Items(PlayerInventories.RecombinatorInput);
        var left = items.FirstOrDefault(i => i.X < RightSlotX);
        var right = items.FirstOrDefault(i => i.X >= RightSlotX);
        return left is not null && right is not null ? (left, right) : null;
    }

    static List<(ItemMod Mod, ModCatalog.Mod Def)> CollectSelectedMods(
        Item item0,
        byte[] idx0,
        Item item1,
        byte[] idx1,
        ItemCatalog catalog)
    {
        var result = new List<(ItemMod, ModCatalog.Mod)>();
        var exp0 = item0.Mods.Where(m => m.Section == ModSection.Explicit).ToList();
        var exp1 = item1.Mods.Where(m => m.Section == ModSection.Explicit).ToList();

        var seen0 = new HashSet<byte>();
        foreach (byte i in idx0)
            if (seen0.Add(i) && i < exp0.Count && catalog.Mods.ByHash(exp0[i].Hash) is { } def)
                result.Add((exp0[i], def));
        var seen1 = new HashSet<byte>();
        foreach (byte i in idx1)
            if (seen1.Add(i) && i < exp1.Count && catalog.Mods.ByHash(exp1[i].Hash) is { } def)
                result.Add((exp1[i], def));

        return result;
    }

    static Item BuildPredictableResult(
        Item item0,
        Item item1,
        IReadOnlyList<(ItemMod Mod, ModCatalog.Mod Def)> selected,
        ItemCatalog catalog,
        ItemRolls rolls,
        Random rng)
    {
        Item chosenBase = rng.Next(2) == 0 ? item0 : item1;
        byte outIlvl = ComputeOutputItemLevel(item0.ItemLevel, item1.ItemLevel);
        var baseType = catalog.ByHash(chosenBase.TypeHash)!;

        var preservedImplicits = chosenBase.Mods.Where(m => m.Section is ModSection.Implicit or ModSection.Enchant).ToList();
        var explicitMods = selected.Select(s => s.Mod with { Section = ModSection.Explicit }).ToList();
        return AssembleResultItem(chosenBase, baseType, outIlvl, preservedImplicits, explicitMods, catalog, rolls, rng);
    }

    static Item BuildUnpredictableResult(
        Item item0,
        Item item1,
        ItemCatalog catalog,
        ItemRolls rolls,
        Random rng)
    {
        Item chosenBase = rng.Next(2) == 0 ? item0 : item1;
        byte outIlvl = ComputeOutputItemLevel(item0.ItemLevel, item1.ItemLevel);
        var baseType = catalog.ByHash(chosenBase.TypeHash)!;

        var prefixPool = new List<(ItemMod Mod, ModCatalog.Mod Def)>();
        var suffixPool = new List<(ItemMod Mod, ModCatalog.Mod Def)>();

        HashSet<uint>? influenceLegalHashes = chosenBase.Influences != ItemInfluence.None
            ? rolls.Pool(baseType, outIlvl, chosenBase.Influences).Select(p => p.Mod.Hash).ToHashSet()
            : null;
        int maxAffixes = baseType.Class.Contains("Jewel", StringComparison.OrdinalIgnoreCase) ? 2 : 3;

        void AddPool(Item src)
        {
            foreach (var m in src.Mods)
            {
                if (m.Section != ModSection.Explicit) continue;
                if (catalog.Mods.ByHash(m.Hash) is not { } def) continue;
                bool legalByWeight = def.WeightFor(baseType.Tags) > 0 || (influenceLegalHashes?.Contains(m.Hash) == true);
                if (def.Domain == baseType.ModDomain && !legalByWeight && !def.EssenceOnly && def.Domain != 9)
                    continue;
                if (def.GenerationType == 1) prefixPool.Add((m, def));
                else if (def.GenerationType == 2) suffixPool.Add((m, def));
            }
        }
        AddPool(item0);
        AddPool(item1);

        var chosenExplicits = new List<ItemMod>();
        var usedFamilies = new HashSet<int>();
        bool hasExclusive = false;

        bool prefixesFirst = rng.Next(2) == 0;
        if (prefixesFirst)
        {
            PickAffixes(prefixPool, chosenExplicits, usedFamilies, ref hasExclusive, maxAffixes, rng);
            PickAffixes(suffixPool, chosenExplicits, usedFamilies, ref hasExclusive, maxAffixes, rng);
        }
        else
        {
            PickAffixes(suffixPool, chosenExplicits, usedFamilies, ref hasExclusive, maxAffixes, rng);
            PickAffixes(prefixPool, chosenExplicits, usedFamilies, ref hasExclusive, maxAffixes, rng);
        }

        var preservedImplicits = chosenBase.Mods.Where(m => m.Section is ModSection.Implicit or ModSection.Enchant).ToList();
        return AssembleResultItem(chosenBase, baseType, outIlvl, preservedImplicits, chosenExplicits, catalog, rolls, rng);
    }

    static void PickAffixes(
        List<(ItemMod Mod, ModCatalog.Mod Def)> pool,
        List<ItemMod> output,
        HashSet<int> usedFamilies,
        ref bool hasExclusive,
        int maxAffixes,
        Random rng)
    {
        if (pool.Count == 0) return;
        int n = Math.Clamp(pool.Count, 0, UnpredictableCountProbs.Length - 1);
        double roll = rng.NextDouble();
        double acc = 0;
        int targetCount = 0;
        var probs = UnpredictableCountProbs[n];
        for (int k = 0; k < probs.Length; k++)
        {
            acc += probs[k];
            if (roll <= acc) { targetCount = k; break; }
        }
        targetCount = Math.Min(targetCount, maxAffixes);
        if (targetCount <= 0) return;

        for (int i = pool.Count - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (pool[i], pool[j]) = (pool[j], pool[i]);
        }

        int picked = 0;
        foreach (var (mod, def) in pool)
        {
            if (picked >= targetCount) break;
            bool isExclusive = def.EssenceOnly || def.Domain == 9;
            if (isExclusive && hasExclusive) continue;

            bool familyConflict = false;
            foreach (int fam in def.Families)
                if (usedFamilies.Contains(fam)) { familyConflict = true; break; }
            if (familyConflict) continue;

            foreach (int fam in def.Families) usedFamilies.Add(fam);
            if (isExclusive) hasExclusive = true;
            output.Add(mod with { Section = ModSection.Explicit });
            picked++;
        }
    }

    static Item AssembleResultItem(
        Item chosenBase,
        ItemCatalog.BaseType baseType,
        byte outIlvl,
        List<ItemMod> implicits,
        List<ItemMod> explicits,
        ItemCatalog catalog,
        ItemRolls rolls,
        Random rng)
    {
        var allMods = new List<ItemMod>(implicits.Count + explicits.Count);
        allMods.AddRange(implicits);
        allMods.AddRange(explicits);

        int prefixCount = 0, suffixCount = 0;
        foreach (var m in explicits)
        {
            if (catalog.Mods.ByHash(m.Hash) is { GenerationType: 1 }) prefixCount++;
            else suffixCount++;
        }

        ItemRarity rarity = explicits.Count == 0 ? ItemRarity.Normal
            : prefixCount <= 1 && suffixCount <= 1 && explicits.Count <= 2 ? ItemRarity.Magic
            : ItemRarity.Rare;

        IReadOnlyList<uint> nameWords = [];
        if (rarity == ItemRarity.Rare)
        {
            var w1 = rolls.Words.Where(w => w.List == 1).ToList();
            var w2 = rolls.Words.Where(w => w.List == 2).ToList();
            if (w1.Count > 0 && w2.Count > 0)
                nameWords = [w1[rng.Next(w1.Count)].Key, w2[rng.Next(w2.Count)].Key];
            else
                nameWords = chosenBase.NameWords;
        }

        return chosenBase with
        {
            ItemLevel = outIlvl,
            Rarity = rarity,
            Identified = true,
            FracturedCount = 0,
            Foreseeing = false,
            ForeseeSeed = 0,
            Note = null,
            SkinTransferSource = null,
            Mods = allMods,
            NameWords = nameWords,
            CustomName = null,
        };
    }

    static byte ComputeOutputItemLevel(byte ilvl0, byte ilvl1)
    {
        int max = Math.Max(ilvl0, ilvl1);
        int avgPlus2 = ((ilvl0 + ilvl1) / 2) + 2;
        return (byte)Math.Clamp(Math.Min(max, avgPlus2), 1, 100);
    }

    static bool AreClassesCompatible(string class0, string class1)
    {
        if (string.Equals(class0, class1, StringComparison.Ordinal)) return true;
        return NormalizeClassGroup(class0) == NormalizeClassGroup(class1);
    }

    static string NormalizeClassGroup(string c) => c switch
    {
        "Thrusting One Hand Sword" => "One Hand Sword",
        "Rune Dagger" => "Dagger",
        "Warstaff" => "Staff",
        _ => c,
    };
}
