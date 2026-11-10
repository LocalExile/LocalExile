using GameServer.Storage;

namespace GameServer.Gateway.Tests;

public sealed class ItemWeightTests
{
    [Fact]
    public void Card_and_scarab_picks_follow_the_published_weights_and_the_drop_level()
    {
        string folder = Path.Combine(Path.GetTempPath(), "localexile-item-weights-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            // world.db's rows, with the card and scarab weights replaced as an update file would.
            string path = Path.Combine(folder, "world.db");
            using var store = new GameDataStore(path);
            using (var db = SqlUpdater.Open(path))
            using (var command = db.CreateCommand())
            {
                command.CommandText = """
                    DELETE FROM drop_item_weights WHERE category IN ('card', 'scarab');
                    INSERT INTO drop_item_weights (category, item_id, weight, enabled, source, confidence, patch, sampled_at, notes) VALUES
                    ('card', 'Metadata/Items/DivinationCards/DivinationCardRainOfChaos', 1, 1, 'test', 'estimated', '3.27.0', '2026-02-08', NULL),
                    ('card', 'Metadata/Items/DivinationCards/DivinationCardTheDoctor', 1e-9, 1, 'test', 'estimated', '3.27.0', '2026-02-08', NULL),
                    ('card', 'Metadata/Items/DivinationCards/DivinationCardTheApothecary', 1e9, 0, 'test', 'estimated', '3.27.0', '2026-02-08', 'disabled'),
                    ('scarab', 'Metadata/Items/Scarabs/ScarabAbyssNew1', 1, 1, 'test', 'estimated', '3.28.0', '2026-03-23', NULL),
                    ('scarab', 'Metadata/Items/Scarabs/ScarabBreach1', 1e9, 0, 'test', 'estimated', '3.28.0', '2026-03-23', 'disabled');
                    """;
                command.ExecuteNonQuery();
            }
            GameDataDrops.Reload(store);

            var drops = new ItemDrops(new ItemCatalog("data"));
            var rng = new Random(20261108);
            for (int i = 0; i < 100; i++)
            {
                Assert.Equal("Rain of Chaos", drops.Card(rng, 100)!.Type.Name);
                Assert.Equal("Abyss Scarab", drops.Scarab(rng, 100)!.Type.Name);
            }
            // No published card weight at level 1; the uniform fallback still honours the DropLevel gate.
            Assert.Equal("Treasures of the Vaal", drops.Card(rng, 1)!.Type.Name);
            Assert.Null(drops.Scarab(rng, 1));
        }
        finally
        {
            GameDataDrops.Reload(GameData.Store!);
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(folder, recursive: true); } catch (IOException) { }
        }
    }
}
