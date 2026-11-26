using GameServer.Core.Accounts;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public sealed class DropPoolOverlaysTests
{
    const string CustomCurrencyId = "Metadata/Items/Currency/CurrencyOverlayTestOrb";
    const string CustomContainerOrbId = "Metadata/Items/Currency/CurrencyOverlayContainerOrb";
    const string CustomBossTokenId = "Metadata/Items/Currency/CurrencyOverlayBossToken";
    const string CustomTaggedOrbId = "Metadata/Items/Currency/CurrencyOverlayTaggedOrb";
    const string CustomModId = "OverlayTestMaxLifeMod1";
    const string CustomUniqueName = "Overlay Test Sovereign Belt";
    const string StockBeltBaseId = "Metadata/Items/Belts/Belt1";

    static ItemCatalog.BaseType MakeCustomCurrency(string id, string name, ushort visualHash, int dropLevel = 1) =>
        new(-1, id, name, Hash: 0, Width: 1, Height: 1, VisualIdentity: visualHash,
            Components: ["Base", "Stack"], MaxCharges: 0, Class: "StackableCurrency",
            DropLevel: dropLevel, MaxStack: 20);

    [Fact]
    public void Custom_catalog_and_drop_pool_overlays_rebuild_lazily_and_honor_gates()
    {
        const string owner = "test-overlay-mod-gates";
        // Construct an ItemDrops instance BEFORE registering any overlays to prove lazy pool rebuild.
        var drops = new ItemDrops(Program.Items, Program.Rolls, maps: Game.MapDevices.Atlas, uniques: Game.Uniques);
        var hardModeDrops = new ItemDrops(Game.HardModeItems, Game.HardModeRolls, hardMode: true,
            maps: Game.HardModeAtlas, uniques: Game.HardModeUniques, leagueId: "RuthlessTest");
        long initialRevision = drops.PoolRevision;
        long initialHardModeRevision = hardModeDrops.PoolRevision;

        try
        {
            var customCurrency = MakeCustomCurrency(CustomCurrencyId, "Test Catalyst Orb", 0x7a01, dropLevel: 1);
            var containerOrb = MakeCustomCurrency(CustomContainerOrbId, "Test Cache Orb", 0x7a02, dropLevel: 1);
            var bossToken = MakeCustomCurrency(CustomBossTokenId, "Test Boss Crest", 0x7a03, dropLevel: 1);
            var taggedOrb = MakeCustomCurrency(CustomTaggedOrbId, "Test Shore Orb", 0x7a04, dropLevel: 1);

            ItemCatalog.RegisterOverlay(owner, customCurrency,
                new ItemCatalog.BaseTypeOverlayOptions(VisualIdentityId: "TestOverlayOrbVisual"));
            ItemCatalog.RegisterOverlay(owner, containerOrb);
            ItemCatalog.RegisterOverlay(owner, bossToken);
            ItemCatalog.RegisterOverlay(owner, taggedOrb);

            // Custom mod & custom unique referencing that mod.
            var customMod = new ModCatalog.Mod(-1, CustomModId, 0xfe21, "of Overlay Testing",
                Domain: 1, GenerationType: 3, Level: 1,
                Stats: [new ModCatalog.ModStat(Stat.BaseMaximumLife, 42, 42)]);
            ModCatalog.RegisterOverlay(owner, customMod);

            var customUnique = new UniqueCatalog.Definition(
                CustomUniqueName,
                StockBeltBaseId,
                [CustomModId],
                ItemLevel: 50)
            {
                FlavourText = "Forged solely in runtime overlay memory.",
                CustomName = CustomUniqueName,
                VisualIdentityHash = 0x7a10,
                DropWeight = 1.0,
                MinDropLevel = 30,
            };
            UniqueCatalog.RegisterOverlay(owner, customUnique);

            // Verify catalog lookups by Id, Hash, Row, All, Visual, Mod, and Unique.
            var resolvedCurrency = Program.Items.ById(CustomCurrencyId);
            Assert.NotNull(resolvedCurrency);
            Assert.Equal(ItemCatalog.ComputeHash32(CustomCurrencyId), resolvedCurrency.Hash);
            Assert.Equal(resolvedCurrency, Program.Items.ByHash(resolvedCurrency.Hash));
            Assert.Equal(resolvedCurrency, Program.Items.Row(resolvedCurrency.Row));
            Assert.Equal((ushort)0x7a01, Program.Items.Visual("TestOverlayOrbVisual"));
            Assert.True(Program.Items.IsVisual(0x7a01));
            Assert.NotNull(Program.Items.Mods.ById(CustomModId));
            Assert.NotNull(Game.Uniques.Find(CustomUniqueName));
            var createdUnique = Game.Uniques.Create(CustomUniqueName, new Random(1));
            Assert.NotNull(createdUnique);
            Assert.Equal(ItemRarity.Unique, createdUnique.Rarity);
            Assert.Equal(CustomUniqueName, createdUnique.CustomName);
            Assert.Contains(createdUnique.Mods, m => m.Hash == 0xfe21 && m.Values is [42]);
            Assert.NotNull(Game.Uniques.RollsOf(createdUnique));

            // Register declarative drop-pool overlays with area-level, rarity, source, league, and area-tag gates.
            DropPoolOverlays.Register(owner, new DropPoolEntry(DropPoolCategory.Currency, CustomCurrencyId, 1_000_000)
            {
                MinAreaLevel = 20,
                MaxAreaLevel = 80,
                Monsters = DropMonsterFilter.Rare | DropMonsterFilter.Unique | DropMonsterFilter.Boss,
                Sources = DropSourceMask.MonsterKill | DropSourceMask.BossBonus | DropSourceMask.MapCurrency,
                StackSize = 3,
            });
            DropPoolOverlays.Register(owner, new DropPoolEntry(DropPoolCategory.Currency, CustomContainerOrbId, 1_000_000)
            {
                MinAreaLevel = 15,
                Sources = DropSourceMask.Container | DropSourceMask.Strongbox,
            });
            DropPoolOverlays.Register(owner, new DropPoolEntry(DropPoolCategory.Boss, CustomBossTokenId, 500)
            {
                MinAreaLevel = 40,
                Sources = DropSourceMask.BossBonus | DropSourceMask.MonsterKill,
                Chance = 1.0,
            });
            DropPoolOverlays.Register(owner, new DropPoolEntry(DropPoolCategory.Currency, CustomTaggedOrbId, 1_000_000)
            {
                MinAreaLevel = 1,
                Leagues = ["OverlayLeague"],
                AreaTags = ["shore"],
                Sources = DropSourceMask.MonsterKill,
            });
            DropPoolOverlays.Register(owner, new DropPoolEntry(DropPoolCategory.Unique, CustomUniqueName, 1_000_000)
            {
                MinAreaLevel = 30,
                Monsters = DropMonsterFilter.Unique | DropMonsterFilter.Boss,
                Sources = DropSourceMask.MonsterKill | DropSourceMask.BossBonus | DropSourceMask.Strongbox,
            });

            // 1. Lazy rebuild happened automatically on already-constructed Standard & HardMode ItemDrops.
            Assert.True(drops.PoolRevision > initialRevision);
            Assert.True(hardModeDrops.PoolRevision > initialHardModeRevision);

            var rng = new Random(42);
            var currencyOnly = new DropWeights { Gems = 0, Flasks = 0, Equipment = 0 };

            // 2. Monster kills: honors MinAreaLevel (20), MaxAreaLevel (80), and MonsterRarity (Rare|Unique|Boss).
            var lowLevelDrops = Enumerable.Range(0, 24)
                .SelectMany(_ => drops.Roll(10, MonsterRarity.Rare, rng, 0, 1, 1, 0, null, null, 1, null, null, currencyOnly))
                .ToList();
            Assert.DoesNotContain(lowLevelDrops, d => d.Type.Id == CustomCurrencyId);

            var highLevelDrops = Enumerable.Range(0, 24)
                .SelectMany(_ => drops.Roll(90, MonsterRarity.Rare, rng, 0, 1, 1, 0, null, null, 1, null, null, currencyOnly))
                .ToList();
            Assert.DoesNotContain(highLevelDrops, d => d.Type.Id == CustomCurrencyId);

            var normalKillDrops = Enumerable.Range(0, 64)
                .SelectMany(_ => drops.Roll(50, MonsterRarity.Normal, rng, 0, 4, 1, 0, null, null, 1, null, null, currencyOnly))
                .ToList();
            Assert.DoesNotContain(normalKillDrops, d => d.Type.Id == CustomCurrencyId);

            var rareKillDrops = Enumerable.Range(0, 24)
                .SelectMany(_ => drops.Roll(50, MonsterRarity.Rare, rng, 0, 2, 1, 0, null, null, 1, null, null, currencyOnly))
                .ToList();
            Assert.Contains(rareKillDrops, d => d.Type.Id == CustomCurrencyId && d.Item.StackSize == 3);
            Assert.DoesNotContain(rareKillDrops, d => d.Type.Id == CustomContainerOrbId);

            // 3. Container & Strongbox sources: container orb drops, monster-only currency does not.
            var containerDrops = drops.Container(50, 16, 0, rng, accepts: t => t.Class == "StackableCurrency");
            Assert.Contains(containerDrops, d => d.Type.Id == CustomContainerOrbId);
            Assert.DoesNotContain(containerDrops, d => d.Type.Id == CustomCurrencyId);

            var strongboxPlayer = new Player(new(1, "1_1_1", 100, 100, 10, 10, null, []), 1, 1)
            {
                Character = { League = "Standard" },
                LeagueModule = new(new League("Standard", "", false, false), new HashSet<string>(["Game"])),
            };
            var currencyBox = StrongboxCatalog.All.First(b => b.Kind == StrongboxCatalog.Contents.Currency);
            var strongboxDrops = StrongboxLoot.Roll(strongboxPlayer, currencyBox, _ => 0, 50,
                new MapMods(new Dictionary<uint, int>()), rng);
            Assert.Contains(strongboxDrops, d => d.Type.Id == CustomContainerOrbId);

            // 4. Boss first-kill bonus: rolls custom currency + guaranteed boss token.
            var bossBonusDrops = drops.RollBossFirstKillBonus(50, rng, actBoss: true);
            Assert.Contains(bossBonusDrops, d => d.Type.Id == CustomCurrencyId);
            Assert.Contains(bossBonusDrops, d => d.Type.Id == CustomBossTokenId);

            // 5. Map currency mod source: honors DropSourceMask.MapCurrency.
            var mapMods = new MapMods(new Dictionary<uint, int> { [Stat.MapCurrencyDropChancePlusPctFinalFromUberMod] = 100 });
            var mapCurrencyDrop = drops.CurrencyDrop(50, rng, weights: null,
                context: DropContext.MapCurrency(MonsterRarity.Rare));
            Assert.NotNull(mapCurrencyDrop);
            Assert.Equal(CustomCurrencyId, mapCurrencyDrop.Type.Id);
            var fromMapMods = Enumerable.Range(0, 16)
                .SelectMany(_ => drops.FromMapMods(mapMods, 50, MonsterRarity.Rare, rng))
                .ToList();
            Assert.Contains(fromMapMods, d => d.Type.Id == CustomCurrencyId);

            // 6. Custom unique rolls naturally under area-level & rarity gates.
            var beltBase = Program.Items.ById(StockBeltBaseId)!;
            Assert.Null(drops.TryUnique(beltBase, 20, 22, rng, DropContext.MonsterKill(MonsterRarity.Unique)));
            var rolledUnique = drops.TryUnique(beltBase, 50, 52, rng, DropContext.MonsterKill(MonsterRarity.Unique));
            Assert.NotNull(rolledUnique);
            Assert.Equal(CustomUniqueName, rolledUnique.CustomName);
            Assert.Contains(rolledUnique.Mods, m => m.Hash == 0xfe21 && m.Values is [42]);

            // 7. League + AreaTag filters and HardModeDrops integration.
            var wrongLeagueDrop = drops.CurrencyDrop(10, rng, weights: null,
                context: DropContext.MonsterKill(MonsterRarity.Normal, areaTags: new HashSet<string>(["shore"]), leagueId: "Standard"));
            Assert.NotEqual(CustomTaggedOrbId, wrongLeagueDrop?.Type.Id);
            var matchingLeagueDrop = drops.CurrencyDrop(10, rng, weights: null,
                context: DropContext.MonsterKill(MonsterRarity.Normal, areaTags: new HashSet<string>(["shore"]), leagueId: "OverlayLeague"));
            Assert.Equal(CustomTaggedOrbId, matchingLeagueDrop?.Type.Id);

            var hmDrop = hardModeDrops.CurrencyDrop(50, rng, weights: null,
                context: DropContext.MonsterKill(MonsterRarity.Rare, hardMode: true));
            Assert.Equal(CustomCurrencyId, hmDrop?.Type.Id);

            // 8. Overlays survive /reload (GameData.Reload) without SQLite mutation.
            GameData.Reload("overlay-preservation-test");
            Assert.NotNull(Program.Items.ById(CustomCurrencyId));
            Assert.NotNull(Game.Uniques.Find(CustomUniqueName));
            var afterReload = drops.CurrencyDrop(50, rng, weights: null, context: DropContext.MonsterKill(MonsterRarity.Rare));
            Assert.Equal(CustomCurrencyId, afterReload?.Type.Id);
        }
        finally
        {
            GameEventOwners.Release(owner, GameEventOwners.ModuleRank);
        }
    }

    [Fact]
    public void Mod_unload_via_GameEventOwners_cleanly_removes_overlays_without_mutating_sqlite()
    {
        const string owner = "test-overlay-mod-cleanup";
        var store = GameData.Store!;
        var baselineDbDrops = store.DropCounts();
        int baselineItemCount = Program.Items.All.Count;
        int baselineModCount = Program.Items.Mods.All.Count;
        int baselineUniqueCount = Game.Uniques.All.Count;
        int baselineGemPoolCount = GemSources.DropPool(Program.Items).Count;
        int baselineOverlayEntries = DropPoolOverlays.Default.Snapshot.Length;

        var drops = new ItemDrops(Program.Items, Program.Rolls, maps: Game.MapDevices.Atlas, uniques: Game.Uniques);
        var customOrb = MakeCustomCurrency(CustomCurrencyId, "Ephemeral Mod Orb", 0x7b01, dropLevel: 1);
        var customGem = new ItemCatalog.BaseType(-1, "Metadata/Items/Gems/SkillGemOverlayTest", "Overlay Test Gem",
            Hash: 0, Width: 1, Height: 1, VisualIdentity: 0x7b02,
            Components: ["Base", "SkillGem"], MaxCharges: 0, Class: "Active Skill Gem", DropLevel: 1);

        try
        {
            ItemCatalog.RegisterOverlay(owner, customOrb,
                new ItemCatalog.BaseTypeOverlayOptions(VisualIdentityId: "EphemeralOrbVisual"));
            ItemCatalog.RegisterOverlay(owner, customGem,
                new ItemCatalog.BaseTypeOverlayOptions(Gem: new ItemCatalog.GemType(-1, false,
                    [new ItemCatalog.GemVariant(0, "Overlay Test Gem", 0, -1, true)], [0])));
            GemSources.RegisterOverlay(owner, customGem.Id);
            ModCatalog.RegisterOverlay(owner, new ModCatalog.Mod(-1, CustomModId, 0xfe22, "of Cleanup",
                Domain: 1, GenerationType: 3, Level: 1,
                Stats: [new ModCatalog.ModStat(Stat.BaseMaximumLife, 10, 10)]));
            UniqueCatalog.RegisterOverlay(owner, new UniqueCatalog.Definition(
                CustomUniqueName, StockBeltBaseId, [CustomModId], ItemLevel: 50)
            {
                VisualIdentityHash = 0x7b03,
                DropWeight = 1_000_000,
            });
            DropDataProvider.Default.RegisterOverlay(owner, new DropPoolEntry(DropPoolCategory.Currency, CustomCurrencyId, 1_000_000));
            DropDataProvider.Default.RegisterOverlay(owner, new DropPoolEntry(DropPoolCategory.Boss, CustomCurrencyId, 100) { Chance = 1.0 });

            // Active before unload, while SQLite remains untouched.
            Assert.NotNull(Program.Items.ById(CustomCurrencyId));
            Assert.Equal(baselineDbDrops, store.DropCounts());
            Assert.True(GemSources.DropPool(Program.Items).Count > baselineGemPoolCount);
            var activeDrop = drops.CurrencyDrop(50, new Random(7));
            Assert.Equal(CustomCurrencyId, activeDrop?.Type.Id);
            long revWhileActive = drops.PoolRevision;

            // Unload owning mod via GameEventOwners.Release.
            GameEventOwners.Release(owner, GameEventOwners.ModuleRank);

            // Catalogs, gem pool, and drop-pool overlays all restored cleanly to stock.
            Assert.Null(Program.Items.ById(CustomCurrencyId));
            Assert.Null(Program.Items.ByHash(ItemCatalog.ComputeHash32(CustomCurrencyId)));
            Assert.Null(Program.Items.Visual("EphemeralOrbVisual"));
            Assert.Null(Program.Items.Mods.ById(CustomModId));
            Assert.Null(Game.Uniques.Find(CustomUniqueName));
            Assert.Equal(baselineItemCount, Program.Items.All.Count);
            Assert.Equal(baselineModCount, Program.Items.Mods.All.Count);
            Assert.Equal(baselineUniqueCount, Game.Uniques.All.Count);
            Assert.Equal(baselineGemPoolCount, GemSources.DropPool(Program.Items).Count);
            Assert.Equal(baselineOverlayEntries, DropPoolOverlays.Default.Snapshot.Length);
            Assert.Equal(baselineDbDrops, store.DropCounts());

            // ItemDrops lazily rebuilds on unload and never drops the unloaded custom item again.
            Assert.True(drops.PoolRevision > revWhileActive);
            var rng = new Random(99);
            var afterDrops = Enumerable.Range(0, 64)
                .Select(_ => drops.CurrencyDrop(50, rng))
                .OfType<ItemDrops.Drop>()
                .Concat(drops.Container(50, 20, 0, rng))
                .Concat(drops.RollBossFirstKillBonus(50, rng, actBoss: true))
                .ToList();
            Assert.NotEmpty(afterDrops);
            Assert.DoesNotContain(afterDrops, d => d.Type.Id == CustomCurrencyId || d.Type.Id == customGem.Id);
        }
        finally
        {
            GameEventOwners.Release(owner, GameEventOwners.ModuleRank);
        }
    }
}
