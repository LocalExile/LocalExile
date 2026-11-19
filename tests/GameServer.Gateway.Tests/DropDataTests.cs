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
        Assert.Equal(120, counts.BossItems);
        Assert.Equal(555, counts.CardAreas);
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
}
