using GameServer.Core.Accounts;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public sealed class DropDataTests
{
    [Fact]
    public void Generic_map_drops_respect_campaign_and_monster_tier_limits()
    {
        var atlas = new AtlasCatalog(Program.Items);
        var drops = new ItemDrops(Program.Items, maps: atlas);
        var random = new Random(21);
        int[] Tiers(MonsterRarity rarity, int tier = 1, bool mapBoss = false) => Enumerable.Range(0, 64).Select(_ =>
            atlas.TierOf(drops.Map(67 + Math.Max(1, tier), rarity, tier, random, chanceFactor: 1000,
                mapBoss: mapBoss)!.Item.TypeHash)!.Value)
            .Distinct().Order().ToArray();

        Assert.Equal([1], Tiers(MonsterRarity.Normal));
        Assert.Equal([1, 2], Tiers(MonsterRarity.Magic));
        Assert.Equal([1, 2], Tiers(MonsterRarity.Rare));
        Assert.Equal([1], Tiers(MonsterRarity.Unique));
        Assert.Equal([1, 2, 3], Tiers(MonsterRarity.Unique, mapBoss: true));
        Assert.Equal([15, 16], Tiers(MonsterRarity.Unique, 15, mapBoss: true));
        Assert.Equal([16], Tiers(MonsterRarity.Rare, 16));
        Assert.Equal([1], Tiers(MonsterRarity.Unique, 0));
    }

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
        // Force a successful chance roll to isolate base eligibility and the selected tier.
        Assert.Null(drops.Map(1, MonsterRarity.Normal, 0, rng, chanceFactor: 1000));
        var first = drops.Map(58, MonsterRarity.Normal, 0, rng, chanceFactor: 1000);
        Assert.NotNull(first);
        Assert.Equal(1, Game.HardModeAtlas.Value.TierOf(first.Item.TypeHash));
        var map = drops.Map(72, MonsterRarity.Unique, 5, rng, chanceFactor: 1000);
        Assert.NotNull(map);
        Assert.True(map.Type.Supported);
        Assert.Equal(5, Game.HardModeAtlas.Value.TierOf(map.Item.TypeHash));
    }
}
