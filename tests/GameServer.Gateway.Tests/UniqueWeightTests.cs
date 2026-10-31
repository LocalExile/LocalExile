using GameServer.Storage;
using Microsoft.Data.Sqlite;

namespace GameServer.Gateway.Tests;

// Unique drop weights: seeded reference rows from VillageUniqueDisenchantValues, admin overrides on top, and the
// provider ItemDrops reads. The extracted JSON is the fallback only until the reference table exists.
public class UniqueWeightTests
{
    // The shared TestEnvironment establishes the checkout root before any catalogue loads.
    static string Data() => "data";

    static readonly Lazy<(ItemCatalog Catalog, string Data, UniqueCatalog Uniques)> Setup = new(() =>
    {
        string data = Data();
        var catalog = new ItemCatalog(data);
        return (catalog, data, new UniqueCatalog(catalog, data));
    });

    static string TempDb() => Path.Combine(Path.GetTempPath(), $"localexile-weights-{Guid.NewGuid():N}.db");

    static void Delete(string path)
    {
        foreach (string file in new[] { path, path + "-wal", path + "-shm" })
            try { File.Delete(file); } catch (IOException) { }
    }

    [Fact]
    public void Schema_two_upgrade_preserves_old_ranges_and_weight_overrides_survive_rebuilds_and_reopen()
    {
        string path = TempDb();
        try
        {
            using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()))
            {
                db.Open();
                using var command = db.CreateCommand();
                command.CommandText = """
                    CREATE TABLE game_data_meta (key TEXT PRIMARY KEY, value TEXT NOT NULL) STRICT;
                    INSERT INTO game_data_meta VALUES ('schema_version', '2');
                    CREATE TABLE mod_stat_overrides (
                        mod_id TEXT NOT NULL, stat_index INTEGER NOT NULL, minimum INTEGER NOT NULL,
                        maximum INTEGER NOT NULL, updated_utc TEXT NOT NULL, PRIMARY KEY (mod_id, stat_index)
                    ) STRICT;
                    INSERT INTO mod_stat_overrides VALUES ('LocalAddedPhysicalDamage9', 0, 12, 34, '2026-01-01T00:00:00Z');
                    """;
                command.ExecuteNonQuery();
            }
            using (var store = new GameDataStore(path))
            {
                Assert.Equal("3", store.Meta("schema_version"));
                Assert.Equal(12, Assert.Single(store.LoadModRanges()).Minimum);
                Assert.True(GameDataWeights.Seed(store));
                store.SetUniqueWeight("Mageblood", .5, .25);
                for (int i = 0; i < 2; i++)
                {
                    Assert.True(GameDataWeights.Seed(store, force: true));
                    Assert.Equal(.5, Assert.Single(store.LoadUniqueWeightOverrides()).Weight);
                }
            }
            using (var reopened = new GameDataStore(path))
            {
                Assert.Equal("3", reopened.Meta("schema_version"));
                Assert.Equal(34, Assert.Single(reopened.LoadModRanges()).Maximum);
                var provider = new UniqueWeightProvider();
                GameDataWeights.Reload(reopened, provider);
                Assert.Equal(.5, provider.Weight("Mageblood", false));
                Assert.Equal(.25, provider.Weight("Mageblood", true));
            }
        }
        finally { Delete(path); }
    }

    [Fact]
    public void Invalid_weights_are_rejected_without_creating_an_override()
    {
        string path = TempDb();
        try
        {
            using var store = new GameDataStore(path);
            foreach (double invalid in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity, -.1, 1.1 })
            {
                Assert.Throws<ArgumentException>(() => store.SetUniqueWeight("Mageblood", invalid, .5));
                Assert.Throws<ArgumentException>(() => store.SetUniqueWeight("Mageblood", .5, invalid));
            }
            Assert.Empty(store.LoadUniqueWeightOverrides());
        }
        finally { Delete(path); }
    }

    [Fact]
    public void The_reference_seeds_from_the_client_table_and_a_unique_uses_its_weight()
    {
        string path = TempDb();
        try
        {
            using var store = new GameDataStore(path);
            Assert.True(GameDataWeights.Seed(store));
            Assert.False(GameDataWeights.Seed(store));
            Assert.Equal(1527, store.UniqueWeightCounts().Weights);

            var provider = new UniqueWeightProvider();
            GameDataWeights.Reload(store, provider);
            Assert.True(provider.HasTable);
            Assert.True(provider.Weight("Wanderlust", false) > provider.Weight("Mageblood", false));
            Assert.Equal(0, provider.Weight("Victario's Flight", false));

            var (catalog, data, uniques) = Setup.Value;
            var drops = new ItemDrops(catalog, new ItemRolls(catalog, data), data,
                uniques: uniques, uniqueWeights: provider);
            Assert.Equal(provider.Weight("Mageblood", false), drops.UniqueWeight("Mageblood"));
            Assert.Equal(0, drops.UniqueWeight("Victario's Flight"));
        }
        finally
        {
            Delete(path);
        }
    }

    [Fact]
    public void An_empty_seeded_reference_does_not_fall_back_to_the_json()
    {
        string path = TempDb();
        try
        {
            using var store = new GameDataStore(path);
            GameDataWeights.Seed(store);
            store.SeedUniqueWeights([]);
            var provider = new UniqueWeightProvider();
            GameDataWeights.Reload(store, provider);
            Assert.True(provider.HasTable);
            Assert.Equal(0, provider.Weight("Mageblood", false));
        }
        finally
        {
            Delete(path);
        }
    }

    [Fact]
    public void An_override_replaces_the_reference_and_clear_restores_it()
    {
        string path = TempDb();
        try
        {
            using var store = new GameDataStore(path);
            GameDataWeights.Seed(store);
            double reference = store.LoadUniqueWeights().Single(row => row.Name == "Mageblood").Weight;

            store.SetUniqueWeight("Mageblood", 0.5, 0.25);
            var provider = new UniqueWeightProvider();
            GameDataWeights.Reload(store, provider);
            Assert.Equal(0.5, provider.Weight("Mageblood", false));
            Assert.Equal(0.25, provider.Weight("Mageblood", true));
            Assert.Equal((1527, 1), store.UniqueWeightCounts());

            store.ClearUniqueWeight("Mageblood");
            GameDataWeights.Reload(store, provider);
            Assert.Equal(reference, provider.Weight("Mageblood", false));
            Assert.Equal((1527, 0), store.UniqueWeightCounts());
        }
        finally
        {
            Delete(path);
        }
    }
}
