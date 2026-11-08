using GameServer.Storage;

namespace GameServer.Gateway.Tests;

public sealed class DropDataTests
{
    [Fact]
    public void Drop_seed_fills_the_tables_from_the_client_tables_and_the_code_seed()
    {
        string folder = Path.Combine(Path.GetTempPath(), "localexile-drop-seed-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            using var store = new GameDataStore(Path.Combine(folder, "drops.db"));
            Assert.True(GameDataDrops.Seed(store));
            Assert.False(GameDataDrops.Seed(store));

            var counts = store.DropCounts();
            Assert.Equal(24, counts.CurrencyWeights);
            Assert.Equal(630, counts.Bosses);
            Assert.Equal(108, counts.BossItems);
            Assert.Equal(555, counts.CardAreas);
            Assert.Equal(531, counts.ItemWeights);

            GameDataDrops.Reload(store);
            var drops = DropDataProvider.Default.Current;
            Assert.True(DropDataProvider.Default.HasTable);
            // The compiled weight with the client's DropLevel.
            Assert.Equal(15, drops.CurrencyWeights["Metadata/Items/Currency/CurrencyRerollImplicit"].Weight);
            Assert.Equal(35, drops.CurrencyWeights["Metadata/Items/Currency/CurrencyRerollImplicit"].DropLevel);
            // A boss from WorldAreas, categorised by the area's own flags.
            Assert.Equal("campaign", drops.Bosses["Metadata/Monsters/seawitchillusion/BossMerveil"].Category);
            Assert.Equal(0.35, drops.BossItems[("Metadata/Monsters/LeagueAzmeri/VoodooKingBoss/VoodooKingBoss", "Pragmatism")].Chance);
            Assert.Null(drops.BossItems[("Metadata/Monsters/Atziri/Atziri", "The Vertex")].Chance);
            Assert.Equal("3.27.0", drops.ItemWeights[("card", "Metadata/Items/DivinationCards/DivinationCardRainOfChaos")].Patch);
        }
        finally
        {
            GameDataDrops.Reset();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(folder, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void Drop_provider_stays_empty_until_a_table_is_seeded()
    {
        string folder = Path.Combine(Path.GetTempPath(), "localexile-drop-empty-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            using var store = new GameDataStore(Path.Combine(folder, "drops.db"));
            GameDataDrops.Reload(store);
            Assert.False(DropDataProvider.Default.HasTable);
            Assert.Empty(DropDataProvider.Default.Current.CurrencyWeights);
            Assert.Empty(DropDataProvider.Default.Current.BossItems);
            Assert.Empty(DropDataProvider.Default.Current.CardAreas);
        }
        finally
        {
            GameDataDrops.Reset();
            try { Directory.Delete(folder, recursive: true); } catch (IOException) { }
        }
    }
}
