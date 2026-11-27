using GameServer.Core.Protocol.Instance;
using GameServer.Modules.Blight;

namespace GameServer.Gateway.Tests;

/// <summary>
/// Full Blight Oil Anointing (all 576 recipes, Amulets, Rings, Blighted &amp; Blight-ravaged maps, Stranglegasp 4x,
/// Cowls 2x, Prismatic Oil, Tainted Oil on corrupted, Reflective Oil on mirrored), passive allocation in Progression.cs
/// (with zero-allocation hot-path guarantee &amp; no double-counting), Oil Extractor, Cassia window, and Blight Unique spores.
/// </summary>
public sealed class BlightAnointingAndSporesTests
{
    static Item MakeOil(ItemCatalog catalog, int oilRow)
    {
        var oil = BlightTables.Oils.First(o => o.Row == oilRow);
        var bt = catalog.Find(oil.Id)!;
        return ItemCatalog.Create(bt) with { ItemLevel = 68, StackSize = 1 };
    }

    static Item MakeOilByUseType(ItemCatalog catalog, int useType)
    {
        var oil = BlightTables.Oils.First(o => o.UseType == useType);
        var bt = catalog.Find(oil.Id)!;
        return ItemCatalog.Create(bt) with { ItemLevel = 68, StackSize = 1 };
    }

    [Fact]
    public async Task Anointing_allocates_passives_handles_special_oils_maps_and_oil_extractor()
    {
        await using var area = await BlightArea.Open();
        var catalog = Game.ItemsFor(area.A);
        var tree = Game.PassivesFor(area.A);
        var uniques = Game.UniquesFor(area.A);

        // Verify complete 3.29.3c Blight crafting catalog loaded.
        Assert.True(BlightTables.Recipes.Count >= 550, $"Expected ~576 BlightCraftingRecipes, got {BlightTables.Recipes.Count}");
        Assert.True(BlightTables.GrantedPassiveModHashes.Length >= 4, "Expected GrantedPassive..GrantedPassive4 mod hashes");
        Assert.True(BlightTables.AnointableUniqueNames.Count >= 6, "Expected BlightCraftingUniques loaded");

        // 1. Amulet Anointing + Passive Allocation in Progression.cs (including zero-sum hot-path cache hit & no double counting).
        var beefRecipe = BlightTables.Recipes.First(r =>
            r.CraftingType == BlightAnointing.TypeAmuletOrUnique && r.PassiveGraphId != 0
            && tree.Find(r.PassiveGraphId) is { } node && node.Stats.Any(s => s.Key == Stat.BaseStrength && s.Value >= 20));
        var beefNode = tree.Find(beefRecipe.PassiveGraphId)!;
        int expectedStr = beefNode.Stats.Where(s => s.Key == Stat.BaseStrength).Sum(s => s.Value);

        var amuletBase = catalog.Find("Metadata/Items/Amulets/Amulet1")!;
        var amulet = ItemCatalog.Create(amuletBase) with { ItemLevel = 68, Rarity = ItemRarity.Normal, Mods = [] };
        var beefOils = beefRecipe.OilRows.Select(r => MakeOil(catalog, r)).ToArray();

        Assert.True(BlightAnointing.TryAnoint(amulet, beefOils, catalog, out var anointedAmulet, out var consumed, out _));
        Assert.NotNull(anointedAmulet);
        Assert.Equal(3, consumed.Count);
        Assert.Contains(beefRecipe.PassiveGraphId, Game.ItemGrantedPassiveIds(anointedAmulet!, catalog.Mods));

        // Equip anointed amulet: inactive at level 1 (Amulet1 DropLevel=5), active at level 68 -> grants the notable's Strength.
        var (baseSources, baseAttrs, _, _) = Game.EquipmentStatSources(area.A, [], level: 68);
        var equip = new (byte Row, InventoryItem Item)[] { (PlayerInventories.Amulet, new InventoryItem(9001, 0, 0, anointedAmulet!)) };
        var (inactiveSources, _, _, _) = Game.EquipmentStatSources(area.A, equip, level: 1);
        Assert.Equal(baseSources.GetValueOrDefault(Stat.BaseStrength), inactiveSources.GetValueOrDefault(Stat.BaseStrength));
        var (anointSources, anointAttrs, _, _) = Game.EquipmentStatSources(area.A, equip, level: 68);
        Assert.Equal(baseSources.GetValueOrDefault(Stat.BaseStrength) + expectedStr, anointSources.GetValueOrDefault(Stat.BaseStrength));
        Assert.Equal(baseAttrs.Apply(0, 0, 0).Strength + expectedStr, anointAttrs.Apply(0, 0, 0).Strength);

        // Cache hit on unchanged stat-sources lookup must call ItemModStats.Sum zero times!
        long sumsBefore = ItemModStats.SumsOnThisThread;
        var (cachedSources, _, _, _) = Game.EquipmentStatSources(area.A, equip, level: 68);
        Assert.Equal(sumsBefore, ItemModStats.SumsOnThisThread);
        Assert.Equal(anointSources.GetValueOrDefault(Stat.BaseStrength), cachedSources.GetValueOrDefault(Stat.BaseStrength));

        // If the notable is ALREADY allocated on the passive tree, wearing the anointed amulet must NOT double-count it!
        var (bothSources, _, _, _) = Game.EquipmentStatSources(area.A, equip, nodes: [beefRecipe.PassiveGraphId], level: 68);
        Assert.Equal(anointSources.GetValueOrDefault(Stat.BaseStrength), bothSources.GetValueOrDefault(Stat.BaseStrength));

        // 2. Stranglegasp: up to 4 distinct simultaneous anoints; refuses duplicate notable; replaces oldest on 5th.
        var stranglegasp = uniques.Create("Stranglegasp", new Random(1))!;
        var fourRecipes = BlightTables.Recipes
            .Where(r => r.CraftingType == BlightAnointing.TypeAmuletOrUnique && r.PassiveGraphId != 0 && tree.Find(r.PassiveGraphId) is not null)
            .GroupBy(r => r.PassiveGraphId).Select(g => g.First()).Take(5).ToArray();
        var current = stranglegasp;
        for (int i = 0; i < 4; i++)
        {
            Assert.True(BlightAnointing.TryAnoint(current, [.. fourRecipes[i].OilRows.Select(r => MakeOil(catalog, r))], catalog, out var next, out _, out string err), err);
            current = next!;
        }
        Assert.Equal(4, Game.ItemGrantedPassiveIds(current, catalog.Mods).Length);
        // Duplicate notable refused.
        Assert.False(BlightAnointing.TryAnoint(current, [.. fourRecipes[0].OilRows.Select(r => MakeOil(catalog, r))], catalog, out _, out _, out _));
        // 5th distinct notable replaces oldest (still 4 total).
        Assert.True(BlightAnointing.TryAnoint(current, [.. fourRecipes[4].OilRows.Select(r => MakeOil(catalog, r))], catalog, out var fifth, out _, out _));
        Assert.Equal(4, Game.ItemGrantedPassiveIds(fifth!, catalog.Mods).Length);
        Assert.Contains(fourRecipes[4].PassiveGraphId, Game.ItemGrantedPassiveIds(fifth!, catalog.Mods));

        // 3. Corrupted items require Tainted Oil (UseType 1); Mirrored items require Reflective Oil (UseType 2) + StashRules cell 78.
        var corruptedAmulet = amulet with { Corrupted = true };
        Assert.False(BlightAnointing.TryAnoint(corruptedAmulet, beefOils, catalog, out _, out _, out _));
        var taintedOil = MakeOilByUseType(catalog, 1);
        Assert.True(BlightAnointing.TryAnoint(corruptedAmulet, [.. beefOils, taintedOil], catalog, out var anointedCorrupted, out var consumedTainted, out _));
        Assert.Equal(4, consumedTainted.Count);

        var mirroredAmulet = amulet with { Mirrored = true };
        Assert.False(BlightAnointing.TryAnoint(mirroredAmulet, beefOils, catalog, out _, out _, out _));
        var reflectiveOil = MakeOilByUseType(catalog, 2);
        Assert.True(BlightAnointing.TryAnoint(mirroredAmulet, [.. beefOils, reflectiveOil], catalog, out _, out var consumedRefl, out _));
        Assert.Equal(4, consumedRefl.Count);
        Assert.Equal([78], catalog.Stash.AnointInputs(StashRules.Blight, reflectiveOil.TypeHash));

        // 4. Ring Anointing (2 oils -> BlightEnchantment* mod).
        var ringRecipe = BlightTables.Recipes.First(r => r.CraftingType == BlightAnointing.TypeRing && r.ModHash != 0);
        var ring = ItemCatalog.Create(catalog.Find("Metadata/Items/Rings/Ring1")!) with { ItemLevel = 68, Mods = [] };
        Assert.True(BlightAnointing.TryAnoint(ring, [.. ringRecipe.OilRows.Select(r => MakeOil(catalog, r))], catalog, out var anointedRing, out _, out _));
        Assert.Contains(anointedRing!.Mods, m => m.Section == ModSection.Enchant && m.Hash == ringRecipe.ModHash);

        // 5. Blighted Map (cap 3) & Blight-ravaged Map (cap 9, max 3 of the same oil!), plus unidentified rejection.
        Assert.False(BlightAnointing.TryAnoint(amulet with { Identified = false }, beefOils, catalog, out _, out _, out _));
        var mapBase = catalog.All.First(t => t.Supported && CurrencyEffects.IsMap(t));
        var plainMap = ItemCatalog.Create(mapBase) with { ItemLevel = 83, Mods = [] };
        var blightedMap = BlightChests.MakeBlightedMap(plainMap, uber: false, randomAnoints: false, new Random(1), catalog.Mods);
        var mapRecipes = BlightTables.Recipes.Where(r => r.CraftingType == BlightAnointing.TypeInfectedMap && r.ModHash != 0).Take(4).ToArray();
        var oilA = MakeOil(catalog, mapRecipes[0].OilRows[0]);
        var oilB = MakeOil(catalog, mapRecipes[1].OilRows[0]);
        var oilC = MakeOil(catalog, mapRecipes[2].OilRows[0]);
        Assert.True(BlightAnointing.TryAnoint(blightedMap, [oilA, oilA, oilA], catalog, out var bmap3, out _, out _));
        Assert.False(BlightAnointing.TryAnoint(bmap3!, [oilB], catalog, out _, out _, out _)); // cap 3 reached

        var uberMap = BlightChests.MakeBlightedMap(plainMap, uber: true, randomAnoints: false, new Random(1), catalog.Mods);
        Assert.True(BlightAnointing.TryAnoint(uberMap, [oilA, oilA, oilA], catalog, out var u3, out _, out _));
        Assert.False(BlightAnointing.TryAnoint(u3!, [oilA], catalog, out _, out _, out _)); // max 3 of same oil on Blight-ravaged!
        Assert.True(BlightAnointing.TryAnoint(u3!, [oilB, oilB, oilB], catalog, out var u6, out _, out _));
        Assert.True(BlightAnointing.TryAnoint(u6!, [oilC, oilC, oilC], catalog, out var u9, out _, out _));
        Assert.False(BlightAnointing.TryAnoint(u9!, [MakeOil(catalog, mapRecipes[3].OilRows[0])], catalog, out _, out _, out _)); // cap 9 reached

        // 6. Oil Extractor works on corrupted anointed jewellery/uniques, returns a real recipe oil, and refuses plain/mirrored/unidentified/maps.
        Assert.True(BlightAnointing.TryExtractOil(anointedCorrupted!, catalog, new Random(1), out var extracted, out _));
        Assert.NotNull(extracted);
        Assert.True(BlightTables.OilByItemId.ContainsKey(catalog.ByHash(extracted!.TypeHash)!.Id));
        Assert.False(BlightAnointing.TryExtractOil(amulet, catalog, new Random(1), out _, out _));
        Assert.False(BlightAnointing.TryExtractOil(anointedAmulet! with { Mirrored = true }, catalog, new Random(1), out _, out _));
        Assert.False(BlightAnointing.TryExtractOil(bmap3!, catalog, new Random(1), out _, out _));
    }

    [Fact]
    public async Task Cassia_window_validation_and_blight_unique_spores_work_end_to_end()
    {
        await using var area = await BlightArea.Open();
        var catalog = Game.ItemsFor(area.A);
        var items = Game.ItemsOf(area.A);
        var recipe = BlightTables.Recipes.First(r => r.CraftingType == BlightAnointing.TypeAmuletOrUnique && r.PassiveGraphId != 0);
        var amulet = ItemCatalog.Create(catalog.Find("Metadata/Items/Amulets/Amulet1")!) with { ItemLevel = 68, Mods = [] };
        var sword = ItemCatalog.Create(catalog.All.First(t => t.Supported && t.Class == "One Hand Sword")) with { ItemLevel = 68 };

        // Inventory placement validation on Cassia's crafting inventories (36 = oils only, 37 = anointable items only).
        Assert.NotNull(BlightAnointing.ValidateCraftingPlacement(new(area.A, BlightAnointing.CraftingInputRow, amulet, null)));
        Assert.Null(BlightAnointing.ValidateCraftingPlacement(new(area.A, BlightAnointing.CraftingInputRow, MakeOil(catalog, recipe.OilRows[0]), null)));
        Assert.NotNull(BlightAnointing.ValidateCraftingPlacement(new(area.A, BlightAnointing.CraftingItemRow, sword, null)));
        Assert.Null(BlightAnointing.ValidateCraftingPlacement(new(area.A, BlightAnointing.CraftingItemRow, amulet, null)));

        // Cassia window atomic anoint (row 37 item + row 36 oils consumed).
        items.Put(BlightAnointing.CraftingItemRow, new InventoryItem(0, 0, 0, amulet));
        for (int i = 0; i < recipe.OilRows.Length; i++)
            items.Put(BlightAnointing.CraftingInputRow, new InventoryItem(0, (byte)i, 0, MakeOil(catalog, recipe.OilRows[i])));
        Assert.True(await BlightAnointing.AnointFromCassiaWindow(area.A));
        var crafted = Assert.Single(items.Items(BlightAnointing.CraftingItemRow));
        Assert.Contains(recipe.PassiveGraphId, Game.ItemGrantedPassiveIds(crafted.Item, catalog.Mods));
        Assert.Empty(items.Items(BlightAnointing.CraftingInputRow));

        // Breathstealer Spore aura spawning (exact data-driven BuffDefinitions stats) + Sporeguard Fungal Ground bonuses.
        Assert.Equal(9, BlightTables.SporeAuras.Count);
        var speedStats = BlightSpores.SporeStatsFor(BlightTables.SporeAuras[0]);
        Assert.Equal((35, 35, 75), (speedStats.GetValueOrDefault(Stat.AttackSpeedPlusPct), speedStats.GetValueOrDefault(Stat.BaseCastSpeedPlusPct), speedStats.GetValueOrDefault(Stat.BaseMovementVelocityPlusPct)));
        Assert.Equal(50, BlightSpores.SporeStatsFor(BlightTables.SporeAuras[2]).GetValueOrDefault(Stat.PhysicalDamagePlusPct));
        Assert.Equal(35, BlightSpores.SporeStatsFor(BlightTables.SporeAuras[4]).GetValueOrDefault(Stat.BaseResistAllElementsPct));
        Assert.Equal(30, BlightSpores.SporeStatsFor(BlightTables.SporeAuras[5]).GetValueOrDefault(Stat.MaximumLifePctToAddAsMaximumEnergyShield));
        Assert.Equal(75, BlightSpores.SporeStatsFor(BlightTables.SporeAuras[6]).GetValueOrDefault(Stat.CriticalStrikeChancePlusPct));

        double now = Game.AreaTime(area.A);
        area.A.Walk.Teleport(300, 300, now);
        var spore = BlightSpores.SpawnSpore(area.Instance, (300, 300), now, new Random(1));
        Assert.NotNull(spore);
        Assert.Single(BlightSpores.ActiveSporesIn(area.Instance, now));

        BlightSpores.CreateFungalGround(area.Instance, (300, 300), 25, now);
        Assert.True(BlightSpores.IsOnFungalGround(area.Instance, (300, 300), now));
        Assert.False(BlightSpores.IsOnFungalGround(area.Instance, (500, 500), now));

        await BlightSpores.Tick(area.A, 0.033);
        var playerStats = new Dictionary<uint, int>();
        BlightSpores.ApplyStats(area.A, playerStats);
        Assert.Equal(1, playerStats.GetValueOrDefault(Stat.IsOnFungalGround));
        Assert.Equal(-10, playerStats.GetValueOrDefault(Stat.BaseDamageTakenPlusPct));
    }
}
