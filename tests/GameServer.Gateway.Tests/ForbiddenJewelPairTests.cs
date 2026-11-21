using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public sealed class ForbiddenJewelPairTests
{
    [Fact]
    public void Factory_rolls_valid_distinct_passive_ids_for_both_native_modifiers()
    {
        var catalog = new UniqueCatalog(Program.Items);
        var random = new Random(39);
        var pools = new List<HashSet<int>>();
        foreach (string name in new[] { "Forbidden Flame", "Forbidden Flesh" })
        {
            var results = new HashSet<int>();
            for (int i = 0; i < 256; i++)
            {
                var item = Assert.IsType<Item>(catalog.Create(name, random));
                Assert.True(item.Corrupted);
                var mod = Assert.Single(item.Mods);
                Assert.Equal(name == "Forbidden Flame" ? ForbiddenJewelNotables.FlameMod : ForbiddenJewelNotables.FleshMod,
                    Program.Items.Mods.ByHash(mod.Hash)!.Id);
                int id = Assert.Single(mod.Values);
                Assert.InRange(id, 2, ushort.MaxValue);
                Assert.NotNull(Program.Passives.Find((ushort)id));
                results.Add(id);
            }
            Assert.True(results.Count > 20);
            pools.Add(results);
        }
        Assert.NotEmpty(pools[0].Intersect(pools[1]));
    }

    [Fact]
    public void Pair_grants_only_an_eligible_matching_notable_and_removal_clears_its_stats()
    {
        using var f = new MinionUtilityTests.Fixture();
        var conn = f.Conn;
        var tree = Game.PassivesFor(conn);
        var inventory = Game.ItemsOf(conn);
        var catalog = new UniqueCatalog(Program.Items);
        const ushort shaper = 53123;
        const ushort wrongClass = 42469; // Fatal Flourish, Duelist only
        uint ignite = Program.Stats.Key("always_ignite")!.Value;
        uint allDamage = Program.Stats.Key("all_damage_can_ignite")!.Value;
        Item Named(string name, ushort id, bool identified = true)
        {
            var jewel = catalog.Create(name, new Random(6))!;
            var mod = Assert.Single(jewel.Mods);
            return jewel with { Identified = identified, Mods = [new ItemMod(mod.Section, mod.Hash, [id])] };
        }
        var a = tree.JewelNodeForSlot(0)!.Value;
        var b = tree.JewelNodeForSlot(1)!.Value;
        (int Ignite, int AllDamage) Read(params ushort[] nodes)
        {
            var s = Game.EquipmentStatSources(conn, [], nodes, 100).Sources;
            return (s.GetValueOrDefault(ignite), s.GetValueOrDefault(allDamage));
        }

        inventory.Put(PlayerInventories.PassiveJewels, new(12, 0, 0, Named("Forbidden Flame", shaper)));
        inventory.Put(PlayerInventories.PassiveJewels, new(13, 1, 0, Named("Forbidden Flesh", wrongClass)));
        Assert.Equal((0, 0), Read(a, b));
        inventory.Change(PlayerInventories.PassiveJewels, 13, _ => Named("Forbidden Flesh", shaper, identified: false));
        Assert.Equal((0, 0), Read(a, b));
        inventory.Change(PlayerInventories.PassiveJewels, 13, _ => Named("Forbidden Flesh", shaper));
        Assert.Equal((0, 0), Read(a));
        Assert.Equal((1, 1), Read(a, b));
        Assert.Equal((1, 1), Read(a, b));
        Assert.Empty(conn.MainAscendancy.Allocated);
        Assert.Equal((1, 1), Read(a, b, shaper)); // an already-paid copy would contribute only its own stats
        inventory.Remove(PlayerInventories.PassiveJewels, 13);
        Assert.Equal((0, 0), Read(a, b));
    }
}
