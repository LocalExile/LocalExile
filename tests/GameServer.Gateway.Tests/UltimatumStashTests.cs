using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public class UltimatumStashTests : IDisposable
{
    public void Dispose() => TestEnvironment.ReleaseOwnSaves();

    static PlayerInventories.Shape? Shape(ItemCatalog catalog, Item item) => catalog.ByHash(item.TypeHash) is { } t
        ? new(t.Width, t.Height, t.Class, t.MaxStack) : null;

    [Fact]
    public void Ultimatum_tab_loads_catalyst_slots_crafting_slot_and_trial_grid_with_reload_persistence()
    {
        TestEnvironment.OpenOwnSaves("ultimatumstash");
        var catalog = new ItemCatalog("data");
        string account = "Ultimatum" + Guid.NewGuid().ToString("N");
        var stash = AccountStash.For(account, catalog);
        var items = PlayerInventories.For(account, catalog);
        PlayerInventories.Shape? ShapeOf(Item item) => Shape(catalog, item);

        try
        {
            Assert.False(StashRules.NotShown(StashRules.Ultimatum));
            Assert.True(catalog.Stash.Slotted(StashRules.Ultimatum));

            int tab = Enumerable.Range(0, stash.Count).Single(i => stash.TabType(i) == StashRules.Ultimatum);
            var slot = stash.Inventory(tab).Slot;
            Assert.Equal((0x21, 62, 5), ((int)slot.Kind, (int)slot.Width, (int)slot.Height));

            // Rows 0-4 in MetamorphosisStashTabLayout have mismatched Id vs StoredItem: StoredItem must win.
            (string Id, int ExpectedSlot)[] catalysts =
            [
                ("Metadata/Items/Currency/CurrencyJewelleryQualityAttack", 0),       // Abrasive Catalyst
                ("Metadata/Items/Currency/CurrencyJewelleryQualityDefense", 1),      // Tempering Catalyst
                ("Metadata/Items/Currency/CurrencyJewelleryQualityResource", 2),     // Fertile Catalyst
                ("Metadata/Items/Currency/CurrencyJewelleryQualityElemental", 3),    // Turbulent Catalyst
                ("Metadata/Items/Currency/CurrencyJewelleryQualityCaster", 4),       // Imbued Catalyst
                ("Metadata/Items/Currency/CurrencyJewelleryQualityResistance", 5),   // Prismatic Catalyst
                ("Metadata/Items/Currency/CurrencyJewelleryQualityAttribute", 6),    // Intrinsic Catalyst
                ("Metadata/Items/Currency/CurrencyJewelleryQualityVaal", 58),        // Tainted Catalyst
                ("Metadata/Items/Currency/CurrencyJewelleryQualityCritical", 59),    // Unstable Catalyst
                ("Metadata/Items/Currency/CurrencyJewelleryQualitySpeed", 60),       // Accelerating Catalyst
                ("Metadata/Items/Currency/CurrencyJewelleryQualityPhysicalChaos", 61), // Noxious Catalyst
            ];

            foreach (var (id, expectedSlot) in catalysts)
            {
                var type = catalog.Find(id)!;
                Assert.True(catalog.Stash.Holds(StashRules.Ultimatum, type.Hash));
                Assert.Equal(expectedSlot, catalog.Stash.SlotOf(StashRules.Ultimatum, type.Hash));
                Assert.Equal((ushort)10000, catalog.Stash.MaxStack(StashRules.Ultimatum, type.Hash));
            }

            // Ctrl+click / affinity deposit routes Catalysts to their exact slot and merges up to 10,000.
            var turbulent = catalog.Find("Metadata/Items/Currency/CurrencyJewelleryQualityElemental")!;
            var invTurbulent = items.AddStacked(PlayerInventories.MainInventory,
                ItemCatalog.Create(turbulent) with { StackSize = 5 }, ShapeOf).Added.Single();
            var moved = ItemMoves.InventoryToStash(stash, items, tab, invTurbulent.Id, ShapeOf, out string why);
            Assert.NotNull(moved);
            Assert.Equal("", why);
            Assert.Equal(((byte)3, (byte)0, (ushort)5), (moved!.TabAdded[0].X, moved.TabAdded[0].Y, moved.TabAdded[0].Item.StackSize));
            var big = stash.AddStacked(tab, ItemCatalog.Create(turbulent) with { StackSize = 9995 }, ShapeOf, out _);
            Assert.NotNull(big);
            Assert.Equal((0, 10000), (big!.Left, big.Changed.Single().Item.StackSize));

            // GenericSlot (slot 7, 2x4) accepts jewellery while other cells refuse jewellery.
            var ring = catalog.All.First(t => t.Class == "Ring" && t.Supported);
            Assert.True(catalog.Stash.TakesAnything(StashRules.Ultimatum, 7, 0));
            Assert.NotNull(stash.Place(tab, 7, 0, new InventoryItem(stash.NewItemId(), 0, 0, ItemCatalog.Create(ring)), ShapeOf, out _));
            Assert.Null(stash.Place(tab, 9, 0, new InventoryItem(stash.NewItemId(), 0, 0, ItemCatalog.Create(ring)), ShapeOf, out string trialWhy));
            Assert.Contains("Inscribed Ultimatums only", trialWhy);

            // ItemisedTrial (12x5 block at X=9..20, Y=0..4) holds multiple Inscribed Ultimatums without colliding at (9,0).
            var trial = catalog.Find("Metadata/Items/Ultimatum/ItemisedTrial")!;
            Assert.True(catalog.Stash.Holds(StashRules.Ultimatum, trial.Hash));
            Assert.True(catalog.Stash.IsTrialSlot(StashRules.Ultimatum, 9, 0));
            Assert.True(catalog.Stash.IsTrialSlot(StashRules.Ultimatum, 20, 4));
            Assert.False(catalog.Stash.IsTrialSlot(StashRules.Ultimatum, 21, 0));
            var invTrial1 = items.Add(PlayerInventories.MainInventory, 0, 0, ItemCatalog.Create(trial));
            var invTrial2 = items.Add(PlayerInventories.MainInventory, 1, 0, ItemCatalog.Create(trial));
            var movedT1 = ItemMoves.InventoryToStash(stash, items, tab, invTrial1.Id, ShapeOf, out _)!;
            var movedT2 = ItemMoves.InventoryToStash(stash, items, tab, invTrial2.Id, ShapeOf, out _)!;
            Assert.Equal(((byte)9, (byte)0), (movedT1.TabAdded[0].X, movedT1.TabAdded[0].Y));
            Assert.Equal(((byte)10, (byte)0), (movedT2.TabAdded[0].X, movedT2.TabAdded[0].Y));
            Assert.NotNull(stash.Place(tab, 20, 4, new InventoryItem(stash.NewItemId(), 0, 0, ItemCatalog.Create(trial)), ShapeOf, out _));

            // Save reload preserves all items in their Ultimatum cells without Repair moving them away.
            AccountStash.Forget(account);
            var reloaded = AccountStash.For(account, catalog);
            var stored = reloaded.Inventory(tab).Items;
            Assert.Contains(stored, i => i.X == 3 && i.Y == 0 && i.Item.TypeHash == turbulent.Hash && i.Item.StackSize == 10000);
            Assert.Contains(stored, i => i.X == 7 && i.Y == 0 && i.Item.TypeHash == ring.Hash);
            Assert.Contains(stored, i => i.X == 9 && i.Y == 0 && i.Item.TypeHash == trial.Hash);
            Assert.Contains(stored, i => i.X == 10 && i.Y == 0 && i.Item.TypeHash == trial.Hash);
            Assert.Contains(stored, i => i.X == 20 && i.Y == 4 && i.Item.TypeHash == trial.Hash);

            // Overflow-only currencies with tab affinity (Sinistral/Dextral Catalyst -> Ultimatum GenericSlot (7,0) capped at shape.MaxStack,
            // Essence of Desolation -> Essence generic slots) actually place and enforce per-cell stack caps via reloaded stash.
            var genericRing = stored.Single(i => i.X == 7 && i.Y == 0);
            Assert.NotNull(reloaded.Take(tab, genericRing.Id));
            var sinistral = catalog.Find("Metadata/Items/Currency/CurrencyJewelleryQualityPrefix")!;
            Assert.True(catalog.Stash.Holds(StashRules.Ultimatum, sinistral.Hash));
            Assert.Null(catalog.Stash.SlotOf(StashRules.Ultimatum, sinistral.Hash));
            var sinistralOver = reloaded.AddStacked(tab, ItemCatalog.Create(sinistral) with { StackSize = (ushort)(sinistral.MaxStack + 5) }, ShapeOf, out string ovWhy);
            Assert.NotNull(sinistralOver);
            Assert.Equal("", ovWhy);
            Assert.Equal(5, sinistralOver!.Left);
            Assert.Equal(((byte)7, (byte)0, sinistral.MaxStack), (sinistralOver.Added[0].X, sinistralOver.Added[0].Y, sinistralOver.Added[0].Item.StackSize));

            int essenceTab = Enumerable.Range(0, reloaded.Count).Single(i => reloaded.TabType(i) == StashRules.Essence);
            var desolation = catalog.Find("Metadata/Items/Currency/CurrencyEssenceFaridun1")!;
            Assert.True(catalog.Stash.Holds(StashRules.Essence, desolation.Hash));
            Assert.Null(catalog.Stash.SlotOf(StashRules.Essence, desolation.Hash));
            var desolationAdded = reloaded.AddStacked(essenceTab, ItemCatalog.Create(desolation) with { StackSize = 3 }, ShapeOf, out string desWhy);
            Assert.NotNull(desolationAdded);
            Assert.Equal("", desWhy);
            Assert.Equal(0, desolationAdded!.Left);
            Assert.Equal((ushort)3, desolationAdded.Added.Single().Item.StackSize);

            // Multi-cell Delve resonators stay mapped to their own Delve slots instead of entering _trialAreas.
            foreach (string resId in new[] { "Metadata/Items/Delve/DelveStackableSocketableCurrencyReroll2", "Metadata/Items/Delve/DelveStackableSocketableCurrencyReroll4" })
            {
                var resonator = catalog.Find(resId)!;
                int? delveSlot = catalog.Stash.SlotOf(StashRules.Delve, resonator.Hash);
                Assert.True(delveSlot is >= 0);
                Assert.False(catalog.Stash.IsTrialSlot(StashRules.Delve, delveSlot.Value, 0));
            }
        }
        finally
        {
            PlayerInventories.Forget(account);
            AccountStash.Forget(account);
        }
    }

    [Fact]
    public void Corrupted_eight_mod_map_drops_and_legacy_fragment_normalization()
    {
        TestEnvironment.OpenOwnSaves("frag8mod");
        var catalog = new ItemCatalog("data");
        var drops = new ItemDrops(catalog, new ItemRolls(catalog), maps: new AtlasCatalog(catalog));
        var mapDrop = drops.Map(83, MonsterRarity.Unique, 16, new Random(7), 0, 1000, 0, 0, corruptEightMods: true);
        Assert.NotNull(mapDrop);
        Assert.True(mapDrop!.Item.Corrupted);
        Assert.True(mapDrop.Item.Identified);
        Assert.Equal(ItemRarity.Rare, mapDrop.Item.Rarity);
        Assert.Equal(8, mapDrop.Item.Mods.Count(m => m.Section == ModSection.Explicit));
        Assert.Equal(4, mapDrop.Item.Mods.Count(m => catalog.Mods.ByHash(m.Hash)!.GenerationType == 1));
        Assert.Equal(4, mapDrop.Item.Mods.Count(m => catalog.Mods.ByHash(m.Hash)!.GenerationType == 2));

        string account = "FragNorm" + Guid.NewGuid().ToString("N");
        var stash = AccountStash.For(account, catalog);
        var items = PlayerInventories.For(account, catalog);
        PlayerInventories.Shape? ShapeOf(Item item) => Shape(catalog, item);
        try
        {
            int tab = Enumerable.Range(0, stash.Count).Single(i => stash.TabType(i) == StashRules.Fragment);
            foreach (string legacyId in new[] { "Metadata/Items/MapFragments/VaalFragment2_1", "Metadata/Items/MapFragments/FragmentPantheonFlask" })
            {
                var legacyType = catalog.Find(legacyId)!;
                var reloaded = CharacterSaves.FromSaved(CharacterSaves.ToSaved(ItemCatalog.Create(legacyType), catalog.Mods), catalog);
                Assert.True(catalog.Stash.Holds(StashRules.Fragment, reloaded.TypeHash));
                Assert.NotEqual(legacyType.Hash, reloaded.TypeHash);
                var inv = items.Add(PlayerInventories.MainInventory, 0, 0, reloaded);
                Assert.NotNull(ItemMoves.InventoryToStash(stash, items, tab, inv.Id, ShapeOf, out _));
            }
            foreach (string slottedId in new[] { "Metadata/Items/Scarabs/ScarabBreach1", "Metadata/Items/MapFragments/CurrencyBreachFragmentFire2" })
            {
                var slottedType = catalog.Find(slottedId)!;
                var reloaded = CharacterSaves.FromSaved(CharacterSaves.ToSaved(ItemCatalog.Create(slottedType), catalog.Mods), catalog);
                Assert.Equal(slottedType.Hash, reloaded.TypeHash);
            }
        }
        finally
        {
            PlayerInventories.Forget(account);
            AccountStash.Forget(account);
        }
    }
}
