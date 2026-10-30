using GameServer.Storage;

namespace GameServer.Gateway.Tests;

// GameDataCatalog.Seed records the applied seed keys, so a catalogue row an admin deleted is not inserted again by
// the next reload or process restart, while a genuinely new seed row still arrives.
public class GameDataCatalogSeedTests
{
    const string Kind = "seed_test";

    static string TempDb() => Path.Combine(Path.GetTempPath(), $"localexile-seed-{Guid.NewGuid():N}.db");

    static void Delete(string path)
    {
        foreach (string file in new[] { path, path + "-wal", path + "-shm" })
            try { File.Delete(file); } catch (IOException) { }
    }

    static void Seed(GameDataStore store, params string[] keys) =>
        GameDataCatalog.Seed(store, Kind, keys, key => key);

    [Fact]
    public void A_deleted_seed_row_stays_deleted_across_reload_and_reopen()
    {
        string path = TempDb();
        try
        {
            using (var store = new GameDataStore(path))
            {
                Seed(store, "A", "B", "C");
                Assert.Equal(3, store.LoadCatalog(Kind).Count);
                store.DeleteCatalogRow(Kind, "B");

                Seed(store, "A", "B", "C");
                Assert.Equal(new[] { "A", "C" }, store.LoadCatalog(Kind).Select(row => row.Key).Order());
            }

            using (var reopened = new GameDataStore(path))
            {
                Seed(reopened, "A", "B", "C");
                Assert.Equal(new[] { "A", "C" }, reopened.LoadCatalog(Kind).Select(row => row.Key).Order());
            }
        }
        finally
        {
            Delete(path);
        }
    }

    [Fact]
    public void An_emptied_kind_stays_empty_across_reload_and_reopen()
    {
        string path = TempDb();
        try
        {
            using (var store = new GameDataStore(path))
            {
                Seed(store, "A", "B");
                foreach (var row in store.LoadCatalog(Kind)) store.DeleteCatalogRow(Kind, row.Key);
                Seed(store, "A", "B");
                Assert.Empty(store.LoadCatalog(Kind));
            }

            using (var reopened = new GameDataStore(path))
            {
                Seed(reopened, "A", "B");
                Assert.Empty(reopened.LoadCatalog(Kind));
            }
        }
        finally
        {
            Delete(path);
        }
    }

    [Fact]
    public void A_new_seed_key_is_added_without_restoring_a_deleted_one()
    {
        string path = TempDb();
        try
        {
            using var store = new GameDataStore(path);
            Seed(store, "A", "B");
            store.DeleteCatalogRow(Kind, "B");

            Seed(store, "A", "B", "C");

            Assert.Equal(new[] { "A", "C" }, store.LoadCatalog(Kind).Select(row => row.Key).Order());
        }
        finally
        {
            Delete(path);
        }
    }

    [Fact]
    public void An_edited_seed_row_is_not_overwritten()
    {
        string path = TempDb();
        try
        {
            using var store = new GameDataStore(path);
            Seed(store, "A");
            store.PutCatalogRow(Kind, "A", "\"edited\"");

            Seed(store, "A");

            Assert.Equal("\"edited\"", store.LoadCatalog(Kind).Single().Payload);
        }
        finally
        {
            Delete(path);
        }
    }
}
