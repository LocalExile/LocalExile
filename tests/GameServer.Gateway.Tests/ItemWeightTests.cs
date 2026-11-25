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

    [Fact]
    public void Map_card_drops_include_global_pool_without_inflating_rare_area_cards()
    {
        string folder = Path.Combine(Path.GetTempPath(), "localexile-map-cards-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string path = Path.Combine(folder, "world.db");
            using var store = new GameDataStore(path);
            using (var db = SqlUpdater.Open(path))
            using (var command = db.CreateCommand())
            {
                command.CommandText = """
                    DELETE FROM drop_item_weights WHERE category = 'card';
                    DELETE FROM drop_card_areas;
                    DELETE FROM drop_card_sources;
                    INSERT INTO drop_item_weights (category, item_id, weight, enabled, source, confidence, patch, sampled_at, notes) VALUES
                    ('card', 'Metadata/Items/DivinationCards/DivinationCardTheMetalsmithsGift', 0.0263, 1, 'test', 'estimated', '3.27.0', '2026-02-08', NULL),
                    ('card', 'Metadata/Items/DivinationCards/DivinationCardTheCarrionCrow', 0.0177, 1, 'test', 'estimated', '3.27.0', '2026-02-08', NULL),
                    ('card', 'Metadata/Items/DivinationCards/DivinationCardISeeBrothers', 3.34e-05, 1, 'test', 'estimated', '3.27.0', '2026-02-08', NULL),
                    ('card', 'Metadata/Items/DivinationCards/DivinationCardDeadlyJoy', 1.67e-05, 1, 'test', 'estimated', '3.27.0', '2026-02-08', NULL),
                    ('card', 'Metadata/Items/DivinationCards/DivinationCardTheDoctor', 1.00e-05, 1, 'test', 'estimated', '3.27.0', '2026-02-08', NULL),
                    ('card', 'Metadata/Items/DivinationCards/DivinationCardTheEndlessDarkness', 0.05, 1, 'test', 'estimated', '3.27.0', '2026-02-08', NULL);
                    INSERT INTO drop_card_areas (area_id, area_name, card_id, min_map_tier, source) VALUES
                    ('Arcade', 'Arcade', 'I See Brothers', 1, 'test'),
                    ('Arcade', 'Arcade', 'Deadly Joy', 13, 'test'),
                    ('Burial Chambers', 'Burial Chambers', 'The Doctor', 7, 'test');
                    INSERT INTO drop_card_sources (card_id, source_kind, source_id, min_map_tier, drop_level, confidence, source, notes) VALUES
                    ('Metadata/Items/DivinationCards/DivinationCardTheEndlessDarkness', 'boss', 'Metadata/Monsters/LeagueAzmeri/VoodooKingBoss/VoodooKingBoss', NULL, 0, 'estimated', 'test', NULL);
                    """;
                command.ExecuteNonQuery();
            }
            GameDataDrops.Reload(store);

            var drops = new ItemDrops(new ItemCatalog("data"));
            var rng = new Random(20261126);
            int globalCount = 0;
            int brothersCount = 0;
            int doctorCount = 0;
            int bossCardCount = 0;
            const int rolls = 2000;
            for (int i = 0; i < rolls; i++)
            {
                var card = drops.Card(rng, level: 83, areaId: "MapWorldsArcade", mapTier: 16);
                Assert.NotNull(card);
                switch (card.Type.Name)
                {
                    case "The Metalsmith's Gift" or "The Carrion Crow":
                        globalCount++;
                        break;
                    case "I See Brothers":
                        brothersCount++;
                        break;
                    case "The Doctor":
                        doctorCount++;
                        break;
                    case "The Endless Darkness":
                        bossCardCount++;
                        break;
                }
            }

            Assert.True(globalCount > 1900, $"Expected global cards to dominate Arcade drops, got {globalCount}/{rolls}");
            Assert.True(brothersCount < 40, $"Expected I See Brothers to stay rare (<2%), got {brothersCount}/{rolls}");
            Assert.Equal(0, doctorCount);
            Assert.Equal(0, bossCardCount);
        }
        finally
        {
            GameDataDrops.Reload(GameData.Store!);
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(folder, recursive: true); } catch (IOException) { }
        }
    }
}
