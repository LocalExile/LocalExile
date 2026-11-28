using GameServer.Core.Accounts;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public sealed class DropDataTests
{
    [Fact]
    public void Drop_tables_come_from_world_db()
    {
        var store = GameData.Store!;
        var counts = store.DropCounts();
        Assert.Equal(36, counts.CurrencyWeights);
        Assert.Equal(634, counts.Bosses);
        Assert.Equal(246, counts.BossItems);
        Assert.Equal(788, counts.CardAreas);
        Assert.Equal(531, counts.ItemWeights);

        var provider = new DropDataProvider();
        Assert.False(provider.HasTable);
        GameDataDrops.Reload(store, provider);
        var drops = provider.Current;
        Assert.True(provider.HasTable);
        // The compiled weight with the client's DropLevel.
        Assert.Equal(15, drops.CurrencyWeights["Metadata/Items/Currency/CurrencyRerollImplicit"].Weight);
        Assert.Equal(35, drops.CurrencyWeights["Metadata/Items/Currency/CurrencyRerollImplicit"].DropLevel);
        // A boss from WorldAreas, categorised by the area's own flags.
        Assert.Equal("campaign", drops.Bosses["Metadata/Monsters/seawitchillusion/BossMerveil"].Category);
        Assert.Equal(0.35, drops.BossItems[("Metadata/Monsters/LeagueAzmeri/VoodooKingBoss/VoodooKingBoss", "Pragmatism")].Chance);
        Assert.Null(drops.BossItems[("Metadata/Monsters/Atziri/Atziri", "The Vertex")].Chance);
        // The Sacred Blossom the Harvest module rolls on a Tier 4 grove boss.
        Assert.Equal(0.12, drops.BossItems[("Metadata/Monsters/LeagueHarvest/Red/HarvestBearBoss", "Metadata/Items/MapFragments/CurrencyHarvestBossKey")].Chance);
        // Oshabi's guaranteed unique: Forbidden Shako's share of the one pick.
        Assert.Equal(0.50, drops.BossItems[("Metadata/Monsters/LeagueHarvest/Oshabi/OshabiBoss", "Forbidden Shako")].Chance);
        Assert.Equal("3.27.0", drops.ItemWeights[("card", "Metadata/Items/DivinationCards/DivinationCardRainOfChaos")].Patch);
    }

    [Fact]
    public void HardMode_drops_include_atlas_maps()
    {
        var ruthless = new League("Ruthless", "Standard", false, false) { DataSet = "HardMode" };
        var conn = new Player(new(1, "ruthless-drops-test", 100, 100, 10, 10, null, []), 1, 1)
        {
            Character = { League = ruthless.Name },
            LeagueModule = new(ruthless, new HashSet<string>(["Game"])),
        };
        var drops = Game.DropsFor(conn);
        var rng = new Random(1);
        // Force a successful chance roll to isolate base eligibility and the selected tier.
        Assert.Null(drops.Map(1, MonsterRarity.Normal, 0, rng, chanceFactor: 1000));
        var first = drops.Map(58, MonsterRarity.Normal, 0, rng, chanceFactor: 1000);
        Assert.NotNull(first);
        Assert.Equal(1, Game.HardModeAtlas.TierOf(first.Item.TypeHash));
        var map = drops.Map(72, MonsterRarity.Unique, 5, rng, chanceFactor: 1000);
        Assert.NotNull(map);
        Assert.True(map.Type.Supported);
        Assert.Equal(5, Game.HardModeAtlas.TierOf(map.Item.TypeHash));
    }

    [Fact]
    public void Boss_drops_roll_fragments_crests_echoes_currency_and_unique_pools_from_world_db()
    {
        var provider = new DropDataProvider();
        GameDataDrops.Reload(GameData.Store!, provider);
        var drops = new ItemDrops(Program.Items, uniques: new UniqueCatalog(Program.Items), dropData: provider);
        var rng = new Random(42);

        // Guaranteed Shaper Guardian fragment, Elder Guardian fragment, Conqueror crest, and Memory echo.
        var phoenix = drops.BossDrops("Metadata/Monsters/AtlasBosses/PhoenixBoss", 83, rng, provider.Current);
        Assert.Contains(phoenix, d => d.Type.Id == "Metadata/Items/MapFragments/CurrencyShaperFragment1");

        var enslaver = drops.BossDrops("Metadata/Monsters/AtlasBosses/TheElder/ElderGuardian1", 83, rng, provider.Current);
        Assert.Contains(enslaver, d => d.Type.Id == "Metadata/Items/MapFragments/CurrencyElderFragment1");

        var baran = drops.BossDrops("Metadata/Monsters/AtlasExiles/AtlasExile1Apparition", 83, rng, provider.Current);
        Assert.Contains(baran, d => d.Type.Id == "Metadata/Items/MapFragments/CurrencySirusFragment2");

        var fragFear = drops.BossDrops("Metadata/Monsters/AtlasMemory/FragmentOfAnger", 83, rng, provider.Current);
        Assert.Contains(fragFear, d => d.Type.Id == "Metadata/Items/MapFragments/AtlasMemory/CurrencyFearKey");

        // Pinnacle bosses roll 1 guaranteed unique from their NULL-chance retail unique pool (including Incarnations & Maven Nucleus).
        var exarch = drops.BossDrops("Metadata/Monsters/AtlasInvaders/CleansingMonsters/CleansingBoss", 84, rng, provider.Current);
        Assert.Contains(exarch, d => d.Item.Rarity == ItemRarity.Unique && !d.Item.Identified);

        var dread = drops.BossDrops("Metadata/Monsters/AtlasMemory/BenevolenceBoss", 83, rng, provider.Current);
        Assert.Contains(dread, d => d.Item.Rarity == ItemRarity.Unique && !d.Item.Identified);

        var nucleus = drops.BossDrops("Metadata/Monsters/MavenBoss/MavenBrainBoss", 84, rng, provider.Current);
        Assert.Contains(nucleus, d => d.Item.Rarity == ItemRarity.Unique && !d.Item.Identified);

        // Mutually-exclusive fragment sets (Shaper, Elder, T17 Nightmare) guarantee exactly 1 fragment on every kill.
        var t17Seen = new HashSet<string>(StringComparer.Ordinal);
        var shaperSeen = new HashSet<string>(StringComparer.Ordinal);
        var elderSeen = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < 40; i++)
        {
            var t17 = drops.BossDrops("Metadata/Monsters/UnholyTrio/UnholyTrioMapUber", 84, rng, provider.Current)
                .Where(d => d.Type.Id.StartsWith("Metadata/Items/MapFragments/CurrencyUberBossKey", StringComparison.Ordinal)).ToList();
            Assert.Single(t17);
            t17Seen.Add(t17[0].Type.Id);

            var shaperFrags = drops.BossDrops("Metadata/Monsters/AtlasBosses/TheShaperBoss", 84, rng, provider.Current)
                .Where(d => d.Type.Id.StartsWith("Metadata/Items/MapFragments/CurrencyUberElderFragment", StringComparison.Ordinal)).ToList();
            Assert.Single(shaperFrags);
            shaperSeen.Add(shaperFrags[0].Type.Id);

            var elderFrags = drops.BossDrops("Metadata/Monsters/AtlasBosses/TheElder/TheElder", 83, rng, provider.Current)
                .Where(d => d.Type.Id.StartsWith("Metadata/Items/MapFragments/CurrencyUberElderFragment", StringComparison.Ordinal)).ToList();
            Assert.Single(elderFrags);
            elderSeen.Add(elderFrags[0].Type.Id);
        }
        Assert.Contains("Metadata/Items/MapFragments/CurrencyUberBossKeySirus", t17Seen);
        Assert.Contains("Metadata/Items/MapFragments/CurrencyUberBossKeyMaven", t17Seen);
        Assert.Contains("Metadata/Items/MapFragments/CurrencyUberElderFragment3", shaperSeen);
        Assert.Contains("Metadata/Items/MapFragments/CurrencyUberElderFragment4", shaperSeen);
        Assert.Contains("Metadata/Items/MapFragments/CurrencyUberElderFragment1", elderSeen);
        Assert.Contains("Metadata/Items/MapFragments/CurrencyUberElderFragment2", elderSeen);

        // MavenBrainBoss's pool resolves Doppelgänger Guise with its exact umlaut spelling.
        Assert.True(provider.Current.BossItems.ContainsKey(("Metadata/Monsters/MavenBoss/MavenBrainBoss", "Doppelgänger Guise")));
    }
}
