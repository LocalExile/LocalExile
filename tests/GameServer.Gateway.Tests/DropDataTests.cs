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

    [Fact]
    public void HardMode_drops_include_atlas_maps()
    {
        var ruthless = new League("Ruthless", "Standard", false, false) { DataSet = "HardMode" };
        var conn = new ClientState(new(1, "ruthless-drops-test", 100, 100, 10, 10, null, []), 1, 1)
        {
            League = ruthless.Name,
            LeagueModule = new(ruthless, new HashSet<string>(["Game"])),
        };
        var drops = Game.DropsFor(conn);
        var rng = new Random(1);
        ItemDrops.Drop? map = null;
        for (int i = 0; i < 100 && map is null; i++)
            map = drops.Map(72, MonsterRarity.Unique, mapTier: 5, rng);
        Assert.NotNull(map);
        Assert.True(map.Type.Supported);
        Assert.Equal(5, Game.HardModeAtlas.Value.TierOf(map.Item.TypeHash));
    }
}
