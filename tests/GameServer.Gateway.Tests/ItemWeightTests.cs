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
            using var store = new GameDataStore(Path.Combine(folder, "drops.db"));
            store.ReplaceItemWeights([
                new("card", "Metadata/Items/DivinationCards/DivinationCardRainOfChaos", 1, true,
                    "test", "estimated", "3.27.0", "2026-02-08", null),
                new("card", "Metadata/Items/DivinationCards/DivinationCardTheDoctor", 1e-9, true,
                    "test", "estimated", "3.27.0", "2026-02-08", null),
                new("card", "Metadata/Items/DivinationCards/DivinationCardTheApothecary", 1e9, false,
                    "test", "estimated", "3.27.0", "2026-02-08", "disabled"),
                new("scarab", "Metadata/Items/Scarabs/ScarabAbyssNew1", 1, true,
                    "test", "estimated", "3.28.0", "2026-03-23", null),
                new("scarab", "Metadata/Items/Scarabs/ScarabBreach1", 1e9, false,
                    "test", "estimated", "3.28.0", "2026-03-23", "disabled"),
            ]);
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
            GameDataDrops.Reset();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(folder, recursive: true); } catch (IOException) { }
        }
    }
}
