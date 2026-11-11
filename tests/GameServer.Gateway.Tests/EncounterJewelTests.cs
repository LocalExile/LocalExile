using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public class EncounterJewelTests
{
    [Fact]
    public void Named_encounter_jewels_choose_distinct_native_mods_from_each_qualified_pool()
    {
        var catalog = new UniqueCatalog(Program.Items);
        var random = new Random(58);
        foreach (string name in new[] { "Watcher's Eye", "Sublime Vision", "The Balance of Terror", "Bound By Destiny", "Megalomaniac" })
        {
            var def = Assert.IsType<UniqueCatalog.Definition>(catalog.Find(name));
            Assert.True(def.EncounterOnly);
            foreach (var level in new byte[] { 85, 86 })
                for (int trial = 0; trial < 16; trial++)
                {
                    var jewel = Assert.IsType<Item>(catalog.Create(def with { ItemLevel = level }, random));
                    var ids = jewel.Mods.Select(mod => Program.Items.Mods.ByHash(mod.Hash)!.Id).ToArray();
                    Assert.Equal(ids.Length, ids.Distinct().Count());
                    Assert.All(def.Mods, id => Assert.Contains(id, ids));
                    Assert.NotEmpty(def.RollGroups!);
                    foreach (var group in def.RollGroups!)
                    {
                        int count = name == "Watcher's Eye" && level >= 86 ? 3 : group.Count;
                        Assert.Equal(count, ids.Count(id => group.Options.Contains(id)));
                    }
                    Assert.Equal(name == "Megalomaniac", jewel.Corrupted);
                }
        }
        var cluster = new UniqueCatalog(Program.Items).Create("Megalomaniac", new Random(202))!;
        Assert.Equal(4, Game.ClusterNodeBudget(cluster));
        Assert.Equal(0, Game.ClusterSocketCount(cluster));
        foreach (int cell in new[] { 20, 51 })
        {
            var layout = Assert.IsType<ClusterJewelLayout.Layout>(ClusterJewelLayout.For(cell, cluster));
            Assert.Equal(3, layout.ByOffset.Values.Count(k => k == ClusterJewelLayout.Kind.Notable));
            Assert.Equal(1, layout.ByOffset.Values.Count(k => k == ClusterJewelLayout.Kind.Small));
            Assert.DoesNotContain(ClusterJewelLayout.Kind.Socket, layout.ByOffset.Values);
            Assert.Equal(new[] { 0, 1, 2 }, layout.NotableOrders.Values.Order().ToArray());
        }
    }

    [Fact]
    public void A_configured_boss_chance_preserves_the_reference_and_only_rewards_the_named_kill()
    {
        string folder = Directory.CreateTempSubdirectory("encounter-jewels-").FullName;
        try
        {
            using var store = new GameServer.Storage.GameDataStore(Path.Combine(folder, "world.db"));
            using var db = GameServer.Storage.SqlUpdater.Open(store.Path);
            var provider = new DropDataProvider();
            var random = new Random(58);
            var drops = new ItemDrops(Program.Items, uniques: new UniqueCatalog(Program.Items));
            const string elder = "Metadata/Monsters/AtlasBosses/TheElder/TheElder";
            const string eye = "Watcher's Eye";
            void Configure(double chance)
            {
                using var command = db.CreateCommand();
                command.CommandText = "INSERT OR REPLACE INTO encounter_jewel_drop_overrides VALUES ($boss, $item, $chance)";
                command.Parameters.AddWithValue("$boss", elder);
                command.Parameters.AddWithValue("$item", eye);
                command.Parameters.AddWithValue("$chance", chance);
                command.ExecuteNonQuery();
                provider.Replace(store);
                Assert.Null(provider.Current.BossItems[(elder, eye)].Chance);
                Assert.Equal(chance, provider.Current.BossItems[(elder, eye)].OverrideChance);
            }
            provider.Replace(store);
            Assert.Null(provider.Current.BossItems[(elder, eye)].OverrideChance);
            Assert.Empty(drops.EncounterJewels(elder, 83, random, provider.Current));
            Configure(0);
            Assert.Empty(drops.EncounterJewels(elder, 83, random, provider.Current));
            Configure(1);
            var reward = Assert.Single(drops.EncounterJewels(elder, 83, random, provider.Current));
            Assert.False(reward.Item.Identified);
            Assert.Equal(83 + ItemDrops.ItemLevelBonus(MonsterRarity.Unique), (int)reward.Item.ItemLevel);
            Assert.Equal(5, reward.Item.Mods.Count);
            Assert.Empty(drops.EncounterJewels("unrelated monster", 83, random, provider.Current));
        }
        finally { Directory.Delete(folder, recursive: true); }
    }
}
