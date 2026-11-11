using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public class UniqueJewelExecutionTests
{
    [Fact]
    public void Ordinary_unique_rolls_produce_readable_timeless_jewels_for_each_faction()
    {
        var items = Program.Items;
        var uniques = new UniqueCatalog(items);
        var random = new Random(318);
        Assert.NotEmpty(TimelessJewelCatalog.All);
        foreach (var family in TimelessJewelCatalog.All)
        {
            var conquerors = new HashSet<int>();
            for (var i = 0; i < 64; i++)
            {
                var jewel = Assert.IsType<Item>(uniques.Create(family.Name, random));
                var spec = Assert.IsType<TimelessSpec>(TimelessJewelCatalog.Read(jewel, items.Mods));
                Assert.Equal(family.Name, spec.Name);
                Assert.InRange(spec.Seed, family.MinSeed, family.MaxSeed);
                Assert.Equal(spec.Seed * family.DisplayMultiplier, spec.DisplayedSeed);
                conquerors.Add(spec.ConquerorIndex);
                Assert.Equal(family.Name == "Militant Faith" ? 3 : 1, jewel.Mods.Count);
            }
            Assert.True(conquerors.SetEquals(family.Conquerors.Select(c => c.Index)));
        }
    }

    [Fact]
    public void Impossible_escape_depends_on_its_socket_and_excludes_its_named_keystone()
    {
        var path = Path.Combine(Path.GetTempPath(), $"escape-{Guid.NewGuid():N}.db");
        Saves.Open(path);
        try
        {
            var conn = new ClientState(new(1, "escape", 100, 100, 100, 100, null, []), 1, 1)
                { Character = $"Escape{Guid.NewGuid():N}" };
            var tree = Game.PassivesFor(conn);
            var socket = tree.JewelNodeForSlot(2)!.Value;
            var inventory = Game.ItemsOf(conn);
            Assert.NotEmpty(ImpossibleEscapeCatalogue.All);
            foreach (var target in ImpossibleEscapeCatalogue.All)
            {
                var jewel = Assert.IsType<Item>(ImpossibleEscapeCatalogue.Create(Program.Items, target.Name));
                inventory.Put(PlayerInventories.PassiveJewels, new(2, 2, 0, jewel));
                var available = Game.AllocationBypassNodes(conn, [socket]);
                Assert.NotEmpty(available);
                Assert.DoesNotContain(target.GraphId, available.Keys);
                Assert.All(available, pair => Assert.Contains(socket, pair.Value));
                Assert.Empty(Game.AllocationBypassNodes(conn, [socket], skipItemId: 2));
                Assert.Empty(Game.AllocationBypassNodes(conn, [target.GraphId]));
                if (target.Name == "Supreme Ego") Assert.Contains((ushort)9015, available.Keys);
                inventory.Remove(PlayerInventories.PassiveJewels, 2);
            }
        }
        finally
        {
            TestEnvironment.ReleaseSaves(path);
        }
    }
}
