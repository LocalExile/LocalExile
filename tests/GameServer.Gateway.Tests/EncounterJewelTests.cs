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
    }

    [Fact]
    public void Megalomaniac_has_three_notables_one_empty_small_and_no_child_socket()
    {
        var jewel = new UniqueCatalog(Program.Items).Create("Megalomaniac", new Random(202))!;
        Assert.Equal(4, Game.ClusterNodeBudget(jewel));
        Assert.Equal(0, Game.ClusterSocketCount(jewel));
        foreach (int cell in new[] { 20, 51 })
        {
            var layout = Assert.IsType<ClusterJewelLayout.Layout>(ClusterJewelLayout.For(cell, jewel));
            Assert.Equal(3, layout.ByOffset.Values.Count(k => k == ClusterJewelLayout.Kind.Notable));
            Assert.Equal(1, layout.ByOffset.Values.Count(k => k == ClusterJewelLayout.Kind.Small));
            Assert.DoesNotContain(ClusterJewelLayout.Kind.Socket, layout.ByOffset.Values);
            Assert.Equal(new[] { 0, 1, 2 }, layout.NotableOrders.Values.Order().ToArray());
        }
    }
}
