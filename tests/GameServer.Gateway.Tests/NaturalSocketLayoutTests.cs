using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public class NaturalSocketLayoutTests
{
    [Fact]
    public void An_unset_ring_has_one_socket_when_created_dropped_and_restored()
    {
        var catalog = Program.Items;
        var ring = Assert.IsType<ItemCatalog.BaseType>(catalog.Find("Metadata/Items/Rings/Ring15"));
        var random = new Random(42);
        var created = ItemCatalog.Create(ring);
        Assert.Single(created.Sockets);
        Assert.Single(catalog.DefaultSockets(ring, 45, random: random).Sockets);
        Assert.Single(catalog.DropSockets(ring, 45, random).Sockets);
        Assert.Equal(1, CurrencyEffects.MaximumSockets(created, ring));
        var saved = CharacterSaves.ToSaved(created, catalog.Mods);
        var restored = CharacterSaves.FromSaved(saved with { Sockets = [], Links = null }, catalog);
        Assert.Equal(SocketColour.White, Assert.Single(restored.Sockets).Colour);
        Assert.Single(CharacterSaves.FromSaved(CharacterSaves.ToSaved(restored, catalog.Mods), catalog).Sockets);

        var iron = Assert.IsType<ItemCatalog.BaseType>(catalog.Find("Metadata/Items/Rings/Ring1"));
        Assert.Empty(catalog.DropSockets(iron, 45, random).Sockets);
        Assert.Empty(ItemCatalog.Create(iron).Sockets);
    }

    [Fact]
    public void Ordinary_armour_drops_vary_within_the_level_and_base_limits()
    {
        var drops = new ItemDrops(Program.Items);
        var rng = new Random(20323);
        foreach (var (level, max) in new[] { (24, 3), (35, 5), (60, 6) })
        {
            var counts = new HashSet<int>();
            foreach (var drop in drops.Container(level, 2000, 0, rng).Where(d => d.Type.Class == "Body Armour"))
            {
                int n = drop.Item.Sockets.Count;
                Assert.InRange(n, 1, max);
                Assert.Equal(n, drop.Item.Links!.Sum(group => (int)group));
                counts.Add(n);
            }
            Assert.True(counts.Count > 1, $"level {level}, socket counts: {string.Join(",", counts)}");
        }
    }
}
