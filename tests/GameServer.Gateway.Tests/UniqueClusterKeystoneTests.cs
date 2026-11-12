using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public class UniqueClusterKeystoneTests
{
    [Fact]
    public void Each_unique_small_cluster_grants_its_own_keystone_on_its_one_native_node()
    {
        var items = Program.Items;
        var catalog = new UniqueCatalog(items);
        var conn = new ClientState(new(1, "keystone", 100, 100, 100, 100, null, []), 1, 1);
        foreach (var (name, stat) in new[]
        {
            ("Calamitous Visions", "keystone_herald_of_doom"), ("Kitava's Teachings", "keystone_disciple_of_kitava"),
            ("Natural Affinity", "keystone_natures_presence"), ("One With Nothing", "keystone_hollow_palm_technique"),
            ("The Front Line", "keystone_veterans_awareness"), ("The Interrogation", "keystone_secrets_of_suffering"),
            ("The Siege", "keystone_barbed_tips"),
        })
        {
            var jewel = catalog.Create(name, new Random(1))!;
            Assert.Equal(1, Game.ClusterNodeBudget(jewel));
            foreach (int cell in new[] { 20, 51 })
            {
                var layout = ClusterJewelLayout.For(cell, jewel)!;
                var position = Assert.Single(layout.ByOffset);
                Assert.Equal(cell == 20 ? 199 : 208, layout.Base + position.Key);
                Assert.Equal(ClusterJewelLayout.Kind.Keystone, position.Value);
                ushort id = (ushort)(layout.Base + position.Key);
                InventoryItem[] placed = [new(11, (byte)cell, 0, jewel)];
                conn.ClusterPassives.Clear();
                Assert.Empty(ClusterJewelStats.For(conn, placed, items.Mods, Program.Stats));
                conn.ClusterPassives.Add(id);
                var granted = Assert.Single(ClusterJewelStats.For(conn, placed, items.Mods, Program.Stats));
                Assert.Equal(Program.Stats.Key(stat)!.Value, granted.Key);
                Assert.Equal(1, granted.Value);
                Assert.Empty(ClusterJewelStats.For(conn, [], items.Mods, Program.Stats));
            }
        }
    }
}
