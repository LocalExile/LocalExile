using GameServer.Storage;

namespace GameServer.Gateway.Tests;

public sealed class DropDataTests
{
    [Fact]
    public void Drop_seed_loads_the_csv_files_and_does_not_duplicate_rows()
    {
        string folder = Path.Combine(Path.GetTempPath(), "localexile-drop-seed-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            File.WriteAllText(Path.Combine(folder, "currency-weights.csv"),
                """
                currency_id,weight,drop_level,enabled,source,confidence,notes
                CurrencyA,10,1,1,test,estimated,
                CurrencyB,2.5,12,1,test,estimated,note
                """);
            File.WriteAllText(Path.Combine(folder, "bosses.csv"),
                """
                area_id,area_name,category,act,area_level,bosses,boss_variety_ids,boss_ids
                area1,Test Area,map,0,80,Test Boss,0,Metadata/TestBoss
                """);
            File.WriteAllText(Path.Combine(folder, "boss-drops.csv"),
                """
                boss_name,boss_id,item,item_kind,chance,confidence,source,notes
                Test Boss,Metadata/TestBoss,Unique A,unique,0.5,estimated,test,
                Test Boss,Metadata/TestBoss,Metadata/Items/Currency/CurrencyIdentification,currency,,unknown,test,
                """);
            File.WriteAllText(Path.Combine(folder, "divination-card-areas.csv"),
                """
                area,card,min_map_tier,source
                Test Area,The Doctor,7,test
                """);

            using var store = new GameDataStore(Path.Combine(folder, "drops.db"));
            Assert.True(GameDataDrops.Seed(store, folder));
            Assert.False(GameDataDrops.Seed(store, folder));

            var counts = store.DropCounts();
            Assert.Equal(2, counts.CurrencyWeights);
            Assert.Equal(1, counts.Bosses);
            Assert.Equal(2, counts.BossItems);
            Assert.Equal(1, counts.CardAreas);

            GameDataDrops.Reload(store);
            Assert.True(DropDataProvider.Default.HasTable);
            Assert.Equal(10, DropDataProvider.Default.Current.CurrencyWeights["CurrencyA"].Weight);
            Assert.Equal(12, DropDataProvider.Default.Current.CurrencyWeights["CurrencyB"].DropLevel);
            Assert.Equal(0.5, DropDataProvider.Default.Current.BossItems[("Metadata/TestBoss", "Unique A")].Chance);
            Assert.Null(DropDataProvider.Default.Current.BossItems[("Metadata/TestBoss", "Metadata/Items/Currency/CurrencyIdentification")].Chance);
            Assert.Equal(7, DropDataProvider.Default.Current.CardAreas[("Test Area", "The Doctor")].MinMapTier);
        }
        finally
        {
            GameDataDrops.Reset();
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
